using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    public class ShowtimeMeterTests
    {
        // small, round-number config so the arithmetic is obvious.
        // BandCaps = cumulative fill targets (band widths 100 / 200 / 300); window ms 1000/2000/3000 by band.
        private static ShowtimeMeter Meter() => new ShowtimeMeter
        {
            BandCaps = new[] { 100, 300, 600 },
            GainPerfect = 10,
            GainCool = 5,
            WindowDurationsMs = new[] { 1000, 2000, 3000 },
        };

        // ---- fill accumulates on good hits; Bad/Miss DEDUCT (band-scaled, clamped at 0) ----

        [Test]
        public void Perfect_And_Cool_Add_Their_Gains()
        {
            var m = Meter();
            m.OnJudge(Judgment.Perfect); Assert.AreEqual(10, m.FillCount);
            m.OnJudge(Judgment.Cool); Assert.AreEqual(15, m.FillCount);
        }

        [Test]
        public void Bad_And_Miss_Reduce_Fill_Band_Scaled_And_Clamp_At_Zero()
        {
            var m = Meter();
            for (int i = 0; i < 5; i++) m.OnJudge(Judgment.Perfect);   // 50 (band 0: < BandCaps[0]=100)
            m.OnJudge(Judgment.Bad); Assert.AreEqual(45, m.FillCount);  // − BadReduce[0]=5
            m.OnJudge(Judgment.Miss); Assert.AreEqual(30, m.FillCount); // − MissReduce[0]=15
            for (int i = 0; i < 5; i++) m.OnJudge(Judgment.Miss);       // 30 − 5×15 → clamps at 0, never negative
            Assert.AreEqual(0, m.FillCount);
        }

        [Test]
        public void Fill_Capped_At_Top_Band()
        {
            var m = Meter();
            for (int i = 0; i < 100; i++) m.OnJudge(Judgment.Perfect);  // 1000 >> 600
            Assert.AreEqual(600, m.FillCount);
        }

        // ---- armed level / display band / ready ----

        [Test]
        public void ArmedLevel_And_DisplayBand_Step_At_Caps()
        {
            var m = Meter();
            Assert.AreEqual(-1, m.ArmedLevel); Assert.AreEqual(0, m.DisplayBand); Assert.IsFalse(m.Ready);
            for (int i = 0; i < 10; i++) m.OnJudge(Judgment.Perfect);   // 100 → band0 done
            Assert.AreEqual(0, m.ArmedLevel); Assert.AreEqual(1, m.DisplayBand); Assert.IsTrue(m.Ready);
            for (int i = 0; i < 20; i++) m.OnJudge(Judgment.Perfect);   // 300 → band1 done
            Assert.AreEqual(1, m.ArmedLevel); Assert.AreEqual(2, m.DisplayBand);
            for (int i = 0; i < 30; i++) m.OnJudge(Judgment.Perfect);   // 600 → band2 done
            Assert.AreEqual(2, m.ArmedLevel); Assert.AreEqual(2, m.DisplayBand);
            Assert.AreEqual(1f, m.DisplayFraction);
        }

        [Test]
        public void DisplayFraction_Rebases_To_Zero_At_Each_Band()
        {
            var m = Meter();
            for (int i = 0; i < 5; i++) m.OnJudge(Judgment.Perfect);    // 50 in band0 (0..100)
            Assert.AreEqual(0.5f, m.DisplayFraction, 1e-4f);
            for (int i = 0; i < 5; i++) m.OnJudge(Judgment.Perfect);    // 100 → band1 starts, rebased to 0
            Assert.AreEqual(1, m.DisplayBand); Assert.AreEqual(0f, m.DisplayFraction, 1e-4f);
            for (int i = 0; i < 10; i++) m.OnJudge(Judgment.Perfect);   // 200, band1 (100..300) → 0.5
            Assert.AreEqual(0.5f, m.DisplayFraction, 1e-4f);
        }

        // ---- release / activation ----

        [Test]
        public void TryActivate_Fails_Below_Band0()
        {
            var m = Meter();
            for (int i = 0; i < 5; i++) m.OnJudge(Judgment.Perfect);    // 50, below cap 100
            Assert.IsFalse(m.TryActivate(1000));
            Assert.IsFalse(m.Active);
            Assert.AreEqual(-1, m.ActivationCount);
        }

        [Test]
        public void TryActivate_Uses_Band_Window_And_Spends_Fill()
        {
            var m = Meter();
            for (int i = 0; i < 30; i++) m.OnJudge(Judgment.Perfect);   // 300 → band1 armed
            Assert.IsTrue(m.TryActivate(1000));
            Assert.IsTrue(m.Active);
            Assert.AreEqual(1, m.ReleasedLevel);
            Assert.AreEqual(0, m.ActivationCount);                      // -1 → 0
            Assert.AreEqual(1, m.BonusMultiplier);
            Assert.AreEqual(2000, m.WindowMs);                          // WindowDurationsMs[1], NOT the banked fill
            Assert.AreEqual(1000 + 2000, m.UntilMs);
            Assert.AreEqual(0, m.FillCount);                            // fill spent → refill from 0
        }

        [Test]
        public void Green_Yellow_Red_Give_Their_Window_Durations()
        {
            var g = Meter(); for (int i = 0; i < 10; i++) g.OnJudge(Judgment.Perfect); g.TryActivate(0);
            Assert.AreEqual(1000, g.WindowMs);
            var y = Meter(); for (int i = 0; i < 30; i++) y.OnJudge(Judgment.Perfect); y.TryActivate(0);
            Assert.AreEqual(2000, y.WindowMs);
            var r = Meter(); for (int i = 0; i < 60; i++) r.OnJudge(Judgment.Perfect); r.TryActivate(0);
            Assert.AreEqual(3000, r.WindowMs);
        }

        [Test]
        public void No_Double_Activate_While_Active()
        {
            var m = Meter();
            for (int i = 0; i < 10; i++) m.OnJudge(Judgment.Perfect);   // band0
            m.TryActivate(0);
            Assert.IsFalse(m.TryActivate(0));
        }

        // ---- window: fill frozen, bonus accrues ----

        [Test]
        public void During_Window_Fill_Frozen_And_Bonus_Accrues()
        {
            var m = Meter();
            for (int i = 0; i < 30; i++) m.OnJudge(Judgment.Perfect);   // band1
            m.TryActivate(0);                                          // fill → 0
            int f = m.FillCount;
            m.OnJudge(Judgment.Perfect);                              // bonus += 1*50
            m.OnJudge(Judgment.Perfect);                              // bonus += 1*50
            Assert.AreEqual(f, m.FillCount);                          // frozen (still 0)
            Assert.AreEqual(100L, m.Bonus);
        }

        // ---- timer end ----

        [Test]
        public void Tick_Ends_Window_Exactly_Once()
        {
            var m = Meter();
            for (int i = 0; i < 10; i++) m.OnJudge(Judgment.Perfect);   // band0 → 1000ms window
            m.TryActivate(1000);                                       // until 2000
            Assert.IsFalse(m.Tick(1999));
            Assert.IsTrue(m.Active);
            Assert.IsTrue(m.Tick(2000));
            Assert.IsFalse(m.Active);
            Assert.AreEqual(-1, m.ReleasedLevel);
            Assert.IsFalse(m.Tick(2100));
        }

        [Test]
        public void RemainingMs_Counts_Down_And_Is_Zero_When_Inactive()
        {
            var m = Meter();
            Assert.AreEqual(0.0, m.RemainingMs(500));
            for (int i = 0; i < 60; i++) m.OnJudge(Judgment.Perfect);   // band2 → 3000ms window
            m.TryActivate(1000);
            Assert.AreEqual(3000.0, m.RemainingMs(1000));
            Assert.AreEqual(1200.0, m.RemainingMs(2800));
            Assert.AreEqual(0.0, m.RemainingMs(5000));
        }

        // ---- multiplier stacks across releases, capped ----

        [Test]
        public void Multiplier_Stacks_Each_Release_And_Caps()
        {
            var m = Meter();
            for (int release = 0; release < ShowtimeMeter.MaxActivations + 3; release++)
            {
                for (int i = 0; i < 10; i++) m.OnJudge(Judgment.Perfect);  // refill to band0
                Assert.IsTrue(m.TryActivate(release * 10000));
                m.Tick(release * 10000 + 1000);                            // end at band0's 1000ms window
            }
            Assert.AreEqual(ShowtimeMeter.MaxActivations, m.ActivationCount);
            Assert.AreEqual(ShowtimeMeter.MaxActivations + 1, m.BonusMultiplier);
        }

        // exe FUN_00643030 @348249: releasing spends the tier's cumulative cost; overflow above it carries over
        [Test]
        public void Activate_Keeps_Overflow_Residual()
        {
            var m = Meter();
            for (int i = 0; i < 12; i++) m.OnJudge(Judgment.Perfect);   // 120: band0 (cap 100) armed, +20 overflow
            Assert.IsTrue(m.TryActivate(0));
            Assert.AreEqual(20, m.FillCount);
        }

        // the caller can supply the official pas-quantised window length; the budget table is only the fallback
        [Test]
        public void WindowMs_Override_Takes_Precedence()
        {
            var m = Meter();
            for (int i = 0; i < 10; i++) m.OnJudge(Judgment.Perfect);
            Assert.IsTrue(m.TryActivate(0, 4321));
            Assert.AreEqual(4321, m.WindowMs);
            Assert.AreEqual(4321, m.UntilMs);
        }

        // pins the OFFICIAL-client pacing the user measured (all-good hits): tier1 @130, tier2 @237, tier3 @410
        [Test]
        public void Default_Caps_Match_Official_Measurement()
        {
            var m = new ShowtimeMeter();
            CollectionAssert.AreEqual(new[] { 130, 237, 410 }, m.BandCaps);
            for (int i = 0; i < 129; i++) m.OnJudge(Judgment.Perfect);
            Assert.AreEqual(-1, m.ArmedLevel);
            m.OnJudge(Judgment.Perfect);                       // hit 130 → tier 1
            Assert.AreEqual(0, m.ArmedLevel);
            for (int i = 0; i < 107; i++) m.OnJudge(Judgment.Perfect);   // hit 237 → tier 2
            Assert.AreEqual(1, m.ArmedLevel);
            for (int i = 0; i < 173; i++) m.OnJudge(Judgment.Perfect);   // hit 410 → tier 3
            Assert.AreEqual(2, m.ArmedLevel);
        }

        [Test]
        public void Reset_Clears_State_But_Keeps_Tunables()
        {
            var m = Meter();
            for (int i = 0; i < 60; i++) m.OnJudge(Judgment.Perfect);
            m.TryActivate(0); m.OnJudge(Judgment.Perfect);
            m.Reset();
            Assert.AreEqual(0, m.FillCount);
            Assert.IsFalse(m.Active);
            Assert.AreEqual(-1, m.ActivationCount);
            Assert.AreEqual(0L, m.Bonus);
            Assert.AreEqual(100, m.BandCaps[0]);   // tunable preserved
        }
    }
}
