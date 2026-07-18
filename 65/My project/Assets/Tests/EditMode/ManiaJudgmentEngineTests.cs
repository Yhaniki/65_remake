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
            Assert.AreEqual(Judgment.Bad, e.JudgeHoldTail(2000, 2060)); // late release within Bad
        }

        [Test]
        public void JudgeHoldTail_NeverReleased_Or_FarLate_Is_Not_Rewarded()
        {
            var e = Engine();
            // The tail is judged on the RELEASE timing. A release far past the tail — or effectively "never
            // released", which the gameplay layer resolves once now runs past end+MissBoundary — is outside the
            // window: null. ScreenGameplay maps that to a MISS, so just holding the key through the end earns
            // nothing (regression guard: the old code auto-awarded a Perfect for holding through).
            Assert.IsNull(e.JudgeHoldTail(2000, 2500));                 // +500ms, well past the miss boundary
            Assert.IsTrue(e.HasPassed(2000, 2500));                     // → the "held through, never released" gate
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
