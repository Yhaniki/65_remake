using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    public class RewardTests
    {
        // ---- Experience: base 30 (clean) / 30-(faults/5) floored at 10, times rank factor ----

        [Test]
        public void Experience_CleanWin_TopTierTimesPlayers()
        {
            // clean run (no bad/miss), winner of a 6-player room -> 30 * 6
            Assert.AreEqual(180, Reward.Experience(0, 0, 1, 6));
        }

        [Test]
        public void Experience_LastPlace_FactorOne()
        {
            // winner gets ×players, last place gets ×1
            Assert.AreEqual(30, Reward.Experience(0, 0, 6, 6));
            Assert.AreEqual(30, Reward.Experience(0, 0, 1, 1));   // solo
        }

        [Test]
        public void Experience_FaultsReduceBase_FlooredAtTen()
        {
            // 30 - faults/5 (integer div). solo so factor = 1.
            Assert.AreEqual(29, Reward.Experience(5, 0, 1, 1));    // 30 - 5/5 = 29
            Assert.AreEqual(28, Reward.Experience(6, 4, 1, 1));    // 30 - 10/5 = 28
            Assert.AreEqual(10, Reward.Experience(200, 0, 1, 1));  // floored at 10
        }

        // ---- Coins: 1 only on a clean run, ×rank factor ×floor(level*1.1) ----

        [Test]
        public void Coins_ZeroUnlessCleanRun()
        {
            Assert.AreEqual(0, Reward.Coins(1, 0, 1, 6, 1));   // any fault -> 0
            Assert.AreEqual(0, Reward.Coins(0, 3, 1, 6, 1));
        }

        [Test]
        public void Coins_CleanRun_ScalesByPlaceAndLevel()
        {
            // clean winner of 6, level 1: 1 * 6 * floor(1.1)=1 -> 6
            Assert.AreEqual(6, Reward.Coins(0, 0, 1, 6, 1));
            // level 10: floor(10*1.1)=11 -> 1 * 6 * 11
            Assert.AreEqual(66, Reward.Coins(0, 0, 1, 6, 10));
            // last place factor 1
            Assert.AreEqual(1, Reward.Coins(0, 0, 6, 6, 1));
        }

        // ---- Points (honor): base 100 / 100-(faults/20) floored 20, ×factor ×floor(level*1.1) ----

        [Test]
        public void Points_CleanRun_BaseHundred()
        {
            Assert.AreEqual(600, Reward.Points(0, 0, 1, 6, 1));   // 100 * 6 * 1
            Assert.AreEqual(100, Reward.Points(0, 0, 1, 1, 1));   // solo
        }

        [Test]
        public void Points_FaultsReduceBase_FlooredAtTwenty()
        {
            Assert.AreEqual(99, Reward.Points(20, 0, 1, 1, 1));   // 100 - 20/20 = 99
            Assert.AreEqual(20, Reward.Points(5000, 0, 1, 1, 1)); // floored at 20
        }

        [Test]
        public void RankFactor_NeverNegative()
        {
            // a place beyond the player count must not produce negative rewards
            Assert.AreEqual(0, Reward.Experience(0, 0, 10, 4));
            Assert.AreEqual(0, Reward.Coins(0, 0, 10, 4, 5));
            Assert.AreEqual(0, Reward.Points(0, 0, 10, 4, 5));
        }
    }
}
