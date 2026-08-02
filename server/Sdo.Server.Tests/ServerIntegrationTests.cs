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
        public void Renaming_Before_Joining_Is_What_Everyone_Else_Sees()
        {
            // ★ 使用者實測回報的那個 bug:在大廳換成男角進房,別人看到的名字還是女角的。
            // 握手在**開機時**就做完了(那時 active profile 是女角),選性別 == 選帳號 ——
            // 所以進房前要補送 setIdentity,否則座位名字就是握手那份,而且之後永遠不會變。
            var a = Connect("舞蹈室主人");
            int code = CreateRoom(a, "舞蹈室");

            // (這裡兩個人的握手名字**必須不同** —— 同名的後來者現在在握手就被擋掉了,
            //  見 NameUniquenessTests。這條測的是「改名有沒有傳出去」,與同名無關。)
            var b = Connect("飄漂o");          // B 開機時的 active profile 是那隻女角
            b.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetIdentity)
                .Str("name", "按黑青眼暴龍壽3")
                .Str("playerId", "00000001")
                .Str("guild", "熱舞家族")
                .Int("level", 11));
            b.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.JoinRoom)
                .Int(NetProto.FieldRequest, 21)
                .Int("code", code));
            Assert.AreEqual(NetProto.JoinOk, NetJson.Str(b.WaitFor(NetProto.JoinResult), "result"));

            var aState = WaitForState(a, s => s.SeatedCount == 2, "A 看到 B 加入");
            Assert.AreEqual("按黑青眼暴龍壽3", aState.Seats[1].Name, "A 要看到 B 換過的名字,不是開機那份");
            Assert.AreEqual("熱舞家族", aState.Seats[1].Guild);
            Assert.AreEqual(11, aState.Seats[1].Level);
        }

        [Test]
        public void Renaming_Inside_A_Room_Is_Broadcast_To_The_Others()
        {
            // 進房後才換身分(例如被踢回選男女畫面又進來)也要即時反映在別人的畫面上。
            var a = Connect("房主");
            int code = CreateRoom(a, "舞蹈室");

            var b = Connect("舊名字");
            b.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.JoinRoom)
                .Int(NetProto.FieldRequest, 22)
                .Int("code", code));
            Assert.AreEqual(NetProto.JoinOk, NetJson.Str(b.WaitFor(NetProto.JoinResult), "result"));
            WaitForState(a, s => s.SeatedCount == 2, "A 看到 B 加入");

            b.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetIdentity)
                .Str("name", "新名字")
                .Int("level", 3));

            var aState = WaitForState(a, s => s.Seats[1].Name == "新名字", "A 看到 B 改名");
            Assert.AreEqual(3, aState.Seats[1].Level);
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

        [Test]
        public void User_List_Shows_Who_Is_Online_And_Where()
        {
            // 大廳玩家名單(全部/好友/家族三個分頁的資料來源)。server 只回事實:誰在線上、幾等、
            // 在大廳還是在某間房 —— 「誰是我的好友」是 client 拿本機清單去比對的(server 沒有帳號持久化)。
            var a = Connect("房主");
            int seq;
            {
                CreateRoom(a, "位置測試");
                a.Send(JObj.New().Str(NetProto.FieldType, NetProto.RoomList).Int(NetProto.FieldRequest, 70));
                var rooms = NetJson.Arr(a.WaitFor(NetProto.RoomListResult), "rooms");
                seq = NetJson.Int(rooms[0], "seq");
            }

            var b = Connect("路人");
            b.Send(JObj.New().Str(NetProto.FieldType, NetProto.UserList).Int(NetProto.FieldRequest, 71));

            var res = b.WaitFor(NetProto.UserListResult);
            Assert.IsNotNull(res);
            var users = NetJson.Arr(res, "users");
            Assert.IsNotNull(users);
            Assert.AreEqual(2, users.Count, "兩條連線都要在名單上(自己也算)");

            // 照 userId 排序 == 上線先後,所以房主一定在第 0 列。
            Assert.AreEqual("房主", NetJson.Str(users[0], "name"));
            Assert.AreEqual(seq, NetJson.Int(users[0], "roomSeq"), "在房裡的人要標出**門牌**(不是加入用的 code)");
            Assert.AreEqual("路人", NetJson.Str(users[1], "name"));
            // 🔴 大廳的哨兵值是 -1 不是 0 —— 門牌從 000 起算,0 是一間真的房。
            Assert.AreEqual(-1, NetJson.Int(users[1], "roomSeq"), "沒進房 = 人在大廳");
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

        // ---- 密語 ----
        //
        // 這幾條釘住的是實際踩到的 bug:client 端把密語轉給離線實作,而離線那份比的是寫死的假名冊,
        // 所以線上密語任何真人都回「找不到玩家」。修法是讓 server 照全服名冊找人 —— 因此下面的測試
        // 一定要包含「跨房也送得到」(chatSay 恰好相反,它不跨房)。

        [Test]
        public void Whisper_Reaches_Only_The_Target_And_Echoes_To_Sender()
        {
            var a = Connect("說密語的");
            int code = CreateRoom(a, "房");
            var b = Connect("收密語的");
            JoinRoom(b, code);
            var c = Connect("旁邊的人");
            JoinRoom(c, code);
            WaitForState(a, s => s.SeatedCount == 3, "三個人都在房裡");

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ChatWhisper)
                .Str("target", "收密語的")
                .Str("text", "只給你看")
                .Str("channel", "current"));

            var incoming = b.WaitFor(NetProto.WhisperMsg);
            Assert.IsNotNull(incoming, "目標沒收到密語");
            Assert.AreEqual(NetProto.WhisperIn, NetJson.Str(incoming, "kind"));
            Assert.AreEqual("說密語的", NetJson.Str(incoming, "party"), "party 要是發送者");
            Assert.AreEqual(a.UserId, NetJson.Int(incoming, "senderUserId"));
            Assert.AreEqual("只給你看", NetJson.Str(incoming, "text"));

            var echo = a.WaitFor(NetProto.WhisperMsg);
            Assert.IsNotNull(echo, "發送者要收到自己那行「你對X說」");
            Assert.AreEqual(NetProto.WhisperOut, NetJson.Str(echo, "kind"));
            Assert.AreEqual("收密語的", NetJson.Str(echo, "party"), "party 要是收件人");

            Assert.IsNull(c.WaitFor(NetProto.WhisperMsg, 500), "同房的第三人不該看到密語");
            Assert.IsNull(c.WaitFor(NetProto.ChatMsg, 300), "密語不該變成公開發言");
        }

        [Test]
        public void Whisper_Crosses_Rooms()
        {
            var a = Connect("A房的人");
            CreateRoom(a, "A房");
            var b = Connect("B房的人");
            CreateRoom(b, "B房");

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ChatWhisper)
                .Str("target", "B房的人")
                .Str("text", "跨房也找得到你"));

            var incoming = b.WaitFor(NetProto.WhisperMsg);
            Assert.IsNotNull(incoming, "密語要跨房送到(這正是它不能沿用 chatSay 的原因)");
            Assert.AreEqual(NetProto.WhisperIn, NetJson.Str(incoming, "kind"));
            Assert.AreEqual("跨房也找得到你", NetJson.Str(incoming, "text"));
        }

        [Test]
        public void Whisper_Is_Case_Insensitive_And_Echo_Uses_The_Canonical_Name()
        {
            var a = Connect("Sender");
            var b = Connect("TargetName");

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ChatWhisper)
                .Str("target", "targetNAME")
                .Str("text", "大小寫不該影響找人"));

            Assert.IsNotNull(b.WaitFor(NetProto.WhisperMsg), "名字比對要不分大小寫");

            var echo = a.WaitFor(NetProto.WhisperMsg);
            Assert.IsNotNull(echo);
            Assert.AreEqual("TargetName", NetJson.Str(echo, "party"),
                "自己那行要顯示 server 認定的正規名字,不是玩家打的大小寫");
        }

        [Test]
        public void Whisper_To_Unknown_Name_Reports_NoId_To_Sender_Only()
        {
            var a = Connect("找人的");
            var b = Connect("無關的人");

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ChatWhisper)
                .Str("target", "不存在的人")
                .Str("text", "有人在嗎"));

            var reply = a.WaitFor(NetProto.WhisperMsg);
            Assert.IsNotNull(reply, "找不到人也要回一則,否則玩家不知道沒送出去");
            Assert.AreEqual(NetProto.WhisperNoId, NetJson.Str(reply, "kind"));
            Assert.AreEqual("不存在的人", NetJson.Str(reply, "party"),
                "要回玩家原本打的那串字,他才知道自己打錯了什麼");
            Assert.IsNull(b.WaitFor(NetProto.WhisperMsg, 400), "別人不該看到這則失敗提示");
        }

        [Test]
        public void Whisper_Without_Target_Or_Body_Is_Dropped()
        {
            var a = Connect("亂送的");

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ChatWhisper)
                .Str("target", "   ").Str("text", "沒填對象"));
            Assert.IsNull(a.WaitFor(NetProto.WhisperMsg, 400),
                "空對象不該被當成去找一個叫「玩家」的人(SanitizeName 會那樣補)");

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ChatWhisper)
                .Str("target", "亂送的").Str("text", ""));
            Assert.IsNull(a.WaitFor(NetProto.WhisperMsg, 400), "只選了對象還沒打內容 → 不送");
        }

        [Test]
        public void Whisper_Carries_Expression_Fields()
        {
            var a = Connect("表情密語");
            var b = Connect("看表情的");

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ChatWhisper)
                .Str("target", "看表情的")
                .Str("text", "後面的字")
                .Int("expressionId", 3)
                .Str("leading", "前面的字"));

            var incoming = b.WaitFor(NetProto.WhisperMsg);
            Assert.IsNotNull(incoming);
            Assert.AreEqual(3, NetJson.Int(incoming, "expressionId"));
            Assert.AreEqual("前面的字", NetJson.Str(incoming, "leadingText"));
            Assert.AreEqual("後面的字", NetJson.Str(incoming, "text"));
        }

        [Test]
        public void Moves_Are_Room_Versioned_And_Old_Slots_Are_Ignored_After_Spectate()
        {
            var host = Connect("房主");
            int code = CreateRoom(host, "走動房");

            host.Send(JObj.New().Str(NetProto.FieldType, NetProto.SetRoomName)
                .Int(NetProto.FieldRequest, 2199).Str("name", "走動房-同步"));
            var initialState = WaitForState(host,
                s => s.Code == code && s.Name == "走動房-同步", "取得目前房間 revision");

            // 先讓 server 留下一筆穩定位置；等自己收到代表 dirty round 已經 flush 完。
            host.Send(RoomMoveMsg(code, initialState.Rev, 0, 1.25, 2.5));
            var initial = host.WaitFor(NetProto.Moves);
            Assert.IsNotNull(initial);
            Assert.AreEqual(code, NetJson.Int(initial, "roomCode"));
            Assert.AreEqual(initialState.Rev, NetJson.Int(initial, "roomRev"));

            var guest = Connect("切旁觀的人");
            JoinRoom(guest, code);
            var joined = WaitForState(host, s => s.SeatIndexOf(guest.UserId) == 1, "客人坐在座位 1");
            var guestJoined = WaitForState(guest, s => s.SeatIndexOf(guest.UserId) == 1, "客人收到座位狀態");

            // 後加入者拿到的 SendMoveSnapshot 也必須帶同一房間與 revision。
            var snapshot = guest.WaitFor(NetProto.Moves);
            Assert.IsNotNull(snapshot);
            Assert.AreEqual(code, NetJson.Int(snapshot, "roomCode"));
            Assert.AreEqual(guestJoined.Rev, NetJson.Int(snapshot, "roomRev"));
            Assert.AreEqual(1.25, NetJson.Num(MoveOf(snapshot, host.UserId), "x"), 0.001);

            // 一般 live push 同樣帶 metadata。
            guest.Send(RoomMoveMsg(code + 1, guestJoined.Rev, 1, 8, 8));
            guest.Send(RoomMoveMsg(code, guestJoined.Rev - 1, 1, 9, 9));
            Assert.IsNull(host.WaitFor(NetProto.Moves, 500), "錯房號或舊 revision 的 move 必須被丟棄");
            guest.Send(RoomMoveMsg(code, guestJoined.Rev, 1, 10, 20));
            var live = host.WaitFor(NetProto.Moves);
            Assert.IsNotNull(live);
            Assert.AreEqual(code, NetJson.Int(live, "roomCode"));
            Assert.AreEqual(joined.Rev, NetJson.Int(live, "roomRev"));
            Assert.AreEqual(10, NetJson.Num(MoveOf(live, guest.UserId), "x"), 0.001);

            guest.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.Spectate)
                .Int(NetProto.FieldRequest, 2201)
                .Int("code", 0));
            var spectating = WaitForState(host,
                s => s.SpectatorIndexOf(guest.UserId) >= 0,
                "客人切成旁觀");
            int spectatorSlot = 1000 + spectating.SpectatorIndexOf(guest.UserId);

            // roomState 已經顯示旁觀後，延遲抵達的舊 seat=1 move 不可重建舊座標。
            guest.Send(RoomMoveMsg(code, spectating.Rev, 1, 99, 99));
            Assert.IsNull(host.WaitFor(NetProto.Moves, 500));

            guest.Send(RoomMoveMsg(code, spectating.Rev, spectatorSlot, 30, 40));
            var spectatorMove = host.WaitFor(NetProto.Moves);
            Assert.IsNotNull(spectatorMove);
            Assert.AreEqual(code, NetJson.Int(spectatorMove, "roomCode"));
            Assert.AreEqual(spectating.Rev, NetJson.Int(spectatorMove, "roomRev"));
            Assert.AreEqual(30, NetJson.Num(MoveOf(spectatorMove, guest.UserId), "x"), 0.001);
        }

        [Test]
        public void Leave_Rejoin_Drops_Stored_Move_And_Rejects_Stale_Same_Slot_Revision()
        {
            var host = Connect("房主");
            int code = CreateRoom(host, "離房重進移動");
            var guest = Connect("離房重進的人");
            JoinRoom(guest, code);
            WaitForState(host, s => s.SeatIndexOf(guest.UserId) == 1, "guest first joined");
            var guestJoined = WaitForState(guest, s => s.SeatIndexOf(guest.UserId) == 1, "guest first state");

            guest.Send(RoomMoveMsg(code, guestJoined.Rev, 1, 10, 20));
            var live = host.WaitFor(NetProto.Moves);
            Assert.IsNotNull(live);
            Assert.AreEqual(10, NetJson.Num(MoveOf(live, guest.UserId), "x"), 0.001);
            Assert.IsNotNull(guest.WaitFor(NetProto.Moves), "先吃掉 sender 自己收到的 live echo");

            guest.Send(JObj.New().Str(NetProto.FieldType, NetProto.LeaveRoom));
            var left = WaitForState(host,
                s => s.SeatIndexOf(guest.UserId) < 0 && s.SeatedCount == 1,
                "guest left");

            JoinRoom(guest, code);
            var rejoined = WaitForState(guest,
                s => s.SeatIndexOf(guest.UserId) == 1 && s.Rev > left.Rev,
                "guest rejoined the same seat");

            Assert.IsNull(guest.WaitFor(NetProto.Moves, 500),
                "離房時必須清掉舊位置，重進不可收到自己的舊 snapshot");
            guest.Send(RoomMoveMsg(code, guestJoined.Rev, 1, 99, 99));
            Assert.IsNull(host.WaitFor(NetProto.Moves, 500),
                "同一 seat 的延遲封包也必須靠舊 revision 被拒絕");

            guest.Send(RoomMoveMsg(code, rejoined.Rev, 1, 30, 40));
            var current = host.WaitFor(NetProto.Moves);
            Assert.IsNotNull(current);
            Assert.AreEqual(30, NetJson.Num(MoveOf(current, guest.UserId), "x"), 0.001);
        }

        [Test]
        public void Spectator_Index_Shift_Clears_Old_Room_Moves()
        {
            var host = Connect("房主");
            int code = CreateRoom(host, "旁觀索引移動");
            var first = Connect("旁觀 A");
            var second = Connect("旁觀 B");

            first.Send(JObj.New().Str(NetProto.FieldType, NetProto.Spectate)
                .Int(NetProto.FieldRequest, 2210).Int("code", code));
            second.Send(JObj.New().Str(NetProto.FieldType, NetProto.Spectate)
                .Int(NetProto.FieldRequest, 2211).Int("code", code));

            var both = WaitForState(host,
                s => s.SpectatorIndexOf(first.UserId) == 0
                    && s.SpectatorIndexOf(second.UserId) == 1,
                "two spectators joined");
            WaitForState(first, s => s.SpectatorIndexOf(second.UserId) == 1, "first sees second");
            var secondState = WaitForState(second,
                s => s.SpectatorIndexOf(second.UserId) == 1, "second sees slot 1001");

            first.Send(RoomMoveMsg(code, both.Rev, 1000, 10, 10));
            Assert.IsNotNull(host.WaitFor(NetProto.Moves));
            second.Send(RoomMoveMsg(code, secondState.Rev, 1001, 20, 20));
            var secondMove = host.WaitFor(NetProto.Moves);
            Assert.IsNotNull(secondMove);
            Assert.AreEqual(20, NetJson.Num(MoveOf(secondMove, second.UserId), "x"), 0.001);

            first.Send(JObj.New().Str(NetProto.FieldType, NetProto.LeaveRoom));
            WaitForState(host,
                s => s.SpectatorIndexOf(first.UserId) < 0
                    && s.SpectatorIndexOf(second.UserId) == 0,
                "second spectator shifted from 1001 to 1000");

            var observer = Connect("後加入者");
            JoinRoom(observer, code);
            var observerJoined = WaitForState(observer,
                s => s.SeatIndexOf(observer.UserId) == 1
                    && s.SpectatorIndexOf(second.UserId) == 0,
                "observer joined after spectator shift");
            Assert.IsNull(observer.WaitFor(NetProto.Moves, 500),
                "slot 1001 的舊 B 座標不可被新 revision 包裝成 snapshot");

            second.Send(RoomMoveMsg(code, secondState.Rev, 1001, 99, 99));
            Assert.IsNull(host.WaitFor(NetProto.Moves, 500),
                "旁觀 index 壓縮前的封包必須被丟棄");

            second.Send(RoomMoveMsg(code, observerJoined.Rev, 1000, 30, 30));
            var current = host.WaitFor(NetProto.Moves);
            Assert.IsNotNull(current);
            Assert.AreEqual(30, NetJson.Num(MoveOf(current, second.UserId), "x"), 0.001);
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
        public void Final_Score_Survives_A_Frame_Flush_Before_The_Last_Player_Finishes()
        {
            TestClient a, b;
            long matchId = StartTwoPlayerMatch(out a, out b);
            const long aScore = 123456;
            const long bScore = 654321;

            a.Send(PlayFinishedMsg(matchId, aScore, 90));

            // 收到這筆表示 server 已執行 200ms frame flush。舊實作也會在這裡
            // Clear 掉同一份 dictionary,使先完成的 A 在稍後結算時變成 0 分。
            var flushed = b.WaitFor(NetProto.Frames, 3000);
            Assert.IsNotNull(flushed, "B 應收到 A 的 final frame");
            Assert.AreEqual(aScore, ScoreOf(flushed, "f", a.UserId), "確認 A 的 final 已經歷一次 frame flush");

            b.Send(PlayFinishedMsg(matchId, bScore, 120));

            var results = b.WaitFor(NetProto.ResultsReady, 3000);
            Assert.IsNotNull(results, "兩人完成後應收到 resultsReady");
            Assert.AreEqual(aScore, ScoreOf(results, "rows", a.UserId), "先完成者的 final score 不可被 frame flush 清掉");
            Assert.AreEqual(bScore, ScoreOf(results, "rows", b.UserId));
        }

        [Test]
        public void Kicked_Participant_Is_Pruned_From_Production_Result_Rows()
        {
            TestClient host, kicked;
            long matchId = StartTwoPlayerMatch(out host, out kicked);

            host.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.KickUser)
                .Int(NetProto.FieldRequest, 2220)
                .Int("userId", kicked.UserId));

            var kickedMsg = kicked.WaitFor(NetProto.Kicked);
            Assert.IsNotNull(kickedMsg);
            Assert.AreEqual(NetProto.KickedByHost, NetJson.Str(kickedMsg, "reason"));
            WaitForState(host,
                s => s.Status == RoomStatus.Playing && s.SeatedCount == 1,
                "kicked participant removed from the active room");

            host.Send(PlayFinishedMsg(matchId, 456789, 123));
            var results = host.WaitFor(NetProto.ResultsReady, 3000);
            Assert.IsNotNull(results);
            var rows = NetJson.Arr(results, "rows");
            Assert.AreEqual(1, rows.Count, "明確踢人不可保留 frozen result participant");
            Assert.AreEqual(host.UserId, NetJson.Int(rows[0], "userId"));
        }

        [Test]
        public void Disconnected_Leader_Is_Replaced_But_Keeps_Last_Result_Frame()
        {
            TestClient a, b;
            long matchId = StartTwoPlayerMatch(out a, out b);
            const long disconnectedScore = 345678;

            // 要送兩筆:leader 是在「全場最新歌曲時間 − 500ms」那個時刻取樣比出來的,只送一筆的話
            // 取樣點還在那一筆之前,誰都還沒有分數(見 LiveLeaderTracker)。
            b.Send(GameplayFrameMsg(matchId, 0, disconnectedScore));
            var relayed = SendFrameAndWait(b, a, matchId, 1600, disconnectedScore);
            Assert.AreEqual(disconnectedScore, ScoreOf(relayed, "f", b.UserId));
            Assert.AreEqual(b.UserId, NetJson.Int(relayed, "leaderUserId"));

            b.Dispose();
            WaitForState(a, s => s.Status == RoomStatus.Playing && s.SeatedCount == 1,
                "disconnected player removed", 5000);

            a.Send(GameplayFrameMsg(matchId, 100));
            var afterDisconnect = a.WaitFor(NetProto.Frames, 3000);
            Assert.IsNotNull(afterDisconnect);
            Assert.AreEqual(a.UserId, NetJson.Int(afterDisconnect, "leaderUserId"));

            a.Send(PlayFinishedMsg(matchId, 111111, 100));
            var results = a.WaitFor(NetProto.ResultsReady, 3000);
            Assert.IsNotNull(results);
            Assert.AreEqual(disconnectedScore, ScoreOf(results, "rows", b.UserId));
            Assert.IsTrue(NetJson.Bool(ResultRowOf(results, b.UserId), "disconnected"));
        }

        [Test]
        public void Only_The_First_Legal_Playing_Final_Is_Accepted()
        {
            TestClient a, b;
            long matchId = StartTwoPlayerMatch(out a, out b, startGameplay: false);

            a.Send(PlayFinishedMsg(matchId, 900000, 900));
            a.Send(SetPlayStateMsg(matchId, "loaded"));
            b.Send(SetPlayStateMsg(matchId, "loaded"));
            Assert.IsNotNull(a.WaitFor(NetProto.GameplayStarted, 3000));
            Assert.IsNotNull(b.WaitFor(NetProto.GameplayStarted, 3000));

            const long legalScore = 123456;
            a.Send(PlayFinishedMsg(matchId, legalScore, 90));
            a.Send(PlayFinishedMsg(matchId, 888888, 888));
            b.Send(PlayFinishedMsg(matchId, 222222, 120));

            var results = b.WaitFor(NetProto.ResultsReady, 3000);
            Assert.IsNotNull(results);
            Assert.AreEqual(legalScore, ScoreOf(results, "rows", a.UserId));
            Assert.AreEqual(222222, ScoreOf(results, "rows", b.UserId));
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

        // ================= 遊玩事件 / 結算 =================
        [Test]
        public void Combo_Milestone_Is_Reliably_Relayed_To_Other_Players()
        {
            TestClient a, b;
            long matchId = StartTwoPlayerMatch(out a, out b);

            var playing = WaitForState(a, s => s.Status == RoomStatus.Playing, "比賽已開始");
            int roomCode = playing.Code;
            var outsider = Connect("別房玩家");
            CreateRoom(outsider, "別房");

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ComboMilestone)
                .Long("matchId", matchId)
                .Int("combo", 100));

            var milestone = b.WaitFor(NetProto.ComboMilestone, 3000);
            Assert.IsNotNull(milestone);
            Assert.AreEqual(matchId, NetJson.Long(milestone, "matchId"));
            Assert.AreEqual(a.UserId, NetJson.Int(milestone, "userId"));
            Assert.AreEqual(100, NetJson.Int(milestone, "combo"));

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ComboMilestone)
                .Long("matchId", matchId)
                .Int("combo", 100));
            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ComboMilestone)
                .Long("matchId", matchId)
                .Int("combo", 50));
            Assert.IsNull(b.WaitFor(NetProto.ComboMilestone, 300));

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ComboMilestone)
                .Long("matchId", matchId)
                .Int("combo", 150));
            milestone = b.WaitFor(NetProto.ComboMilestone, 3000);
            Assert.IsNotNull(milestone);
            Assert.AreEqual(150, NetJson.Int(milestone, "combo"));

            Assert.IsNull(a.WaitFor(NetProto.ComboMilestone, 300), "sender 已在本機播放，不可 echo");
            Assert.IsNull(outsider.WaitFor(NetProto.ComboMilestone, 300), "事件不可洩漏到別房");

            // Non-boundaries are malformed and must not create arbitrary remote effects.
            b.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ComboMilestone)
                .Long("matchId", matchId)
                .Int("combo", 75));

            Assert.IsNull(a.WaitFor(NetProto.ComboMilestone, 300));

            var spectator = Connect("未參與者");
            spectator.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.Spectate)
                .Int(NetProto.FieldRequest, 2100)
                .Int("code", roomCode));
            WaitForState(spectator, s => s.SpectatorIndexOf(spectator.UserId) >= 0,
                "未參與者進入同房旁觀");

            spectator.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ComboMilestone)
                .Long("matchId", matchId)
                .Int("combo", 50));
            Assert.IsNull(a.WaitFor(NetProto.ComboMilestone, 300), "非參與者不可偽造 combo 特效");
            Assert.IsNull(b.WaitFor(NetProto.ComboMilestone, 300), "非參與者不可偽造 combo 特效");

            a.Send(PlayFinishedMsg(matchId, 1000, 150));
            b.Send(PlayFinishedMsg(matchId, 900, 100));
            Assert.IsNotNull(b.WaitFor(NetProto.ResultsReady, 3000));

            long nextMatchId = StartNextTwoPlayerMatch(a, b);
            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ComboMilestone)
                .Long("matchId", nextMatchId)
                .Int("combo", 50));
            var nextMilestone = b.WaitFor(NetProto.ComboMilestone, 3000);
            Assert.IsNotNull(nextMilestone);
            Assert.AreEqual(50, NetJson.Int(nextMilestone, "combo"));
        }

        /// <summary>
        /// 端到端釘住 frames 的 <c>leaderUserId</c>:它是「所有人在同一個歌曲時刻的分數」比出來的,
        /// 再加上換人節流 —— 不是比最後收到的那筆(見 <see cref="LiveLeaderTracker"/> 的說明)。
        ///
        /// 取樣點 = 全場最新歌曲時間 − 500ms;換人節流 1000ms(歌曲時間)。序列裡每個 tMs 兩人各送一筆。
        /// </summary>
        [Test]
        public void Frames_Carry_A_Time_Aligned_And_Throttled_Authoritative_Leader()
        {
            TestClient a, b;
            long matchId = StartTwoPlayerMatch(out a, out b);   // a = 房主 = seat 0 → 開場的 leader

            // 開場:取樣點還在 0 之前,誰都沒有「那個時刻」的分數 → leader 停在最低座位。
            SendFrameAndWait(a, b, matchId, 0, 0);
            var frames = SendFrameAndWait(b, a, matchId, 0, 0);
            Assert.AreEqual(a.UserId, NetJson.Int(frames, "leaderUserId"));

            // b 在歌曲時間 1000 爆到 60000。取樣點是 500,那時候兩人都還是 0 → 不換。
            // 舊版的 300 分門檻在這裡完全無效(差距 60000),會直接換人 —— 那就是震盪的來源。
            SendFrameAndWait(a, b, matchId, 1000, 0);
            frames = SendFrameAndWait(b, a, matchId, 1000, 60000);
            Assert.AreEqual(a.UserId, NetJson.Int(frames, "leaderUserId"),
                "60000 分是歌曲時間 1000 的資料,取樣點還在 500 → 不算數");

            // 時間再走 600ms,取樣點 1100 看得到那 60000 —— 這才是真的超車。
            SendFrameAndWait(a, b, matchId, 1600, 0);
            frames = SendFrameAndWait(b, a, matchId, 1600, 60000);
            Assert.AreEqual(b.UserId, NetJson.Int(frames, "leaderUserId"));

            // a 在 1900 反超 939999 分,但取樣點 1400 還看不到。
            SendFrameAndWait(a, b, matchId, 1900, 999999);
            frames = SendFrameAndWait(b, a, matchId, 1900, 60000);
            Assert.AreEqual(b.UserId, NetJson.Int(frames, "leaderUserId"));

            // 取樣點推進到 1900,已經看得到 a 領先 939999 分 —— 但距離上次換位只有 800ms。
            SendFrameAndWait(a, b, matchId, 2400, 999999);
            frames = SendFrameAndWait(b, a, matchId, 2400, 60000);
            Assert.AreEqual(b.UserId, NetJson.Int(frames, "leaderUserId"),
                "節流是頻率上限,分數差多少都一樣擋 —— 這正是固定門檻做不到的事");

            // 距離上次換位滿 1 秒 → 放行。
            SendFrameAndWait(a, b, matchId, 2700, 999999);
            frames = SendFrameAndWait(b, a, matchId, 2700, 60000);
            Assert.AreEqual(a.UserId, NetJson.Int(frames, "leaderUserId"));

            a.Send(PlayFinishedMsg(matchId, 999999, 90));
            b.Send(PlayFinishedMsg(matchId, 60000, 60));
            Assert.IsNotNull(b.WaitFor(NetProto.ResultsReady, 3000));

            // 新的一場:tracker 重建,不帶上一場的分數也不繼承上一場的 leader。
            long nextMatchId = StartNextTwoPlayerMatch(a, b);
            SendFrameAndWait(a, b, nextMatchId, 0, 0);
            frames = SendFrameAndWait(b, a, nextMatchId, 0, 999999);
            Assert.AreEqual(a.UserId, NetJson.Int(frames, "leaderUserId"));

            // 🔴 上面那一條光靠「開場 leader = 最低座位」就會過,沿用上一場 tracker 的實作也會過 ——
            // 所以要再往前推一輪才真的鑑別得出來。沿用的話:第二場所有 tMs 都 ≤ 上一場最後一筆
            // (2700),Record 一路走「覆蓋最後一筆」→ 序列時間軸永不前進 → tRef 恆等於殘留的
            // _lastSwitchTMs → 節流永久擋住,領隊格**整首歌**卡在上一場的結果。
            SendFrameAndWait(a, b, nextMatchId, 1600, 0);
            frames = SendFrameAndWait(b, a, nextMatchId, 1600, 50000);
            Assert.AreEqual(b.UserId, NetJson.Int(frames, "leaderUserId"),
                "第二場的取樣點與節流時限都要從零開始");
        }


        [Test]
        public void Finished_Host_Can_Spectate_And_Remains_In_Frozen_Results()
        {
            TestClient host, other;
            long matchId = StartTwoPlayerMatch(out host, out other);
            const long hostScore = 222222;
            const long otherScore = 333333;

            host.Send(PlayFinishedMsg(matchId, hostScore, 100));
            WaitForState(other,
                state =>
                {
                    var seat = state.SeatOf(host.UserId);
                    return seat != null && seat.PlayState == PlayState.Finished;
                },
                "host reached finished state");

            host.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.Spectate)
                .Int(NetProto.FieldRequest, 2001)
                .Int("code", 0));
            WaitForState(host, state => state.SpectatorIndexOf(host.UserId) >= 0,
                "finished host moved to spectator");

            host.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetIdentity)
                .Str("name", "賽後改名")
                .Str("guild", "新家族")
                .Int("level", 99));
            host.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetLook)
                .Put("look", JObj.New()
                    .Int("gender", 1)
                    .Int("bodyIndex", 4)
                    .Put("parts", JArr.New().Add("AFTER"))));
            WaitForState(other, state =>
                {
                    int index = state.SpectatorIndexOf(host.UserId);
                    if (index < 0) return false;
                    var live = state.Spectators[index];
                    return live.Name == "賽後改名" && live.Level == 99
                        && live.Look.Gender == 1 && live.Look.BodyIndex == 4;
                },
                "結算前 live 旁觀資料確實已變更");

            other.Send(PlayFinishedMsg(matchId, otherScore, 120));
            var results = host.WaitFor(NetProto.ResultsReady, 3000);
            Assert.IsNotNull(results);
            var rows = NetJson.Arr(results, "rows");
            Assert.AreEqual(2, rows.Count, "leaving the live seat table must not remove a match participant");
            Assert.AreEqual(other.UserId, NetJson.Int(rows[0], "userId"), "server results must be score sorted");
            Assert.AreEqual(hostScore, ScoreOf(results, "rows", host.UserId));
            Assert.AreEqual(otherScore, ScoreOf(results, "rows", other.UserId));

            var hostRow = ResultRowOf(results, host.UserId);
            Assert.AreEqual("先完成", NetJson.Str(hostRow, "name"), "名字要使用開場時凍結值");
            Assert.AreEqual(0, NetJson.Int(hostRow, "seat"), "座位要使用開場時凍結值");
            Assert.AreEqual(7, NetJson.Int(hostRow, "level"));
            Assert.AreEqual((int)TeamTag.Free, NetJson.Int(hostRow, "team"));
            var hostLook = NetJson.Sub(hostRow, "look");
            Assert.AreEqual(0, NetJson.Int(hostLook, "gender"), "外觀不可被賽後 live 更新覆蓋");
            Assert.AreEqual(0, NetJson.Int(hostLook, "bodyIndex"));
            Assert.AreEqual(0, NetJson.Arr(hostLook, "parts").Count);

            var otherRow = ResultRowOf(results, other.UserId);
            Assert.AreEqual(1, NetJson.Int(otherRow, "seat"));
            Assert.AreEqual("後完成", NetJson.Str(otherRow, "name"));
        }

        [Test]
        public void Gameplay_Frames_From_Waiting_Or_Nonparticipants_Are_Ignored()
        {
            TestClient a, b;
            long matchId = StartTwoPlayerMatch(out a, out b, startGameplay: false);

            b.Send(GameplayFrameMsg(matchId, 999));
            Assert.IsNull(a.WaitFor(NetProto.Frames, 500));

            a.Send(SetPlayStateMsg(matchId, "loaded"));
            b.Send(SetPlayStateMsg(matchId, "loaded"));
            Assert.IsNotNull(a.WaitFor(NetProto.GameplayStarted, 3000));
            Assert.IsNotNull(b.WaitFor(NetProto.GameplayStarted, 3000));

            var playing = WaitForState(a, s => s.Status == RoomStatus.Playing, "gameplay started");
            var spectator = Connect("frame spectator");
            spectator.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.Spectate)
                .Int(NetProto.FieldRequest, 2200)
                .Int("code", playing.Code));
            WaitForState(spectator,
                s => s.SpectatorIndexOf(spectator.UserId) >= 0,
                "spectator joined");

            spectator.Send(GameplayFrameMsg(matchId, 999999));
            Assert.IsNull(a.WaitFor(NetProto.Frames, 500));
            Assert.IsNull(b.WaitFor(NetProto.Frames, 500));
        }

        // ---- helper ----
        private long StartTwoPlayerMatch(out TestClient a, out TestClient b, bool startGameplay = true)
        {
            a = Connect("先完成");
            int code = CreateRoom(a, "成績房");
            b = Connect("後完成");
            JoinRoom(b, code);
            WaitForState(a, s => s.SeatedCount == 2, "A 看到 B 加入");

            a.Send(JObj.New().Str(NetProto.FieldType, NetProto.SetSong).Int(NetProto.FieldRequest, 83)
                .Put("song", JObj.New().Bool("official", true).Str("gn", "sdom1435k.gn").Int("fileId", 11435)));
            WaitForState(b, s => s.Song != null, "B 看到選了歌");

            SetAvailability(a, "sdom1435k.gn");
            SetAvailability(b, "sdom1435k.gn");
            WaitForState(a, s => s.SeatedCount == 2
                              && s.Seats[0].Avail == Availability.Have
                              && s.Seats[1].Avail == Availability.Have, "兩邊都上報有歌");

            b.Send(JObj.New().Str(NetProto.FieldType, NetProto.SetReady).Int(NetProto.FieldRequest, 84).Bool("ready", true));
            WaitForState(a, s => s.Seats[1].Ready, "A 看到 B 準備好了");

            a.Send(JObj.New().Str(NetProto.FieldType, NetProto.RequestStart).Int(NetProto.FieldRequest, 85)
                .Bool("force", false)
                .Put("resolved", JObj.New().Int("sceneId", 9).Int("formationType", 0).Int("teamLayout", -1)));

            var aStart = a.WaitFor(NetProto.MatchStarting);
            var bStart = b.WaitFor(NetProto.MatchStarting);
            Assert.IsNotNull(aStart);
            Assert.IsNotNull(bStart);
            long matchId = NetJson.Long(aStart, "matchId");

            if (startGameplay)
            {
                a.Send(SetPlayStateMsg(matchId, "loaded"));
                b.Send(SetPlayStateMsg(matchId, "loaded"));
                Assert.IsNotNull(a.WaitFor(NetProto.GameplayStarted, 3000));
                Assert.IsNotNull(b.WaitFor(NetProto.GameplayStarted, 3000));
            }
            return matchId;
        }

        private long StartNextTwoPlayerMatch(TestClient a, TestClient b)
        {
            WaitForState(a,
                s => s.Status == RoomStatus.Open && s.SeatOf(b.UserId) != null,
                "room reopened");

            b.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetReady)
                .Int(NetProto.FieldRequest, 86)
                .Bool("ready", true));
            WaitForState(a, s => s.SeatOf(b.UserId).Ready, "B ready");

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.RequestStart)
                .Int(NetProto.FieldRequest, 87)
                .Bool("force", false)
                .Put("resolved", JObj.New().Int("sceneId", 9).Int("formationType", 0).Int("teamLayout", -1)));

            var aStart = a.WaitFor(NetProto.MatchStarting);
            var bStart = b.WaitFor(NetProto.MatchStarting);
            Assert.IsNotNull(aStart);
            Assert.IsNotNull(bStart);
            long matchId = NetJson.Long(aStart, "matchId");

            a.Send(SetPlayStateMsg(matchId, "loaded"));
            b.Send(SetPlayStateMsg(matchId, "loaded"));
            Assert.IsNotNull(a.WaitFor(NetProto.GameplayStarted, 3000));
            Assert.IsNotNull(b.WaitFor(NetProto.GameplayStarted, 3000));
            return matchId;
        }


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

        private static JObj PlayFinishedMsg(long matchId, long score, int maxCombo)
            => JObj.New()
                .Str(NetProto.FieldType, NetProto.PlayFinished)
                .Long("matchId", matchId)
                .Long("score", score)
                .Int("combo", maxCombo)
                .Int("maxCombo", maxCombo)
                .Int("p", maxCombo)
                .Int("c", 0)
                .Int("b", 0)
                .Int("m", 0);

        /// <summary>不在意歌曲時間的測試用這個(tMs = 0)。</summary>
        private static JObj GameplayFrameMsg(long matchId, long score)
            => GameplayFrameMsg(matchId, 0, score);

        private static JObj GameplayFrameMsg(long matchId, double tMs, long score)
            => JObj.New()
                .Str(NetProto.FieldType, NetProto.Frame)
                .Long("matchId", matchId)
                .Num("tMs", tMs)
                .Long("score", score)
                .Int("combo", 0)
                .Int("maxCombo", 0)
                .Int("p", 0)
                .Int("c", 0)
                .Int("b", 0)
                .Int("m", 0);

        private static JObj RoomMoveMsg(int roomCode, int roomRev, int slot, double x, double z)
            => JObj.New()
                .Str(NetProto.FieldType, NetProto.Move)
                .Int("slot", slot)
                .Int("roomCode", roomCode)
                .Int("roomRev", roomRev)
                .Num("x", x)
                .Num("z", z)
                .Num("f", 0)
                .Bool("w", false);

        private static object MoveOf(object message, int userId)
        {
            var moves = NetJson.Arr(message, "m");
            for (int i = 0; i < moves.Count; i++)
            {
                var move = moves[i];
                if (NetJson.Int(move, "userId") == userId) return move;
            }

            Assert.Fail("找不到 userId=" + userId + " 的 move");
            return null;
        }


        private static object ResultRowOf(object message, int userId)
        {
            var rows = NetJson.Arr(message, "rows");
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (NetJson.Int(row, "userId") == userId) return row;
            }
            Assert.Fail("找不到 userId=" + userId + " 的 results row");
            return null;
        }

        private static long ScoreOf(object message, string rowsField, int userId)
        {
            var rows = NetJson.Arr(message, rowsField);
            for (int i = 0; i < rows.Count; i++)
                if (NetJson.Int(rows[i], "userId") == userId)
                    return NetJson.Long(rows[i], "score");
            Assert.Fail("找不到 userId=" + userId + " 的 " + rowsField + " row");
            return -1;
        }

        /// <summary>
        /// 送一筆 gameplay frame,等到 <paramref name="watcher"/> 真的收到**那一筆**,回傳那個
        /// frames 訊息(呼叫端接著斷言它的 <c>leaderUserId</c>)。
        ///
        /// 用 tMs 認而不是用分數:同一個人連續兩筆的分數可能一樣,分數認不出來是哪一筆。
        /// 而且一次只送一筆、等到它出現才送下一筆 —— server 的 leader 是在 5 Hz 的 push 上算的,
        /// 不序列化的話兩人的 frame 會落在同一輪裡,測試就不知道自己在斷言哪個狀態。
        /// </summary>
        private static object SendFrameAndWait(
            TestClient sender, TestClient watcher, long matchId, double tMs, long score)
        {
            sender.Send(GameplayFrameMsg(matchId, tMs, score));

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000)
            {
                int remaining = (int)Math.Max(1, 3000 - sw.ElapsedMilliseconds);
                var frames = watcher.WaitFor(NetProto.Frames, remaining);
                if (frames == null) break;
                if (HasFrameAt(frames, sender.UserId, tMs)) return frames;
            }

            Assert.Fail("沒等到 userId=" + sender.UserId + " tMs=" + tMs + " 的 frame");
            return null;
        }

        private static bool HasFrameAt(object message, int userId, double tMs)
        {
            var rows = NetJson.Arr(message, "f");
            for (int i = 0; i < rows.Count; i++)
            {
                if (NetJson.Int(rows[i], "userId") != userId) continue;
                return NetJson.Num(rows[i], "tMs") == tMs;
            }
            return false;
        }

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
