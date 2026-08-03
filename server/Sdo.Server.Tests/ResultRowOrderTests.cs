using NUnit.Framework;
using Sdo.Server.Net;

namespace Sdo.Tests
{
    public class ResultRowOrderTests
    {
        [TestCase(200, 4, 30, 100, 0, 10, -1, TestName = "Higher_score_sorts_first")]
        [TestCase(100, 0, 30, 100, 4, 10, -1, TestName = "Equal_score_uses_seat_ascending")]
        [TestCase(100, 2, 10, 100, 2, 30, -1, TestName = "Equal_score_and_seat_uses_user_id_ascending")]
        [TestCase(100, 2, 10, 100, 2, 10, 0, TestName = "Identical_keys_compare_equal")]
        public void Compare_Uses_Score_Then_Seat_Then_UserId(
            long leftScore, int leftSeat, int leftUserId,
            long rightScore, int rightSeat, int rightUserId,
            int expectedSign)
        {
            int actual = ResultRowOrder.Compare(
                leftScore, leftSeat, leftUserId,
                rightScore, rightSeat, rightUserId);

            Assert.AreEqual(expectedSign, System.Math.Sign(actual));
        }

        // 同分時「站在領隊格的那位」排第一 —— 領隊格同分不換位(LiveLeaderTracker),所以被追平時
        // 站最前面的人座位序不一定最小。這兩件事判不一致,畫面上就是「面板第一名不是台上站前面那個,
        // 而勝利定格跟著面板走」(使用者回報)。
        [TestCase(100, 4, 30, 100, 0, 10, 30, -1, TestName = "Tie_leader_beats_lower_seat")]
        [TestCase(100, 0, 10, 100, 4, 30, 30, 1, TestName = "Tie_leader_wins_even_from_a_higher_seat")]
        [TestCase(100, 0, 10, 100, 4, 30, 99, -1, TestName = "Tie_leader_not_in_this_pair_falls_back_to_seat")]
        [TestCase(100, 0, 10, 100, 4, 30, 0, -1, TestName = "Tie_no_leader_known_falls_back_to_seat")]
        [TestCase(200, 4, 30, 100, 0, 10, 10, -1, TestName = "Leader_never_outranks_a_higher_score")]
        public void Compare_Puts_The_Leader_First_On_Ties(
            long leftScore, int leftSeat, int leftUserId,
            long rightScore, int rightSeat, int rightUserId,
            int leaderUserId, int expectedSign)
        {
            int actual = ResultRowOrder.Compare(
                leftScore, leftSeat, leftUserId,
                rightScore, rightSeat, rightUserId,
                leaderUserId);

            Assert.AreEqual(expectedSign, System.Math.Sign(actual));
        }
    }
}
