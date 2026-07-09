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
        public void Chat_Has_Welcome_And_Echoes_Send()
        {
            var c = new MockChatService(new FakeClock { Now = 0 });
            Assert.AreEqual(1, c.History.Count);
            Assert.IsTrue(c.History[0].System);

            ChatMessage got = null;
            c.MessageReceived += m => got = m;
            c.Send("hello");
            Assert.AreEqual(2, c.History.Count);
            Assert.AreEqual("我", c.History[1].Sender);
            Assert.AreEqual("hello", c.History[1].Text);
            Assert.IsNotNull(got);
        }

        [Test]
        public void Chat_Local_Sender_Uses_Profile_Name_When_Provided()
        {
            // 左下聊天列表本機發言者顯示 active 使用者名字(id)，不是寫死的「我」。
            var c = new MockChatService(new FakeClock { Now = 0 }, null, () => "玩家001");
            c.Send("hi");
            Assert.AreEqual("玩家001", c.History[1].Sender);
            c.SendExpression(3);
            Assert.AreEqual("玩家001", c.History[2].Sender);
        }

        [Test]
        public void Chat_Local_Sender_Falls_Back_To_Me_Without_Name()
        {
            var c = new MockChatService(new FakeClock { Now = 0 });   // 沒給 localName → 回退 "我"
            c.Send("hi");
            Assert.AreEqual("我", c.History[1].Sender);
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
            Assert.AreEqual(2, c.History.Count);
            Assert.AreEqual(2, c.History[1].ExpressionId);
            Assert.IsTrue(c.History[1].Local);
            Assert.AreEqual(RoomChatCommand.ExpressionDisplayText(2), c.History[1].Text);
        }

        [TestCase("/YES", 13)]   // 大寫 emoji 指令也要送成表情訊息（左下角打 /YES → 對應表情圖）
        [TestCase("/yes", 13)]
        [TestCase("/GO", 3)]
        [TestCase("/是", 13)]
        public void Chat_Send_Emoji_Command_Produces_Expression(string text, int expectedId)
        {
            var c = new MockChatService(new FakeClock { Now = 0 });
            c.Send(text);
            Assert.AreEqual(2, c.History.Count);
            Assert.AreEqual(expectedId, c.History[1].ExpressionId);
            Assert.IsTrue(c.History[1].Local);
        }

        [Test]
        public void Chat_Parses_Room_Action_Command()
        {
            var c = new MockChatService(new FakeClock { Now = 0 });
            c.Send("哈哈");
            Assert.AreEqual(2, c.History.Count);
            Assert.AreEqual("哈哈", c.History[1].Text);
            Assert.AreEqual("action_1", c.History[1].RoomActionId);
            Assert.IsTrue(c.History[1].Local);
        }

        [Test]
        public void Chat_Parses_Room_Action_With_Player_Gender()
        {
            // "88" resolves differently per gender table: female → action_5 (再見), male → action_6 (再見).
            var female = new MockChatService(new FakeClock { Now = 0 }, () => false);
            female.Send("88");
            Assert.AreEqual("action_5", female.History[1].RoomActionId);

            var male = new MockChatService(new FakeClock { Now = 0 }, () => true);
            male.Send("88");
            Assert.AreEqual("action_6", male.History[1].RoomActionId);

            // A male-only keyword ("昏") is ignored on the default (female) table.
            var def = new MockChatService(new FakeClock { Now = 0 });
            def.Send("昏");
            Assert.IsNull(def.History[1].RoomActionId);
        }

        [Test]
        public void Chat_Records_Selected_Channel()
        {
            var c = new MockChatService(new FakeClock { Now = 0 });
            c.Send("family hi", ChatChannel.Family);
            c.SendExpression(3, ChatChannel.Friend);
            Assert.AreEqual(ChatChannel.Family, c.History[1].Channel);
            Assert.AreEqual(ChatChannel.Friend, c.History[2].Channel);
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
    }
}
