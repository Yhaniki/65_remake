using System;
using System.Collections.Generic;
using Sdo.Net;
using Sdo.Net.Server;

namespace Sdo.Server.Net
{
    /// <summary>
    /// Hub 的訊息處理。**全部在 actor 執行緒上執行**,所以可以自由碰房間狀態、不需要任何 lock。
    ///
    /// 每個 handler 的形狀都一樣:驗權限與狀態(交給 <see cref="NetRoom"/> 的純邏輯)→
    /// 失敗回 <c>error</c>、成功廣播新的 <c>roomState</c>。
    /// 權限**絕不**只靠 client 隱藏按鈕 —— 每個 host-only 操作 server 都獨立驗一次。
    /// </summary>
    public sealed partial class Hub
    {
        /// <summary>roomCode → (userId → 最新一筆 frame)。固定頻率彙整後推出去,見 <see cref="PushPendingFrames"/>。</summary>
        private readonly Dictionary<int, Dictionary<int, FrameSample>> _pendingFrames
            = new Dictionary<int, Dictionary<int, FrameSample>>();

        /// <summary>
        /// roomCode → (userId → 本場收到的最新一筆 frame)。
        /// 不隨 200ms 廣播清空；斷線者沒送 playFinished 時，R16 結算要用這份最後快照。
        /// </summary>
        private readonly Dictionary<int, Dictionary<int, FrameSample>> _latestFrames
            = new Dictionary<int, Dictionary<int, FrameSample>>();

        /// <summary>
        /// roomCode → (userId → playFinished 帶來的最終成績)。
        /// 與 200ms 推送後會清空的 <see cref="_pendingFrames"/> 分開保存,直到本場結算或中止。
        /// </summary>
        private readonly Dictionary<int, Dictionary<int, FrameSample>> _finalFrames
            = new Dictionary<int, Dictionary<int, FrameSample>>();
        /// <summary>roomCode to each user's last forwarded combo milestone in this match.</summary>
        private readonly Dictionary<int, Dictionary<int, int>> _comboMilestones
            = new Dictionary<int, Dictionary<int, int>>();

        /// <summary>roomCode to the authoritative live leader state for this match.</summary>
        private readonly Dictionary<int, LiveLeaderTracker> _liveLeaders
            = new Dictionary<int, LiveLeaderTracker>();


        /// <summary>roomCode → (userId → 最新一筆房間內位置)。同上,見 <see cref="PushPendingMoves"/>。</summary>
        private readonly Dictionary<int, Dictionary<int, MoveSample>> _moves
            = new Dictionary<int, Dictionary<int, MoveSample>>();

        /// <summary>這一輪有人動過的房間。位置表不清空(要留最新位置給後進房的人),所以用它決定要不要推。</summary>
        private readonly HashSet<int> _movesDirty = new HashSet<int>();

        // ================= frame 進入點 =================

        private void HandleFrame(Connection conn, byte kind, byte[] payload)
        {
            if (conn.IsClosed) return;

            if (kind == NetLimits.FrameKindChunk)
            {
                // 🔴 握手之前一個 chunk 都不收。下面 JSON 那條路徑有「HelloDone 之前只准 hello」的守門,
                // 這裡少了同一道的話,任何連上 port 的人都能不認證就一直丟 64 KiB 的 chunk 進來 ——
                // 每一塊都會排進單執行緒的 actor loop 並讓我們回一封錯誤,等於免費的放大器。
                if (!conn.HelloDone) { conn.Kill(NetProto.ErrProto); return; }

                conn.CurMsgType = "chunk";

                // 上傳的位元組。刻意不吃 control 的 rate limit —— 一首歌是幾百塊 chunk,
                // 32/s 會把正常上傳擋死。總量的防線在 blobUploadBegin 那份清單上
                // (超過宣稱長度就中止),而清單本身是 control 訊息、有被限流。
                OnUploadChunk(conn, payload, NowMs());
                return;
            }

            object node;
            string type;
            if (!NetJson.TryParseMessage(payload, 0, payload.Length, out node, out type))
            {
                conn.Kill(NetProto.ErrBadJson);
                return;
            }

            long now = NowMs();
            int rq = NetJson.Int(node, NetProto.FieldRequest);

            // 握手之前只准 hello。
            if (!conn.HelloDone && type != NetProto.Hello)
            {
                conn.Kill(NetProto.ErrProto);
                return;
            }

            // rate limit:三個獨立的 bucket。
            // 🔴 move 一定要有自己的:走動是 10/s + 換方向的 edge,吃 control 的 32/s 會與
            // setReady / chatSay / setLook 搶同一個窗,搶輸的被靜默丟掉(而且 Strikes 累積還會斷線)。
            // 這與 frame 為什麼要獨立是同一個理由。
            bool ok = type == NetProto.Frame ? conn.Rate.AllowFrame(now)
                    : type == NetProto.Move ? conn.Rate.AllowMove(now)
                    : conn.Rate.AllowControl(now);
            if (!ok)
            {
                conn.Rate.Strikes++;
                // 偶爾爆一下就丟掉那筆;一直爆代表對方壞了或在攻擊。
                if (conn.Rate.Strikes > 20) { conn.Kill(NetProto.ErrRateLimit); }
                return;
            }
            // 🔴 放行就把 strikes 歸零。不歸零的話它是一個**只會往上加的計數器** ——
            // 正常玩家偶爾爆一下(切畫面時一批訊息擠在一起)累積個二十次就會莫名被踢,
            // 而且症狀是「玩了一陣子突然斷線」,完全指不到任何一次爆量。
            // 要擋的是「持續爆量」,那種情況下根本不會有訊息被放行。
            conn.Rate.Strikes = 0;

            LogVerbose("← #" + conn.ConnId + " " + type);
            // 被拒的 log 要說出「是哪一個請求被拒」—— 記在連線上,由 SendError 取用。
            conn.CurMsgType = type;
            Dispatch(conn, type, node, rq, now);
        }

        private void Dispatch(Connection conn, string type, object node, int rq, long now)
        {
            switch (type)
            {
                case NetProto.Hello: OnHello(conn, node, rq, now); break;
                case NetProto.Ping: OnPing(conn, node); break;
                case NetProto.Bye: conn.Close("byeFromClient"); break;

                case NetProto.RoomList: OnRoomList(conn, rq); break;
                case NetProto.UserList: OnUserList(conn, rq); break;
                case NetProto.CreateRoom: OnCreateRoom(conn, node, rq); break;
                case NetProto.JoinRoom: OnJoinRoom(conn, node, rq); break;
                case NetProto.LeaveRoom: OnLeaveRoom(conn); break;

                case NetProto.SetRoomName: OnSetRoomName(conn, node, rq); break;
                case NetProto.SetSong: OnSetSong(conn, node, rq); break;
                case NetProto.SetRoomSettings: OnSetRoomSettings(conn, node, rq); break;
                case NetProto.AssignTeams: OnAssignTeams(conn, node, rq); break;
                case NetProto.SetOwnTeam: OnSetOwnTeam(conn, node, rq); break;
                case NetProto.SetReady: OnSetReady(conn, node, rq); break;
                case NetProto.SetLook: OnSetLook(conn, node); break;
                case NetProto.SetIdentity: OnSetIdentity(conn, node); break;
                case NetProto.Move: OnRoomMove(conn, node, now); break;
                case NetProto.SetAvailability: OnSetAvailability(conn, node, now); break;

                case NetProto.BlobQuery: OnBlobQuery(conn, node, rq, now); break;
                case NetProto.BlobUploadBegin: OnBlobUploadBegin(conn, node, rq, now); break;
                case NetProto.BlobUploadDone: OnBlobUploadDone(conn, node, rq, now); break;
                case NetProto.BlobDownloadBegin: OnBlobDownloadBegin(conn, node, rq, now); break;

                case NetProto.KickUser: OnKickUser(conn, node, rq); break;
                case NetProto.SetSeatClosed: OnSetSeatClosed(conn, node, rq); break;
                case NetProto.TransferHost: OnTransferHost(conn, node, rq); break;

                case NetProto.Spectate: OnSpectate(conn, node, rq); break;
                case NetProto.StopSpectate: OnStopSpectate(conn, rq); break;

                case NetProto.RequestStart: OnRequestStart(conn, node, rq, now); break;
                case NetProto.SetPlayState: OnSetPlayState(conn, node, rq); break;
                case NetProto.Frame: OnGameplayFrame(conn, node); break;
                case NetProto.PlayFinished: OnPlayFinished(conn, node); break;
                case NetProto.ComboMilestone: OnComboMilestone(conn, node); break;

                case NetProto.ChatSay: OnChatSay(conn, node, now); break;
                case NetProto.ChatWhisper: OnChatWhisper(conn, node, now); break;

                default:
                    // 不認得的訊息型別:可能是新版 client。回錯誤但不斷線。
                    SendError(conn, rq, NetProto.ErrProto, "unknown message: " + type);
                    break;
            }
        }

