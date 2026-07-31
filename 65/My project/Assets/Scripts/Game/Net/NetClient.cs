using System;
using System.Collections.Generic;
using Sdo.Net;
using UnityEngine;

namespace Sdo.Game.Net
{
    /// <summary>
    /// 遊戲端的房間狀態維護層 —— 對應 osu! 的 <c>MultiplayerClient</c>。
    ///
    /// **它不知道 socket 存在。** 三層切分:
    ///   <see cref="NetConnection"/> 只搬位元組 → 這一層維護狀態並發事件 →
    ///   <c>OnlineRoomService</c> 把它接到 UI 既有的 <c>IRoomService</c> 介面。
    ///
    /// 這個切分是照抄 osu 最值得抄的工程決策:因為狀態層不碰 socket,
    /// 同一份房間規則(<c>Sdo.Net.Server.NetRoom</c>)可以被「同 process 的假伺服器」驅動,
    /// 於是 UI 開發完全不需要真 server,而假伺服器的行為與線上逐位元組相同。
    ///
    /// 狀態的唯一真相是 server 推來的 <c>roomState</c> 快照 —— 這一層**絕不**自己推測狀態
    /// (例如「我按了準備所以我現在是準備好的」)。樂觀更新是 divergence bug 的溫床:
    /// 一旦本機猜的與 server 的不一致,畫面就會停在一個永遠不會被修正的錯誤狀態。
    /// </summary>
    public sealed class NetClient
    {
        private readonly NetConnection _link = new NetConnection();
        private readonly Dictionary<int, Action<object>> _pending = new Dictionary<int, Action<object>>();
        private int _nextRq = 1;
        private long _lastPingSentMs;
        private int _lastSeenRev;
        private bool _helloSent;

        private enum RoomStateAcceptance
        {
            Closed,
            WaitingForJoinResult,
            WaitingForEntrySnapshot,
            Current,
            WaitingForSpectatorSnapshot,
        }

        private RoomStateAcceptance _roomStateAcceptance;
        private int _roomStateGeneration;
        private int _expectedRoomCode;
        private int _roomEntryRq;

        // ---- 狀態 ----

        public NetLinkState LinkState => _link.State;

        /// <summary>
        /// 給玩家看的失敗原因。優先用 server 送來的 <c>bye</c> 理由(那比「連線中斷」精確得多 ——
        /// 例如「密碼不符」直接告訴他要去改 config.ini),沒有才退回連線層的錯誤。
        /// </summary>
        public string LastError => !string.IsNullOrEmpty(_byeReason) ? _byeReason : _link.LastError;

        private string _byeReason;

        /// <summary>
        /// server 送 <c>bye</c> 時的**原始 code**(沒收到 bye = 空字串)。
        ///
        /// <see cref="LastError"/> 是給 log 看的整句人話;這個是給程式分支用的 ——
        /// 例如「名字被佔用」要彈自己那一句 Toast,而不是通用的「連不上伺服器」。
        /// </summary>
        public string ByeCode { get; private set; } = "";

        /// <summary>把 server 的 <c>bye</c> reason 翻成人看得懂的話。</summary>
        private static string ExplainBye(string reason)
        {
            switch (reason)
            {
                case NetProto.ErrBadPassword:
                    return "密碼不符 —— 請確認 config.ini 的 [Net] serverPassword 與伺服器一致";
                case NetProto.ErrBadToken:
                    return "token 不被接受 —— 請確認 config.ini 的 [Net] serverToken(公網伺服器需要)";
                case NetProto.ErrNameTaken:
                    return "登入失敗:這個名稱已被使用(同一個名字同時只能有一個人在線)";
                case "notAllowed":
                    return "這台伺服器不接受你的來源位址";
                case "tooManyFromIp":
                    return "同一個位址的連線數過多";
                case NetProto.ErrProto:
                    return "協定版本不合 —— 遊戲與伺服器的版本不一樣,需要更新其中一邊";
                case NetProto.ErrRateLimit:
                    return "訊息過於頻繁,被伺服器中斷連線";
                case "serverFull":
                    return "伺服器連線數已滿";
                case "pingTimeout":
                    return "連線逾時(網路中斷?)";
                default:
                    return string.IsNullOrEmpty(reason) ? "" : "伺服器中斷連線(" + reason + ")";
            }
        }
        public bool IsConnected => _link.IsConnected && UserId != 0;

        /// <summary>server 配的使用者 id。0 = 還沒握手完成。</summary>
        public int UserId { get; private set; }

        public string SessionKey { get; private set; } = "";

        /// <summary>目前所在房間的快照。null = 不在任何房間。**只讀** —— 唯一作者是 server。</summary>
        public NetRoomSnapshot Room { get; private set; }

        public bool InRoom => Room != null;

        /// <summary>本機是這間房的房主嗎?</summary>
        public bool IsHost => Room != null && Room.IsHost(UserId);

        /// <summary>本機的座位(不在座位上 → null)。</summary>
        public NetSeat LocalSeat => Room != null ? Room.SeatOf(UserId) : null;

        /// <summary>本機是旁觀者嗎?</summary>
        public bool IsSpectating => Room != null && Room.SpectatorIndexOf(UserId) >= 0;

        /// <summary>最近一次 ping 的往返時間(ms)。-1 = 還沒量到。</summary>
        public float RttMs { get; private set; } = -1f;

        /// <summary>診斷資訊(除錯面板用)。</summary>
        public string Diagnostics =>
            string.Format("link={0} user={1} room={2} rev={3} rtt={4:0}ms sent={5} recv={6} pending={7}",
                _link.State, UserId, Room != null ? Room.Code.ToString() : "-",
                _lastSeenRev, RttMs, _link.SentCount, _link.RecvCount, _link.PendingInbound);

        // ---- 事件(全部在主執行緒上觸發 —— Pump 裡) ----

        /// <summary>房間狀態變了(加入/離開/準備/選歌/座位…都會來一次)。</summary>
        public event Action<NetRoomSnapshot> RoomUpdated;

        /// <summary>離開了房間(自己離開、被踢、房間關閉)。參數是原因。</summary>
        public event Action<string> RoomLeft;

