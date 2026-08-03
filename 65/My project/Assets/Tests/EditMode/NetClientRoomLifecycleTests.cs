using System.Reflection;
using NUnit.Framework;
using Sdo.Game.Net;
using Sdo.Net;
using Sdo.UI.Core;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    public class NetClientRoomLifecycleTests
    {
        private const int User = 77;
        private const int RemoteUser = 88;

        [Test]
        public void VoluntaryLeaveRaisesRoomLeftOnceAndClearsRoomService()
        {
            var net = new NetClient();
            var session = new GameSession();
            var rooms = new OnlineRoomService(net, session);
            try
            {
                var snapshot = new NetRoomSnapshot { Code = 12345 };
                var roomProperty = typeof(NetClient).GetProperty(nameof(NetClient.Room),
                    BindingFlags.Instance | BindingFlags.Public);
                Assert.IsNotNull(roomProperty);
                var roomSetter = roomProperty.GetSetMethod(true);
                Assert.IsNotNull(roomSetter);
                roomSetter.Invoke(net, new object[] { snapshot });

                var applySnapshot = typeof(OnlineRoomService).GetMethod("OnRoomUpdated",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(applySnapshot);
                applySnapshot.Invoke(rooms, new object[] { snapshot });
                Assert.AreEqual(12345, rooms.CurrentRoom.Id);

                int calls = 0;
                string reason = null;
                net.RoomLeft += value =>
                {
                    calls++;
                    reason = value;
                };

                net.LeaveRoom();
                net.LeaveRoom();

                Assert.AreEqual(1, calls, "voluntary leave must emit RoomLeft exactly once");
                Assert.AreEqual("left", reason);
                Assert.IsNull(net.Room);
                Assert.IsNull(rooms.CurrentRoom);
                Assert.AreEqual(-1, session.CurrentRoomId);
            }
            finally
            {
                rooms.Dispose();
                net.Disconnect("test");
            }
        }

        [Test]
        public void LeaveThenLateOldSnapshotIsIgnored()
        {
            var net = NewClient();
            OpenSeatedRoom(net, 12345, 40);
            Assert.AreEqual(12345, net.Room.Code);

            net.LeaveRoom();
            Deliver(net, SeatedSnapshot(12345, 41).EncodeMessage());

            Assert.IsNull(net.Room, "a late pre-leave snapshot must not resurrect the room");
            Assert.AreEqual(0, Field<int>(net, "_lastSeenRev"), "rejected state must not poison the next generation");
        }

        [Test]
        public void RejoiningTheSameRoomAcceptsItsNewLowerRevision()
        {
            var net = NewClient();
            OpenSeatedRoom(net, 12345, 80);
            net.LeaveRoom();

            BeginSuccessfulJoin(net, 12345);
            Deliver(net, SeatedSnapshot(12345, 1).EncodeMessage());

            Assert.IsNotNull(net.Room);
            Assert.AreEqual(12345, net.Room.Code);
            Assert.AreEqual(1, net.Room.Rev, "a new room-entry generation owns a fresh revision sequence");
        }

        [Test]
        public void AJoinGenerationIgnoresSnapshotsForAnotherRoomWithoutRevPoisoning()
        {
            var net = NewClient();
            BeginSuccessfulJoin(net, 12345);

            Deliver(net, SeatedSnapshot(54321, 999).EncodeMessage());
            Assert.IsNull(net.Room, "a snapshot for an unrequested room must be ignored");

            Deliver(net, SeatedSnapshot(12345, 1).EncodeMessage());
            Assert.IsNotNull(net.Room);
            Assert.AreEqual(1, net.Room.Rev, "the wrong-room revision must not block the expected room");
        }

        [Test]
        public void PendingSpectateRejectsSeatedSnapshotThenAcceptsSpectatorSnapshot()
        {
            var net = NewClient();
            OpenSeatedRoom(net, 12345, 10);

            int calls = 0;
            string result = null;
            net.Spectate(0, value => { calls++; result = value; });

            Deliver(net, SeatedSnapshot(12345, 11).EncodeMessage());
            Assert.AreEqual(10, net.Room.Rev, "a stale seated state is not spectate success");
            Assert.IsFalse(net.IsSpectating);
            Assert.AreEqual(0, calls);

            Deliver(net, SpectatorSnapshot(12345, 12).EncodeMessage());
            Assert.IsTrue(net.IsSpectating);
            Assert.AreEqual(12, net.Room.Rev);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(NetProto.JoinOk, result);
        }

        [Test]
        public void SeatToSpectatorTransitionRejectsDelayedMovesFromThePreviousRoomRevision()
        {
            var net = NewClient();
            BeginSuccessfulJoin(net, 12345);
            Deliver(net, SnapshotWithRemote(12345, 20, false).EncodeMessage());

            Deliver(net, MovesMessage(12345, 20, 10f));
            Assert.IsTrue(net.Moves.ContainsKey(RemoteUser));

            Deliver(net, SnapshotWithRemote(12345, 21, true).EncodeMessage());
            Assert.IsFalse(net.Moves.ContainsKey(RemoteUser),
                "changing logical slot must invalidate the old interpolated position");

            Deliver(net, MovesMessage(12345, 20, 20f));
            Assert.IsFalse(net.Moves.ContainsKey(RemoteUser),
                "a delayed pre-transition move batch must stay rejected");

            Deliver(net, MovesMessage(12345, 21, 30f));
            Assert.IsTrue(net.Moves.ContainsKey(RemoteUser));
            Assert.AreEqual(30f, net.Moves[RemoteUser].X);
        }

        private static NetClient NewClient()
        {
            var net = new NetClient();
            var property = typeof(NetClient).GetProperty(nameof(NetClient.UserId),
                BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(property);
            var setter = property.GetSetMethod(true);
            Assert.IsNotNull(setter);
            setter.Invoke(net, new object[] { User });
            return net;
        }

        private static void OpenSeatedRoom(NetClient net, int code, int rev)
        {
            BeginSuccessfulJoin(net, code);
            Deliver(net, SeatedSnapshot(code, rev).EncodeMessage());
        }

        private static void BeginSuccessfulJoin(NetClient net, int code)
        {
            int rq = Field<int>(net, "_nextRq");
            net.JoinRoom(code, null);
            Deliver(net, JObj.New()
                .Str(NetProto.FieldType, NetProto.JoinResult)
                .Int(NetProto.FieldRequest, rq)
                .Str("result", NetProto.JoinOk)
                .Int("code", code));
        }

        private static NetRoomSnapshot SeatedSnapshot(int code, int rev)
        {
            var snap = new NetRoomSnapshot { Code = code, Rev = rev, HostUserId = User };
            snap.Seats[0].State = SeatState.Taken;
            snap.Seats[0].UserId = User;
            snap.Seats[0].Name = "me";
            return snap;
        }

        private static NetRoomSnapshot SpectatorSnapshot(int code, int rev)
        {
            var snap = new NetRoomSnapshot { Code = code, Rev = rev };
            snap.Spectators = new[] { new NetSpectator { UserId = User, Name = "me" } };
            return snap;
        }

        private static NetRoomSnapshot SnapshotWithRemote(int code, int rev, bool remoteSpectating)
        {
            var snap = SeatedSnapshot(code, rev);
            if (remoteSpectating)
            {
                snap.Spectators = new[] { new NetSpectator { UserId = RemoteUser, Name = "remote" } };
            }
            else
            {
                snap.Seats[1].State = SeatState.Taken;
                snap.Seats[1].UserId = RemoteUser;
                snap.Seats[1].Name = "remote";
            }
            return snap;
        }

        private static JObj MovesMessage(int code, int rev, float x)
        {
            var rows = JArr.New().Add(JObj.New()
                .Int("userId", RemoteUser)
                .Num("x", x).Num("z", 2f).Num("f", 90f).Bool("w", true));
            return JObj.New()
                .Str(NetProto.FieldType, NetProto.Moves)
                .Int("roomCode", code)
                .Int("roomRev", rev)
                .Put("m", rows);
        }

        private static T Field<T>(NetClient net, string name)
        {
            var field = typeof(NetClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, name);
            return (T)field.GetValue(net);
        }

        private static void Deliver(NetClient net, JObj message)
        {
            object node;
            string type;
            Assert.IsTrue(NetJson.TryParseMessage(message.Json(), out node, out type), message.Json());
            var handle = typeof(NetClient).GetMethod("Handle", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(handle);
            handle.Invoke(net, new[] { type, node });
        }
    }
}
