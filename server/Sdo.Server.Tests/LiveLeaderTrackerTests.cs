using NUnit.Framework;
using Sdo.Net.Server;
using Sdo.Server.Net;

namespace Sdo.Tests
{
    [TestFixture]
    public class LiveLeaderTrackerTests
    {
        [Test]
        public void Initial_Leader_Is_Lowest_Seat_Then_UserId()
        {
            var tracker = new LiveLeaderTracker(new[]
            {
                Player(40, 4),
                Player(20, 1),
                Player(10, 1),
            });

            Assert.AreEqual(10, tracker.CurrentUserId);
        }

        [Test]
        public void Leader_Changes_Only_At_The_300_Point_Boundary()
        {
            var players = new[] { Player(10, 0), Player(20, 1) };
            var tracker = new LiveLeaderTracker(players);
            var active = new[] { 10, 20 };

            tracker.Record(active, 20, 299);
            Assert.AreEqual(10, tracker.Resolve(active));

            tracker.Record(active, 20, 300);
            Assert.AreEqual(20, tracker.Resolve(active));

            tracker.Record(active, 10, 599);
            Assert.AreEqual(20, tracker.Resolve(active));

            tracker.Record(active, 10, 600);
            Assert.AreEqual(10, tracker.Resolve(active));
        }

        [Test]
        public void Removed_Leader_Is_Replaced_And_New_Tracker_Starts_Clean()
        {
            var players = new[] { Player(10, 0), Player(20, 1) };
            var active = new[] { 10, 20 };
            var tracker = new LiveLeaderTracker(players);
            tracker.Record(active, 20, 900);
            Assert.AreEqual(20, tracker.Resolve(active));

            var hostOnly = new[] { 10 };
            Assert.AreEqual(10, tracker.Resolve(hostOnly));

            var nextMatch = new LiveLeaderTracker(players);
            Assert.AreEqual(10, nextMatch.Resolve(active));
        }

        private static NetMatchPlayerSnapshot Player(int userId, int seat)
            => new NetMatchPlayerSnapshot { UserId = userId, Seat = seat };
    }
}
