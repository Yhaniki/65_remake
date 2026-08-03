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
    }
}
