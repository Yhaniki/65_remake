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

        // ---- SDO original (tick-based, BPM-dependent) ----

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
