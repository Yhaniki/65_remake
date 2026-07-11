using System.Linq;
using NUnit.Framework;
using Sdo.UI.Core;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    public class MockServicesTests
    {
        private sealed class FakeClock : IClock { public double Now; public double NowMs => Now; }

        private static MockRoomService NewRooms(out GameSession s)
        {
            s = new GameSession();
            return new MockRoomService(s, 999);
        }

        [Test]
        public void Seeds_Four_Rooms()
            => Assert.AreEqual(4, NewRooms(out _).GetRooms().Count);

        [Test]
        public void Create_Sets_Host_Current_And_Adds_Room()
        {
            var r = NewRooms(out var s);
            var room = r.CreateRoom(GameMode.Normal);
            Assert.IsNotNull(r.CurrentRoom);
            Assert.AreEqual(room.Id, s.CurrentRoomId);
            Assert.IsTrue(r.IsHost);
            Assert.AreEqual(5, r.GetRooms().Count);
        }

        [Test]
        public void Join_Full_Room_Returns_Full()
        {
            var r = NewRooms(out _);
            var full = r.GetRooms().First(x => x.IsFull);
            Assert.AreEqual(JoinResult.Full, r.JoinRoom(full.Id));
        }

        [Test]
        public void Join_InGame_Room_Returns_InGame()
        {
            var r = NewRooms(out _);
            var ingame = r.GetRooms().First(x => x.Status == RoomStatus.InGame);
            Assert.AreEqual(JoinResult.InGame, r.JoinRoom(ingame.Id));
        }

        [Test]
        public void Join_Unknown_Returns_NotFound()
            => Assert.AreEqual(JoinResult.NotFound, NewRooms(out _).JoinRoom(99999));

        [Test]
        public void Join_Waiting_Room_Ok_Sets_Current()
        {
            var r = NewRooms(out _);
            var open = r.GetRooms().First(x => x.Status == RoomStatus.Waiting && !x.IsFull);
            Assert.AreEqual(JoinResult.Ok, r.JoinRoom(open.Id));
            Assert.AreEqual(open.Id, r.CurrentRoom.Id);
            Assert.IsFalse(r.IsHost);
        }

        [Test]
        public void Leave_NonHost_Keeps_Room()
        {
            var r = NewRooms(out _);
            var open = r.GetRooms().First(x => x.Status == RoomStatus.Waiting && !x.IsFull);
            r.JoinRoom(open.Id);
            int before = r.GetRooms().Count;
            r.LeaveRoom();
            Assert.IsNull(r.CurrentRoom);
            Assert.AreEqual(before, r.GetRooms().Count);
        }

        [Test]
        public void Leave_Host_Dissolves_Room()
        {
            var r = NewRooms(out _);
            r.CreateRoom(GameMode.Normal);
            int before = r.GetRooms().Count;
            r.LeaveRoom();
            Assert.IsNull(r.CurrentRoom);
            Assert.AreEqual(before - 1, r.GetRooms().Count);
        }

        // ---- RoomEntry.EnsureOwnHostRoom (選性別/建自己的房 → 保證本機是房主) ----

        [Test]
        public void EnsureOwnHostRoom_Creates_When_None()
        {
            var r = NewRooms(out var s);
            s.LocalPlayerId = "female";
            RoomEntry.EnsureOwnHostRoom(r, GameMode.Normal);
            Assert.IsNotNull(r.CurrentRoom);
            Assert.IsTrue(r.IsHost);
        }

        [Test]
        public void EnsureOwnHostRoom_Keeps_Room_When_Already_Host()
        {
            var r = NewRooms(out var s);
            s.LocalPlayerId = "female";
            var first = r.CreateRoom(GameMode.Normal);
            RoomEntry.EnsureOwnHostRoom(r, GameMode.Normal);   // 已是房主 → 不重建
            Assert.AreEqual(first.Id, r.CurrentRoom.Id);
            Assert.IsTrue(r.IsHost);
        }

        // 女→(未清房)→男 再進房：男角必須成為新房房主(IsHost=true)，否則房主標記消失。
        [Test]
        public void EnsureOwnHostRoom_Reassigns_Host_After_Identity_Switch()
        {
            var r = NewRooms(out var s);
            s.LocalPlayerId = "female";
            var femaleRoom = r.CreateRoom(GameMode.Normal);
            Assert.IsTrue(r.IsHost);

            // 切成男角但女角的房仍是 current → 男角在該房內非房主(這正是 bug 觸發條件)
            s.LocalPlayerId = "male";
            Assert.IsFalse(r.IsHost);

            RoomEntry.EnsureOwnHostRoom(r, GameMode.Normal);   // 應離開女角房 + 建男角自己的房
            Assert.IsNotNull(r.CurrentRoom);
            Assert.AreNotEqual(femaleRoom.Id, r.CurrentRoom.Id);
            Assert.IsTrue(r.IsHost);
        }

        [Test]
        public void Ready_Toggle_And_AllReady()
        {
            var r = NewRooms(out _);
            var open = r.GetRooms().First(x => x.Status == RoomStatus.Waiting && !x.IsFull);
            r.JoinRoom(open.Id);
            Assert.IsFalse(r.AllReady());   // local just joined, not ready
            r.SetReady(true);
            Assert.IsTrue(r.AllReady());
        }

        [Test]
        public void CanStart_Requires_Host_AllReady_And_Song()
        {
            var r = NewRooms(out _);
            // non-host cannot start even when ready
            var open = r.GetRooms().First(x => x.Status == RoomStatus.Waiting && !x.IsFull);
            r.JoinRoom(open.Id);
            r.SetReady(true);
            Assert.IsFalse(r.CanStart());
            r.LeaveRoom();

            // host: needs a song
            r.CreateRoom(GameMode.Normal);
            Assert.IsTrue(r.AllReady());
            Assert.IsFalse(r.CanStart());
            r.SetSong("Test Song");
            Assert.IsTrue(r.CanStart());
        }

        [Test]
        public void RoomUpdated_Fires_On_Ready()
        {
            var r = NewRooms(out _);
            r.CreateRoom(GameMode.Normal);
            int n = 0;
            r.RoomUpdated += _ => n++;
            r.SetReady(false);
            Assert.GreaterOrEqual(n, 1);
        }

        // ---- chat ----

        [Test]
        public void Chat_Starts_Empty_And_Echoes_Send()
        {
            var c = new MockChatService(new FakeClock { Now = 0 });
            Assert.AreEqual(0, c.History.Count);   // 不再有「歡迎」系統訊息

            ChatMessage got = null;
            c.MessageReceived += m => got = m;
            c.Send("hello");
            Assert.AreEqual(1, c.History.Count);
            Assert.AreEqual("我", c.History[0].Sender);
            Assert.AreEqual("hello", c.History[0].Text);
            Assert.IsNotNull(got);
        }

        [Test]
        public void Chat_Local_Sender_Uses_Profile_Name_When_Provided()
        {
            // 左下聊天列表本機發言者顯示 active 使用者名字(id)，不是寫死的「我」。
            var c = new MockChatService(new FakeClock { Now = 0 }, null, () => "玩家001");
            c.Send("hi");
            Assert.AreEqual("玩家001", c.History[0].Sender);
            c.SendExpression(3);
            Assert.AreEqual("玩家001", c.History[1].Sender);
        }

        [Test]
        public void Chat_Local_Sender_Falls_Back_To_Me_Without_Name()
        {
            var c = new MockChatService(new FakeClock { Now = 0 });   // 沒給 localName → 回退 "我"
            c.Send("hi");
            Assert.AreEqual("我", c.History[0].Sender);
        }

        [Test]
        public void Chat_Ignores_Blank()
        {
            var c = new MockChatService(new FakeClock());
            int before = c.History.Count;
            c.Send("   ");
            Assert.AreEqual(before, c.History.Count);
        }

        [Test]
        public void Chat_Parses_Expression_Command()
        {
            var c = new MockChatService(new FakeClock { Now = 0 });
            c.Send("/開始");
            Assert.AreEqual(1, c.History.Count);
            Assert.AreEqual(2, c.History[0].ExpressionId);
            Assert.IsTrue(c.History[0].Local);
            Assert.AreEqual(RoomChatCommand.ExpressionDisplayText(2), c.History[0].Text);
        }

        [TestCase("/YES", 13)]   // 大寫 emoji 指令也要送成表情訊息（左下角打 /YES → 對應表情圖）
        [TestCase("/yes", 13)]
        [TestCase("/GO", 3)]
        [TestCase("/是", 13)]
        public void Chat_Send_Emoji_Command_Produces_Expression(string text, int expectedId)
        {
            var c = new MockChatService(new FakeClock { Now = 0 });
            c.Send(text);
            Assert.AreEqual(1, c.History.Count);
            Assert.AreEqual(expectedId, c.History[0].ExpressionId);
            Assert.IsTrue(c.History[0].Local);
        }

        [Test]
        public void Chat_Parses_Room_Action_Command()
        {
            var c = new MockChatService(new FakeClock { Now = 0 });
            c.Send("哈哈");
            Assert.AreEqual(1, c.History.Count);
            Assert.AreEqual("哈哈", c.History[0].Text);
            Assert.AreEqual("action_1", c.History[0].RoomActionId);
            Assert.IsTrue(c.History[0].Local);
        }

        [Test]
        public void Chat_Parses_Room_Action_With_Player_Gender()
        {
            // "88" resolves differently per gender table: female → action_5 (再見), male → action_6 (再見).
            var female = new MockChatService(new FakeClock { Now = 0 }, () => false);
            female.Send("88");
            Assert.AreEqual("action_5", female.History[0].RoomActionId);

            var male = new MockChatService(new FakeClock { Now = 0 }, () => true);
            male.Send("88");
            Assert.AreEqual("action_6", male.History[0].RoomActionId);

            // A male-only keyword ("昏") is ignored on the default (female) table.
            var def = new MockChatService(new FakeClock { Now = 0 });
            def.Send("昏");
            Assert.IsNull(def.History[0].RoomActionId);
        }

        [Test]
        public void Chat_Records_Selected_Channel()
        {
            var c = new MockChatService(new FakeClock { Now = 0 });
            c.Send("family hi", ChatChannel.Family);
            c.SendExpression(3, ChatChannel.Friend);
            Assert.AreEqual(ChatChannel.Family, c.History[0].Channel);
            Assert.AreEqual(ChatChannel.Friend, c.History[1].Channel);
        }

        [Test]
        public void Chat_Bot_Speaks_After_Interval()
        {
            var clk = new FakeClock { Now = 0 };
            var c = new MockChatService(clk);
            int n0 = c.History.Count;
            clk.Now = 1000; c.Tick();
            Assert.AreEqual(n0, c.History.Count);   // before first bot time (3000ms)
            clk.Now = 3000; c.Tick();
            Assert.AreEqual(n0 + 1, c.History.Count);
        }

        // ---- whisper (密語) ----

        // 對象在同頻道(online) → 送出「你對X說」+ 對方回「X對你說」，都是密語行且 Local 只有前者。
        [Test]
        public void Whisper_To_Online_Sends_Out_And_Reply()
        {
            var c = new MockChatService(new FakeClock { Now = 0 }, null, () => "玩家001",
                onlineNames: () => new[] { "小舞", "Neo" });
            int n0 = c.History.Count;
            c.SendWhisper("小舞", "你好嗎");
            Assert.AreEqual(n0 + 2, c.History.Count);

            var outgoing = c.History[n0];
            Assert.AreEqual(WhisperKind.Outgoing, outgoing.Whisper);
            Assert.IsTrue(outgoing.Local);
            Assert.AreEqual("小舞", outgoing.WhisperParty);
            Assert.AreEqual("你好嗎", outgoing.Text);

            var reply = c.History[n0 + 1];
            Assert.AreEqual(WhisperKind.Incoming, reply.Whisper);
            Assert.IsFalse(reply.Local);
            Assert.AreEqual("小舞", reply.WhisperParty);
        }

        // 名字大小寫不敏感 + 回正規名（"neo" → "Neo"）。
        [Test]
        public void Whisper_Resolves_Canonical_Name_Case_Insensitively()
        {
            var c = new MockChatService(new FakeClock { Now = 0 }, null, null,
                onlineNames: () => new[] { "Neo" });
            c.SendWhisper("neo", "hi");
            Assert.AreEqual(WhisperKind.Outgoing, c.History[c.History.Count - 2].Whisper);
            Assert.AreEqual("Neo", c.History[c.History.Count - 2].WhisperParty);
        }

        // 帳號存在但不在本頻道 → 只出一條「X不在當前頻道」，無回話。
        [Test]
        public void Whisper_To_Offline_Says_Not_In_Channel()
        {
            var c = new MockChatService(new FakeClock { Now = 0 }, null, null,
                onlineNames: () => new[] { "小舞" }, offlineNames: () => new[] { "小雨" });
            int n0 = c.History.Count;
            c.SendWhisper("小雨", "在嗎");
            Assert.AreEqual(n0 + 1, c.History.Count);
            Assert.AreEqual(WhisperKind.OffChannel, c.History[n0].Whisper);
            Assert.AreEqual("小雨", c.History[n0].WhisperParty);
        }

        // 查無帳號 → 只出一條「X無此id」。
        [Test]
        public void Whisper_To_Unknown_Says_No_Such_Id()
        {
            var c = new MockChatService(new FakeClock { Now = 0 }, null, null,
                onlineNames: () => new[] { "小舞" }, offlineNames: () => new string[0]);
            int n0 = c.History.Count;
            c.SendWhisper("路人甲", "hi");
            Assert.AreEqual(n0 + 1, c.History.Count);
            Assert.AreEqual(WhisperKind.NoId, c.History[n0].Whisper);
            Assert.AreEqual("路人甲", c.History[n0].WhisperParty);
        }

        // 只選了對象還沒打內容（body 空）→ 不送任何訊息。
        [Test]
        public void Whisper_With_Empty_Body_Sends_Nothing()
        {
            var c = new MockChatService(new FakeClock { Now = 0 }, null, null,
                onlineNames: () => new[] { "小舞" });
            int n0 = c.History.Count;
            c.SendWhisper("小舞", "   ");
            Assert.AreEqual(n0, c.History.Count);
        }

        // 手打 `[名字] 內容` 經 Send 也走密語（不落成一般訊息，也不解析成表情）。
        // 手打 `[名字] 內容` 經 Send 走密語（純文字內容）。
        [Test]
        public void Chat_Send_Bracket_Syntax_Routes_To_Whisper()
        {
            var c = new MockChatService(new FakeClock { Now = 0 }, null, null,
                onlineNames: () => new[] { "小舞" });
            c.Send("[小舞] 你好");
            var outgoing = c.History[c.History.Count - 2];
            Assert.AreEqual(WhisperKind.Outgoing, outgoing.Whisper);
            Assert.AreEqual("小舞", outgoing.WhisperParty);
            Assert.AreEqual("你好", outgoing.Text);
            Assert.AreEqual(0, outgoing.ExpressionId);
        }

        // 密語裡送表情（[X] /GO）：表情存進 outgoing.ExpressionId（讓聊天列畫 inline emoji），不落成 "/GO" 文字。
        [Test]
        public void Whisper_Carries_Expression()
        {
            var c = new MockChatService(new FakeClock { Now = 0 }, null, null,
                onlineNames: () => new[] { "小舞" });
            c.Send("[小舞] /GO");
            var outgoing = c.History[c.History.Count - 2];
            Assert.AreEqual(WhisperKind.Outgoing, outgoing.Whisper);
            Assert.AreEqual(3, outgoing.ExpressionId);   // /GO → exp 3
            Assert.AreEqual("", outgoing.Text);           // 指令後無尾隨字
        }

        // ---- stage enter/leave (進出舞台廣播) ----

        [Test]
        public void Stage_Announcements_Are_Tagged()
        {
            var c = new MockChatService(new FakeClock { Now = 0 });
            int n0 = c.History.Count;
            c.AnnounceStageEnter("小舞");
            c.AnnounceStageLeave("Neo");
            Assert.AreEqual(n0 + 2, c.History.Count);
            Assert.AreEqual(StageEventKind.Enter, c.History[n0].Stage);
            Assert.AreEqual("小舞", c.History[n0].Sender);
            Assert.AreEqual(StageEventKind.Leave, c.History[n0 + 1].Stage);
            Assert.AreEqual("Neo", c.History[n0 + 1].Sender);
        }

        [Test]
        public void Stage_Announcement_Ignores_Blank_Name()
        {
            var c = new MockChatService(new FakeClock { Now = 0 });
            int n0 = c.History.Count;
            c.AnnounceStageEnter("   ");
            Assert.AreEqual(n0, c.History.Count);
        }

        // ---- scope (大廳 / 房間 隔離) ----

        [Test]
        public void Send_Defaults_To_Lobby_Scope()
        {
            var c = new MockChatService(new FakeClock { Now = 0 });
            c.Send("hi");
            Assert.AreEqual(ChatScope.Lobby, c.History[0].Scope);
        }

        [Test]
        public void SetScope_Stamps_Room_On_Sends_And_Stage()
        {
            var c = new MockChatService(new FakeClock { Now = 0 });
            c.SetScope(ChatScope.Room, 7);
            c.Send("hi");
            c.AnnounceStageEnter("小舞");
            Assert.AreEqual(ChatScope.Room, c.History[0].Scope);
            Assert.AreEqual(7, c.History[0].RoomId);
            Assert.AreEqual(ChatScope.Room, c.History[1].Scope);
            Assert.AreEqual(7, c.History[1].RoomId);
        }

        // bot 閒聊固定屬大廳，即使目前作用域在房間（→ 房間裡看不到，回大廳才看得到）。
        [Test]
        public void Bot_Tick_Always_Lobby_Scope_Even_In_Room()
        {
            var clk = new FakeClock { Now = 0 };
            var c = new MockChatService(clk);
            c.SetScope(ChatScope.Room, 3);
            clk.Now = 3000; c.Tick();
            Assert.AreEqual(1, c.History.Count);
            Assert.AreEqual(ChatScope.Lobby, c.History[0].Scope);
        }
    }
}
