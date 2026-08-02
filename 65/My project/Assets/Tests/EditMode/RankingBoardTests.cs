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

        /// <summary>帶座位序的名單(線上:座位序決定平手誰在前)。</summary>
        private static List<PlayerEntry> Seated(params (string name, long score, bool local, int seat)[] rows)
        {
            var list = new List<PlayerEntry>();
            foreach (var r in rows) list.Add(new PlayerEntry(r.name, r.score, r.local, r.seat));
            return list;
        }

        // ---- DisplayRanks / LocalDisplayRank(畫面上的名次:同分並列、不跳號)----

        [Test]
        public void DisplayRanks_Tie_SharesTheNumber_AndDoesNotSkip()
        {
            // 🔴 1, 1, 2(密集排名)—— **不是**競賽排名的 1, 1, 3(使用者指定)。
            CollectionAssert.AreEqual(new[] { 1, 1, 2 }, RankingBoard.DisplayRanks(new long[] { 900, 900, 100 }));
            CollectionAssert.AreEqual(new[] { 1, 2, 2 }, RankingBoard.DisplayRanks(new long[] { 900, 100, 100 }));
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, RankingBoard.DisplayRanks(new long[] { 900, 900, 900 }));
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, RankingBoard.DisplayRanks(new long[] { 900, 500, 100 }));
            CollectionAssert.AreEqual(new int[0], RankingBoard.DisplayRanks(new long[0]));
            CollectionAssert.AreEqual(new int[0], RankingBoard.DisplayRanks(null));
        }

        [Test]
        public void LocalDisplayRank_Tie_BothSidesGetTheSameNumber()
        {
            // 使用者截圖那一局:兩人同分 71740。兩台寫的名次都要是 1,而**定格動作**仍只有座位序在前的那位做
            // (LocalRank 照舊回 1 / 2)。
            var meLateSeat = Seated(("x", 71740, false, 0), ("me", 71740, true, 1));
            var meEarlySeat = Seated(("me", 71740, true, 0), ("x", 71740, false, 1));
            Assert.AreEqual((1, 2), RankingBoard.LocalDisplayRank(meLateSeat), "同分:畫面上的名次兩邊都是 1");
            Assert.AreEqual((1, 2), RankingBoard.LocalDisplayRank(meEarlySeat));
            Assert.AreEqual(2, RankingBoard.LocalRank(meLateSeat).rank, "但定格用的嚴格名次照舊分先後");
            Assert.AreEqual(1, RankingBoard.LocalRank(meEarlySeat).rank);
        }

        [Test]
        public void LocalDisplayRank_DoesNotSkipNumbers_AfterATie()
        {
            // 前兩名同分 → 本機是「第 2 名」,不是第 3 名。
            var r = Seated(("a", 900, false, 0), ("b", 900, false, 1), ("me", 100, true, 2));
            Assert.AreEqual((2, 3), RankingBoard.LocalDisplayRank(r));
            Assert.AreEqual(3, RankingBoard.LocalRank(r).rank, "嚴格名次仍是 3");
        }

        [Test]
        public void LocalDisplayRank_NoLocalEntry_IsZero()
        {
            // 與 LocalRank 同一個契約:旁觀者不在名單裡 → 0(呼叫端靠它把「N / M」關掉)。
            Assert.AreEqual((0, 2), RankingBoard.LocalDisplayRank(Roster(("x", 500, false), ("y", 300, false))));
        }

        // ---- LocalTiedForTop(戰績用的「贏」:同分也算)----

        [Test]
        public void LocalTiedForTop_Tie_BothSidesCountAsWin()
        {
            // 🔴 同分時**兩台都**要判 true —— 戰績是「兩個人都記勝場」(使用者指定)。
            // 名次面板/勝利定格另一條路(LocalRank,平手照座位序)只會有一個第一名,那是刻意的差別。
            var meSecondSeat = Seated(("x", 71740, false, 0), ("me", 71740, true, 1));
            var meFirstSeat = Seated(("me", 71740, true, 0), ("x", 71740, false, 1));
            Assert.IsTrue(RankingBoard.LocalTiedForTop(meSecondSeat), "同分但座位在後 → 戰績仍是勝場");
            Assert.IsTrue(RankingBoard.LocalTiedForTop(meFirstSeat));
            // 對照:名次只有座位在前的那位是第 1 名。
            Assert.AreEqual(2, RankingBoard.LocalRank(meSecondSeat).rank);
            Assert.AreEqual(1, RankingBoard.LocalRank(meFirstSeat).rank);
        }

        [Test]
        public void LocalTiedForTop_LowerScore_IsNotAWin()
        {
            var r = Seated(("x", 71741, false, 0), ("me", 71740, true, 1));
            Assert.IsFalse(RankingBoard.LocalTiedForTop(r), "只差 1 分也是輸");
        }

        [Test]
        public void LocalTiedForTop_ThreeWayTop_TieCountsForEveryoneOnTop()
        {
            // 三人同分並列第一、第四名落後 → 本機(並列的那三人之一)算勝場。
            var r = Seated(("a", 900, false, 0), ("me", 900, true, 1), ("c", 900, false, 2), ("d", 100, false, 3));
            Assert.IsTrue(RankingBoard.LocalTiedForTop(r));
        }

        [Test]
        public void LocalTiedForTop_SinglePlayer_And_NoLocal()
        {
            Assert.IsTrue(RankingBoard.LocalTiedForTop(Roster(("me", 0, true))), "只有自己 → 第一名");
            // 旁觀者不在名單裡 → 不是參賽者,沒有勝場可記(呼叫端另有 !spectatorMode 守門)。
            Assert.IsFalse(RankingBoard.LocalTiedForTop(Roster(("x", 500, false), ("y", 300, false))));
            Assert.IsFalse(RankingBoard.LocalTiedForTop(new List<PlayerEntry>()));
            Assert.IsFalse(RankingBoard.LocalTiedForTop(null));
        }

        // ---- SortedIndices ----

        [Test]
        public void SortedIndices_Orders_By_Score_Descending()
        {
            var r = Roster(("a", 100, false), ("b", 300, true), ("c", 200, false));
            CollectionAssert.AreEqual(new[] { 1, 2, 0 }, RankingBoard.SortedIndices(r));
        }

        [Test]
        public void SortedIndices_Tie_SeatOrderDecides_NotLocal()
        {
            // 🔴 同分**不是**本機先,是座位序小的先。本機先是每台各自成立的規則 —— 同分時兩台都會判自己第一名,
            // 於是兩邊都做勝利定格,而面板用的是 server 照 (seat, userId) 排出來的名次(使用者回報的
            // 「面板寫我第 2 名,人卻在跳勝利動作」)。這裡本機坐 1 號位、對手坐 0 號位 → 對手在前。
            var r = Seated(("a", 200, false, 0), ("me", 200, true, 1), ("c", 50, false, 2));
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, RankingBoard.SortedIndices(r));
        }

        [Test]
        public void SortedIndices_Tie_LocalWithLowerSeat_RanksAhead()
        {
            var r = Seated(("a", 200, false, 1), ("me", 200, true, 0), ("c", 50, false, 2));
            CollectionAssert.AreEqual(new[] { 1, 0, 2 }, RankingBoard.SortedIndices(r));
        }

        [Test]
        public void SortedIndices_Tie_NoSeatData_KeepsRosterOrder()
        {
            // 離線/假對手:沒有座位資料 → 退回名單順序(本機是第一個加進去的 → 仍然在前,行為與過去一致)。
            var r = Roster(("me", 200, true), ("a", 200, false), ("c", 50, false));
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, RankingBoard.SortedIndices(r));
        }

        [Test]
        public void SortedIndices_SeatedPlayers_RankAhead_Of_SeatlessOnes_OnTie()
        {
            // NoSeat = int.MaxValue → 有座位的先。線上名單裡查不到座位的只會是中途離開/資料還沒到的人。
            var r = Seated(("ghost", 200, false, PlayerEntry.NoSeat), ("me", 200, true, 3));
            CollectionAssert.AreEqual(new[] { 1, 0 }, RankingBoard.SortedIndices(r));
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
        public void LocalRank_Tie_SeatOrderDecides()
        {
            // 同分:座位序小的是第 1 名。本機坐後面 → 第 2 名(結算面板/server 也是這個答案,見
            // SortedIndices_Tie_SeatOrderDecides_NotLocal 的說明)。
            var r = Seated(("x", 200, false, 0), ("me", 200, true, 1));
            Assert.AreEqual((2, 2), RankingBoard.LocalRank(r));

            var r2 = Seated(("x", 200, false, 1), ("me", 200, true, 0));
            Assert.AreEqual((1, 2), RankingBoard.LocalRank(r2));
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
