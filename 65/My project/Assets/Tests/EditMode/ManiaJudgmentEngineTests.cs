using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    public class ManiaJudgmentEngineTests
    {
        // OD8: Perfect=16.1, Cool=40, Bad=73, Miss=127
        private ManiaJudgmentEngine Engine() => new ManiaJudgmentEngine(8);

        [Test]
        public void JudgeHit_Uses_Window_Regardless_Of_Sign()
        {
            var e = Engine();
            Assert.AreEqual(Judgment.Perfect, e.JudgeHit(1000, 1000));
            Assert.AreEqual(Judgment.Cool, e.JudgeHit(1000, 1030));  // +30
            Assert.AreEqual(Judgment.Cool, e.JudgeHit(1000, 970));   // -30
            Assert.AreEqual(Judgment.Bad, e.JudgeHit(1000, 1070));
            Assert.AreEqual(Judgment.Miss, e.JudgeHit(1000, 1120));
        }

        [Test]
        public void JudgeHit_Too_Early_Is_Ignored()
        {
            var e = Engine();
            Assert.IsNull(e.JudgeHit(1000, 700)); // -300, outside miss boundary
        }

        [Test]
        public void JudgeHoldTail_Judges_Against_EndTime()
        {
            var e = Engine();
            Assert.AreEqual(Judgment.Perfect, e.JudgeHoldTail(2000, 2005));
            Assert.AreEqual(Judgment.Bad, e.JudgeHoldTail(2000, 2060)); // +60ms release: past tail Cool (48), within tail Bad (87.6)
        }

        [Test]
        public void HoldTail_Windows_Are_Press_Windows_Times_ScaleFactor()
        {
            var e = Engine();
            // OD8 press: Perfect=16.1, Cool=40, Bad=73, Miss=127. Tail = press × HoldTailWindowScale.
            double s = ManiaJudgmentEngine.HoldTailWindowScale;
            Assert.AreEqual(1.2, s, 1e-9);   // shipped value
            Assert.AreEqual(e.Windows.Perfect * s, e.HoldTailWindows.Perfect, 1e-9);
            Assert.AreEqual(e.Windows.Cool * s, e.HoldTailWindows.Cool, 1e-9);
            Assert.AreEqual(e.Windows.Bad * s, e.HoldTailWindows.Bad, 1e-9);
            Assert.AreEqual(e.Windows.MissBoundary * s, e.HoldTailWindows.MissBoundary, 1e-9);
        }

        [Test]
        public void JudgeHoldTail_Is_More_Forgiving_Than_A_Press_At_The_Same_Error()
        {
            var e = Engine();
            // +18ms error: a press is only Cool (>16.1), but the ×1.2 tail Perfect (19.32) still grants Perfect.
            Assert.AreEqual(Judgment.Cool, e.JudgeHit(2000, 2018));
            Assert.AreEqual(Judgment.Perfect, e.JudgeHoldTail(2000, 2018));
            // +80ms: a press is a Miss (>73), but the ×1.2 tail Bad (87.6) still grants Bad.
            Assert.AreEqual(Judgment.Miss, e.JudgeHit(2000, 2080));
            Assert.AreEqual(Judgment.Bad, e.JudgeHoldTail(2000, 2080));
        }

        [Test]
        public void JudgeHoldTail_NeverReleased_Or_FarLate_Is_Not_Rewarded()
        {
            var e = Engine();
            // The tail is judged on the RELEASE timing. A release far past the (widened) tail — or effectively
            // "never released", which the gameplay layer resolves once now runs past end + tail-MissBoundary — is
            // outside the window: null. ScreenGameplay maps that to a MISS, so just holding the key through the end
            // earns nothing (regression guard: the old code auto-awarded a Perfect for holding through).
            Assert.IsNull(e.JudgeHoldTail(2000, 2500));                 // +500ms, well past the tail miss boundary (152.4)
            Assert.IsTrue(e.HoldTailHasPassed(2000, 2500));             // → the "held through, never released" gate
        }

        [Test]
        public void HoldTailHasPassed_Tracks_The_Widened_Tail_Boundary()
        {
            var e = Engine();
            // OD8 tail MissBoundary = 127 × 1.2 = 152.4ms. A note still held at +140ms must NOT auto-miss yet —
            // a release there would still earn a grade — even though the press boundary (127) has already passed.
            Assert.IsTrue(e.HasPassed(2000, 2140));                     // press boundary passed
            Assert.IsFalse(e.HoldTailHasPassed(2000, 2140));            // but the tail window has not
            Assert.IsTrue(e.HoldTailHasPassed(2000, 2160));             // +160ms > 152.4 → give up, tail miss
        }

        [Test]
        public void HasPassed_True_When_Beyond_Miss_Boundary()
        {
            var e = Engine();
            Assert.IsFalse(e.HasPassed(1000, 1100)); // 100 < 127
            Assert.IsTrue(e.HasPassed(1000, 1200));  // 200 > 127
        }
    }
}
