using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Sdo.Net;
using Sdo.Server;
using Sdo.Server.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// **真的開 socket** 的整合測試:啟動一個 Hub,用真的 TcpClient 走完整協定。
    ///
    /// 為什麼需要這一層(狀態機已經有 265 個單元測試了):那些測的是「規則對不對」,
    /// 這裡測的是「線接對了沒有」—— framing、握手、dispatch、廣播對象、
    /// actor loop 的 marshalling。那些接錯的話單元測試一個都不會紅,
    /// 但實際上兩個 client 永遠看不到彼此。
    ///
    /// port 用 0 讓 OS 配,所以這些測試可以並行也不會互搶 port。
    /// </summary>
    public class ServerIntegrationTests
    {
        private Hub _hub;
        private Task _hubTask;
        private string _dataDir;
        private readonly List<TestClient> _clients = new List<TestClient>();

        [SetUp]
        public void StartServer()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "sdo_srv_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dataDir);

            var opts = new ServerOptions
            {
                Port = 0,                 // 讓 OS 挑一個空閒 port
                Bind = "127.0.0.1",
                DataDir = _dataDir,
                CodeSeed = 4242,          // 固定種子 → 房號可重現
                Password = "",            // 這批測試不驗密碼(密碼有自己的測試,見下面)
            };
            string err;
            Assert.IsTrue(opts.Validate(out err), err);

            _hub = new Hub(opts);
            _hubTask = Task.Factory.StartNew(_hub.Run, TaskCreationOptions.LongRunning);

            // 等它真的開始監聽。
            var sw = Stopwatch.StartNew();
            while (!_hub.IsListening && sw.ElapsedMilliseconds < 5000) Thread.Sleep(5);
            Assert.IsTrue(_hub.IsListening, "server 沒有在 5 秒內開始監聽");
        }

        [TearDown]
        public void StopServer()
        {
            for (int i = 0; i < _clients.Count; i++) _clients[i].Dispose();
            _clients.Clear();

            if (_hub != null) _hub.Stop();
            if (_hubTask != null) { try { _hubTask.Wait(3000); } catch { } }
            try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true); } catch { }
        }

        /// <summary>連一個 client 上去並完成握手,回傳它的 userId。</summary>
        private TestClient Connect(string name)
        {
            var c = new TestClient(_hub.ActualPort);
            _clients.Add(c);

            c.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.Hello)
                .Int(NetProto.FieldRequest, 1)
                .Int("proto", NetProto.Version)
                .Str("role", NetProto.RoleControl)
                .Str("playerId", "00000000")
                .Str("name", name)
                .Int("level", 7));

            var welcome = c.WaitFor(NetProto.Welcome);
            Assert.IsNotNull(welcome, name + " 沒收到 welcome");
            c.UserId = NetJson.Int(welcome, "userId");
            c.SessionKey = NetJson.Str(welcome, "sessionKey");
            Assert.Greater(c.UserId, 0);
            return c;
        }

        // ================= 握手 =================

        [Test]
        public void Handshake_Assigns_A_User_Id_And_Session_Key()
        {
            var a = Connect("玩家A");
            Assert.Greater(a.UserId, 0);
            Assert.IsNotEmpty(a.SessionKey);
        }

        [Test]
        public void Each_Client_Gets_A_Distinct_User_Id()
        {
            var a = Connect("玩家A");
            var b = Connect("玩家B");
            Assert.AreNotEqual(a.UserId, b.UserId);
        }

        [Test]
        public void Wrong_Protocol_Version_Is_Rejected()
        {
            // 版本不合要明確擋掉 —— 讓它半殘地跑然後在某個角落出怪事更難查。
            var c = new TestClient(_hub.ActualPort);
            _clients.Add(c);
            c.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.Hello)
                .Int("proto", NetProto.Version + 999)
                .Str("name", "舊版"));

            var bye = c.WaitFor(NetProto.Bye);
            Assert.IsNotNull(bye, "應該收到 bye");
            Assert.AreEqual(NetProto.ErrProto, NetJson.Str(bye, "reason"));
        }

        [Test]
        public void Messages_Before_Hello_Are_Rejected()
        {
            var c = new TestClient(_hub.ActualPort);
            _clients.Add(c);
            c.Send(JObj.New().Str(NetProto.FieldType, NetProto.RoomList).Int(NetProto.FieldRequest, 1));

            var bye = c.WaitFor(NetProto.Bye);
            Assert.IsNotNull(bye, "握手之前只准 hello");
        }

        [Test]
        public void Server_Default_Password_Comes_From_The_Shared_Constant()
        {
            // 「兩邊都不改就能連上」是設計意圖。預設密碼放在共用的 NetLimits,
            // client(RoomConfig.DefaultServerPassword)與 server 都指向它 ——
            // 所以漂移在結構上就不可能發生。
            //
            // 這條測試釘住的是「有人把它改成硬編字串」那種退步:
            // 那之後兩邊就會各自漂移,而症狀(誰都連不進來)完全看不出根因。
            //
            // (client 端那一半在 Unity EditMode 測 —— RoomConfig 有 UnityEngine 依賴,server 編不到。)
            Assert.AreEqual(NetLimits.DefaultServerPassword, ServerOptions.DefaultPassword);
            Assert.IsNotEmpty(ServerOptions.DefaultPassword, "預設要有密碼(不是空密碼放行)");
        }

        [Test]
        public void Ping_Echoes_T0()
        {
            // client 用 echo 回來的 t0 算 RTT。
            var a = Connect("玩家A");
            a.Send(JObj.New().Str(NetProto.FieldType, NetProto.Ping).Num("t0", 1234.5));

            var pong = a.WaitFor(NetProto.Pong);
            Assert.IsNotNull(pong);
            Assert.AreEqual(1234.5, NetJson.Num(pong, "t0"), 1e-6);
        }

        [Test]
        public void Garbage_Json_Kills_The_Connection()
        {
            var a = Connect("玩家A");
            a.SendRaw(System.Text.Encoding.UTF8.GetBytes("{ this is not json"));

            var bye = a.WaitFor(NetProto.Bye);
            Assert.IsNotNull(bye, "壞 JSON 應該斷線而不是被當成空物件");
        }

        // ================= 建房 / 加房 =================

        [Test]
        public void Create_Room_Returns_A_Five_Digit_Code_And_A_Room_State()
        {
            var a = Connect("房主");
            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.CreateRoom)
                .Int(NetProto.FieldRequest, 10)
                .Str("name", "測試房"));

            var res = a.WaitFor(NetProto.JoinResult);
            Assert.IsNotNull(res);
            Assert.AreEqual(NetProto.JoinOk, NetJson.Str(res, "result"));

            int code = NetJson.Int(res, "code");
            Assert.GreaterOrEqual(code, 10000, "房號要是 5 位數");
            Assert.LessOrEqual(code, 99999);

            var state = a.WaitFor(NetProto.RoomState);
            Assert.IsNotNull(state, "建房之後要收到 roomState");

            var snap = NetRoomSnapshot.Decode(state);
            Assert.AreEqual(code, snap.Code);
            Assert.AreEqual("測試房", snap.Name);
            Assert.AreEqual(a.UserId, snap.HostUserId, "建房的人是房主");
            Assert.AreEqual(1, snap.SeatedCount);
            Assert.AreEqual("房主", snap.Seats[0].Name);
            Assert.AreEqual(6, snap.Seats.Length);
        }

        [Test]
        public void Second_Client_Joining_Is_Seen_By_Both()
        {
            // ★ 這是整個 M2 的核心驗證:兩個 client 真的看到彼此。
            var a = Connect("房主");
            int code = CreateRoom(a, "一起跳舞");

            var b = Connect("路人");
            b.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.JoinRoom)
                .Int(NetProto.FieldRequest, 20)
                .Int("code", code));

            var joinRes = b.WaitFor(NetProto.JoinResult);
            Assert.AreEqual(NetProto.JoinOk, NetJson.Str(joinRes, "result"));

            // 兩邊都要收到含兩個人的 roomState。
            var bState = WaitForState(b, s => s.SeatedCount == 2, "B 看到兩個人");
            var aState = WaitForState(a, s => s.SeatedCount == 2, "A 看到兩個人");

            Assert.AreEqual("房主", aState.Seats[0].Name);
            Assert.AreEqual("路人", aState.Seats[1].Name);
            Assert.AreEqual(a.UserId, aState.HostUserId);
            Assert.IsFalse(aState.IsHost(b.UserId), "後進來的不是房主");
        }

        [Test]
        public void Joining_An_Unknown_Code_Returns_NotFound()
        {
            var a = Connect("玩家A");
            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.JoinRoom)
                .Int(NetProto.FieldRequest, 30)
                .Int("code", 55555));

            var res = a.WaitFor(NetProto.JoinResult);
            Assert.AreEqual(NetProto.JoinNotFound, NetJson.Str(res, "result"));
        }

        [Test]
        public void Room_List_Shows_Open_Rooms()
        {
            var a = Connect("房主");
            int code = CreateRoom(a, "列表測試");

            var b = Connect("路人");
            b.Send(JObj.New().Str(NetProto.FieldType, NetProto.RoomList).Int(NetProto.FieldRequest, 40));

            var res = b.WaitFor(NetProto.RoomListResult);
            Assert.IsNotNull(res);
            var rooms = NetJson.Arr(res, "rooms");
            Assert.IsNotNull(rooms);
            Assert.AreEqual(1, rooms.Count);
            Assert.AreEqual(code, NetJson.Int(rooms[0], "code"));
            Assert.AreEqual("列表測試", NetJson.Str(rooms[0], "name"));
            Assert.AreEqual("房主", NetJson.Str(rooms[0], "hostName"));
            Assert.AreEqual(1, NetJson.Int(rooms[0], "count"));
        }

        // ================= 離開 / 房主轉移 =================

        [Test]
        public void Leaving_Is_Broadcast_To_The_Others()
        {
            var a = Connect("房主");
            int code = CreateRoom(a, "房");
            var b = Connect("路人");
            JoinRoom(b, code);
            WaitForState(a, s => s.SeatedCount == 2, "A 看到 B 加入");

            b.Send(JObj.New().Str(NetProto.FieldType, NetProto.LeaveRoom));

            var aState = WaitForState(a, s => s.SeatedCount == 1, "A 看到 B 走了");
            Assert.IsTrue(aState.Seats[1].IsOpen);
        }

        [Test]
        public void Host_Leaving_Transfers_The_Host_Role()
        {
            var a = Connect("房主");
            int code = CreateRoom(a, "房");
            var b = Connect("接班人");
            JoinRoom(b, code);

            a.Send(JObj.New().Str(NetProto.FieldType, NetProto.LeaveRoom));

            var bState = WaitForState(b, s => s.HostUserId == b.UserId, "B 成為新房主");
            Assert.AreEqual(1, bState.SeatedCount);
        }

        [Test]
        public void Disconnecting_Counts_As_Leaving()
        {
            // 斷線 == leaveRoom(R6)。直接關 socket,不送 leaveRoom。
            var a = Connect("房主");
            int code = CreateRoom(a, "房");
            var b = Connect("路人");
            JoinRoom(b, code);
            WaitForState(a, s => s.SeatedCount == 2, "A 看到 B 加入");

            b.Dispose();

            WaitForState(a, s => s.SeatedCount == 1, "斷線的人被移出房間", 5000);
        }

        // ================= host 權限 =================

        [Test]
        public void Non_Host_Setting_The_Song_Gets_NotHost()
        {
            // 🔴 client 隱藏按鈕只是 UX —— server 必須獨立擋。
            var a = Connect("房主");
            int code = CreateRoom(a, "房");
            var b = Connect("路人");
            JoinRoom(b, code);

            b.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetSong)
                .Int(NetProto.FieldRequest, 50)
                .Put("song", JObj.New().Bool("official", true).Str("gn", "sdom1435k.gn").Int("fileId", 11435)));

            var err = b.WaitFor(NetProto.Error);
            Assert.IsNotNull(err, "非房主選歌應該被拒");
            Assert.AreEqual(NetProto.ErrNotHost, NetJson.Str(err, "code"));
        }

        [Test]
        public void Host_Setting_The_Song_Is_Broadcast()
        {
            var a = Connect("房主");
            int code = CreateRoom(a, "房");
            var b = Connect("路人");
            JoinRoom(b, code);

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetSong)
                .Int(NetProto.FieldRequest, 51)
                .Put("song", JObj.New()
                    .Bool("official", true)
                    .Str("gn", "sdom1435k.gn")
                    .Int("fileId", 11435)
                    .Str("title", "測試歌")
                    .Str("artist", "測試曲師")));

            var bState = WaitForState(b, s => s.Song != null, "B 看到房主選的歌");
            Assert.IsNotNull(bState.Song, "B 應該看到房主選的歌");
            Assert.AreEqual("sdom1435k.gn", bState.Song.Gn);
            Assert.AreEqual("測試歌", bState.Song.Title);
            Assert.IsTrue(bState.Song.Official);
        }

        [Test]
        public void Host_Can_Kick_And_The_Target_Is_Told()
        {
            var a = Connect("房主");
            int code = CreateRoom(a, "房");
            var b = Connect("倒楣鬼");
            JoinRoom(b, code);
            WaitForState(a, s => s.SeatedCount == 2, "A 看到 B 加入");

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.KickUser)
                .Int(NetProto.FieldRequest, 60)
                .Int("userId", b.UserId));

            var kicked = b.WaitFor(NetProto.Kicked);
            Assert.IsNotNull(kicked, "被踢的人要收到通知");
            Assert.AreEqual(NetProto.KickedByHost, NetJson.Str(kicked, "reason"));

            WaitForState(a, s => s.SeatedCount == 1, "A 看到 B 被踢掉");
        }

        [Test]
        public void Closing_An_Occupied_Seat_Kicks_The_Player()
        {
            // 需求 12:「host 點大頭貼兩下可以鎖格子,如果原本那格有玩家就把他踢出去」。
            var a = Connect("房主");
            int code = CreateRoom(a, "房");
            var b = Connect("倒楣鬼");
            JoinRoom(b, code);
            WaitForState(a, s => s.SeatedCount == 2, "A 看到 B 加入");

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetSeatClosed)
                .Int(NetProto.FieldRequest, 70)
                .Int("seat", 1)
                .Bool("closed", true));

            var kicked = b.WaitFor(NetProto.Kicked);
            Assert.IsNotNull(kicked);
            Assert.AreEqual(NetProto.KickedSeatClosed, NetJson.Str(kicked, "reason"));

            var aState = WaitForState(a, s => s.Seats[1].IsClosed, "A 看到座位 1 被關閉");
            Assert.AreEqual(1, aState.SeatedCount);
        }

        [Test]
        public void Closed_Seat_Blocks_New_Joiners_From_That_Slot()
        {
            var a = Connect("房主");
            int code = CreateRoom(a, "房");

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetSeatClosed)
                .Int(NetProto.FieldRequest, 71).Int("seat", 1).Bool("closed", true));
            WaitForState(a, s => s.Seats[1].IsClosed, "座位 1 已關閉");

            var b = Connect("路人");
            JoinRoom(b, code);
            var bState = WaitForState(b, s => s.SeatIndexOf(b.UserId) >= 0, "B 坐下了");

            Assert.AreEqual(2, bState.SeatIndexOf(b.UserId), "應該跳過被關閉的座位 1");
        }

        // ================= 聊天 =================

        [Test]
        public void Chat_Is_Relayed_To_Everyone_In_The_Room()
        {
            var a = Connect("說話的人");
            int code = CreateRoom(a, "房");
            var b = Connect("聽的人");
            JoinRoom(b, code);
            WaitForState(a, s => s.SeatedCount == 2, "A 看到 B 加入");

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ChatSay)
                .Str("text", "大家好啊")
                .Str("channel", "current"));

            var msg = b.WaitFor(NetProto.ChatMsg);
            Assert.IsNotNull(msg);
            Assert.AreEqual("大家好啊", NetJson.Str(msg, "text"));
            Assert.AreEqual("說話的人", NetJson.Str(msg, "sender"));
            Assert.AreEqual(a.UserId, NetJson.Int(msg, "senderUserId"));
            Assert.AreEqual(code, NetJson.Int(msg, "roomId"));
        }

        [Test]
        public void Chat_Does_Not_Leak_To_Other_Rooms()
        {
            var a = Connect("A房的人");
            CreateRoom(a, "A房");
            var b = Connect("B房的人");
            CreateRoom(b, "B房");

            a.Send(JObj.New().Str(NetProto.FieldType, NetProto.ChatSay).Str("text", "秘密"));

            var leaked = b.WaitFor(NetProto.ChatMsg, 500);
            Assert.IsNull(leaked, "別房的人不該看到這句話");
        }

        // ================= 開場的完整流程 =================

        [Test]
        public void Two_Clients_Load_And_Start_Together()
        {
            // ★ M4 的核心語意,但線接對了現在就能驗:
            //   ready → requestStart → 兩邊都 loaded → 一起收到 gameplayStarted。
            var a = Connect("房主");
            int code = CreateRoom(a, "房");
            var b = Connect("玩家B");
            JoinRoom(b, code);
            WaitForState(a, s => s.SeatedCount == 2, "A 看到 B 加入");

            // 選歌
            a.Send(JObj.New().Str(NetProto.FieldType, NetProto.SetSong).Int(NetProto.FieldRequest, 80)
                .Put("song", JObj.New().Bool("official", true).Str("gn", "sdom1435k.gn").Int("fileId", 11435)));
            WaitForState(b, s => s.Song != null, "B 看到選了歌");

            // 兩邊都上報「有歌」
            SetAvailability(a, "sdom1435k.gn");
            SetAvailability(b, "sdom1435k.gn");
            WaitForState(a, s => s.SeatedCount == 2
                              && s.Seats[0].Avail == Availability.Have
                              && s.Seats[1].Avail == Availability.Have, "兩邊都上報有歌");

            // B 準備(房主不需要 —— 它按的是開始)
            b.Send(JObj.New().Str(NetProto.FieldType, NetProto.SetReady).Int(NetProto.FieldRequest, 81).Bool("ready", true));
            WaitForState(a, s => s.Seats[1].Ready, "A 看到 B 準備好了");
            b.DrainRoomStates();

            // 房主開始(它不需要準備)
            a.Send(JObj.New().Str(NetProto.FieldType, NetProto.RequestStart).Int(NetProto.FieldRequest, 82)
                .Bool("force", false)
                .Put("resolved", JObj.New().Int("sceneId", 9).Int("formationType", 0).Int("teamLayout", -1)));

            var aStart = a.WaitFor(NetProto.MatchStarting);
            var bStart = b.WaitFor(NetProto.MatchStarting);
            Assert.IsNotNull(aStart, "房主要收到 matchStarting");
            Assert.IsNotNull(bStart, "參與者要收到 matchStarting");

            long matchId = NetJson.Long(aStart, "matchId");
            Assert.Greater(matchId, 0);
            Assert.AreEqual(2, NetJson.Arr(aStart, "participants").Count);
            Assert.AreEqual(9, NetJson.Int(NetJson.Sub(aStart, "resolved"), "sceneId"));

            // 只有一邊載完 → 還不能開場
            a.Send(SetPlayStateMsg(matchId, "loaded"));
            Assert.IsNull(a.WaitFor(NetProto.GameplayStarted, 400), "還有人在載入,不該開場");

            // 兩邊都載完 → 一起開場
            b.Send(SetPlayStateMsg(matchId, "loaded"));
            Assert.IsNotNull(a.WaitFor(NetProto.GameplayStarted, 3000), "房主要收到 gameplayStarted");
            Assert.IsNotNull(b.WaitFor(NetProto.GameplayStarted, 3000), "B 也要收到");
        }

        [Test]
        public void Client_Cannot_Claim_A_Server_Reserved_State()
        {
            // 🔴 安全邊界:改過的 client 自稱 playing 想繞過載入同步。
            var a = Connect("房主");
            CreateRoom(a, "房");

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetPlayState)
                .Int(NetProto.FieldRequest, 90)
                .Str("state", "playing")
                .Long("matchId", 1));

            var err = a.WaitFor(NetProto.Error);
            Assert.IsNotNull(err);
            Assert.AreEqual(NetProto.ErrBadState, NetJson.Str(err, "code"));
        }

        // ---- helper ----

        /// <summary>
        /// 等到收到一份**滿足條件**的 roomState。
        ///
        /// 為什麼不直接 <c>WaitFor(RoomState)</c> 拿第一份:協定是非同步的,inbox 裡可能已經
        /// 堆了好幾份 snapshot(例如「B 加入」與「房主選歌」各廣播一次)。
        /// 「拿第 N 份」這種寫法會隨實作細節而壞掉 —— 只要哪個操作多 Touch() 一次,
        /// 測試就開始間歇性失敗,而且症狀跟真正的 bug 長得一模一樣。
        /// 用「等到狀態符合預期」則對廣播次數完全免疫。
        /// </summary>
        private static NetRoomSnapshot WaitForState(TestClient c, Func<NetRoomSnapshot, bool> until,
                                                    string what, int timeoutMs = 3000)
        {
            var sw = Stopwatch.StartNew();
            NetRoomSnapshot last = null;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                int remaining = (int)Math.Max(1, timeoutMs - sw.ElapsedMilliseconds);
                var node = c.WaitFor(NetProto.RoomState, remaining);
                if (node == null) break;
                last = NetRoomSnapshot.Decode(node);
                if (until(last)) return last;
            }
            Assert.Fail("等不到「" + what + "」的 roomState。" +
                        (last != null
                            ? "最後收到的是 rev=" + last.Rev + " 座位數=" + last.SeatedCount +
                              " host=" + last.HostUserId + " 歌=" + (last.Song != null ? last.Song.Gn : "(無)")
                            : "完全沒收到任何 roomState"));
            return null;
        }

        private int CreateRoom(TestClient c, string name)
        {
            c.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.CreateRoom)
                .Int(NetProto.FieldRequest, 1000)
                .Str("name", name));
            var res = c.WaitFor(NetProto.JoinResult);
            Assert.IsNotNull(res, "建房沒有回應");
            Assert.AreEqual(NetProto.JoinOk, NetJson.Str(res, "result"));
            int code = NetJson.Int(res, "code");
            c.WaitFor(NetProto.RoomState);   // 吃掉建房後的那份
            return code;
        }

        private void JoinRoom(TestClient c, int code)
        {
            c.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.JoinRoom)
                .Int(NetProto.FieldRequest, 1001)
                .Int("code", code));
            var res = c.WaitFor(NetProto.JoinResult);
            Assert.IsNotNull(res, "加入沒有回應");
            Assert.AreEqual(NetProto.JoinOk, NetJson.Str(res, "result"), "加入房 " + code + " 失敗");
        }

        private static void SetAvailability(TestClient c, string packId)
        {
            c.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetAvailability)
                .Str("packId", packId)
                .Str("state", "have")
                .Num("progress", 0));
        }

        private static JObj SetPlayStateMsg(long matchId, string state)
            => JObj.New()
                .Str(NetProto.FieldType, NetProto.SetPlayState)
                .Int(NetProto.FieldRequest, 1002)
                .Str("state", state)
                .Long("matchId", matchId);

        /// <summary>
        /// 測試用的極簡 client:framing + JSON + 「等某個型別的訊息」。
        ///
        /// 收到的訊息會先進 inbox,<see cref="WaitFor"/> 會掃 inbox 再繼續讀 ——
        /// 因為協定是非同步的,等 joinResult 的時候可能先收到 roomState。
        /// </summary>
        private sealed class TestClient : IDisposable
        {
            private readonly TcpClient _tcp;
            private readonly NetworkStream _stream;
            private readonly List<Envelope> _inbox = new List<Envelope>();

            public int UserId;
            public string SessionKey = "";

            public TestClient(int port)
            {
                _tcp = new TcpClient();
                _tcp.NoDelay = true;
                _tcp.Connect("127.0.0.1", port);
                _stream = _tcp.GetStream();
            }

            public void Send(JObj msg) => SendRaw(msg.Utf8());

            public void SendRaw(byte[] payload)
            {
                NetFrame.Write(_stream, NetLimits.FrameKindJson, payload);
                _stream.Flush();
            }

            /// <summary>等一個指定型別的訊息。收不到回 null(測試用它斷言「不該收到」)。</summary>
            public object WaitFor(string type, int timeoutMs = 3000)
            {
                for (int i = 0; i < _inbox.Count; i++)
                {
                    if (_inbox[i].Type != type) continue;
                    var node = _inbox[i].Node;
                    _inbox.RemoveAt(i);
                    return node;
                }

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    int remaining = (int)Math.Max(1, timeoutMs - sw.ElapsedMilliseconds);
                    _tcp.ReceiveTimeout = remaining;

                    byte kind;
                    byte[] payload;
                    FrameStatus st;
                    try { st = NetFrame.TryRead(_stream, out kind, out payload); }
                    catch (IOException) { return null; }              // ReceiveTimeout 到了
                    catch (ObjectDisposedException) { return null; }

                    if (st != FrameStatus.Ok) return null;
                    if (kind != NetLimits.FrameKindJson) continue;

                    object node;
                    string got;
                    if (!NetJson.TryParseMessage(payload, 0, payload.Length, out node, out got)) continue;

                    if (got == type) return node;
                    _inbox.Add(new Envelope(got, node));
                }
                return null;
            }

            /// <summary>把已經到達的 roomState 全部吃掉(不在意中間狀態時用)。</summary>
            public void DrainRoomStates()
            {
                while (WaitFor(NetProto.RoomState, 150) != null) { }
            }

            public void Dispose()
            {
                try { _tcp.Close(); } catch { }
            }

            private struct Envelope
            {
                public readonly string Type;
                public readonly object Node;
                public Envelope(string type, object node) { Type = type; Node = node; }
            }
        }
    }
}
