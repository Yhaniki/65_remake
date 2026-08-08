using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    public class ScoreProcessorTests
    {
        // ---- Score（HUD 與結算顯示的那個）----
        // floor(1.04 × (68×maxCombo + 48.4×(hits − maxCombo) − 10×Cool))，hits = Perfect + Cool。
        // 由 4 張官方結算截圖 18 列反推，見 OfficialResultScreens_Are_Reproduced。

        [Test]
        public void Score_Grows_With_Combo()
        {
            var s = new ScoreProcessor();
            // 連段還沒超過下限 10 之前，倍率一律夾在 10：每個 perfect 就是 +10（再 ×1.04 取整）。
            s.Apply(Judgment.Perfect); Assert.AreEqual(10L, s.Score);   // floor(10 × 1.04)
            s.Apply(Judgment.Perfect); Assert.AreEqual(20L, s.Score);   // floor(20 × 1.04)

            // 連段拉過 10 之後倍率 = maxCombo，跟著長：11 連 → floor(11*11 × 1.04) = 125（不是 11*10）。
            for (int i = 0; i < 9; i++) s.Apply(Judgment.Perfect);
            Assert.AreEqual(11, s.MaxCombo);
            Assert.AreEqual(125L, s.Score);
        }

        [Test]
        public void Score_Combo_Multiplier_Caps_At_68()
        {
            var s = new ScoreProcessor();
            for (int i = 0; i < 100; i++) s.Apply(Judgment.Perfect);
            Assert.AreEqual(100, s.MaxCombo);
            Assert.AreEqual(7072L, s.Score);   // 倍率封頂在 68，不是 100：floor(100 × 68 × 1.04)
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
            Assert.AreEqual(416L, p.Score);   // floor(20 perfect × 20 × 1.04)
            Assert.AreEqual(405L, c.Score);   // floor((19×20 + 1×10) × 1.04)
        }

        [Test]
        public void Miss_Scores_Zero()
        {
            var s = new ScoreProcessor();
            s.Apply(Judgment.Miss);
            Assert.AreEqual(0L, s.Score);
        }

        [Test]
        public void Longer_MaxCombo_Beats_Better_Judgements_When_Both_Past_68()
        {
            // 使用者回報的實例：1253 全連（含 40 個 cool）不該輸給 892 連但幾乎全 perfect 的人。
            // 舊公式兩邊的倍率都被夾在 68，combo 完全失去鑑別力，於是 892 那位靠 perfect 數贏。
            var longRun = new ScoreProcessor();   // maxCombo 1253：1213 perfect + 40 cool，全連
            for (int i = 0; i < 1213; i++) longRun.Apply(Judgment.Perfect);
            for (int i = 0; i < 40; i++) longRun.Apply(Judgment.Cool);
            Assert.AreEqual(1253, longRun.MaxCombo);

            var shortRun = new ScoreProcessor();  // maxCombo 892：1249 perfect + 3 cool + 1 miss
            for (int i = 0; i < 892; i++) shortRun.Apply(Judgment.Perfect);
            shortRun.Apply(Judgment.Miss);
            for (int i = 0; i < 357; i++) shortRun.Apply(Judgment.Perfect);
            for (int i = 0; i < 3; i++) shortRun.Apply(Judgment.Cool);
            Assert.AreEqual(892, shortRun.MaxCombo);

            Assert.Greater(longRun.Score, shortRun.Score);
            Assert.AreEqual(1253L * 68 - 40 * 10, 84804L);          // 全連的 base
            Assert.AreEqual(88196L, longRun.Score);                 // floor(84804 × 1.04)
            Assert.AreEqual(81172L, shortRun.Score);                // floor((892×68 + 360×48.4 − 30) × 1.04)
        }

        [Test]
        public void Broken_Run_Multiplier_Never_Exceeds_The_Longest_Run()
        {
            // 最長連段自己都還沒到 48.4 時，斷過連的音符不可以比它更值錢。
            var s = new ScoreProcessor();
            for (int i = 0; i < 20; i++) { s.Apply(Judgment.Perfect); s.Apply(Judgment.Miss); }
            Assert.AreEqual(1, s.MaxCombo);
            Assert.AreEqual(20, s.PerfectCount);
            Assert.AreEqual(208L, s.Score);   // 20 顆全部吃 clamp(1,10,68) = 10 → floor(200 × 1.04)
        }

        // ---- 官方結算截圖回歸（4 場 × 18 列）----
        // 三個全連玩家必須「精確」吻合；其餘容許 4%（見 SDO_SCORE_FORMULA.md §0.1 的殘差表）。
        // 每一列：maxCombo, Perfect, Cool, Bad, Miss, 官方總積分, 是否 GAME OVER。
        // GAME OVER 的人官方會排到最後（不照分數）—— 場B 的 Polaris 分數比第 5 名高卻掛在第 6，
        // 就是這條規則，不是分數算錯。
        static readonly int[][] OfficialRows =
        {
            new[] { 1236, 1198,  38,   0,   0,  87014, 0 },   // 場A Eithwa（全連）
            new[] { 1149, 1231,   3,   1,   1,  84832, 0 },   // 場A 冬至忆旧年
            new[] {  547, 1174,  51,   7,   4,  72099, 0 },   // 場A 帅德·布耀布耀德
            new[] {  507, 1111, 107,   8,  10,  69706, 0 },   // 場A ﹨秋祭尸凉薄
            new[] {  462, 1066, 141,  15,  14,  68090, 0 },   // 場A 古谜妹妹少女
            new[] {  357, 1077, 137,  16,   6,  66676, 0 },   // 場A ⌒梨尸尸小姐
            new[] { 1556, 1545,  11,   0,   0, 109925, 0 },   // 場B Eithwa（全連）
            new[] { 1129, 1503,  41,   7,   5, 100118, 0 },   // 場B 榛果__奶茶
            new[] { 1002, 1521,  31,   2,   2,  97252, 0 },   // 場B 乂怪炎輪乂
            new[] {  912, 1492,  27,  29,   8,  93521, 0 },   // 場B 清纯小蛇
            new[] {  351, 1364, 118,  16,  47,  79878, 0 },   // 場B ≶小酿貓具≶
            new[] {  858, 1343,  20,   4,  42,  83054, 1 },   // 場B Polaris晴天坊（GAME OVER）
            new[] {  515, 2380, 489,  92,  66, 154970, 0 },   // 場C 失眠梦°Tristem
            new[] {  450, 2305, 497, 166,  67, 150256, 0 },   // 場C Kucalb
            new[] {  293,  581, 158,  16,  54,  40424, 1 },   // 場C ﹨癔奴（GAME OVER）
            new[] { 1892, 1849,  43,   0,   0, 133355, 0 },   // 場D Kucalb（全連）
            new[] { 1134, 1799,  79,  10,   4, 117092, 0 },   // 場D Polaris晴天坊
            new[] {  589, 1753, 121,   4,  14, 105092, 0 },   // 場D 蟹查__奶茶
        };

        [Test]
        public void OfficialResultScreens_Are_Reproduced()
        {
            foreach (var row in OfficialRows)
            {
                int maxCombo = row[0], perfect = row[1], cool = row[2], bad = row[3], miss = row[4];
                long official = row[5];

                long got = ScoreOf(maxCombo, perfect, cool, bad, miss);
                bool fullCombo = bad == 0 && miss == 0;
                string what = $"maxCombo={maxCombo} P={perfect} C={cool} B={bad} M={miss}";

                if (fullCombo)
                    Assert.AreEqual(official, got, $"全連必須精確吻合官方：{what}");
                else
                    Assert.LessOrEqual(System.Math.Abs(got - official) / (double)official, 0.04,
                        $"與官方差超過 4%：{what}，算出 {got}，官方 {official}");
            }
        }

        [Test]
        public void OfficialResultScreens_Ranking_Order_Matches()
        {
            // 每一場的名次順序（活到最後的人照總積分排）必須跟我們算出來的一致。
            // GAME OVER 的人不參與這個比較 —— 官方把他們釘在最後，跟分數高低無關。
            int[][] games = { new[] { 0, 6 }, new[] { 6, 12 }, new[] { 12, 15 }, new[] { 15, 18 } };
            foreach (var g in games)
            {
                long prev = long.MaxValue;
                int place = 0;
                for (int i = g[0]; i < g[1]; i++)
                {
                    if (OfficialRows[i][6] != 0) continue;   // GAME OVER：跳過
                    place++;
                    long got = ScoreOf(OfficialRows[i][0], OfficialRows[i][1], OfficialRows[i][2],
                                       OfficialRows[i][3], OfficialRows[i][4]);
                    Assert.Less(got, prev, $"第 {place} 名的分數應該低於前一名（列 {i}）");
                    prev = got;
                }
            }
        }

        /// <summary>照給定的判定數重播一局，回傳分數。連段安排成「最長段 = maxCombo，其餘平分」。</summary>
        static long ScoreOf(int maxCombo, int perfect, int cool, int bad, int miss)
        {
            var s = new ScoreProcessor();
            int hits = perfect + cool, breaks = bad + miss;
            int inRun = System.Math.Min(maxCombo, hits), rest = hits - inRun;

            // 最長那段先打完（用 perfect 填），再用 bad/miss 斷開、把剩下的命中平分到後面的段。
            int perfectLeft = perfect, coolLeft = cool;
            void Hit()
            {
                if (perfectLeft > 0) { s.Apply(Judgment.Perfect); perfectLeft--; }
                else if (coolLeft > 0) { s.Apply(Judgment.Cool); coolLeft--; }
            }
            for (int i = 0; i < inRun; i++) Hit();
            for (int b = 0; b < breaks; b++)
            {
                s.Apply(b < bad ? Judgment.Bad : Judgment.Miss);
                int take = rest / System.Math.Max(1, breaks) + (b < rest % System.Math.Max(1, breaks) ? 1 : 0);
                for (int i = 0; i < take; i++) Hit();
            }
            while (perfectLeft > 0 || coolLeft > 0) Hit();

            Assert.AreEqual(perfect, s.PerfectCount);
            Assert.AreEqual(cool, s.CoolCount);
            Assert.AreEqual(maxCombo, s.MaxCombo, "重播出來的 maxCombo 必須跟官方那一列一致");
            return s.Score;
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
            // 顯示分是由 MaxCombo 推導出來的，所以「不加分」必須是快照：
            // 死後把連段從 12 打到 68，先前那 12 個 perfect 的倍率也不能跟著漲。
            var s = new ScoreProcessor();
            for (int i = 0; i < 12; i++) s.Apply(Judgment.Perfect);
            Assert.AreEqual(149L, s.Score);   // floor(12 × 12 × 1.04)

            s.FreezeScore();
            for (int i = 0; i < 100; i++) s.Apply(Judgment.Perfect);

            Assert.AreEqual(149L, s.Score);
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
            Assert.AreEqual(149L, s.Score);   // 沒凍結就照常加：floor(12 × 12 × 1.04)
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
