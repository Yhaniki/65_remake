using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    public class JudgmentWindowsTests
    {
        [Test]
        public void At_OD5_Returns_Mid_Values()
        {
            var w = new JudgmentWindows(5);
            Assert.AreEqual(19.4, w.Perfect, 1e-9);
            Assert.AreEqual(49.0, w.Cool, 1e-9);
            Assert.AreEqual(82.0, w.Bad, 1e-9);
            Assert.AreEqual(136.0, w.MissBoundary, 1e-9);
        }

        [Test]
        public void At_OD10_Returns_Max_Values()
        {
            var w = new JudgmentWindows(10);
            Assert.AreEqual(13.9, w.Perfect, 1e-9);
            Assert.AreEqual(34.0, w.Cool, 1e-9);
            Assert.AreEqual(67.0, w.Bad, 1e-9);
            Assert.AreEqual(121.0, w.MissBoundary, 1e-9);
        }

        [Test]
        public void At_OD0_Returns_Min_Values()
        {
            var w = new JudgmentWindows(0);
            Assert.AreEqual(22.4, w.Perfect, 1e-9);
            Assert.AreEqual(64.0, w.Cool, 1e-9);
            Assert.AreEqual(97.0, w.Bad, 1e-9);
            Assert.AreEqual(151.0, w.MissBoundary, 1e-9);
        }

        [Test]
        public void At_OD8_Interpolates_Between_Mid_And_Max()
        {
            var w = new JudgmentWindows(8);
            // mid + (max-mid)*(8-5)/5
            Assert.AreEqual(19.4 + (13.9 - 19.4) * 3 / 5, w.Perfect, 1e-9);
            Assert.AreEqual(49.0 + (34.0 - 49.0) * 3 / 5, w.Cool, 1e-9);
        }

        [Test]
        public void Judge_Classifies_By_Window()
        {
            var w = new JudgmentWindows(8); // Perfect=16.1, Cool=40, Bad=73, Miss=127
            Assert.AreEqual(Judgment.Perfect, w.Judge(0));
            Assert.AreEqual(Judgment.Perfect, w.Judge(-16));
            Assert.AreEqual(Judgment.Cool, w.Judge(30));
            Assert.AreEqual(Judgment.Bad, w.Judge(70));
            Assert.AreEqual(Judgment.Miss, w.Judge(120));
            Assert.IsNull(w.Judge(200)); // outside miss boundary -> ignored press
        }

        // ---- StepMania judge levels (fixed ms, what the game uses) ----

        [Test]
        public void Sm_Judge2_Is_The_Shipped_Default()
        {
            var w = JudgmentWindows.FromStepManiaJudge();   // 精2, scale 1.33
            Assert.AreEqual(JudgmentWindows.DefaultSmJudge, 2);
            Assert.AreEqual(45.0 * 1.33, w.Perfect, 1e-9);        // MARVELOUS + PERFECT
            Assert.AreEqual(90.0 * 1.33, w.Cool, 1e-9);           // GREAT
            Assert.AreEqual(135.0 * 1.33, w.Bad, 1e-9);           // GOOD
            Assert.AreEqual(180.0 * 1.33, w.MissBoundary, 1e-9);  // BOO (and anything past it)
        }

        [Test]
        public void Sm_Judge4_Is_The_Unscaled_Base()
        {
            var w = JudgmentWindows.FromStepManiaJudge(4);   // scale 1.0
            Assert.AreEqual(45.0, w.Perfect, 1e-9);
            Assert.AreEqual(90.0, w.Cool, 1e-9);
            Assert.AreEqual(135.0, w.Bad, 1e-9);
            Assert.AreEqual(180.0, w.MissBoundary, 1e-9);
        }

        [Test]
        public void Sm_Higher_Judge_Is_Stricter()
        {
            Assert.Less(JudgmentWindows.FromStepManiaJudge(7).Perfect,
                        JudgmentWindows.FromStepManiaJudge(2).Perfect);
        }

        [Test]
        public void Sm_Judge_Is_Clamped_To_The_Table()
        {
            Assert.AreEqual(JudgmentWindows.FromStepManiaJudge(1).Perfect,
                            JudgmentWindows.FromStepManiaJudge(0).Perfect, 1e-9);   // below 精1 -> 精1
            Assert.AreEqual(JudgmentWindows.FromStepManiaJudge(9).Perfect,          // 9 = JUSTICE (0.20)
                            JudgmentWindows.FromStepManiaJudge(99).Perfect, 1e-9);
        }

        [Test]
        public void Sm_Judge2_Classifies_Folded_Tiers()
        {
            var w = JudgmentWindows.FromStepManiaJudge(2);   // 59.85 / 119.7 / 179.55 / 239.4
            Assert.AreEqual(Judgment.Perfect, w.Judge(20));    // SM MARVELOUS band
            Assert.AreEqual(Judgment.Perfect, w.Judge(-59));   // SM PERFECT band, early side
            Assert.AreEqual(Judgment.Cool, w.Judge(100));      // SM GREAT
            Assert.AreEqual(Judgment.Bad, w.Judge(170));       // SM GOOD
            Assert.AreEqual(Judgment.Miss, w.Judge(230));      // SM BOO
            Assert.IsNull(w.Judge(240));                       // past BOO -> press ignored (note auto-misses)
        }

        // ---- SDO original (tick-based, BPM-dependent) — kept for reference, no longer wired in ----

        [Test]
        public void Sdo_FromBpm_Uses_Tick_Windows()
        {
            var w = JudgmentWindows.FromSdoBpm(120); // 1 tick = 1250/120 ms; ticks 6/15/20/25
            double k = 1250.0 / 120.0;
            Assert.AreEqual(6 * k, w.Perfect, 1e-9);
            Assert.AreEqual(15 * k, w.Cool, 1e-9);
            Assert.AreEqual(20 * k, w.Bad, 1e-9);
            Assert.AreEqual(25 * k, w.MissBoundary, 1e-9);
        }

        [Test]
        public void Sdo_Faster_Bpm_Is_Stricter()
        {
            var slow = JudgmentWindows.FromSdoBpm(60);
            var fast = JudgmentWindows.FromSdoBpm(240); // 4x BPM -> 1/4 the ms window
            Assert.Less(fast.Perfect, slow.Perfect);
            Assert.AreEqual(slow.Perfect, fast.Perfect * 4, 1e-9);
        }

        [Test]
        public void Sdo_Zero_Bpm_Falls_Back_To_120()
        {
            Assert.AreEqual(JudgmentWindows.FromSdoBpm(120).Perfect,
                            JudgmentWindows.FromSdoBpm(0).Perfect, 1e-9);
        }
    }
}
