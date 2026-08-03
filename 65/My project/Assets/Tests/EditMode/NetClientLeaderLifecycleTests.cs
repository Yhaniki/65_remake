using System.Reflection;
using NUnit.Framework;
using Sdo.Game.Net;
using Sdo.Net;

namespace Sdo.Tests
{
    public class NetClientLeaderLifecycleTests
    {
        [Test]
        public void FramesTrackOnlyTheCurrentMatchAndNewMatchOrClearResetLeader()
        {
            var net = new NetClient();
            int frameEvents = 0;
            net.FramesReceived += _ => frameEvents++;

            Deliver(net, JObj.New()
                .Str(NetProto.FieldType, NetProto.MatchStarting)
                .Long("matchId", 100));
            Deliver(net, Frames(100, 77));

            Assert.AreEqual(77, net.LeaderUserId);
            Assert.AreEqual(1, frameEvents);

            Deliver(net, Frames(99, 88));
            Assert.AreEqual(77, net.LeaderUserId, "stale match frames cannot replace current authority");
            Assert.AreEqual(1, frameEvents, "stale frames cannot poison the current opponent-score cache");

            Deliver(net, JObj.New()
                .Str(NetProto.FieldType, NetProto.MatchStarting)
                .Long("matchId", 200));
            Assert.AreEqual(0, net.LeaderUserId);

            Deliver(net, Frames(100, 77));
            Assert.AreEqual(0, net.LeaderUserId);
            Assert.AreEqual(1, frameEvents);

            Deliver(net, Frames(200, 88));
            Assert.AreEqual(88, net.LeaderUserId);
            Assert.AreEqual(2, frameEvents);

            Deliver(net, JObj.New()
                .Str(NetProto.FieldType, NetProto.GameplayAborted)
                .Long("matchId", 200)
                .Str("reason", "test"));
            Assert.IsNull(net.Match);
            Assert.AreEqual(0, net.LeaderUserId);
        }

        [Test]
        public void ResultsReadyOnlyClosesAndRaisesForTheCurrentMatch()
        {
            var net = new NetClient();
            int resultEvents = 0;
            net.ResultsReady += _ => resultEvents++;

            Deliver(net, JObj.New()
                .Str(NetProto.FieldType, NetProto.MatchStarting)
                .Long("matchId", 300));
            Deliver(net, JObj.New()
                .Str(NetProto.FieldType, NetProto.GameplayStarted)
                .Long("matchId", 300));
            Assert.IsTrue(net.GameplayGateOpen);

            Deliver(net, Results(299));
            Assert.IsTrue(net.GameplayGateOpen, "stale results cannot close the active match gate");
            Assert.AreEqual(0, resultEvents);

            Deliver(net, Results(300));
            Assert.IsFalse(net.GameplayGateOpen);
            Assert.AreEqual(1, resultEvents);
        }

        private static JObj Frames(long matchId, int leaderUserId)
            => JObj.New()
                .Str(NetProto.FieldType, NetProto.Frames)
                .Long("matchId", matchId)
                .Int("leaderUserId", leaderUserId)
                .Put("f", JArr.New());

        private static JObj Results(long matchId)
            => JObj.New()
                .Str(NetProto.FieldType, NetProto.ResultsReady)
                .Long("matchId", matchId)
                .Put("rows", JArr.New());

        private static void Deliver(NetClient net, JObj message)
        {
            object node;
            string type;
            string json = message.Json();
            Assert.IsTrue(NetJson.TryParseMessage(json, out node, out type), json);
            var handle = typeof(NetClient).GetMethod("Handle", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(handle);
            handle.Invoke(net, new[] { type, node });
        }
    }
}