        /// <summary>被踢出房間。參數是原因(host / seatClosed / roomClosed)。</summary>
        public event Action<string> Kicked;

        /// <summary>server 回了一個錯誤。參數:code, msg。</summary>
        public event Action<string, string> ErrorReceived;

        /// <summary>連線掛了(含握手失敗)。參數是人看得懂的原因。</summary>
        public event Action<string> Disconnected;

        /// <summary>要開場了(參與者 + 有歌的旁觀者收到)。</summary>
        public event Action<NetMatchStart> MatchStarting;

        /// <summary>所有人都載入完成,現在開始跑。</summary>
        public event Action<long> GameplayStarted;

        /// <summary>本場被中止(載入逾時 / 沒有參與者)。參數:matchId, reason。</summary>
        public event Action<long, string> GameplayAborted;

        /// <summary>結算資料到了。</summary>
        public event Action<NetResultRow[]> ResultsReady;

        /// <summary>遊玩中的分數流(房內所有人的最新一筆)。</summary>
        public event Action<NetFrameRow[]> FramesReceived;

        /// <summary>Reliable one-shot 50-combo visual event from a remote player.</summary>
        public event Action<NetComboMilestone> ComboMilestoneReceived;

        /// <summary>
        /// 這一場的開場資料(matchStarting 收到的那份)。null = 現在不在一場裡。
        ///
        /// 為什麼由 NetClient 保管、而不是讓收到事件的畫面自己記:進場流程橫跨
        /// 房間畫面 → 轉場(RoomScreen.OnHide)→ ScreenGameplay,中間房間畫面會被拆掉。
        /// 而 gameplay 需要 matchId(送 setPlayState/frame)與 Resolved(隨機場景/難度要與所有人一致)。
        /// </summary>
        public NetMatchStart Match { get; private set; }

        /// <summary>
        /// server 說「大家都載完了,開始跑」了嗎 —— gameplay 的 ReadyGate 讀它。
        /// 開場前為 false,收到 gameplayStarted 才變 true;本場中止/結算就關掉。
        /// </summary>
        public bool GameplayGateOpen { get; private set; }

        /// <summary>
        /// Server-authoritative current leader for the active match. Zero means that no authoritative frame has
        /// arrived yet; gameplay then falls back to its deterministic local score rule.
        /// </summary>
        public int LeaderUserId { get; private set; }

        /// <summary>這一場結束/作廢 —— matchId 與閘門一起清掉。</summary>
        private void ClearMatch()
        {
            Match = null;
            GameplayGateOpen = false;
            LeaderUserId = 0;
        }

        /// <summary>本機在這一場裡是舞者(而不是旁觀者)嗎?</summary>
        public bool IsMatchParticipant => Match != null && Match.IsParticipant(UserId);

        /// <summary>聊天訊息。</summary>
        public event Action<NetChatMessage> ChatReceived;

        /// <summary>密語(含「你對X說」與「找不到玩家X」——三種都由 server 回)。</summary>
        public event Action<NetWhisperMessage> WhisperReceived;

        // ---- 缺歌傳檔(M5)----

        /// <summary>server 回答「我有沒有這首歌」:(packId, 有沒有)。</summary>
        public event Action<string, bool> BlobInfoReceived;

        /// <summary>房內廣播:某首歌上傳完成、現在可以下載了(packId)。</summary>
        public event Action<string> BlobAvailable;

        /// <summary>房內廣播:某個人正在上傳/下載到幾成 —— (userId, 0..1, 是不是上傳)。</summary>
        public event Action<int, float, bool> BlobProgress;

