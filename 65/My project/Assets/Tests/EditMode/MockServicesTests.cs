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
        public void Chat_Ignores_Blank()
        {
            var c = new MockChatService(new FakeClock());
            int before = c.History.Count;
            c.Send("   ");
            Assert.AreEqual(before, c.History.Count);
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
