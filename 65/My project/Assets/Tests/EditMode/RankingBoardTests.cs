using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    public class RankingBoardTests
    {
        private static List<PlayerEntry> Roster(params (string name, long score, bool local)[] rows)
        {
            var list = new List<PlayerEntry>();
            foreach (var r in rows) list.Add(new PlayerEntry(r.name, r.score, r.local));
            return list;
        }

        // ---- SortedIndices ----

        [Test]
        public void SortedIndices_Orders_By_Score_Descending()
        {
            var r = Roster(("a", 100, false), ("b", 300, true), ("c", 200, false));
            CollectionAssert.AreEqual(new[] { 1, 2, 0 }, RankingBoard.SortedIndices(r));
        }

        [Test]
        public void SortedIndices_Tie_LocalRanksAhead()
        {
            // a (non-local) and b (local) tie at 200; local must come first.
            var r = Roster(("a", 200, false), ("b", 200, true), ("c", 50, false));
            CollectionAssert.AreEqual(new[] { 1, 0, 2 }, RankingBoard.SortedIndices(r));
        }

        [Test]
        public void SortedIndices_Tie_NonLocal_KeepsOriginalOrder_Deterministic()
        {
            var r = Roster(("a", 100, false), ("b", 100, false), ("c", 100, false));
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, RankingBoard.SortedIndices(r));
        }

        [Test]
        public void SortedIndices_Empty_ReturnsEmpty()
        {
            Assert.AreEqual(0, RankingBoard.SortedIndices(new List<PlayerEntry>()).Length);
            Assert.AreEqual(0, RankingBoard.SortedIndices(null).Length);
        }

        // ---- LocalRank ----

        [Test]
        public void LocalRank_Local_First()
        {
            var r = Roster(("me", 500, true), ("x", 300, false), ("y", 100, false));
            Assert.AreEqual((1, 3), RankingBoard.LocalRank(r));
        }

        [Test]
        public void LocalRank_Local_Middle()
        {
            var r = Roster(("x", 500, false), ("me", 300, true), ("y", 100, false));
            Assert.AreEqual((2, 3), RankingBoard.LocalRank(r));
        }

        [Test]
        public void LocalRank_Local_Last()
        {
            var r = Roster(("x", 500, false), ("y", 300, false), ("me", 100, true));
            Assert.AreEqual((3, 3), RankingBoard.LocalRank(r));
        }

        [Test]
        public void LocalRank_SinglePlayer_Is_1_of_1()
        {
            var r = Roster(("me", 0, true));
            Assert.AreEqual((1, 1), RankingBoard.LocalRank(r));
        }

        [Test]
        public void LocalRank_With_No_Local_Entry_Is_Rank_Zero()
        {
            // 旁觀模式的名單裡**沒有自己**(旁觀者不是參賽者)→ rank = 0 =「找不到本機」。
            //
            // 🔴 這個 0 不可以被「修」成 1:呼叫端用 `rank <= 1` 判斷贏家,回 1 的話旁觀者會看到 YOU WIN 旗。
            // ScreenGameplay 因此在那裡多一道 !spectatorMode 的守門;這條測試把 0 這個契約釘住。
            var r = Roster(("x", 500, false), ("y", 300, false));
            Assert.AreEqual((0, 2), RankingBoard.LocalRank(r));
        }

        [Test]
        public void LocalRank_Tie_Local_Outranks()
        {
            // local ties the leader on score -> local takes rank 1.
            var r = Roster(("x", 200, false), ("me", 200, true));
            Assert.AreEqual((1, 2), RankingBoard.LocalRank(r));
        }

        [Test]
        public void LocalRank_NoLocal_ReturnsZeroRank()
        {
            var r = Roster(("x", 200, false), ("y", 100, false));
            Assert.AreEqual((0, 2), RankingBoard.LocalRank(r));
        }

        [Test]
        public void LocalRank_SixPlayers_Upper_Bound()
        {
            var r = Roster(
                ("p0", 600, false), ("p1", 500, false), ("p2", 400, false),
                ("p3", 300, false), ("p4", 200, false), ("me", 100, true));
            Assert.AreEqual((6, 6), RankingBoard.LocalRank(r));
        }
    }
}
