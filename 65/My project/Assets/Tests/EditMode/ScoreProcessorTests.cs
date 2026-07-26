using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    public class ScoreProcessorTests
    {
        // ---- Score（HUD 與結算顯示的那個）= ServerScore：Perfects*C + Cools*(C-10)，C = clamp(maxCombo, 10, 68) ----

        [Test]
        public void Score_Grows_With_Combo()
        {
            var s = new ScoreProcessor();
            // 連段還沒超過下限 10 之前，C 一律夾在 10：每個 perfect 就是 +10。
            s.Apply(Judgment.Perfect); Assert.AreEqual(10L, s.Score);
            s.Apply(Judgment.Perfect); Assert.AreEqual(20L, s.Score);

            // 連段拉過 10 之後 C = maxCombo，倍率跟著長：11 連 → 11*11 = 121（不是 11*10）。
            for (int i = 0; i < 9; i++) s.Apply(Judgment.Perfect);
            Assert.AreEqual(11, s.MaxCombo);
            Assert.AreEqual(121L, s.Score);
        }

        [Test]
        public void Score_Combo_Multiplier_Caps_At_68()
        {
            var s = new ScoreProcessor();
            for (int i = 0; i < 100; i++) s.Apply(Judgment.Perfect);
            Assert.AreEqual(100, s.MaxCombo);
            Assert.AreEqual(6800L, s.Score);   // C 封頂在 68，不是 100
        }

        [Test]
        public void Cool_Worth_10_Less_Than_Perfect_At_Same_Combo()
        {
            // 同樣 20 連的情況下比較單一判定的價值：perfect = C、cool = C-10。
            var p = new ScoreProcessor();
            var c = new ScoreProcessor();
            for (int i = 0; i < 19; i++) { p.Apply(Judgment.Perfect); c.Apply(Judgment.Perfect); }
            p.Apply(Judgment.Perfect);
            c.Apply(Judgment.Cool);

            Assert.AreEqual(20, p.MaxCombo);
            Assert.AreEqual(20, c.MaxCombo);
            Assert.AreEqual(20L * 20, p.Score);                  // 20 個 perfect × C(20)
            Assert.AreEqual(19L * 20 + 1 * (20 - 10), c.Score);  // 19 perfect × 20 + 1 cool × 10
        }

        [Test]
        public void Miss_Scores_Zero()
        {
            var s = new ScoreProcessor();
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

        // ---- FreezeScore（完奏模式：血用完後不再加分，但判定統計繼續記錄） ----

        [Test]
        public void FreezeScore_Stops_Score_But_Keeps_Counting_Judgements()
        {
            var s = new ScoreProcessor();
            for (int i = 0; i < 20; i++) s.Apply(Judgment.Perfect);
            long atDeath = s.Score;
            long flatAtDeath = s.StandaloneScore;

            s.FreezeScore();   // 血歸零
            Assert.IsTrue(s.ScoreFrozen);

            for (int i = 0; i < 30; i++) s.Apply(Judgment.Perfect);
            s.Apply(Judgment.Cool); s.Apply(Judgment.Bad); s.Apply(Judgment.Miss);

            Assert.AreEqual(atDeath, s.Score);              // 分數釘死在死亡當下
            Assert.AreEqual(flatAtDeath, s.StandaloneScore);
            Assert.AreEqual(50, s.PerfectCount);            // 判定統計照常累計
            Assert.AreEqual(1, s.CoolCount);
            Assert.AreEqual(1, s.BadCount);
            Assert.AreEqual(1, s.MissCount);
            Assert.AreEqual(53, s.TotalJudged);
            Assert.AreEqual(51, s.MaxCombo);                // 連段也照常長（50 perfect + 1 cool 才被 bad 斷）
        }

        [Test]
        public void FreezeScore_Later_Combo_Does_Not_Retroactively_Raise_Score()
        {
            // ServerScore = P*C（C 跟著 MaxCombo 長），所以「不加分」必須是快照：
            // 死後把連段從 12 打到 68，先前那 12 個 perfect 的倍率也不能跟著漲。
            var s = new ScoreProcessor();
            for (int i = 0; i < 12; i++) s.Apply(Judgment.Perfect);
            Assert.AreEqual(12L * 12, s.Score);

            s.FreezeScore();
            for (int i = 0; i < 100; i++) s.Apply(Judgment.Perfect);

            Assert.AreEqual(12L * 12, s.Score);
        }

        [Test]
        public void FreezeScore_Is_Idempotent()
        {
            var s = new ScoreProcessor();
            for (int i = 0; i < 15; i++) s.Apply(Judgment.Perfect);
            s.FreezeScore();
            long frozen = s.Score;

            for (int i = 0; i < 15; i++) s.Apply(Judgment.Perfect);
            s.FreezeScore();   // 再凍一次不能重新取樣（否則死後的分數會被補進來）

            Assert.AreEqual(frozen, s.Score);
        }

        [Test]
        public void ScoreFrozen_Defaults_False_And_Score_Tracks_Play()
        {
            var s = new ScoreProcessor();
            Assert.IsFalse(s.ScoreFrozen);
            for (int i = 0; i < 12; i++) s.Apply(Judgment.Perfect);
            Assert.AreEqual(12L * 12, s.Score);   // 沒凍結就照常加
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