        // ================= 連線 / 工作階段 =================

        private void OnHello(Connection conn, object node, int rq, long now)
        {
            if (conn.HelloDone) { conn.Kill(NetProto.ErrProto); return; }

            int proto = NetJson.Int(node, "proto", -1);
            if (proto != NetProto.Version)
            {
                // 版本不合就明確擋掉 —— 讓它半殘地跑然後在某個角落出怪事更難查。
                conn.Kill(NetProto.ErrProto);
                return;
            }

            if (!string.IsNullOrEmpty(_opts.Password))
            {
                string pw = NetJson.Str(node, "password");
                if (!string.Equals(pw, _opts.Password, StringComparison.Ordinal))
                {
                    // 記下來但**不印出密碼本身** —— log 常常會被貼到 issue 或截圖分享。
                    // 只說「空的」還是「不對」就夠診斷了(最常見的原因就是 client 那邊留空)。
                    Log("連線 #" + conn.ConnId + " 密碼不符,拒絕(client 送的是" +
                        (string.IsNullOrEmpty(pw) ? "空值" : "另一個值") + ")");
                    conn.Kill(NetProto.ErrBadPassword);
                    return;
                }
            }

            // ---- token 認證(M10:公網化)。沒有 token 檔就完全跳過,行為回到 MVP。 ----
            // 🔴 這是「身分由誰決定」的分界線。MVP 階段 playerId 與名字是 client 說了算 ——
            // 在 LAN/朋友之間沒差,一開公網就等於任何人都能冒用任何人。
            // 啟用之後:token 查不到 → 直接拒;token 綁了身分 → **用 token 的,不用 client 送的**。
            AuthIdentity ident;
            if (!_tokens.TryAuth(NetJson.Str(node, "authToken"), out ident))
            {
                // 同密碼那邊的理由:**不印 token 本身**(日誌常被貼到 issue 或截圖)。
                Log("連線 #" + conn.ConnId + " token 認證失敗,拒絕");
                conn.Kill(NetProto.ErrBadToken);
                return;
            }

            string role = NetJson.Str(node, "role", NetProto.RoleControl);
            conn.Role = role == NetProto.RoleFile ? NetProto.RoleFile : NetProto.RoleControl;

            if (conn.Role == NetProto.RoleFile)
            {
                // file 連線靠 sessionKey 認親到既有的 control 連線 —— 它不是新玩家。
                string key = NetJson.Str(node, "sessionKey");
                int owner;
                if (string.IsNullOrEmpty(key) || !_sessions.TryGetValue(key, out owner))
                {
                    conn.Kill("badSession");
                    return;
                }
                conn.UserId = owner;
                conn.SessionKey = key;
                conn.HelloDone = true;
                conn.LastRecvMs = now;
                conn.Send(JObj.New()
                    .Str(NetProto.FieldType, NetProto.Welcome)
                    .Int(NetProto.FieldRequest, rq)
                    .Int("userId", owner)
                    .Str("role", NetProto.RoleFile));
                LogVerbose("#" + conn.ConnId + " 是 user " + owner + " 的檔案連線");
                return;
            }

            conn.PlayerId = Clip(NetJson.Str(node, "playerId"), 32);
            conn.Name = SanitizeName(NetJson.Str(node, "name"));
            // token 綁了身分就覆寫 client 自稱的那份(見上面的註解 —— 這是整個 token 機制的重點)。
            if (ident.HasPlayerId) { conn.PlayerId = Clip(ident.PlayerId, 32); conn.PlayerIdLocked = true; }
            if (ident.HasName) { conn.Name = SanitizeName(ident.Name); conn.NameLocked = true; }
            // 🔴 名字要唯一 —— 同名的**後來者被擋**(先上線的不受影響)。
            // 名字是這裡唯一認人的東西:密語照名字找人(ControlByName)、名字牌、線上名單都是它。
            // 兩個「小明」同時在線的話,密語會進到其中一個而寄的人不知道是哪個,收的人也不知道
            // 為什麼有一半的話不見了 —— 那種 bug 沒人查得出來,所以在門口就不讓它成立。
            //
            // 代價寫在這裡免得日後當成 bug 查:client 當掉重開會被自己那條還沒被清掉的舊連線擋住,
            // 要等 ping 逾時(NetLimits.PingTimeoutMs)把幽靈連線掃掉才進得來。這是有意的取捨 ——
            // 「同名就踢掉舊的」在被冒名時等於送對方一把踢人的鑰匙。
            var sameName = ControlByName(conn.Name);
            if (sameName != null)
            {
                Log("連線 #" + conn.ConnId + " 想用「" + conn.Name + "」上線,但 user "
                    + sameName.UserId + " 已經在線上用這個名字 → 拒絕");
                conn.Kill(NetProto.ErrNameTaken);
                return;
            }

            conn.Guild = Clip(NetJson.Str(node, "guild"), NetLimits.MaxNameChars);
            conn.Level = Math.Max(0, NetJson.Int(node, "level"));
            conn.Look = NetAvatarLook.Decode(NetJson.Sub(node, "look"));

            conn.UserId = _nextUserId++;
            conn.SessionKey = Guid.NewGuid().ToString("N");
            conn.HelloDone = true;
            conn.LastRecvMs = now;

            _byUser[conn.UserId] = conn;
            _sessions[conn.SessionKey] = conn.UserId;

            conn.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.Welcome)
                .Int(NetProto.FieldRequest, rq)
                .Int("userId", conn.UserId)
                .Str("sessionKey", conn.SessionKey)
                .Str("role", NetProto.RoleControl)
                .Int("proto", NetProto.Version)
                .Int("capacity", NetLimits.RoomCapacity)
                .Int("maxSpectators", NetLimits.MaxSpectators)
                .Int("fileTtlHours", _opts.TtlHours)
                .Long("maxBlobBytes", NetLimits.DefaultMaxBlobBytes)
                .Int("serverNumber", 1)
                .Int("channel", 1)
                .Long("serverTimeMs", now));

