using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    public class KeysoundVoicePoolTests
    {
        [Test]
        public void FindFree_Returns_First_Voice_Whose_Sample_Has_Finished()
        {
            var busyUntil = new List<double> { 10.0, 4.0, 3.0 };
            Assert.AreEqual(1, KeysoundVoicePool.FindFree(busyUntil, 5.0));
        }

        [Test]
        public void FindFree_Treats_Exactly_Expired_As_Free()
        {
            var busyUntil = new List<double> { 5.0 };
            Assert.AreEqual(0, KeysoundVoicePool.FindFree(busyUntil, 5.0));
        }

        [Test]
        public void FindFree_Returns_Negative_When_Every_Voice_Is_Still_Sounding()
        {
            var busyUntil = new List<double> { 6.0, 7.0, 9.5 };
            Assert.AreEqual(-1, KeysoundVoicePool.FindFree(busyUntil, 5.0));
            Assert.AreEqual(-1, KeysoundVoicePool.FindFree(null, 5.0));
        }

        [Test]
        public void FindStealable_Picks_The_Oldest_Sounding_Voice()
        {
            // 音源 2 起音最早 → 它的鋼琴衰減尾巴最接近聽不見,是最便宜的犧牲品。
            var startsAt = new List<double> { 4.0, 4.5, 1.0, 3.0 };
            Assert.AreEqual(2, KeysoundVoicePool.FindStealable(startsAt, null, 5.0));
        }

        [Test]
        public void FindStealable_Never_Steals_A_Voice_That_Has_Not_Sounded_Yet()
        {
            // 8.0 是「已排程、還沒響」的未來取樣:偷了那顆音就等於從沒響過,比截斷尾巴糟得多。
            var startsAt = new List<double> { 8.0, 4.0 };
            Assert.AreEqual(1, KeysoundVoicePool.FindStealable(startsAt, null, 5.0));
        }

        [Test]
        public void FindStealable_Skips_Paused_Voices()
        {
            // 暫停中的音源(pausedSamples >= 0)握著恢復播放要用的取樣位置,偷掉就接不回去。
            var startsAt = new List<double> { 1.0, 2.0, 3.0 };
            var paused = new List<int> { 4410, -1, -1 };
            Assert.AreEqual(1, KeysoundVoicePool.FindStealable(startsAt, paused, 5.0));
        }

        [Test]
        public void FindStealable_Returns_Negative_When_Nothing_Is_Sounding_Yet()
        {
            var startsAt = new List<double> { 6.0, 7.0 };
            Assert.AreEqual(-1, KeysoundVoicePool.FindStealable(startsAt, null, 5.0));
            Assert.AreEqual(-1, KeysoundVoicePool.FindStealable(null, null, 5.0));
        }

        [Test]
        public void FindStealable_Ignores_Paused_List_Shorter_Than_The_Pool()
        {
            var startsAt = new List<double> { 1.0, 2.0 };
            var paused = new List<int> { 4410 };
            Assert.AreEqual(1, KeysoundVoicePool.FindStealable(startsAt, paused, 5.0));
        }

        [Test]
        public void PriorityForAge_Falls_From_Fresh_Through_Decaying_To_Tail()
        {
            Assert.AreEqual(KeysoundVoicePool.PriorityFresh, KeysoundVoicePool.PriorityForAge(0.0));
            Assert.AreEqual(KeysoundVoicePool.PriorityFresh,
                KeysoundVoicePool.PriorityForAge(KeysoundVoicePool.FreshSec - 0.01));
            Assert.AreEqual(KeysoundVoicePool.PriorityDecaying,
                KeysoundVoicePool.PriorityForAge(KeysoundVoicePool.FreshSec));
            Assert.AreEqual(KeysoundVoicePool.PriorityDecaying,
                KeysoundVoicePool.PriorityForAge(KeysoundVoicePool.DecaySec - 0.01));
            Assert.AreEqual(KeysoundVoicePool.PriorityTail,
                KeysoundVoicePool.PriorityForAge(KeysoundVoicePool.DecaySec));
            Assert.AreEqual(KeysoundVoicePool.PriorityTail, KeysoundVoicePool.PriorityForAge(6.5));
        }

        [Test]
        public void PriorityForAge_Treats_A_Not_Yet_Sounding_Voice_As_Fresh()
        {
            Assert.AreEqual(KeysoundVoicePool.PriorityFresh, KeysoundVoicePool.PriorityForAge(-0.25));
        }

        [Test]
        public void AssistTick_Outranks_Piano_Tails_But_Yields_To_Fresh_Notes()
        {
            // Unity 的 priority 數字越大越先被虛擬化。打拍音要夾在「起音」與「尾巴」之間。
            Assert.Greater(KeysoundVoicePool.PriorityAssistTick, KeysoundVoicePool.PriorityFresh);
            Assert.Greater(KeysoundVoicePool.PriorityAssistTick, KeysoundVoicePool.PriorityDecaying);
            Assert.Less(KeysoundVoicePool.PriorityAssistTick, KeysoundVoicePool.PriorityTail);
        }

        [Test]
        public void Pool_Ceiling_Clears_The_Heaviest_Measured_Chart_And_Fits_Unity_Real_Voices()
        {
            // 實測峰值 170(william tell)。池要裝得下,又要留位置給打拍音與遊戲音效(Real Voices = 255)。
            Assert.GreaterOrEqual(KeysoundVoicePool.MaxVoices, 170);
            Assert.LessOrEqual(KeysoundVoicePool.MaxVoices, 255);
        }
    }
}