        /// <summary>問 server 有沒有這首歌(缺歌的人要先確認,不然下載會撲空)。</summary>
        public void SendBlobQuery(string packId)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.BlobQuery)
                .Int(NetProto.FieldRequest, NextRq(null))
                .Str("packId", packId ?? ""));

        /// <summary>
        /// 房間裡每個人的最新位置(userId → row),自己那筆已經濾掉。
        ///
        /// 為什麼是**可輪詢的字典**而不是事件:房間畫面是 OnShow/OnHide 建拆的,而位置是
        /// **狀態**不是事件 —— 用字典的話,晚建起來的 3D 房間可以立刻把每個人放到對的位置,
        /// 不必等對方下一次走動(而站著不動的人根本不會再送)。
        /// 也順便省掉「OnShow 訂閱 / OnHide 退訂」那一類很容易漏的配對。
        /// </summary>
        public readonly Dictionary<int, NetMoveRow> Moves = new Dictionary<int, NetMoveRow>();

        // ---- 連線 ----

        /// <summary>
        /// 開始連線並握手。非阻塞 —— 呼叫端輪詢 <see cref="LinkState"/> / <see cref="IsConnected"/>。
        /// </summary>
        /// <param name="tls">走 TLS(<c>config.ini serverTls</c>)。</param>
        /// <param name="pinFingerprint">釘選的 server 憑證指紋(<c>config.ini serverCertFingerprint</c>)。</param>
        public void Connect(string host, int port, string password, NetHelloIdentity identity,
                            bool tls = false, string pinFingerprint = null)
        {
            _identity = identity;
            _password = password ?? "";
            _helloSent = false;
            UserId = 0;
            Room = null;
            CloseRoomStateAcceptance();
            ClearMatch();   // 離開房間/斷線 → 這一場也沒了(不清的話 gameplay 會拿著舊 matchId 送封包)
            _link.BeginConnect(host, port, 5000, tls, pinFingerprint);
        }

        public void Disconnect(string reason = "userQuit")
        {
            _link.Close(reason);
            UserId = 0;
            Room = null;
            CloseRoomStateAcceptance();
            ClearMatch();   // 離開房間/斷線 → 這一場也沒了(不清的話 gameplay 會拿著舊 matchId 送封包)
            _sentLook = null;       // 重連後要重送外觀(server 那邊的 conn 已經沒了)
            _sentIdentity = null;   // 同上:名字也是記在那條 conn 上的
            Moves.Clear();
        }

        private NetHelloIdentity _identity;
        private string _password = "";

        // ---- 每幀 pump ----

        /// <summary>
        /// 由主執行緒每幀呼叫一次(<c>FrontendApp.Update</c>)。
        /// 處理收到的訊息、送心跳、偵測斷線。**所有事件都從這裡觸發**,
        /// 所以 UI 端不需要考慮執行緒安全。
        /// </summary>
        public void Pump()
        {
            // 剛連上 → 送 hello。
            if (_link.IsConnected && !_helloSent)
            {
                _helloSent = true;
                SendHello();
            }

            // 收訊息
            byte kind;
            byte[] payload;
            int guard = 0;
            while (_link.Poll(out kind, out payload))
            {
                // 一幀處理上限:避免大量積壓的訊息把一幀拖成幾百 ms(那會看起來像卡頓)。
                if (++guard > 256) break;
                if (kind != NetLimits.FrameKindJson) continue;   // 檔案 chunk 在 M5 才處理

                object node;
                string type;
                if (!NetJson.TryParseMessage(payload, 0, payload.Length, out node, out type)) continue;
                Handle(type, node);
            }

            // 心跳
            if (IsConnected)
            {
                long now = NowMs();
                if (now - _lastPingSentMs >= NetLimits.PingIntervalMs)
                {
                    _lastPingSentMs = now;
                    _link.Send(JObj.New().Str(NetProto.FieldType, NetProto.Ping).Num("t0", now));
                }
            }

            // 斷線偵測
            if ((_link.State == NetLinkState.Closed || _link.State == NetLinkState.Failed) && _reportedDown == false)
            {
                _reportedDown = true;
                var wasInRoom = Room != null;
                Room = null;
                CloseRoomStateAcceptance();
                UserId = 0;
                ClearMatch();
                Moves.Clear();
                if (wasInRoom) Raise(RoomLeft, "disconnected");
                Raise(Disconnected, string.IsNullOrEmpty(_link.LastError) ? "連線中斷" : _link.LastError);
            }
        }

        private bool _reportedDown;

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private void SendHello()
        {
            var look = JObj.New()
                .Int("gender", _identity.Gender)
                .Int("bodyIndex", _identity.BodyIndex);
            var parts = JArr.New();
            if (_identity.AvatarParts != null)
                for (int i = 0; i < _identity.AvatarParts.Length; i++) parts.Add(_identity.AvatarParts[i]);
            look.Put("parts", parts);

            var hello = JObj.New()
                .Str(NetProto.FieldType, NetProto.Hello)
                .Int(NetProto.FieldRequest, NextRq(null))
                .Int("proto", NetProto.Version)
                .Str("role", NetProto.RoleControl)
                .Str("playerId", _identity.PlayerId ?? "")
                .Str("name", _identity.Name ?? "")
                .Str("guild", _identity.Guild ?? "")
                .Int("level", _identity.Level)
                // 這顆 client 是哪個 commit(視窗標題那串,BuildScript 在 build 時用 git 寫進 productName)。
                // server 會把它印在連線 log 裡,而且與自己的版本不同時警告 —— 「兩邊版本對不對」
                // 是排查連線問題的第一步,而它以前完全看不出來。
                .Str("build", Application.productName ?? "")
                .Put("look", look);

            if (!string.IsNullOrEmpty(_password)) hello.Str("password", _password);
            // 公網 server 的 token(M10)。空的就不帶 —— server 沒啟用 token 認證時也不會看它。
            // 🔴 它與密碼的差別:密碼是共用的一道門,token 是「server 認得的你」——
            // 啟用之後身分由 server 依 token 決定,不再信這裡送的 playerId。
            var token = Sdo.Settings.RoomConfig.serverToken;
            if (!string.IsNullOrEmpty(token)) hello.Str("authToken", token);
            _link.Send(hello);
        }

        // ---- 訊息處理 ----

        private void Handle(string type, object node)
        {
            switch (type)
            {
                case NetProto.Welcome:
                    UserId = NetJson.Int(node, "userId");
                    SessionKey = NetJson.Str(node, "sessionKey");
                    Debug.Log("[net] 已連上 server,userId=" + UserId);
                    break;

                case NetProto.Pong:
                    RttMs = (float)(NowMs() - NetJson.Num(node, "t0"));
                    break;

                case NetProto.Bye:
                    {
                        string reason = NetJson.Str(node, "reason");
                        Debug.LogWarning("[net] server 要求斷線:" + reason);
                        // 把協定 code 翻成人看得懂的話 —— 這條訊息會直接顯示給玩家看
                        // (開機連不上時的 Toast)。「badPassword」對玩家毫無意義,
                        // 「密碼不符」他才知道要去改 config.ini。
                        ByeCode = reason ?? "";
                        _byeReason = ExplainBye(reason);
                        _link.Close("bye:" + reason);
                        break;
                    }

                case NetProto.RoomState:
                    ApplyRoomState(node);
                    break;

                case NetProto.JoinResult:
                    CompletePending(node);
                    break;

                case NetProto.RoomListResult:
                    CompletePending(node);
                    break;

                case NetProto.Kicked:
                    {
                        string reason = NetJson.Str(node, "reason");
                        Room = null;
                        CloseRoomStateAcceptance();
                        ClearMatch();
                        Moves.Clear();
                        Raise(Kicked, reason);
                        Raise(RoomLeft, "kicked:" + reason);
                        break;
                    }

                case NetProto.Error:
                    {
                        string code = NetJson.Str(node, "code");
                        string msg = NetJson.Str(node, "msg");
                        // 有帶 rq 的錯誤先交給發起那個請求的 callback。
                        if (!CompletePending(node))
                        {
                            if (ErrorReceived != null) ErrorReceived(code, msg);
                        }
                        break;
                    }

                case NetProto.MatchStarting:
                    {
                        var start = NetMatchStart.Decode(node);
                        Match = start;               // 保管到這一場結束(見 Match 的註解)
                        GameplayGateOpen = false;
                        LeaderUserId = 0;
                        Raise(MatchStarting, start);
                        break;
                    }

                case NetProto.GameplayStarted:
                    {
                        long mid = NetJson.Long(node, "matchId");
                        // 只認**這一場**的 —— 上一場遲到的 gameplayStarted 不該把新一場的閘門打開。
                        if (Match != null && Match.MatchId == mid) GameplayGateOpen = true;
                        Raise(GameplayStarted, mid);
                        break;
                    }

                case NetProto.GameplayAborted:
                    {
                        long mid = NetJson.Long(node, "matchId");
                        if (Match != null && Match.MatchId == mid) ClearMatch();
                        if (GameplayAborted != null) GameplayAborted(mid, NetJson.Str(node, "reason"));
                        break;
                    }

                case NetProto.ResultsReady:
                    {
                        long mid = NetJson.Long(node, "matchId");
                        if (Match == null || Match.MatchId != mid) break;
                        // 結算 = 這一場結束。閘門一定要關掉,否則下一場在收到自己的 gameplayStarted 之前
                        // 就會被當成「可以開始」→ 那台會提早開跑,與別人不同步。
                        GameplayGateOpen = false;
                        Raise(ResultsReady, NetResultRow.DecodeAll(NetJson.Arr(node, "rows")));
                        break;
                    }

                case NetProto.Frames:
                    {
                        long mid = NetJson.Long(node, "matchId");
                        if (Match != null && Match.MatchId == mid)
                        {
                            int leader = NetJson.Int(node, "leaderUserId");
                            LeaderUserId = leader > 0 ? leader : 0;
                            Raise(FramesReceived, NetFrameRow.DecodeAll(NetJson.Arr(node, "f")));
                        }
                        break;
                    }
                case NetProto.ComboMilestone:
                    {
                        var milestone = NetComboMilestone.Decode(node);
                        if (Match != null && milestone.MatchId == Match.MatchId)
                            Raise(ComboMilestoneReceived, milestone);
                        break;
                    }


                case NetProto.ChatMsg:
                    Raise(ChatReceived, NetChatMessage.Decode(node));
                    break;

                case NetProto.WhisperMsg:
                    Raise(WhisperReceived, NetWhisperMessage.Decode(node));
                    break;

                case NetProto.BlobInfo:
                    {
                        var h = BlobInfoReceived;
                        if (h != null) h(NetJson.Str(node, "packId"), NetJson.Bool(node, "have"));
                        break;
                    }

                case NetProto.BlobAvailable:
                    Raise(BlobAvailable, NetJson.Str(node, "packId"));
                    break;

                case NetProto.BlobProgress:
                    {
                        var h = BlobProgress;
                        if (h != null)
                            h(NetJson.Int(node, "userId"), (float)NetJson.Num(node, "frac"), NetJson.Bool(node, "up"));
                        break;
                    }

                case NetProto.Moves:
                    {
                        // 座位↔旁觀切換會把角色傳送到新位置並清掉他的位置快取,所以**切換之前**發出的
                        // 那一批不能把舊的插值位置放回去 —— 那一批帶的 rev 比現在小,擋掉它就夠了。
                        //
                        // 🔴 這裡曾經要求 rev **完全相等**,那會餓死整條位置流:server 推 moves 時帶的是它
                        // 當下最新的 rev,而 client 只要丟棄過任何一份 roomState(CanAcceptRoomState 的
                        // 進房/旁觀等待狀態、或 rev 沒有前進的重送),Room.Rev 就永久落後 server 一截 ——
                        // 之後每一批 moves 都「不等於」,遠端玩家就從此定在原地不動了。
                        // 位置資料本身是 userId-keyed 的,比當前 rev 新沒有任何害處(那只代表對應的
                        // roomState 還在路上;還沒生出來的人 RoomScene3D.ApplyRemoteMove 本來就會忽略)。
                        if (Room == null
                            || NetJson.Int(node, "roomCode") != Room.Code
                            || NetJson.Int(node, "roomRev") < Room.Rev)
                            break;

                        var rows = NetJson.Arr(node, "m");
                        if (rows != null)
                            for (int i = 0; i < rows.Count; i++)
                            {
                                var row = NetMoveRow.Decode(rows[i]);
                                if (row.UserId == 0 || row.UserId == UserId) continue;   // 自己那筆不用
                                Moves[row.UserId] = row;
                            }
                        break;
                    }

                default:
                    // 不認得的訊息:可能是新版 server。忽略比斷線好。
                    break;
            }
        }

        private void ApplyRoomState(object node)
        {
            var snap = NetRoomSnapshot.Decode(node);
            RoomStateAcceptance accepting = _roomStateAcceptance;
            if (!CanAcceptRoomState(snap, accepting)) return;

            // rev 單調遞增。丟掉過期的快照 —— TCP 有序所以正常不會發生,
            // 但 loopback 假伺服器與測試路徑需要這道保護。
            if (snap.Rev != 0 && snap.Rev <= _lastSeenRev) return;
            _lastSeenRev = snap.Rev;

            bool wasIn = Room != null;
            bool stillIn = snap.Contains(UserId);

            if (stillIn) InvalidateMovesForSlotChanges(Room, snap);
            else Moves.Clear();
            Room = stillIn ? snap : null;
            if (!stillIn)
            {
                CloseRoomStateAcceptance();
                if (wasIn) Raise(RoomLeft, "left");
                return;
            }

            _roomStateAcceptance = RoomStateAcceptance.Current;

            // 旁觀請求成功的**唯一證據**:我出現在這份快照的旁觀名單裡。
            // (server 對成功的 spectate 不回應那個 rq,只廣播快照 —— 見 Spectate 的註解。)
            if (accepting == RoomStateAcceptance.WaitingForSpectatorSnapshot)
                CompleteSpectate(_roomStateGeneration, NetProto.JoinOk);

            Raise(RoomUpdated, snap);
        }

        private bool CanAcceptRoomState(NetRoomSnapshot snap, RoomStateAcceptance accepting)
        {
            if (snap == null || UserId == 0 || _expectedRoomCode <= 0 || snap.Code != _expectedRoomCode)
                return false;

            switch (accepting)
            {
                case RoomStateAcceptance.Current:
                    return true;
                case RoomStateAcceptance.WaitingForEntrySnapshot:
                    // The server sends joinResult before the first snapshot. Requiring membership here prevents an
                    // old same-room snapshot from closing or rev-poisoning a just-opened generation.
                    return snap.Contains(UserId);
                case RoomStateAcceptance.WaitingForSpectatorSnapshot:
                    // Spectate has no success response: only a matching snapshot that actually moved us into the
                    // spectator list proves success. A late seated snapshot must not consume this generation.
                    return snap.SpectatorIndexOf(UserId) >= 0;
                default:
                    return false;
            }
        }

        private void CancelPendingRoomOperations()
        {
            if (_roomEntryRq != 0)
            {
                _pending.Remove(_roomEntryRq);
                _roomEntryRq = 0;
            }
            if (_spectateRq != 0)
            {
                _pending.Remove(_spectateRq);
                _spectateRq = 0;
            }
            _spectateCb = null;
        }

        private void CloseRoomStateAcceptance()
        {
            _roomStateGeneration++;
            CancelPendingRoomOperations();
            _roomStateAcceptance = RoomStateAcceptance.Closed;
            _expectedRoomCode = 0;
            _lastSeenRev = 0;
        }

        private int BeginRoomEntry(int requestedCode)
        {
            CloseRoomStateAcceptance();
            _roomStateAcceptance = RoomStateAcceptance.WaitingForJoinResult;
            _expectedRoomCode = requestedCode > 0 ? requestedCode : 0;
            return _roomStateGeneration;
        }

        private int BeginSpectatorEntry(int requestedCode)
        {
            CloseRoomStateAcceptance();
            _roomStateAcceptance = RoomStateAcceptance.WaitingForSpectatorSnapshot;
            _expectedRoomCode = requestedCode > 0 ? requestedCode : 0;
            return _roomStateGeneration;
        }

        private void InvalidateMovesForSlotChanges(NetRoomSnapshot previous, NetRoomSnapshot current)
        {
            if (previous == null || current == null) return;
            for (int i = 0; i < previous.Seats.Length; i++)
            {
                int userId = previous.Seats[i].IsTaken ? previous.Seats[i].UserId : 0;
                if (userId != 0 && userId != UserId
                    && SnapshotSlot(previous, userId) != SnapshotSlot(current, userId))
                    Moves.Remove(userId);
            }

            var spectators = previous.Spectators;
            if (spectators == null) return;
            for (int i = 0; i < spectators.Length; i++)
            {
                int userId = spectators[i] != null ? spectators[i].UserId : 0;
                if (userId != 0 && userId != UserId
                    && SnapshotSlot(previous, userId) != SnapshotSlot(current, userId))
                    Moves.Remove(userId);
            }
        }

        private static int SnapshotSlot(NetRoomSnapshot snap, int userId)
        {
            if (snap == null || userId == 0) return int.MinValue;
            int seat = snap.SeatIndexOf(userId);
            if (seat >= 0) return seat;
            int spectator = snap.SpectatorIndexOf(userId);
            // Keep spectator positions in a separate namespace from the six seated slots.
            return spectator >= 0 ? 1000 + spectator : int.MinValue;
        }

        // ---- request / response 配對 ----

        private int NextRq(Action<object> onReply)
        {
            int rq = _nextRq++;
            if (onReply != null) _pending[rq] = onReply;
            return rq;
        }

        /// <summary>把帶 rq 的回應交給發起者。回 true = 有人在等這個 rq。</summary>
        private bool CompletePending(object node)
        {
            int rq = NetJson.Int(node, NetProto.FieldRequest);
            if (rq == 0) return false;

            Action<object> cb;
            if (!_pending.TryGetValue(rq, out cb)) return false;
            _pending.Remove(rq);
            if (cb != null)
            {
                try { cb(node); }
                catch (Exception ex) { Debug.LogError("[net] 回應處理例外: " + ex); }
            }
            return true;
        }

        // ---- 房間操作 ----

        /// <summary>要一份房間列表。</summary>
        public void RequestRoomList(Action<NetRoomListEntry[]> onList)
        {
            Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.RoomList)
                .Int(NetProto.FieldRequest, NextRq(node =>
                {
                    if (onList != null) onList(NetRoomListEntry.DecodeAll(NetJson.Arr(node, "rooms")));
                })));
        }

        /// <summary>「現在誰在線上」——大廳玩家名單的資料來源。與 <see cref="RequestRoomList"/> 同一個問答模式
        /// (server 沒有上下線推播,只能自己回頭問)。</summary>
        public void RequestUserList(Action<NetUserListEntry[]> onList)
        {
            Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.UserList)
                .Int(NetProto.FieldRequest, NextRq(node =>
                {
                    if (onList != null) onList(NetUserListEntry.DecodeAll(NetJson.Arr(node, "users")));
                })));
        }

        /// <summary>建房。<paramref name="onResult"/> 收到 (result, code)。</summary>
        public void CreateRoom(string name, Action<string, int> onResult)
        {
            PublishLook();       // 🔴 一定要在 createRoom **之前** —— 見 PublishLook 的註解
            PublishIdentity();   // 同上:座位的名字是進房那一刻從連線上抄過去的
            int generation = BeginRoomEntry(0);
            int rq = NextRq(node => ReportJoin(node, onResult, generation, 0));
            _roomEntryRq = rq;
            Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.CreateRoom)
                .Int(NetProto.FieldRequest, rq)
                .Str("name", name ?? ""));
        }

        /// <summary>用房號加入。<paramref name="onResult"/> 收到 (result, code)。</summary>
        public void JoinRoom(int code, Action<string, int> onResult)
        {
            PublishLook();       // 🔴 一定要在 joinRoom **之前** —— 見 PublishLook 的註解
            PublishIdentity();   // 同上:座位的名字是進房那一刻從連線上抄過去的
            int generation = BeginRoomEntry(code);
            int rq = NextRq(node => ReportJoin(node, onResult, generation, code));
            _roomEntryRq = rq;
            Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.JoinRoom)
                .Int(NetProto.FieldRequest, rq)
                .Int("code", code));
        }

        /// <summary>
        /// 加入房間;**座位滿了(或房間正在打)就自動改用旁觀身分進去**。
        ///
        /// 為什麼這條政策放在這裡而不是各畫面自己寫:進房有兩條路(選男女畫面的「加入」框、
        /// dev 的 <c>SDO_JOINFIRST</c>),各寫一份的話一定會有一條漏掉,而漏掉的症狀是
        /// 「這條路進不去滿的房間」—— 沒有錯誤、只是行為不一致,很難聯想。
        ///
        /// 官方的房間本來就是「六個舞者 + 十個旁觀」;server 也早就支援 <c>spectate{code}</c>
        /// (含遊戲中的房間 —— R18/D10:進得了房間看頭貼,只是不中途接入 gameplay)。
        /// 真正把人擋在門外的一直是 client 自己。
        /// </summary>
        /// <param name="onResult">(result, asSpectator)。result 是 <c>JoinOk</c> 或失敗的 code
        /// (座位與旁觀席都滿 → <see cref="NetProto.ErrLookerFull"/>)。</param>
        /// <param name="onFallback">要改用旁觀之前呼叫一次,參數 = 觸發的 joinResult(<c>full</c>/<c>inGame</c>)。
        /// UI 可以趁機說一聲「座位滿了,以旁觀身分進入」。</param>
        public void JoinOrSpectate(int code, Action<string, bool> onResult, Action<string> onFallback = null)
        {
            JoinRoom(code, (result, _) =>
            {
                if (result == NetProto.JoinOk) { if (onResult != null) onResult(result, false); return; }
                if (result != NetProto.JoinFull && result != NetProto.JoinInGame)
                {
                    if (onResult != null) onResult(result, false);
                    return;
                }
                if (onFallback != null) onFallback(result);
                Spectate(code, spec =>
                {
                    if (onResult != null) onResult(spec, spec == NetProto.JoinOk);
                });
            });
        }

        private void ReportJoin(object node, Action<string, int> onResult, int generation, int requestedCode)
        {
            if (generation != _roomStateGeneration
                || _roomStateAcceptance != RoomStateAcceptance.WaitingForJoinResult)
                return;

            _roomEntryRq = 0;
            // 可能收到 joinResult,也可能收到 error(例如房間滿了以外的失敗)。
            string t = NetJson.Str(node, NetProto.FieldType);
            string result = t == NetProto.Error ? NetProto.JoinNotFound : NetJson.Str(node, "result");
            int code = t == NetProto.Error ? 0 : NetJson.Int(node, "code");
            bool accepted = result == NetProto.JoinOk && code > 0
                && (requestedCode <= 0 || requestedCode == code);

            if (accepted)
            {
                _expectedRoomCode = code;
                _lastSeenRev = 0;
                _roomStateAcceptance = RoomStateAcceptance.WaitingForEntrySnapshot;
            }
            else if (Room != null)
            {
                _expectedRoomCode = Room.Code;
                _lastSeenRev = Room.Rev;
                _roomStateAcceptance = RoomStateAcceptance.Current;
            }
            else
            {
                _expectedRoomCode = 0;
                _lastSeenRev = 0;
                _roomStateAcceptance = RoomStateAcceptance.Closed;
            }

            if (onResult != null) onResult(result, code);
        }

        public void LeaveRoom()
        {
            bool wasInRoom = Room != null;
            Send(JObj.New().Str(NetProto.FieldType, NetProto.LeaveRoom));
            Room = null;
            CloseRoomStateAcceptance();
            ClearMatch();   // 離開房間/斷線 → 這一場也沒了(不清的話 gameplay 會拿著舊 matchId 送封包)

            Moves.Clear();   // 位置是「這間房裡的狀態」,離房就沒有意義了(留著會變幽靈)
            if (wasInRoom) Raise(RoomLeft, "left");
        }

        public void SetReady(bool ready)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.SetReady)
                .Int(NetProto.FieldRequest, NextRq(null)).Bool("ready", ready));

        public void SetOwnTeam(int team)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.SetOwnTeam)
                .Int(NetProto.FieldRequest, NextRq(null)).Int("team", team));

        public void SetRoomName(string name)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.SetRoomName)
                .Int(NetProto.FieldRequest, NextRq(null)).Str("name", name ?? ""));

        public void SetSong(NetSongRef song)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.SetSong)
                .Int(NetProto.FieldRequest, NextRq(null))
                .Put("song", song != null ? song.Encode() : null));

        public void SetRoomSettings(NetRoomSettings settings)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.SetRoomSettings)
                .Int(NetProto.FieldRequest, NextRq(null))
                .Put("settings", settings != null ? settings.Encode() : null));

        public void AssignTeams(TeamLayout layout)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.AssignTeams)
                .Int(NetProto.FieldRequest, NextRq(null))
                .Str("layout", TeamLayoutRules.ToWire(layout)));

        public void KickUser(int userId)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.KickUser)
                .Int(NetProto.FieldRequest, NextRq(null)).Int("userId", userId));

        public void SetSeatClosed(int seat, bool closed)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.SetSeatClosed)
                .Int(NetProto.FieldRequest, NextRq(null)).Int("seat", seat).Bool("closed", closed));

        public void TransferHost(int userId)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.TransferHost)
                .Int(NetProto.FieldRequest, NextRq(null)).Int("userId", userId));

        // 進行中的旁觀請求。0 = 沒有。**成功與失敗走的是兩條不同的路**(見 Spectate)。
        private int _spectateRq;
        private Action<string> _spectateCb;

        /// <summary>
        /// 以旁觀身分加入(<paramref name="code"/> = 房號;0 = 我現在這間房裡切成旁觀)。
        ///
        /// 🔴 **成功與失敗的回應形狀不一樣**,所以不能只用 rq 配對:
        ///   • 失敗 → <c>error{rq,code}</c>(lookerFull / notInRoom …)—— 這條走 <see cref="NextRq"/>。
        ///   • 成功 → server **不回應那個 rq**,只廣播一份新的 <c>roomState</c>。
        ///     所以「我出現在旁觀名單裡」才是成功的證據(見 <see cref="ApplyRoomState"/> 末端)。
        /// 這與整個連線層的原則一致:不做樂觀更新,一律等 server 的快照。
        ///
        /// <paramref name="onResult"/> 收到 <c>NetProto.JoinOk</c> 或 server 的 error code。
        /// </summary>
        public void Spectate(int code = 0, Action<string> onResult = null)
        {
            PublishLook();       // 旁觀者在房間 3D 裡也是站在那邊的人,外觀一樣要對
            PublishIdentity();   // 旁觀名單顯示的也是名字
            int requestedCode = code > 0 ? code : (Room != null ? Room.Code : _expectedRoomCode);
            int generation = BeginSpectatorEntry(requestedCode);
            int rq = NextRq(node => CompleteSpectate(generation, NetJson.Str(node, "code")));
            _spectateRq = rq;
            _spectateCb = onResult;
            Send(JObj.New().Str(NetProto.FieldType, NetProto.Spectate)
                .Int(NetProto.FieldRequest, rq).Int("code", code));
        }

        /// <summary>結束一次旁觀請求(成功或失敗都走這裡),並把 pending 收乾淨。</summary>
        private void CompleteSpectate(string result)
            => CompleteSpectate(_roomStateGeneration, result);

        private void CompleteSpectate(int generation, string result)
        {
            if (generation != _roomStateGeneration) return;
            if (_spectateRq != 0) { _pending.Remove(_spectateRq); _spectateRq = 0; }
            var cb = _spectateCb;
            _spectateCb = null;
            string r = string.IsNullOrEmpty(result) ? NetProto.ErrBadState : result;
            if (r != NetProto.JoinOk)
            {
                if (Room != null)
                {
                    _expectedRoomCode = Room.Code;
                    _lastSeenRev = Room.Rev;
                    _roomStateAcceptance = RoomStateAcceptance.Current;
                }
                else
                {
                    _expectedRoomCode = 0;
                    _lastSeenRev = 0;
                    _roomStateAcceptance = RoomStateAcceptance.Closed;
                }
            }
            if (cb == null)
            {
                // 沒人在等結果(房間裡按「旁觀」鈕那條)→ 失敗要交回一般的錯誤通道,
                // 否則它會被這個 pending 吃掉變成靜默失敗(旁觀席滿了按了沒反應)。
                if (r != NetProto.JoinOk && ErrorReceived != null) ErrorReceived(r, "");
                return;
            }
            try { cb(r); }
            catch (Exception ex) { Debug.LogError("[net] 旁觀結果處理例外: " + ex); }
        }

        public void StopSpectate()
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.StopSpectate)
                .Int(NetProto.FieldRequest, NextRq(null)));

        /// <summary>上報「我有沒有這首歌」。</summary>
        public void SetAvailability(string packId, Availability avail, float progress = 0f)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.SetAvailability)
                .Str("packId", packId ?? "")
                .Str("state", NetState.ToWire(avail))
                .Num("progress", progress));

        /// <summary>房主按開始。<paramref name="force"/> = 連按兩下的強制開始。</summary>
        public void RequestStart(bool force, NetResolvedRound resolved)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.RequestStart)
                .Int(NetProto.FieldRequest, NextRq(null))
                .Bool("force", force)
                .Put("resolved", resolved != null ? resolved.Encode() : null));

        public void SetPlayState(PlayState state, long matchId)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.SetPlayState)
                .Int(NetProto.FieldRequest, NextRq(null))
                .Str("state", NetState.ToWire(state))
                .Long("matchId", matchId));

        /// <summary>
        /// 遊玩中的一筆分數。**用 lossy 送** —— 送不出去就丟掉,絕不阻塞 gameplay。
        ///
        /// ⚠️ **呼叫端必須在每個 8 拍結算點送一筆**(除了固定的 200ms 節奏之外)。
        /// 收端要靠相鄰兩筆的判定計數差值推導出「這個 8 拍區塊有沒有斷、有沒有中」,
        /// 才能重現遠端舞者的跳/停 gate(見 <c>Sdo.Ruleset.DanceGate</c>)。
        /// 少了 8 拍邊界那筆,一筆 frame 會跨越多個區塊,中間的資訊就丟失了 ——
        /// 症狀是遠端舞者該停的時候還在跳。
        /// </summary>
        public void SendFrame(long matchId, double tMs, long score, int combo, int maxCombo,
                              float hp, int p, int c, int b, int m)
        {
            if (!IsConnected) return;
            _link.SendLossy(JObj.New()
                .Str(NetProto.FieldType, NetProto.Frame)
                .Long("matchId", matchId)
                .Num("tMs", tMs)
                .Long("score", score)
                .Int("combo", combo)
                .Int("maxCombo", maxCombo)
                .Num("hp", hp)
                .Int("p", p).Int("c", c).Int("b", b).Int("m", m));
        }

        public void SendPlayFinished(long matchId, long score, int combo, int maxCombo,
                                     int p, int c, int b, int m)
            => Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.PlayFinished)
                .Long("matchId", matchId)
                .Long("score", score)
                .Int("combo", combo).Int("maxCombo", maxCombo)
                .Int("p", p).Int("c", c).Int("b", b).Int("m", m));

        /// <summary>Reliably reports a one-shot combo effect, separately from lossy frames.</summary>
        public void SendComboMilestone(long matchId, int combo)
            => Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ComboMilestone)
                .Long("matchId", matchId)
                .Int("combo", combo));

        /// <summary>
        /// 回報自己在房間裡的位置與朝向。**只在走動時送**(見 <see cref="NetLimits.ClientMoveIntervalMs"/>),
        /// 停下來時送最後一筆就不再送 —— 站著不動的人不需要每 100ms 提醒別人他還在那裡。
        /// </summary>
        public void SendMove(float x, float z, float facing, bool walking)
        {
            var room = Room;
            if (room == null) return;
            int slot = SnapshotSlot(room, UserId);
            if (slot == int.MinValue) return;

            Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.Move)
                .Int("roomCode", room.Code)
                .Int("roomRev", room.Rev)
                .Int("slot", slot)
                .Num("x", x).Num("z", z).Num("f", facing).Bool("w", walking));
        }

        /// <summary>
        /// 「我現在長什麼樣」的提供者,由 <c>AppContext</c> 注入(它是唯一知道 profile / 穿搭解析的地方)。
        /// null → <see cref="PublishLook"/> 什麼都不做。
        /// </summary>
        public Func<NetAvatarLook> LocalLook;

        private NetAvatarLook _sentLook;

        /// <summary>
        /// 把自己的外觀報給 server。**進房的三個出口(建房/加入/旁觀)都在第一行呼叫它。**
        ///
        /// 🔴 為什麼一定要在 join **之前**:server 開座位時吃的是這條連線上記著的那份外觀
        /// (<c>Hub.Handlers.JoinUserOf(conn)</c>),所以先送 = **第一份廣播出去的快照就是對的**。
        /// 晚送的話別人會先看到一隻預設外觀的角色,然後才「pop」一下換裝 —— 而換裝意味著
        /// 重新讀十幾個部件檔(50-100ms hitch)。更糟的是:如果收端沒做「外觀變了要重建」,
        /// 那一下 pop 根本不會發生,遠端角色就**永遠**是預設的女角(實測踩過)。
        ///
        /// 進房前送、換裝後送,兩者都經過這裡;內容沒變就不送(見下面的去重理由)。
        /// </summary>
        public void PublishLook()
        {
            if (LocalLook == null) return;
            var look = LocalLook();
            if (look == null) return;
            // 去重:每送一次 server 都會 rev++ 並向全房廣播一份完整快照(6 人 × 1-2 KB)。
            // 「進房 / 打完歌回房 / 每次換裝面板關閉」都會呼叫這裡,而多數時候外觀根本沒變。
            if (_sentLook != null && _sentLook.SameAs(look)) return;
            _sentLook = look;
            SendLook(look.Gender, look.BodyIndex, look.Parts);
        }

        /// <summary>
        /// 回報自己的外觀(性別 / 體型 / 穿戴部件)。一般走 <see cref="PublishLook"/>(有去重);
        /// 這個多載留給「就是要強制送一次」的呼叫端。
        /// </summary>
        public void SendLook(int gender, int bodyIndex, string[] parts)
        {
            var look = JObj.New().Int("gender", gender).Int("bodyIndex", bodyIndex);
            var arr = JArr.New();
            if (parts != null)
            {
                int n = parts.Length < NetAvatarLook.MaxParts ? parts.Length : NetAvatarLook.MaxParts;
                for (int i = 0; i < n; i++) arr.Add(parts[i] ?? "");
            }
            look.Put("parts", arr);
            Send(JObj.New().Str(NetProto.FieldType, NetProto.SetLook).Put("look", look));
        }

        /// <summary>
        /// 「我現在是誰」的提供者,由 <c>AppContext</c> 注入(同 <see cref="LocalLook"/> 的理由 ——
        /// 只有它知道 session / profile 怎麼解析)。null → <see cref="PublishIdentity"/> 什麼都不做。
        /// </summary>
        public Func<NetPlayerIdentity> LocalIdentity;

        private NetPlayerIdentity _sentIdentity;

        /// <summary>
        /// 把自己的身分(名字 / playerId / 家族 / 等級)報給 server。
        /// **與 <see cref="PublishLook"/> 成對** —— 進房的三個出口都在第一行呼叫兩者。
        ///
        /// 🔴 為什麼名字不能只靠 hello:握手在開機時就做完了,而**選性別 == 選帳號**
        /// (女角/男角是兩個 profile,名字不一樣)。只補送 setLook 的話,別人看到的會是
        /// 「新的男角模型」配「舊的女角名字」—— 而且**永遠**不會變回來,因為座位名字是
        /// 進房那一刻從連線上抄過去的,之後沒有任何東西會再碰它(實測踩過)。
        ///
        /// 去重理由同 PublishLook:每送一次 server 就 rev++ 並向全房廣播一份完整快照。
        /// </summary>
        public void PublishIdentity()
        {
            if (LocalIdentity == null) return;
            var id = LocalIdentity();
            if (id == null) return;
            if (_sentIdentity != null && _sentIdentity.SameAs(id)) return;
            _sentIdentity = id;
            SendIdentity(id.Name, id.PlayerId, id.Guild, id.Level);
        }

        /// <summary>
        /// 回報自己的身分。一般走 <see cref="PublishIdentity"/>(有去重);
        /// 這個多載留給「就是要強制送一次」的呼叫端。
        /// </summary>
        public void SendIdentity(string name, string playerId, string guild, int level)
            => Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetIdentity)
                .Str("name", name ?? "")
                .Str("playerId", playerId ?? "")
                .Str("guild", guild ?? "")
                .Int("level", level));

        public void SendChat(string text, string channel = "current", int expressionId = 0, string leading = null)
            => Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ChatSay)
                .Str("text", text ?? "")
                .Str("channel", channel ?? "current")
                .Int("expressionId", expressionId)
                .Str("leading", leading ?? ""));

        /// <summary>
        /// 密語。對方是誰由 server 照名字找(全服、跨房),所以本機不需要、也不可能自己驗證名字存不存在。
        /// 結果(送到了 / 找不到人)一律由 <see cref="WhisperReceived"/> 回來。
        /// </summary>
        public void SendWhisper(string target, string text, string channel = "current",
                                int expressionId = 0, string leading = null)
            => Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ChatWhisper)
                .Str("target", target ?? "")
                .Str("text", text ?? "")
                .Str("channel", channel ?? "current")
                .Int("expressionId", expressionId)
                .Str("leading", leading ?? ""));

        private void Send(JObj msg)
        {
            if (!_link.IsConnected) return;
            _link.Send(msg);
        }

        // ---- 事件觸發的小 helper(把 null 檢查與例外隔離集中在一處) ----

        private static void Raise<T>(Action<T> ev, T arg)
        {
            if (ev == null) return;
            try { ev(arg); }
            catch (Exception ex) { Debug.LogError("[net] 事件處理例外: " + ex); }
        }
    }

    /// <summary>握手時要送給 server 的本機身分。</summary>
    public struct NetHelloIdentity
    {
        public string PlayerId;
        public string Name;
        public string Guild;
        public int Level;
        public int Gender;
        public int BodyIndex;
        public string[] AvatarParts;
    }
}