            // client 的版本一起印。**版本不一樣就明講**:更新了一邊忘了另一邊時,症狀(某個功能沒反應)
            // 與「功能寫錯了」長得一樣,而這一行能立刻分辨。比對用尾巴那段(拿掉各自的產品名):
            // client「dance v1.5.0-dev-d41da」對 server「sdo-server v1.5.0-dev-d41da」。
            string clientBuild = Clip(NetJson.Str(node, "build").Trim(), 64);
            conn.Build = clientBuild;
            // 一個人上線只印這一行:名字、從哪來、哪個版本。IP 在這裡才有意義(對得上人)。
            Log("user " + conn.UserId + "「" + conn.Name + "」上線  " + conn.RemoteLabel
                + "  " + (clientBuild.Length > 0 ? clientBuild : "(未報版本)"));
            if (clientBuild.Length > 0 && !BuildVersionMatch.Same(clientBuild, BuildInfo.Version))
                Log("⚠️  版本不一致:client=" + clientBuild + " server=" + BuildInfo.Banner
                    + " —— 兩邊不是同一個 commit,新加的訊息型別在舊的那一邊會被當成不認識而忽略。");
        }

        private void OnPing(Connection conn, object node)
        {
            // 原樣把 t0 echo 回去 —— client 用它算 RTT。
            conn.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.Pong)
                .Num("t0", NetJson.Num(node, "t0"))
                .Long("serverTimeMs", NowMs()));
        }

        // ================= 房間生命週期 =================

        private void OnRoomList(Connection conn, int rq)
        {
            var arr = JArr.New();
            var list = _rooms.ListOpenRooms();
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i].State;
                string hostName = "";
                var hostSeat = s.SeatOf(s.HostUserId);
                if (hostSeat != null) hostName = hostSeat.Name;

                arr.Add(JObj.New()
                    .Int("code", s.Code)
                    // 門牌序號:大廳房卡上顯示的那個 3 位數(官方 %03d)。與 code 是兩件事 ——
                    // code 是 5 位數的「加入鑰匙」,seq 是「這是第幾間房」。不送 seq 的話大廳只能拿
                    // 列表位置湊,而那個數字會隨排序/刷新跳來跳去。
                    .Int("seq", s.Seq)
                    .Str("name", s.Name)
                    .Str("hostName", hostName)
                    .Str("status", NetState.ToWire(s.Status))
                    .Int("count", s.SeatedCount)
                    // 每個座位的性別(0=女 1=男),空位不列 —— 大廳房卡上那排愛心要照性別上色
                    // (官方:女=粉紅 FEMALE.AN、男=藍 MALE.AN、空位=灰 MAN.AN)。
                    // 只送「坐著的人」的性別、依座位順序,長度就等於 count。
                    .Put("genders", SeatGenders(s))
                    .Int("capacity", s.Capacity)
                    .Int("spectators", s.Spectators != null ? s.Spectators.Length : 0)
                    .Int("mode", s.Settings.GameMode)
                    .Str("songTitle", s.Song != null ? s.Song.Title : "")
                    // 譜面難度。大廳的「房間信息」那格官方寫成「歌名 (9級)」—— 沒有這個欄位就只能顯示歌名。
                    // 🔴 0 = 沒歌 or 譜面沒標難度,呼叫端要當「不知道」而不是「0 級」(不然整排房間都會寫 0級)。
                    .Int("songLevel", s.Song != null ? s.Song.Level : 0));
            }

            conn.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.RoomListResult)
                .Int(NetProto.FieldRequest, rq)
                .Put("rooms", arr));
        }

        /// <summary>
        /// 房間裡「坐著的人」的性別,依座位順序(0=女 1=男)。長度 == <c>SeatedCount</c>。
        ///
        /// 大廳房卡上那排愛心要照這個上色(官方:女=粉紅、男=藍、空位=灰)。只送坐著的、不送空位 ——
        /// 空位的顏色是固定的,client 自己補得出來,沒必要把六格都送。
        /// </summary>
        private static JArr SeatGenders(NetRoomSnapshot s)
        {
            var arr = JArr.New();
            if (s.Seats != null)
                for (int i = 0; i < s.Seats.Length; i++)
                {
                    var seat = s.Seats[i];
                    if (seat == null || !seat.IsTaken) continue;
                    arr.Add(seat.Look != null ? seat.Look.Gender : 0);
                }
            return arr;
        }

        /// <summary>
        /// 「現在誰在線上」——大廳玩家名單(全部 / 好友 / 家族 / 黑名單四個分頁)的唯一資料來源。
        ///
        /// 只回**事實**:誰在線上、叫什麼、幾等、屬於哪個家族、人在大廳還是某間房。
        /// 「這個人是不是我的好友」不在這裡判斷 —— 好友清單存在玩家**自己那台機器**上
        /// (server 沒有帳號持久化,見 client 的 <c>FriendList</c>),所以那是 client 拿這份名單去比對的事。
        ///
        /// 照 userId 排序:Dictionary 的列舉順序不保證,不排的話名單每刷一次順序就跳一遍。
        /// userId 遞增 == 上線先後,正好也是官方名單「先來的在上面」的排法。
        /// </summary>
        private void OnUserList(Connection conn, int rq)
        {
            var users = new List<Connection>();
            foreach (var kv in _byUser)
            {
                var c = kv.Value;
                if (c == null || c.IsClosed) continue;
                users.Add(c);
            }
            users.Sort((a, b) => a.UserId.CompareTo(b.UserId));

            var arr = JArr.New();
            for (int i = 0; i < users.Count; i++)
            {
                var c = users[i];
                var room = _rooms.RoomOf(c.UserId);
                arr.Add(JObj.New()
                    .Int("userId", c.UserId)
                    .Str("name", c.Name)
                    .Str("guild", c.Guild)
                    .Int("level", c.Level)
                    .Int("gender", c.Look != null ? c.Look.Gender : 0)
                    // 門牌(seq)而不是 code:名單只是給人看「他在幾號房」,不是給人拿去闖房的鑰匙。
                    // 🔴 不在房裡送 **-1** 不是 0 —— 門牌從 000 起算(見 RoomRegistry.NextFreeSeq),
                    //    0 是一間真的房,拿它當「在大廳」的哨兵會把 000 房的人標成在大廳。
                    .Int("roomSeq", room != null ? room.State.Seq : -1));
            }

            conn.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.UserListResult)
                .Int(NetProto.FieldRequest, rq)
                .Put("users", arr));
        }

        private void OnCreateRoom(Connection conn, object node, int rq)
        {
            string name = NetJson.Str(node, "name");

            NetRoom room;
            LeaveResult left;
            var op = _rooms.TryCreate(JoinUserOf(conn), name, out room, out left);

            AfterImplicitLeave(left, conn.UserId);

            if (op != NetRoomOp.Ok)
            {
                conn.Send(JObj.New()
                    .Str(NetProto.FieldType, NetProto.JoinResult)
                    .Int(NetProto.FieldRequest, rq)
                    .Str("result", op.ToJoinResult())
                    .Int("code", 0));
                return;
            }

            conn.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.JoinResult)
                .Int(NetProto.FieldRequest, rq)
                .Str("result", NetProto.JoinOk)
                .Int("code", room.Code));

            BroadcastRoomState(room);
            Log("房 " + room.Code + " 開房  user " + conn.UserId + "「" + conn.Name + "」");
        }

        private void OnJoinRoom(Connection conn, object node, int rq)
        {
            int code = NetJson.Int(node, "code");

            NetRoom room;
            int seat;
            LeaveResult left;
            var op = _rooms.TryJoin(code, JoinUserOf(conn), out room, out seat, out left);

            AfterImplicitLeave(left, conn.UserId);

            conn.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.JoinResult)
                .Int(NetProto.FieldRequest, rq)
                .Str("result", op.ToJoinResult())
                .Int("code", op == NetRoomOp.Ok ? code : 0));

            if (op == NetRoomOp.Ok)
            {
                BroadcastRoomState(room);
                SendMoveSnapshot(conn, room);   // 讓他立刻知道大家站在哪裡(見那個方法的註解)
                Log("房 " + code + " 加入  user " + conn.UserId + "「" + conn.Name + "」座位 " + seat);
            }
        }

        /// <summary>
        /// 把房裡每個人**目前**的位置一次送給某一條連線。
        ///
        /// 為什麼需要它:位置流只在有人動的時候推,而站著不動的人永不回報 ——
        /// 所以剛進房的人若不補這一發,他會看到所有人站在「座位算出來的 fallback 點」上,
        /// 而那些點與別人畫面上的位置完全不同(症狀:同一間房每台看到的站位都不一樣)。
        /// </summary>
        private void SendMoveSnapshot(Connection conn, NetRoom room)
        {
            if (room == null || conn == null) return;
            Dictionary<int, MoveSample> byUser;
            if (!_moves.TryGetValue(room.Code, out byUser) || byUser.Count == 0) return;

            var arr = JArr.New();
            foreach (var mv in byUser)
                if (mv.Key != conn.UserId) arr.Add(mv.Value.Encode(mv.Key));

            conn.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.Moves)
                .Int("roomCode", room.Code)
                .Int("roomRev", room.State.Rev)
                .Put("m", arr));
        }

        private void OnLeaveRoom(Connection conn)
        {
            LeaveRoomFor(conn.UserId);
        }

        /// <summary>離房的共同路徑(主動離開 / 斷線 / ping 逾時都走這裡)。</summary>
        private void LeaveRoomFor(int userId)
        {
            var left = _rooms.Leave(userId);
            if (left.Room == null) return;
            DropRoomMoves(left.Room.Code);

            if (left.RoomClosed)
            {
                // 關房時理論上已經沒人了(關房條件是「一個人都不剩」),
                // 但若狀態不同步還有人在,要通知他們。
                var evicted = left.EvictedUserIds;
                for (int i = 0; i < evicted.Length; i++) SendKicked(evicted[i], NetProto.KickedRoomClosed);
                DropRoomScratch(left.Room.Code);
                Log("房 " + left.Room.Code + " 關閉(沒人了)");
                return;
            }

            if (left.NewHostUserId != 0)
                Log("房 " + left.Room.Code + " 房主換成 user " + left.NewHostUserId);

            BroadcastRoomState(left.Room);
        }

        /// <summary>「已在別房 → 先隱式離房」之後要處理的廣播。</summary>
        private void AfterImplicitLeave(LeaveResult left, int userId)
        {
            if (left.Room == null) return;
            DropRoomMoves(left.Room.Code);
            if (left.RoomClosed)
            {
                var evicted = left.EvictedUserIds;
                for (int i = 0; i < evicted.Length; i++) SendKicked(evicted[i], NetProto.KickedRoomClosed);
                DropRoomScratch(left.Room.Code);
                return;
            }
            BroadcastRoomState(left.Room);
        }

        // ================= 房間設定(host only) =================

        private void OnSetRoomName(Connection conn, object node, int rq)
        {
            var room = _rooms.RoomOf(conn.UserId);
            if (room == null) { SendError(conn, rq, NetProto.ErrNotInRoom); return; }

            var op = room.SetRoomName(conn.UserId, NetJson.Str(node, "name"));
            if (op != NetRoomOp.Ok) { SendOpError(conn, rq, op); return; }
            BroadcastRoomState(room);
        }

        private void OnSetSong(Connection conn, object node, int rq)
        {
            var room = _rooms.RoomOf(conn.UserId);
            if (room == null) { SendError(conn, rq, NetProto.ErrNotInRoom); return; }

            NetSongRef song = null;
            var songNode = NetJson.Sub(node, "song");
            if (songNode != null && !NetSongRef.TryDecode(songNode, out song))
            {
                // 歌曲參照是嚴格驗證的(packId 格式、譜面路徑安全性)—— 壞的就拒絕整個請求。
                SendError(conn, rq, NetProto.ErrBadState, "bad song ref");
                return;
            }

            var op = room.SetSong(conn.UserId, song);
            if (op != NetRoomOp.Ok)
            {
                SendOpError(conn, rq, op, "song=" + (song != null ? song.Title + " packId=" + song.PackId : "(清空)"));
                return;
            }
            // 選歌是房間狀態的重大變更(保留 ready、重設 availability = R9),而且「這間房有沒有歌」
            // 是準備/開始的前提 —— 沒印出來的話「按開始沒反應」完全查不到(踩過)。
            Log("房 " + room.Code + " 換歌:" + (song != null ? song.Title : "(清空)"));
            BroadcastRoomState(room);
        }

        private void OnSetRoomSettings(Connection conn, object node, int rq)
        {
            NetRoom room;
            int[] kickedSpecs;
            var op = _rooms.SetRoomSettings(conn.UserId, NetJson.Sub(node, "settings"), out room, out kickedSpecs);
            if (op != NetRoomOp.Ok) { SendOpError(conn, rq, op); return; }

            if (kickedSpecs.Length != 0) DropRoomMoves(room.Code);
            for (int i = 0; i < kickedSpecs.Length; i++)
            {
                SendKicked(kickedSpecs[i], NetProto.KickedRoomClosed);
            }
            BroadcastRoomState(room);
        }

        private void OnAssignTeams(Connection conn, object node, int rq)
        {
            var room = _rooms.RoomOf(conn.UserId);
            if (room == null) { SendError(conn, rq, NetProto.ErrNotInRoom); return; }

            TeamLayout layout;
            if (!TeamLayoutRules.TryParseLayout(NetJson.Str(node, "layout"), out layout) || layout == TeamLayout.None)
            {
                SendError(conn, rq, NetProto.ErrBadTeams, "bad layout");
                return;
            }

            var op = room.AssignTeams(conn.UserId, layout);
            if (op != NetRoomOp.Ok) { SendOpError(conn, rq, op, "layout=" + layout); return; }
            BroadcastRoomState(room);
        }

        // ================= 個人操作 =================

        private void OnSetOwnTeam(Connection conn, object node, int rq)
        {
            var room = _rooms.RoomOf(conn.UserId);
            if (room == null) { SendError(conn, rq, NetProto.ErrNotInRoom); return; }

            int team = NetJson.Int(node, "team", (int)TeamTag.Free);
            var op = room.SetOwnTeam(conn.UserId, team);
            if (op != NetRoomOp.Ok) { SendOpError(conn, rq, op, "team=" + team); return; }
            BroadcastRoomState(room);
        }

        private void OnSetReady(Connection conn, object node, int rq)
        {
            var room = _rooms.RoomOf(conn.UserId);
            if (room == null) { SendError(conn, rq, NetProto.ErrNotInRoom); return; }

            bool ready = NetJson.Bool(node, "ready");
            var op = room.SetReady(conn.UserId, ready);
            if (op != NetRoomOp.Ok) { SendOpError(conn, rq, op, "ready=" + ready); return; }
            BroadcastRoomState(room);
        }

        /// <summary>
        /// 玩家回報自己的外觀(性別 / 體型 / 穿戴部件)。
        ///
        /// 不回 error:不在房裡就只更新這條連線上記著的那份 —— 那是正常的競態(還沒進房就先報了、
        /// 或剛離房送出的最後一筆),而且進房時會拿 <c>conn.Look</c> 去填座位,所以不會漏。
        /// </summary>
        private void OnSetLook(Connection conn, object node)
        {
            var look = NetAvatarLook.Decode(NetJson.Sub(node, "look"));
            if (look == null) return;
            conn.Look = look;

            var room = _rooms.RoomOf(conn.UserId);
            if (room == null) return;
            if (room.SetLook(conn.UserId, look) == NetRoomOp.Ok) BroadcastRoomState(room);
        }

        /// <summary>
        /// 玩家回報自己的身分(名字 / playerId / 家族 / 等級)。
        ///
        /// 為什麼握手之後還會變:**選性別 == 選帳號** —— 女角與男角是兩個 profile,各有自己的名字,
        /// 而握手是開機時就做完的。沒有這條路徑,換成男角進房的人在別人畫面上會是
        /// 「男角的模型 + 女角的名字」(setLook 只帶外觀)。
        ///
        /// 🔴 token 綁定優先:綁了名字/playerId 的連線改不動那兩項(見 <see cref="Connection.NameLocked"/>)。
        /// 不回 error(理由同 <see cref="OnSetLook"/>:不在房裡只更新連線上那份,進房時會拿去填座位)。
        /// </summary>
        private void OnSetIdentity(Connection conn, object node)
        {
            if (!conn.NameLocked)
            {
                // 名字空白 → 保留原本的。SanitizeName 會把空的變成「玩家」,但那是握手時
                // 「這個 client 根本沒報名字」該有的行為,不該讓一筆壞掉的更新把好名字洗掉。
                string raw = (NetJson.Str(node, "name") ?? "").Trim();
                if (raw.Length > 0)
                {
                    string want = SanitizeName(raw);
                    // 🔴 撞到別人的名字就不改(保留原本的)。少了這一段,hello 的同名檢查等於白做 ——
                    // 用另一個名字進來、握手後再改成別人的名字,結果一樣是兩個同名的人同時在線。
                    // 找到的是自己時要放行:換性別(男女各一個 profile)本來就會重送同一個名字。
                    var holder = ControlByName(want);
                    if (holder == null || holder == conn) conn.Name = want;
                    else
                        Log("✗ user " + conn.UserId + " 想改名成「" + want + "」,但 user "
                            + holder.UserId + " 正在用這個名字 → 保留原名「" + conn.Name + "」");
                }
            }
            if (!conn.PlayerIdLocked)
            {
                string pid = Clip(NetJson.Str(node, "playerId"), 32);
                if (pid.Length > 0) conn.PlayerId = pid;
            }
            conn.Guild = Clip(NetJson.Str(node, "guild"), NetLimits.MaxNameChars);
            conn.Level = Math.Max(0, NetJson.Int(node, "level"));

            var room = _rooms.RoomOf(conn.UserId);
            if (room == null) return;
            if (room.SetIdentity(conn.UserId, conn.Name, conn.Guild, conn.Level) == NetRoomOp.Ok)
                BroadcastRoomState(room);
        }

        private void OnSetAvailability(Connection conn, object node, long now)
        {
            var room = _rooms.RoomOf(conn.UserId);
            if (room == null) return;   // 不在房裡的可用性回報靜默忽略(正常的競態)

            Availability avail;
            if (!NetState.TryParseAvailability(NetJson.Str(node, "state"), out avail)) return;

            // 下載進度回報要節流。client 自己也會節流,但不能假設對方的 client 沒被改過。
            if (avail == Availability.Downloading && !conn.Rate.AllowAvailProgress(now)) return;

            var op = room.SetAvailability(conn.UserId, NetJson.Str(node, "packId"),
                                          avail, (float)NetJson.Num(node, "progress"));
            if (op == NetRoomOp.Ok) BroadcastRoomState(room);
        }

        // ================= 座位管理(host only) =================

        private void OnKickUser(Connection conn, object node, int rq)
        {
            int target = NetJson.Int(node, "userId");

            NetRoom room;
            LeaveResult left;
            var op = _rooms.KickUser(conn.UserId, target, out room, out left);
            if (op != NetRoomOp.Ok) { SendOpError(conn, rq, op, "目標 user " + target); return; }

            DropRoomMoves(room.Code);
            SendKicked(target, NetProto.KickedByHost);
            if (left.RoomClosed) { DropRoomScratch(room.Code); return; }
            BroadcastRoomState(room);
            Log("房 " + room.Code + " 踢出  user " + target);
        }

        private void OnSetSeatClosed(Connection conn, object node, int rq)
        {
            int seat = NetJson.Int(node, "seat", -1);
            bool closed = NetJson.Bool(node, "closed", true);

            NetRoom room;
            int kicked;
            var op = _rooms.SetSeatClosed(conn.UserId, seat, closed, out room, out kicked);
            if (op != NetRoomOp.Ok) { SendOpError(conn, rq, op, "座位" + seat + " closed=" + closed); return; }

            // 關閉有人的座位 → 那個人先被踢出去(需求 12)。
            if (kicked != 0)
            {
                DropRoomMoves(room.Code);
                SendKicked(kicked, NetProto.KickedSeatClosed);
            }
            BroadcastRoomState(room);
        }

        private void OnTransferHost(Connection conn, object node, int rq)
        {
            var room = _rooms.RoomOf(conn.UserId);
            if (room == null) { SendError(conn, rq, NetProto.ErrNotInRoom); return; }

            int newHost = NetJson.Int(node, "userId");
            var op = room.TransferHost(conn.UserId, newHost);
            if (op != NetRoomOp.Ok) { SendOpError(conn, rq, op, "目標 user " + newHost); return; }
            BroadcastRoomState(room);
        }

        // ================= 旁觀 =================

        private void OnSpectate(Connection conn, object node, int rq)
        {
            // 帶 code = 從房間列表以旁觀身分加入;不帶 = 在目前房間內切成旁觀。
            int code = NetJson.Int(node, "code");
            if (code == 0)
            {
                var cur = _rooms.RoomOf(conn.UserId);
                if (cur == null) { SendError(conn, rq, NetProto.ErrNotInRoom); return; }
                code = cur.Code;
            }

            NetRoom room;
            LeaveResult left;
            var op = _rooms.TrySpectate(code, JoinUserOf(conn), out room, out left);
            AfterImplicitLeave(left, conn.UserId);

            if (op != NetRoomOp.Ok) { SendOpError(conn, rq, op, "房 " + code); return; }
            DropRoomMoves(room.Code);
            // 座位有 log、旁觀沒有 → 實機驗證時「他到底進去了沒」只能用猜的。補上。
            Log("房 " + room.Code + " 旁觀  user " + conn.UserId + "「" + conn.Name
                + "」(座位 " + room.State.SeatedCount + " 人)");
            BroadcastRoomState(room);
        }

        private void OnStopSpectate(Connection conn, int rq)
        {
            NetRoom room;
            int seat;
            var op = _rooms.TryUnspectate(JoinUserOf(conn), out room, out seat);
            if (op != NetRoomOp.Ok) { SendOpError(conn, rq, op); return; }
            DropRoomMoves(room.Code);
            BroadcastRoomState(room);
        }

        // ================= 開場 =================

        private void OnRequestStart(Connection conn, object node, int rq, long now)
        {
            var room = _rooms.RoomOf(conn.UserId);
            if (room == null) { SendError(conn, rq, NetProto.ErrNotInRoom); return; }

            NetResolvedRound resolved;
            if (!NetResolvedRound.TryDecode(NetJson.Sub(node, "resolved"), out resolved))
            {
                // 隨機值的範圍驗證失敗 —— server 是最終權威,不照著跑。
                SendError(conn, rq, NetProto.ErrBadState, "bad resolved round");
                return;
            }

            bool force = NetJson.Bool(node, "force");

            NetMatchInfo match;
            var op = room.RequestStart(conn.UserId, force, resolved, now, out match);
            if (op != NetRoomOp.Ok) { SendOpError(conn, rq, op, "force=" + force); return; }
            _comboMilestones.Remove(room.Code);
            _liveLeaders[room.Code] = new LiveLeaderTracker(match.Participants);

            SendMatchStarting(room, match);
            BroadcastRoomState(room);
            Log("房 " + room.Code + " 開始第 " + match.MatchId + " 場(" +
                match.ParticipantUserIds.Length + " 人" +
                (match.SpectatorUserIds.Length > 0 ? " + " + match.SpectatorUserIds.Length + " 旁觀" : "") + ")");
        }

        private void SendMatchStarting(NetRoom room, NetMatchInfo match)
        {
            var participants = JArr.New();
            for (int i = 0; i < match.Participants.Length; i++)
            {
                var player = match.Participants[i];
                participants.Add(JObj.New()
                    .Int("userId", player.UserId)
                    .Int("seat", player.Seat)
                    .Str("name", player.Name)
                    .Int("level", player.Level)
                    .Int("team", player.Team)
                    .Put("look", player.Look != null ? player.Look.Encode() : null));
            }

            var spectatorNames = JArr.New();
            var specs = room.State.Spectators;
            if (specs != null)
                for (int i = 0; i < specs.Length; i++) spectatorNames.Add(specs[i].Name);

            var msg = JObj.New()
                .Str(NetProto.FieldType, NetProto.MatchStarting)
                .Long("matchId", match.MatchId)
                .Long("startEpochMs", match.StartEpochMs)
                .Int("loadTimeoutMs", match.LoadTimeoutMs)
                .Put("participants", participants)
                .Put("spectatorNames", spectatorNames)
                .Put("resolved", match.Resolved.Encode())
                .Put("song", match.Song != null ? match.Song.Encode() : null)
                .Put("settings", room.State.Settings.Encode())
                .Utf8();

            // 收件人 = 參與者 + 有歌的旁觀者(缺歌的旁觀者留在房間看頭貼)。
            for (int i = 0; i < match.ParticipantUserIds.Length; i++)
            {
                var c = ControlOf(match.ParticipantUserIds[i]);
                if (c != null) c.SendPreEncoded(msg);
            }
            for (int i = 0; i < match.SpectatorUserIds.Length; i++)
            {
                var c = ControlOf(match.SpectatorUserIds[i]);
                if (c != null) c.SendPreEncoded(msg);
            }
        }

        private void OnSetPlayState(Connection conn, object node, int rq)
        {
            var room = _rooms.RoomOf(conn.UserId);
            if (room == null) { SendError(conn, rq, NetProto.ErrNotInRoom); return; }

            long matchId = NetJson.Long(node, "matchId");

            PlayState state;
            if (!NetState.TryParsePlayState(NetJson.Str(node, "state"), out state))
            {
                SendError(conn, rq, NetProto.ErrBadState, "unknown state",
                          "state=" + NetJson.Str(node, "state") + " matchId=" + matchId);
                return;
            }
            if (!NetState.IsClientSettable(state))
            {
                // 🔴 安全邊界:server 保留狀態不准 client 自稱(否則能繞過載入同步)。
                SendError(conn, rq, NetProto.ErrBadState, "server-reserved state",
                          "state=" + state + " matchId=" + matchId);
                return;
            }

            var op = room.SetPlayState(conn.UserId, state, matchId);
            // 🔴 送上來的 matchId 一定要進 log:這條路徑最常見的拒絕就是「這一場已經被 server 收掉了」
            // (client 拿著上一場的 matchId 送),而那唯一的證據就是「它送的號碼」與
            // DescribeConn 印的「server 現在認的那一場」對不起來。
            if (op != NetRoomOp.Ok) { SendOpError(conn, rq, op, "state=" + state + " matchId=" + matchId); return; }
            BroadcastRoomState(room);
        }

        // ================= 遊玩中的分數流 =================

        // ================= 房間裡的走動 =================

        /// <summary>
        /// 玩家在房間裡走動的位置回報。與分數流(<see cref="OnGameplayFrame"/>)是同一個形狀:
        /// 只留每個人的最新一筆,固定頻率彙整推出去。
        ///
        /// **server 不驗證位置** —— 可走區的遮罩(MASK.MSK)只有 client 有,而房間裡走到牆裡面
        /// 不影響任何人的遊玩結果。這是刻意的取捨(離線重製沒有防作弊需求),寫在這裡免得
        /// 以後有人以為是漏掉了。
        /// </summary>
        private void OnRoomMove(Connection conn, object node, long now)
        {
            // rate limit 已經在 Dispatch 的入口扣過了(move 有自己的 bucket),這裡不要再扣一次。
            var room = _rooms.RoomOf(conn.UserId);
            if (room == null) return;

            if (NetJson.Int(node, "roomCode") != room.Code
                || NetJson.Int(node, "roomRev", -1) != room.State.Rev) return;

            // 座位 ↔ 旁觀切換後，舊畫面可能還有一筆 move 排在 socket/actor queue 裡。
            // 用 server 目前認定的 slot 擋掉它，否則 DropRoomMoves 清完後它又會把舊座標塞回來。
            int seat = room.State.SeatIndexOf(conn.UserId);
            int spectator = room.State.SpectatorIndexOf(conn.UserId);
            int expectedSlot = seat >= 0 ? seat
                : spectator >= 0 ? 1000 + spectator
                : -1;
            if (expectedSlot < 0 || NetJson.Int(node, "slot", -1) != expectedSlot) return;

            Dictionary<int, MoveSample> byUser;
            if (!_moves.TryGetValue(room.Code, out byUser))
            {
                byUser = new Dictionary<int, MoveSample>();
                _moves[room.Code] = byUser;
            }
            byUser[conn.UserId] = MoveSample.Decode(node);
            _movesDirty.Add(room.Code);
        }

        /// <summary>固定頻率把每個房間彙整好的 moves 推出去(N×ServerMoveHz,不是 N² 轉發風暴)。</summary>
        private void PushPendingMoves()
        {
            if (_moves.Count == 0) return;

            List<int> emptied = null;
            foreach (var kv in _moves)
            {
                var byUser = kv.Value;
                if (byUser.Count == 0) continue;

                var room = _rooms.Find(kv.Key);
                if (room == null)
                {
                    if (emptied == null) emptied = new List<int>();
                    emptied.Add(kv.Key);
                    continue;
                }

                // 🔴 只推「這一輪有人動過」的房間,但**不清空表** —— 表裡留著每個人的最新位置,
                // 因為後進房的人要靠它知道大家站在哪裡(清掉的話他只會看到所有人站在座位 fallback 點)。
                if (!_movesDirty.Contains(kv.Key)) continue;

                var arr = JArr.New();
                foreach (var mv in byUser) arr.Add(mv.Value.Encode(mv.Key));

                var bytes = JObj.New()
                    .Str(NetProto.FieldType, NetProto.Moves)
                    .Int("roomCode", room.Code)
                    .Int("roomRev", room.State.Rev)
                    .Put("m", arr)
                    .Utf8();

                // lossy:佇列滿了寧可丟掉這一輪,也不要斷線或拖慢 actor loop(位置下一輪就補上了)。
                ForEachInRoom(room, c => c.SendPreEncoded(bytes, critical: false));
            }
            _movesDirty.Clear();

            if (emptied != null)
                for (int i = 0; i < emptied.Count; i++) _moves.Remove(emptied[i]);
        }

        /// <summary>房間裡某個人的最新位置。<c>W</c> = 正在走(收端用它決定播走路還是待機 clip)。</summary>
        private struct MoveSample
        {
            public float X, Z, Facing;
            public bool W;

            public static MoveSample Decode(object node)
            {
                var m = new MoveSample();
                m.X = (float)NetJson.Num(node, "x");
                m.Z = (float)NetJson.Num(node, "z");
                m.Facing = (float)NetJson.Num(node, "f");
                m.W = NetJson.Bool(node, "w");
                return m;
            }

            public JObj Encode(int userId)
                => JObj.New()
                    .Int("userId", userId)
                    .Num("x", X)
                    .Num("z", Z)
                    .Num("f", Facing)
                    .Bool("w", W);
        }

        /// <summary>
        /// 房間沒了 → 清掉它的暫存流(分數 + 位置)。
        ///
        /// 為什麼要抽出來:`_pendingFrames.Remove` 原本散在 5 個地方,加了 `_moves` 之後
        /// 「改了一處忘了另一處」就會留下幽靈 —— 舊 userId 的殘留位置會把新生的角色瞬移到上一場的位置。
        /// </summary>
        private void DropRoomScratch(int roomCode)
        {
            _pendingFrames.Remove(roomCode);
            _latestFrames.Remove(roomCode);
            _finalFrames.Remove(roomCode);
            _comboMilestones.Remove(roomCode);
            _liveLeaders.Remove(roomCode);
            _moves.Remove(roomCode);
            _movesDirty.Remove(roomCode);
        }

        private void DropRoomMoves(int roomCode)
        {
            _moves.Remove(roomCode);
            _movesDirty.Remove(roomCode);
        }

        private void OnGameplayFrame(Connection conn, object node)
        {
            var room = _rooms.RoomOf(conn.UserId);
            if (room == null) return;
            if (room.Match == null) return;
            if (NetJson.Long(node, "matchId") != room.Match.MatchId) return;   // 上一場的遲到訊息
            if (room.State.Status != RoomStatus.Playing) return;

            bool participant = false;
            var ids = room.Match.ParticipantUserIds;
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] == conn.UserId) { participant = true; break; }
            if (!participant) return;

            var seat = room.State.SeatOf(conn.UserId);
            if (seat == null || seat.PlayState != PlayState.Playing) return;

            Dictionary<int, FrameSample> finalized;
            if (_finalFrames.TryGetValue(room.Code, out finalized)
                && finalized.ContainsKey(conn.UserId))
                return;   // playFinished 已拍板；遲到的 lossy frame 不可覆蓋 final

            Dictionary<int, FrameSample> byUser;
            if (!_pendingFrames.TryGetValue(room.Code, out byUser))
            {
                byUser = new Dictionary<int, FrameSample>();
                _pendingFrames[room.Code] = byUser;
            }

            // 只留最新一筆 —— 這是狀態快照而不是事件流,舊的沒有價值。
            var sample = FrameSample.Decode(node);
            byUser[conn.UserId] = sample;

            Dictionary<int, FrameSample> latestByUser;
            if (!_latestFrames.TryGetValue(room.Code, out latestByUser))
            {
                latestByUser = new Dictionary<int, FrameSample>();
                _latestFrames[room.Code] = latestByUser;
            }
            latestByUser[conn.UserId] = sample;
            RecordLiveScore(room, conn.UserId, sample.TMs, sample.Score);
        }
        /// <summary>
        /// 餵一筆分數給權威 leader 的追蹤器。**歌曲時間一定要一起帶** —— 它是「同一時刻取樣」
        /// 的依據,少了它就退回「比最後收到的分數」= 拿不同時刻的分數比大小(見 <see cref="LiveLeaderTracker"/>)。
        /// </summary>
        private void RecordLiveScore(NetRoom room, int userId, double tMs, long score)
        {
            if (room.State.Status != RoomStatus.Playing || room.Match == null) return;

            LiveLeaderTracker tracker;
            if (!_liveLeaders.TryGetValue(room.Code, out tracker)) return;
            tracker.Record(room.Match.ParticipantUserIds, userId, tMs, score);
        }
        /// <summary>
        /// 固定頻率把每個房間彙整好的 frames 推出去。
        /// N 人的下行是 N×<see cref="NetLimits.ServerFrameHz"/> 訊息/秒,而不是 N² 的轉發風暴。
        /// </summary>

        /// <summary>
        /// Reliably forwards a one-shot combo effect. It cannot be inferred from 5 Hz
        /// snapshots because a receiver may see combo jump directly from 49 to 53.
        /// </summary>
        private void OnComboMilestone(Connection conn, object node)
        {
            var room = _rooms.RoomOf(conn.UserId);
            if (room == null || room.Match == null || room.State.Status != RoomStatus.Playing) return;

            long matchId = NetJson.Long(node, "matchId");
            int combo = NetJson.Int(node, "combo");
            if (matchId != room.Match.MatchId || combo < 50 || combo > 1000000 || combo % 50 != 0) return;

            bool participant = false;
            var ids = room.Match.ParticipantUserIds;
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] != conn.UserId) continue;
                participant = true;
                break;
            }
            if (!participant) return;

            var seat = room.State.SeatOf(conn.UserId);
            if (seat == null || seat.PlayState != PlayState.Playing) return;
            Dictionary<int, int> lastByUser;
            if (!_comboMilestones.TryGetValue(room.Code, out lastByUser))
            {
                lastByUser = new Dictionary<int, int>();
                _comboMilestones[room.Code] = lastByUser;
            }

            int last;
            if (lastByUser.TryGetValue(conn.UserId, out last) && combo <= last) return;
            lastByUser[conn.UserId] = combo;
            var bytes = JObj.New()
                .Str(NetProto.FieldType, NetProto.ComboMilestone)
                .Long("matchId", matchId)
                .Int("userId", conn.UserId)
                .Int("combo", combo)
                .Utf8();

            // The sender already played it locally; avoid replaying the same effect there.
            ForEachInRoom(room, c =>
            {
                if (c.UserId != conn.UserId) c.SendPreEncoded(bytes);
            });
        }

        private void PushPendingFrames()
        {
            if (_pendingFrames.Count == 0) return;

            List<int> emptied = null;
            foreach (var kv in _pendingFrames)
            {
                var byUser = kv.Value;
                if (byUser.Count == 0) continue;

                var room = _rooms.Find(kv.Key);
                if (room == null || room.Match == null)
                {
                    if (emptied == null) emptied = new List<int>();
                    emptied.Add(kv.Key);
                    continue;
                }

                var arr = JArr.New();
                foreach (var f in byUser) arr.Add(f.Value.Encode(f.Key));

                LiveLeaderTracker tracker;
                int leaderUserId = _liveLeaders.TryGetValue(room.Code, out tracker)
                    ? tracker.Resolve(room.Match.ParticipantUserIds)
                    : 0;

                var bytes = JObj.New()
                    .Str(NetProto.FieldType, NetProto.Frames)
                    .Long("matchId", room.Match.MatchId)
                    .Int("leaderUserId", leaderUserId)
                    .Put("f", arr)
                    .Utf8();

                // 用 lossy 送:佇列滿了寧可丟掉這一輪,也不要斷線或拖慢 actor loop。
                ForEachInRoom(room, c => c.SendPreEncoded(bytes, critical: false));

                byUser.Clear();
            }

            if (emptied != null)
                for (int i = 0; i < emptied.Count; i++) _pendingFrames.Remove(emptied[i]);
        }

        private void OnPlayFinished(Connection conn, object node)
        {
            var room = _rooms.RoomOf(conn.UserId);
            if (room == null || room.Match == null) return;
            if (NetJson.Long(node, "matchId") != room.Match.MatchId) return;

            // 🔴 這兩道守衛是**安全邊界**,不要放寬:載入階段(還沒開跳)送上來的 final 一律不收,
            //    否則改過的 client 可以在開場前先塞一個好看的分數。載入階段按 Esc 中離的人不靠這一則
            //    退場 —— 他會另外送 setPlayState{idle}(見 NetRoom.AbortDuringLoad),那一則才是退場。
            if (room.State.Status != RoomStatus.Playing) return;
            var seat = room.State.SeatOf(conn.UserId);
            if (seat == null || seat.PlayState != PlayState.Playing) return;

            bool participant = false;
            var ids = room.Match.ParticipantUserIds;
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] == conn.UserId) { participant = true; break; }
            if (!participant) return;

            var final = FrameSample.Decode(node);

            // 最終成績必須活到整場 resultsReady,不能跟 200ms pending frames 一起清空。
            Dictionary<int, FrameSample> finalByUser;
            if (!_finalFrames.TryGetValue(room.Code, out finalByUser))
            {
                finalByUser = new Dictionary<int, FrameSample>();
                _finalFrames[room.Code] = finalByUser;
            }
            if (finalByUser.ContainsKey(conn.UserId)) return;
            finalByUser[conn.UserId] = final;

            Dictionary<int, FrameSample> latestByUser;
            if (!_latestFrames.TryGetValue(room.Code, out latestByUser))
            {
                latestByUser = new Dictionary<int, FrameSample>();
                _latestFrames[room.Code] = latestByUser;
            }
            latestByUser[conn.UserId] = final;

            // final 同時也是最新的一筆 live frame,照舊在下一輪推給其他玩家。
            Dictionary<int, FrameSample> pendingByUser;
            if (!_pendingFrames.TryGetValue(room.Code, out pendingByUser))
            {
                pendingByUser = new Dictionary<int, FrameSample>();
                _pendingFrames[room.Code] = pendingByUser;
            }
            pendingByUser[conn.UserId] = final;
            // playFinished 沒有帶 tMs(它是「這一場結束」而不是某個時刻的快照),所以這裡的 0 會走
            // Record 的「時間沒有前進 → 只更新最後一筆的分數」那條路 —— 正是想要的語義:最終成績
            // 取代這個人最後一刻的分數,而他的時間軸停在那裡不動。
            RecordLiveScore(room, conn.UserId, final.TMs, final.Score);

            var op = room.SetPlayState(conn.UserId, PlayState.Finished, room.Match.MatchId);
            if (op == NetRoomOp.Ok) BroadcastRoomState(room);
        }

        // ================= 房間狀態機的結果 =================

        private void ApplyRoomTick(RoomTickResult r, long now)
        {
            var room = r.Room;
            var tick = r.Tick;

            // 載入太久被逐出本場的人:unicast 告訴他們原因(其他人不需要知道)。
            var timedOut = tick.LoadTimedOutUserIds;
            for (int i = 0; i < timedOut.Length; i++)
            {
                SendTo(timedOut[i], JObj.New()
                    .Str(NetProto.FieldType, NetProto.GameplayAborted)
                    .Long("matchId", tick.MatchId)
                    .Str("reason", NetProto.AbortLoadTookTooLong));
                Log("房 " + room.Code + " 載入逾時,逐出本場:user " + timedOut[i]);
            }

            if (tick.MatchAborted)
            {
                var bytes = JObj.New()
                    .Str(NetProto.FieldType, NetProto.GameplayAborted)
                    .Long("matchId", tick.MatchId)
                    .Str("reason", NetProto.AbortNoParticipants)
                    .Utf8();
                ForEachInRoom(room, c => c.SendPreEncoded(bytes));
                DropRoomScratch(room.Code);
                Log("房 " + room.Code + " 本場取消(沒有人載入成功)");
            }

            if (tick.GameplayStarted)
            {
                var bytes = JObj.New()
                    .Str(NetProto.FieldType, NetProto.GameplayStarted)
                    .Long("matchId", tick.MatchId)
                    .Long("serverStartMs", now)
                    .Utf8();
                ForEachInRoom(room, c => c.SendPreEncoded(bytes));
                // 「開始第 N 場」上面已經印過(載入前);這裡是載入完真的開跳,對營運沒有新資訊 → verbose。
                LogVerbose("房 " + room.Code + ":第 " + tick.MatchId + " 場開始跳了");
            }

            if (tick.ResultsReady)
            {
                SendResultsReady(room, tick.MatchId);
                // 🔴 這裡**不**呼叫 room.ClearResults()。以前呼叫的後果是:它就在下面那次
                //    BroadcastRoomState 之前把所有人打回 idle,於是那份帶 playState=results 的快照
                //    從來沒被送出去過一次 —— 留在房間的人看到 PLAYING 在曲末就消失,而那些人其實
                //    還盯著結算面板(最多 30 秒)。清除改由「人回來了」(setPlayState{idle})、
                //    寬限期逾時、或房主開下一局來觸發,見 NetRoom.ClearResults。
                DropRoomScratch(room.Code);
            }

            if (tick.Changed) BroadcastRoomState(room);
        }

        private void SendResultsReady(NetRoom room, long matchId)
        {
            Dictionary<int, FrameSample> finalByUser;
            Dictionary<int, FrameSample> latestByUser;
            _finalFrames.TryGetValue(room.Code, out finalByUser);
            _latestFrames.TryGetValue(room.Code, out latestByUser);

            var rows = JArr.New();
            var players = new List<NetMatchPlayerSnapshot>(room.Match.Participants);
            players.Sort((a, b) =>
            {
                FrameSample af = ResultFrame(finalByUser, latestByUser, a.UserId);
                FrameSample bf = ResultFrame(finalByUser, latestByUser, b.UserId);
                return ResultRowOrder.Compare(
                    af.Score, a.Seat, a.UserId,
                    bf.Score, b.Seat, b.UserId);
            });
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];

                // 完整 final 優先；斷線沒送 playFinished 時退回本場最後一筆 frame。
                FrameSample f = ResultFrame(finalByUser, latestByUser, player.UserId);

                rows.Add(JObj.New()
                    .Int("userId", player.UserId)
                    .Int("seat", player.Seat)
                    .Str("name", player.Name)
                    .Int("level", player.Level)
                    .Int("team", player.Team)
                    .Put("look", player.Look != null ? player.Look.Encode() : null)
                    .Long("score", f.Score)
                    .Int("perfect", f.P)
                    .Int("cool", f.C)
                    .Int("bad", f.B)
                    .Int("miss", f.M)
                    .Int("maxCombo", f.MaxCombo)
                    .Bool("disconnected", ControlOf(player.UserId) == null));
            }

            var bytes = JObj.New()
                .Str(NetProto.FieldType, NetProto.ResultsReady)
                .Long("matchId", matchId)
                .Put("rows", rows)
                .Utf8();
            ForEachInRoom(room, c => c.SendPreEncoded(bytes));
            Log("房 " + room.Code + " 第 " + matchId + " 場結算");
        }

        private static FrameSample ResultFrame(
            Dictionary<int, FrameSample> finalByUser,
            Dictionary<int, FrameSample> latestByUser,
            int userId)
        {
            FrameSample frame;
            if (finalByUser != null && finalByUser.TryGetValue(userId, out frame)) return frame;
            if (latestByUser != null && latestByUser.TryGetValue(userId, out frame)) return frame;
            return default(FrameSample);
        }

        // ================= 聊天 =================

        private void OnChatSay(Connection conn, object node, long now)
        {
            if (!conn.Rate.AllowChat(now)) return;   // 洗頻:靜默丟掉,不斷線

            string text = Clip(NetJson.Str(node, "text"), NetLimits.MaxChatChars);
            int expressionId = NetJson.Int(node, "expressionId");
            if (string.IsNullOrEmpty(text) && expressionId == 0) return;

            // 🔴 **不在房間裡 = 在大廳**,那就廣播給大廳裡的所有人 —— 以前這裡是
            //    `if (room == null) return;`(註解寫「大廳聊天在後續階段」),結果在大廳打字送出去之後
            //    server 直接把它丟掉,連自己那一行都不會回來 → 使用者回報「我打字都沒辦法送出」。
            //    大廳沒有「房間」這個容器,所以收件人是「所有線上、且同樣不在任何房間裡的連線」。
            var room = _rooms.RoomOf(conn.UserId);
            string channel = NetJson.Str(node, "channel", "current");

            var bytes = JObj.New()
                .Str(NetProto.FieldType, NetProto.ChatMsg)
                .Int("senderUserId", conn.UserId)
                .Str("sender", conn.Name)
                .Str("text", text)
                .Str("channel", channel)
                .Int("expressionId", expressionId)
                .Str("leadingText", Clip(NetJson.Str(node, "leading"), NetLimits.MaxChatChars))
                // roomId=0 是「這句話發生在大廳」的標記(房間號從 1 起)。client 靠它分辨要不要顯示。
                .Int("roomId", room != null ? room.Code : 0)
                .Utf8();

            if (room != null)
            {
                ForEachInRoom(room, c => c.SendPreEncoded(bytes));
                return;
            }

            // 大廳的**家族頻道只送給同一個家族的人** —— 大廳是全服共用的一塊,不像房間本來就只有六個人;
            // 家族的話被整個大廳看光,那個頻道就沒有存在的意義了。沒有家族的人送家族頻道 → 只有自己收得到
            // (client 那邊會顯示「你沒有家族」,見 RoomScreen 的同一條規則)。
            bool familyOnly = string.Equals(channel, "family", StringComparison.OrdinalIgnoreCase);
            ForEachInLobby(c =>
            {
                if (familyOnly && c.UserId != conn.UserId && !SameGuild(conn, c)) return;
                c.SendPreEncoded(bytes);
            });
        }

        /// <summary>兩條連線屬於同一個家族嗎(沒有家族的人永遠不算同族,免得「都沒家族」變成一個大家族)。</summary>
        private static bool SameGuild(Connection a, Connection b)
            => a != null && b != null && !string.IsNullOrEmpty(a.Guild)
               && string.Equals(a.Guild, b.Guild, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 密語。收件人**照名字**在全服的連線裡找 —— 不是在房裡找:密語本來就跨房,
        /// 對方在大廳、在別間房、在旁觀都要收得到。這也是它不能沿用 chatSay 的原因。
        ///
        /// 三種結果都由 server 回,連發送者自己那行「你對X說」也是(見 <see cref="NetProto.WhisperMsg"/>):
        /// 名字存不存在只有 server 知道,本機沒有全服名冊,先畫了才發現送不到就是騙人。
        /// </summary>
        private void OnChatWhisper(Connection conn, object node, long now)
        {
            // 與公開發言共用同一個洗頻窗 —— 否則密語就成了繞過聊天限速的後門。
            if (!conn.Rate.AllowChat(now)) return;

            // 不能用 SanitizeName:它把空字串補成「玩家」,那會讓「只打了 [] 沒填名字」變成去找一個
            // 真的叫「玩家」的人。這裡空的就是空的,直接不處理。
            string target = Clip(NetJson.Str(node, "target").Trim(), NetLimits.MaxNameChars);
            string text = Clip(NetJson.Str(node, "text"), NetLimits.MaxChatChars);
            int expressionId = NetJson.Int(node, "expressionId");
            if (target.Length == 0) return;
            if (string.IsNullOrEmpty(text) && expressionId == 0) return;   // 只選了對象還沒打內容

            string channel = NetJson.Str(node, "channel", "current");
            string leading = Clip(NetJson.Str(node, "leading"), NetLimits.MaxChatChars);

            var to = ControlByName(target);
            if (to == null)
            {
                // 密語找不到人要留 log:玩家回報「密語沒反應」時,這一行能立刻分辨是
                // 「server 沒收到」(完全沒有這行 → 版本不對或封包沒送出)還是「真的沒這個人」。
                LogVerbose("user " + conn.UserId + " 密語找不到「" + target + "」");
                // party 用玩家原本打的那串字,不是正規化後的 —— 錯字要照樣顯示出來,他才知道自己打錯了什麼。
                conn.Send(JObj.New()
                    .Str(NetProto.FieldType, NetProto.WhisperMsg)
                    .Str("kind", NetProto.WhisperNoId)
                    .Str("party", target));
                return;
            }

            // 對方看到的:「X 對你說」。senderUserId 給收端認人(頭上泡不彈,但點名字回話要用)。
            to.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.WhisperMsg)
                .Str("kind", NetProto.WhisperIn)
                .Str("party", conn.Name)
                .Int("senderUserId", conn.UserId)
                .Str("text", text)
                .Str("channel", channel)
                .Int("expressionId", expressionId)
                .Str("leadingText", leading));

            // 自己看到的:「你對 X 說」。party 用 server 認定的正規名字(玩家可能打了不同大小寫)。
            conn.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.WhisperMsg)
                .Str("kind", NetProto.WhisperOut)
                .Str("party", to.Name)
                .Int("senderUserId", conn.UserId)
                .Str("text", text)
                .Str("channel", channel)
                .Int("expressionId", expressionId)
                .Str("leadingText", leading));
        }

        // ================= 小工具 =================

        private static NetJoinUser JoinUserOf(Connection c)
            => new NetJoinUser(c.UserId, c.Name, c.Guild, c.Level, c.Look);

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max);
        }

        /// <summary>
        /// 玩家名稱的清理:去頭尾空白、拿掉控制字元、截長度。
        /// 控制字元一定要拿掉 —— 換行會破壞聊天行的排版,而 0x00 之類的東西在某些
        /// 文字渲染路徑上會出現奇怪的結果。
        /// </summary>
        private static string SanitizeName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "玩家";
            var sb = new System.Text.StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char ch = raw[i];
                if (ch < 0x20 || ch == 0x7F) continue;
                sb.Append(ch);
            }
            string s = sb.ToString().Trim();
            if (s.Length == 0) return "玩家";
            return s.Length <= NetLimits.MaxNameChars ? s : s.Substring(0, NetLimits.MaxNameChars);
        }

        /// <summary>遊玩中的一筆分數快照。</summary>
        private struct FrameSample
        {
            public double TMs;
            public long Score;
            public int Combo, MaxCombo, P, C, B, M;
            public float Hp;

            public static FrameSample Decode(object node)
            {
                var f = new FrameSample();
                f.TMs = NetJson.Num(node, "tMs");
                f.Score = NetJson.Long(node, "score");
                f.Combo = NetJson.Int(node, "combo");
                f.MaxCombo = NetJson.Int(node, "maxCombo");
                f.Hp = (float)NetJson.Num(node, "hp");
                f.P = NetJson.Int(node, "p");
                f.C = NetJson.Int(node, "c");
                f.B = NetJson.Int(node, "b");
                f.M = NetJson.Int(node, "m");
                return f;
            }

            public JObj Encode(int userId)
                => JObj.New()
                    .Int("userId", userId)
                    .Num("tMs", TMs)
                    .Long("score", Score)
                    .Int("combo", Combo)
                    .Int("maxCombo", MaxCombo)
                    .Num("hp", Hp)
                    .Int("p", P)
                    .Int("c", C)
                    .Int("b", B)
                    .Int("m", M);
        }
    }
}
