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
        public void HasPassed_True_When_Beyond_Miss_Boundary()
        {
            var e = Engine();
            Assert.IsFalse(e.HasPassed(1000, 1100)); // 100 < 127
            Assert.IsTrue(e.HasPassed(1000, 1200));  // 200 > 127
        }
    }
}
