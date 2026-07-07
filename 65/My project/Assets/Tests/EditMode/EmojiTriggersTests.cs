using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    public class EmojiTriggersTests
    {
        // ---- combo milestones --------------------------------------------------------------------------------

        [Test]
        public void Combo_Milestones_Fire_Once_Each_As_A_Streak_Climbs()
        {
            var t = new EmojiTriggers();
            var fired = new List<(int combo, EmojiKind k)>();
            for (int combo = 1; combo <= 850; combo++)
            {
                var k = t.OnJudge(Judgment.Perfect, combo);
                if (k != EmojiKind.None) fired.Add((combo, k));
            }
            CollectionAssert.AreEqual(
                new[] { (50, EmojiKind.HH), (150, EmojiKind.SHSH), (350, EmojiKind.JRKL), (550, EmojiKind.KJ), (800, EmojiKind.HE) },
                fired);
        }

        [Test]
        public void Cool_Counts_As_A_Successful_Hit_For_Milestones()
        {
            var t = new EmojiTriggers();
            Assert.AreEqual(EmojiKind.HH, t.OnJudge(Judgment.Cool, 50));
        }

        [Test]
        public void Non_Milestone_Combos_Show_Nothing()
        {
            var t = new EmojiTriggers();
            Assert.AreEqual(EmojiKind.None, t.OnJudge(Judgment.Perfect, 49));
            Assert.AreEqual(EmojiKind.None, t.OnJudge(Judgment.Perfect, 100));
            Assert.AreEqual(EmojiKind.None, t.OnJudge(Judgment.Perfect, 250));
            Assert.AreEqual(EmojiKind.None, t.OnJudge(Judgment.Perfect, 500));
        }

        [Test]
        public void Milestone_Re_Fires_After_A_Break_Rebuilds_The_Streak()
        {
            var t = new EmojiTriggers();
            Assert.AreEqual(EmojiKind.HH, t.OnJudge(Judgment.Perfect, 50));
            Assert.AreEqual(EmojiKind.None, t.OnJudge(Judgment.Miss, 0));   // break (combo reset upstream)
            Assert.AreEqual(EmojiKind.HH, t.OnJudge(Judgment.Perfect, 50)); // rebuilt -> fires again
        }

        // ---- consecutive bad/miss ----------------------------------------------------------------------------

        [Test]
        public void Consecutive_Miss_Stages_Fire_At_10_30_50()
        {
            var t = new EmojiTriggers();
            var fired = new List<(int n, EmojiKind k)>();
            for (int n = 1; n <= 60; n++)
            {
                var k = t.OnJudge(Judgment.Miss, 0);
                if (k != EmojiKind.None) fired.Add((n, k));
            }
            CollectionAssert.AreEqual(
                new[] { (10, EmojiKind.H), (30, EmojiKind.Y), (50, EmojiKind.JS) },
                fired);
        }

        [Test]
        public void Bad_Also_Counts_Toward_The_Miss_Run()
        {
            var t = new EmojiTriggers();
            for (int i = 0; i < 9; i++) Assert.AreEqual(EmojiKind.None, t.OnJudge(Judgment.Bad, 0));
            Assert.AreEqual(EmojiKind.H, t.OnJudge(Judgment.Bad, 0));   // 10th consecutive bad
            Assert.AreEqual(10, t.ConsecMiss);
        }

        [Test]
        public void A_Successful_Hit_Resets_The_Miss_Run()
        {
            var t = new EmojiTriggers();
            for (int i = 0; i < 9; i++) t.OnJudge(Judgment.Miss, 0);
            Assert.AreEqual(9, t.ConsecMiss);
            t.OnJudge(Judgment.Perfect, 1);            // clean hit -> run resets
            Assert.AreEqual(0, t.ConsecMiss);
            Assert.AreEqual(0, t.MissStage);
            // it now takes a fresh 10 to re-fire H
            EmojiKind last = EmojiKind.None;
            for (int i = 0; i < 10; i++) last = t.OnJudge(Judgment.Miss, 0);
            Assert.AreEqual(EmojiKind.H, last);
        }

        [Test]
        public void Miss_Stage_Does_Not_Re_Fire_Within_The_Same_Run()
        {
            var t = new EmojiTriggers();
            for (int i = 0; i < 10; i++) t.OnJudge(Judgment.Miss, 0);   // H fired at 10
            for (int i = 0; i < 19; i++) Assert.AreEqual(EmojiKind.None, t.OnJudge(Judgment.Miss, 0)); // 11..29 silent
            Assert.AreEqual(EmojiKind.Y, t.OnJudge(Judgment.Miss, 0));  // 30 -> Y
        }

        // ---- GTH = cumulative 100 misses (total, not consecutive) --------------------------------------------

        [Test]
        public void Gth_Fires_Once_At_100_Total_Misses()
        {
            var t = new EmojiTriggers();
            int firedAt = -1;
            for (int n = 1; n <= 120; n++)
                if (t.OnJudge(Judgment.Miss, 0) == EmojiKind.GTH) { Assert.AreEqual(-1, firedAt, "GTH fired twice"); firedAt = n; }
            Assert.AreEqual(100, firedAt);        // fires exactly on the 100th miss
            Assert.AreEqual(120, t.TotalMiss);
        }

        [Test]
        public void Gth_Counts_Total_Misses_Across_Broken_Runs()
        {
            var t = new EmojiTriggers();
            // 99 misses, each broken by a clean hit -> consecutive run never exceeds 1, but the TOTAL climbs to 99.
            for (int i = 0; i < 99; i++) { t.OnJudge(Judgment.Miss, 0); t.OnJudge(Judgment.Perfect, 1); }
            Assert.AreEqual(99, t.TotalMiss);
            Assert.AreEqual(EmojiKind.GTH, t.OnJudge(Judgment.Miss, 0));   // 100th total miss -> GTH (proves cumulative, not consecutive)
        }

        // ---- low HP → VOICE_0012 signal (no longer shows an emoji) --------------------------------------------

        [Test]
        public void Low_Hp_Signals_Once_Below_30_Percent()
        {
            var t = new EmojiTriggers();
            Assert.IsFalse(t.OnHp(0.5f));
            Assert.IsTrue(t.OnHp(0.29f));
            Assert.IsFalse(t.OnHp(0.10f));  // still low -> does not re-signal
        }

        [Test]
        public void Low_Hp_Re_Arms_Only_After_Recovering_Above_40_Percent()
        {
            var t = new EmojiTriggers();
            Assert.IsTrue(t.OnHp(0.2f));
            Assert.IsFalse(t.OnHp(0.35f));  // between 30 and 40 -> NOT re-armed
            Assert.IsFalse(t.OnHp(0.25f));  // dipped again but still disarmed -> nothing
            Assert.IsFalse(t.OnHp(0.45f));  // recovered above 40 -> re-arm (no signal on the way up)
            Assert.IsTrue(t.OnHp(0.29f));   // drops again -> signals
        }

        [Test]
        public void Low_Hp_Starts_Armed()
        {
            var t = new EmojiTriggers();
            Assert.IsTrue(t.LowHpArmed);
        }
    }
}
