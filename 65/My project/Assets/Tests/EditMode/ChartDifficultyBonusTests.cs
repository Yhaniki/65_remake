using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 炸彈＋變速的難度加成（<see cref="ChartDifficultyBonus"/>）：同一張譜只是多灑了雷、或多了變速
    /// （BPM 換段 / osu 綠線 SV / SDO 捲動速度 / 停拍），難度就該比原版高**一點點**。
    /// 兩個計算器（osu 星數、MinaCalc MSD）共用同一份加成。
    /// </summary>
    public class ChartDifficultyBonusTests
    {
        // 乾淨的譜（沒炸彈、單一 BPM、沒綠線）拿 1.0 —— 加了這層之後，這種譜的等級和以前**完全一樣**。
        [Test]
        public void Plain_Chart_Gets_No_Bonus()
        {
            Assert.AreEqual(1.0, ChartDifficultyBonus.Multiplier(Stream(40, 300)), 1e-12);
            Assert.AreEqual(1.0, ChartDifficultyBonus.Multiplier(null), 1e-12);
            Assert.AreEqual(1.0, ChartDifficultyBonus.Multiplier(new OsuBeatmap { Keys = 4 }), 1e-12);
        }

        // ---- 炸彈 ----

        [Test]
        public void More_Bombs_Means_More_Bonus_Up_To_The_Cap()
        {
            double few = ChartDifficultyBonus.BombBonus(WithBombs(Stream(40, 300), 10));
            double many = ChartDifficultyBonus.BombBonus(WithBombs(Stream(40, 300), 80));
            Assert.Greater(few, 0.0);
            Assert.Greater(many, few, "雷越多加越多");
            Assert.Less(many, ChartDifficultyBonus.BombMax, "但永遠碰不到上限（飽和曲線）");
        }

        // warp 掃掉的裝飾音（IsFake）玩家連一幀都碰不到，兩邊都不該算進去。
        [Test]
        public void Warped_Away_Objects_Are_Ignored()
        {
            var bm = Stream(40, 300);
            for (int i = 0; i < 40; i++)
                bm.HitObjects.Add(new OsuHitObject(i % 4, 600 + i * 300, null, isBomb: true, isFake: true));
            Assert.AreEqual(0.0, ChartDifficultyBonus.BombBonus(bm), 1e-12);
        }

        // 整張只有炸彈 = 一顆打得到的音符都沒有。加成給滿也沒差：那種譜的星數/MSD 本來就是 0。
        [Test]
        public void All_Bomb_Chart_Does_Not_Divide_By_Zero()
        {
            var bm = new OsuBeatmap { Keys = 4 };
            for (int i = 0; i < 20; i++)
                bm.HitObjects.Add(new OsuHitObject(i % 4, 500 + i * 200, null, isBomb: true));
            Assert.AreEqual(ChartDifficultyBonus.BombMax, ChartDifficultyBonus.BombBonus(bm), 1e-12);
        }

        // ---- 變速 ----

        [Test]
        public void Bpm_Change_Adds_A_Small_Bonus()
        {
            var one = Stream(40, 300);
            one.TimingPoints.Add(new OsuTimingPoint(0.0, 500.0));            // 120 BPM 從頭到尾
            var two = Stream(40, 300);
            two.TimingPoints.Add(new OsuTimingPoint(0.0, 500.0));            // 120 BPM
            two.TimingPoints.Add(new OsuTimingPoint(6000.0, 1000.0 / 3.0));  // 中途換 180 BPM

            Assert.AreEqual(0.0, ChartDifficultyBonus.SpeedBonus(one), 1e-12, "單一 BPM 不是變速");
            Assert.Greater(ChartDifficultyBonus.SpeedBonus(two), 0.0);
            Assert.Less(ChartDifficultyBonus.SpeedBonus(two), ChartDifficultyBonus.SpeedMax);
        }

        // osu 綠線（inherited point）＝ 純顯示變速，MinaCalc 和 osu 星數都看不到它，得靠這層補。
        [Test]
        public void Osu_Green_Line_Sv_Adds_A_Small_Bonus()
        {
            var bm = Stream(40, 300);
            bm.TimingPoints.Add(new OsuTimingPoint(0.0, 500.0));      // 紅線
            bm.TimingPoints.Add(new OsuTimingPoint(4000.0, -50.0));   // 綠線 SV ×2
            bm.TimingPoints.Add(new OsuTimingPoint(8000.0, -200.0));  // 綠線 SV ×0.5
            Assert.Greater(ChartDifficultyBonus.SpeedBonus(bm), 0.0);
            Assert.Less(ChartDifficultyBonus.SpeedBonus(bm), ChartDifficultyBonus.SpeedMax);
        }

        // 「整首都是 SV 0.7」跟「整首都是 SV 1.0」打起來一樣 —— 加分的是**段與段之間差多少**，不是絕對值。
        [Test]
        public void A_Constant_Sv_Over_The_Whole_Chart_Is_Not_A_Speed_Change()
        {
            var bm = Stream(40, 300);
            bm.TimingPoints.Add(new OsuTimingPoint(0.0, 500.0));       // 紅線
            bm.TimingPoints.Add(new OsuTimingPoint(0.0, -100.0 / 0.7)); // 同一時刻的綠線：SV 0.7 一路到底
            Assert.AreEqual(0.0, ChartDifficultyBonus.SpeedBonus(bm), 1e-12);
        }

        // 紅線會把 SV 重設回 1.0（osu 的語意）——所以「綠線 0.5 之後接一條紅線」也是一次變速。
        [Test]
        public void A_Red_Line_Resets_Sv_To_One()
        {
            var bm = Stream(40, 300);
            bm.TimingPoints.Add(new OsuTimingPoint(0.0, 500.0));
            bm.TimingPoints.Add(new OsuTimingPoint(1000.0, -200.0));   // SV ×0.5
            bm.TimingPoints.Add(new OsuTimingPoint(6000.0, 500.0));    // 紅線（同 BPM）→ SV 回 1.0
            Assert.Greater(ChartDifficultyBonus.SpeedBonus(bm), 0.0, "紅線把 SV 打回 1.0 也是變速");
        }

        [Test]
        public void Stops_Add_A_Small_Bonus()
        {
            var bm = Stream(40, 300);
            bm.Stops.Add(new ScrollStop(3000.0, 400.0));
            bm.Stops.Add(new ScrollStop(7000.0, 400.0));
            Assert.Greater(ChartDifficultyBonus.SpeedBonus(bm), 0.0);
            Assert.Less(ChartDifficultyBonus.SpeedBonus(bm), ChartDifficultyBonus.SpeedMax);
        }

        [Test]
        public void Sdo_Scroll_Speed_Track_Adds_A_Small_Bonus()
        {
            var bm = Stream(40, 300);
            bm.ScrollSpeeds.Add(new OsuScrollSpeed(0.0, 1.0));
            bm.ScrollSpeeds.Add(new OsuScrollSpeed(5000.0, 2.5));
            Assert.Greater(ChartDifficultyBonus.SpeedBonus(bm), 0.0);
            Assert.Less(ChartDifficultyBonus.SpeedBonus(bm), ChartDifficultyBonus.SpeedMax);
        }

        // StepMania 的負 BPM 被 SmChart 壓成 1 ms 的超高速捲動窗（倍率上千）。它只佔 1 ms，
        // 不能讓一個 warp 就把整首的變速分數炸滿。
        [Test]
        public void A_Warp_Window_Does_Not_Blow_Up_The_Speed_Score()
        {
            var bm = Stream(40, 300);
            bm.TimingPoints.Add(new OsuTimingPoint(0.0, 500.0));
            bm.TimingPoints.Add(new OsuTimingPoint(6000.0, 0.001));   // warp 窗：1 ms 內跑完好幾拍
            bm.TimingPoints.Add(new OsuTimingPoint(6001.0, 500.0));   // 落地，回原速
            Assert.Less(ChartDifficultyBonus.SpeedBonus(bm), 0.1 * ChartDifficultyBonus.SpeedMax,
                "1 ms 的窗只該有 1 ms 的份量");
        }

        // ---- 合起來 ----

        [Test]
        public void The_Total_Is_Capped()
        {
            var bm = WithBombs(Stream(40, 300), 200);
            bm.TimingPoints.Add(new OsuTimingPoint(0.0, 500.0));
            bm.TimingPoints.Add(new OsuTimingPoint(3000.0, 20.0));
            bm.TimingPoints.Add(new OsuTimingPoint(6000.0, 2000.0));
            bm.Stops.Add(new ScrollStop(4000.0, 5000.0));
            double m = ChartDifficultyBonus.Multiplier(bm);
            Assert.Greater(m, 1.0);
            Assert.Less(m, 1.0 + ChartDifficultyBonus.BombMax + ChartDifficultyBonus.SpeedMax,
                "再怎麼灑雷、再怎麼變速都封在 +8% 以內");
        }

        // 兩套計算器吃的是同一份加成 —— 房間換難度計器不會讓「哪張比較難」翻過來。
        [Test]
        public void Both_Calculators_Apply_The_Same_Bonus()
        {
            var bm = WithBombs(Stream(60, 200), 90);
            bm.TimingPoints.Add(new OsuTimingPoint(0.0, 500.0));
            bm.TimingPoints.Add(new OsuTimingPoint(6000.0, 1000.0 / 3.0));
            double mult = ChartDifficultyBonus.Multiplier(bm);
            Assert.Greater(mult, 1.0);

            Assert.AreEqual(ManiaStarRating.Calculate(bm) * mult, ManiaStarRating.CalculateAdjusted(bm), 1e-9);
            Assert.AreEqual((float)(ManiaMsd.Overall(bm) * mult), ManiaMsd.OverallAdjusted(bm), 1e-4f);
        }

        // ---- helpers ----

        private static OsuBeatmap Stream(int count, int stepMs)
        {
            var bm = new OsuBeatmap { Keys = 4 };
            for (int i = 0; i < count; i++)
                bm.HitObjects.Add(new OsuHitObject(i % 4, 500 + i * stepMs));
            return bm;
        }

        // 在譜面時間範圍內平均灑 n 顆炸彈。
        private static OsuBeatmap WithBombs(OsuBeatmap bm, int n)
        {
            int first = (int)bm.FirstNoteMs, last = (int)bm.LastNoteMs;
            for (int i = 0; i < n; i++)
                bm.HitObjects.Add(new OsuHitObject(i % 4, first + (last - first) * i / System.Math.Max(1, n),
                    null, isBomb: true));
            bm.HitObjects.Sort((a, b) => a.StartTimeMs.CompareTo(b.StartTimeMs));
            return bm;
        }
    }
}
