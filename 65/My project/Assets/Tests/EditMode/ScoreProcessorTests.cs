using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    public class ScoreProcessorTests
    {
        // ---- Score = combo-multiplied (doc per-note: JudgeMul·(combo+1)/2·LevelScore) ----

        [Test]
        public void Score_Grows_With_Combo()
        {
            var s = new ScoreProcessor { LevelScore = 100 };
            // perfect at combo 0: 2.0 * (0+1)/2 * 100 = 100
            s.Apply(Judgment.Perfect); Assert.AreEqual(100L, s.Score);
            // perfect at combo 1: 2.0 * (1+1)/2 * 100 = 200  -> total 300
            s.Apply(Judgment.Perfect); Assert.AreEqual(300L, s.Score);
            // perfect at combo 2: 2.0 * 3/2 * 100 = 300 -> total 600
            s.Apply(Judgment.Perfect); Assert.AreEqual(600L, s.Score);
        }

        [Test]
        public void Cool_Worth_75pct_Of_Perfect_At_Same_Combo()
        {
            var p = new ScoreProcessor { LevelScore = 100 };
            p.Apply(Judgment.Perfect);                 // 100
            var c = new ScoreProcessor { LevelScore = 100 };
            c.Apply(Judgment.Cool);                    // 1.5/2.0 * 100 = 75
            Assert.AreEqual(100L, p.Score);
            Assert.AreEqual(75L, c.Score);
        }

        [Test]
        public void Miss_Scores_Zero()
        {
            var s = new ScoreProcessor { LevelScore = 100 };
            s.Apply(Judgment.Miss);
            Assert.AreEqual(0L, s.Score);
        }

        // ---- StandaloneScore = exe flat formula (no combo mult) ----

        [Test]
        public void StandaloneScore_Is_Flat_PerJudgement()
        {
            var s = new ScoreProcessor();
            s.Apply(Judgment.Perfect); Assert.AreEqual(50L, s.StandaloneScore);
            s.Apply(Judgment.Cool); Assert.AreEqual(90L, s.StandaloneScore);
            s.Apply(Judgment.Bad); Assert.AreEqual(110L, s.StandaloneScore);
            s.Apply(Judgment.Miss); Assert.AreEqual(100L, s.StandaloneScore);
        }

        [Test]
        public void StandaloneScore_Floored_At_Zero()
        {
            var s = new ScoreProcessor();
            s.Apply(Judgment.Miss); Assert.AreEqual(0L, s.StandaloneScore);
        }

        // ---- combo (Perfect & Cool keep, Bad & Miss break) ----

        [Test]
        public void Perfect_And_Cool_Continue_Combo()
        {
            var s = new ScoreProcessor();
            s.Apply(Judgment.Perfect); s.Apply(Judgment.Cool); s.Apply(Judgment.Perfect);
            Assert.AreEqual(3, s.Combo);
        }

        [Test]
        public void Bad_And_Miss_Break_Combo()
        {
            var s = new ScoreProcessor();
            s.Apply(Judgment.Perfect); s.Apply(Judgment.Perfect); s.Apply(Judgment.Bad);
            Assert.AreEqual(0, s.Combo);
            s.Apply(Judgment.Perfect); s.Apply(Judgment.Miss);
            Assert.AreEqual(0, s.Combo);
            Assert.AreEqual(2, s.MaxCombo);
        }

        // ---- holds ----

        [Test]
        public void ApplyHold_HeadBad_Forces_Release_Miss()
        {
            var s = new ScoreProcessor();
            s.ApplyHold(Judgment.Bad, Judgment.Perfect);
            Assert.AreEqual(1, s.BadCount);
            Assert.AreEqual(1, s.MissCount);
            Assert.AreEqual(2, s.TotalJudged);
            Assert.AreEqual(0, s.Combo);
        }

        [Test]
        public void ApplyHold_HeadPerfect_Judges_Tail_Separately()
        {
            var s = new ScoreProcessor();
            s.ApplyHold(Judgment.Perfect, Judgment.Cool);
            Assert.AreEqual(1, s.PerfectCount);
            Assert.AreEqual(1, s.CoolCount);
            Assert.AreEqual(2, s.TotalJudged);
        }

        // ---- online server score (kept for hybrid path) ----

        [Test]
        public void ServerScore_Matches_Captured_Packet()
        {
            var s = new ScoreProcessor();
            for (int i = 0; i < 79; i++) s.Apply(Judgment.Perfect);
            for (int i = 0; i < 3; i++) s.Apply(Judgment.Cool);
            Assert.AreEqual(5546L, s.ServerScore); // 79*68 + 3*58
        }
    }
}
