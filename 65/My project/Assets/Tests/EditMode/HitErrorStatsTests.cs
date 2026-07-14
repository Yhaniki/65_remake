using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    /// <summary>
    /// 打擊誤差統計（校時用）。誤差 delta = 打擊時間 − 音符時間：負 = 太早、正 = 太晚。
    /// 建議 offset 的方向不能弄反 —— 弄反的話照著調只會越調越糟。
    /// </summary>
    public class HitErrorStatsTests
    {
        private static HitErrorStats With(params double[] deltas)
        {
            var s = new HitErrorStats();
            foreach (var d in deltas) s.Add(d);
            return s;
        }

        [Test]
        public void Empty_IsAllZero_AndSuggestsCurrentOffset()
        {
            var s = new HitErrorStats();
            Assert.AreEqual(0, s.Count);
            Assert.AreEqual(0.0, s.Mean, 1e-9);
            Assert.AreEqual(0.0, s.Median, 1e-9);
            Assert.AreEqual(0.0, s.UnstableRate, 1e-9);
            Assert.AreEqual(12.0, s.SuggestedOffset(12.0), 1e-9);   // 沒資料 → 不動現值
        }

        [Test]
        public void Mean_Median_StdDev_UnstableRate()
        {
            var s = With(-10, -10, 10, 10);
            Assert.AreEqual(0.0, s.Mean, 1e-9);
            Assert.AreEqual(0.0, s.Median, 1e-9);
            Assert.AreEqual(10.0, s.StdDev, 1e-9);
            Assert.AreEqual(100.0, s.UnstableRate, 1e-9);   // UR = 10 × 標準差

            var odd = With(-30, -5, 1);
            Assert.AreEqual(-5.0, odd.Median, 1e-9);        // 奇數筆取正中間
        }

        [Test]
        public void Median_NotMean_DrivesSuggestion()
        {
            // 一次離譜的晚打不該把建議值拉走：平均被拉到 +20.5（甚至變成「偏晚」），中位數仍是 -5（偏早）
            var s = With(-5, -5, -5, -5, -5, -5, -5, -5, -5, 250);
            Assert.Greater(s.Mean, s.Median, "平均應該被那一擊拉到比中位數大");
            Assert.Greater(s.Mean, 0.0, "平均甚至會反號 —— 所以不能拿平均去校 offset");
            Assert.AreEqual(-5.0, s.Median, 1e-9);
            double suggested = s.SuggestedOffset(0.0);
            Assert.Greater(suggested, 0.0, "整體偏早 → 建議值該是正的");
        }

        [Test]
        public void SuggestedOffset_CorrectsEarlyHits_Positive()
        {
            // 整體偏早 20ms（delta = -20）→ 判定時鐘要往後挪 20ms，誤差才會回到 0
            var s = new HitErrorStats();
            for (int i = 0; i < 20; i++) s.Add(-20.0);
            Assert.AreEqual(20.0, s.SuggestedOffset(0.0), 1e-6);
            Assert.AreEqual(25.0, s.SuggestedOffset(5.0), 1e-6);   // 從現值再修正
        }

        [Test]
        public void SuggestedOffset_CorrectsLateHits_Negative()
        {
            var s = new HitErrorStats();
            for (int i = 0; i < 20; i++) s.Add(15.0);              // 整體偏晚
            Assert.AreEqual(-15.0, s.SuggestedOffset(0.0), 1e-6);
        }

        [Test]
        public void SuggestedOffset_IsDampedWhenPlayIsSloppy()
        {
            // 中位數同樣是 -20，但打得很亂（UR 遠大於 90）→ 建議值要被指數衰減，不能整個 -20 都吃下去
            var tight = new HitErrorStats();
            var sloppy = new HitErrorStats();
            for (int i = 0; i < 40; i++)
            {
                tight.Add(-20.0 + (i % 2 == 0 ? -1 : 1));                 // UR 很小
                sloppy.Add(-20.0 + (i % 2 == 0 ? -60 : 60));              // UR 很大
            }
            Assert.Less(tight.UnstableRate, HitErrorStats.UrDampCutoff);
            Assert.Greater(sloppy.UnstableRate, HitErrorStats.UrDampCutoff);

            double tSug = tight.SuggestedOffset(0.0);
            double sSug = sloppy.SuggestedOffset(0.0);
            Assert.AreEqual(20.0, tSug, 1.5, "打得穩 → 建議值幾乎等於中位數的反向");
            Assert.Less(sSug, tSug, "打得亂 → 建議值要保守很多");
            Assert.Greater(sSug, 0.0, "但方向仍然要對（偏早 → 正的）");
        }

        [Test]
        public void SuggestedOffset_NeedsEnoughSamples()
        {
            var s = With(-20, -20, -20);
            Assert.AreEqual(0.0, s.SuggestedOffset(0.0, minSamples: 10), 1e-9);   // 樣本太少 → 不建議
            Assert.AreEqual(20.0, s.SuggestedOffset(0.0, minSamples: 3), 1e-6);
        }

        [Test]
        public void Histogram_BinsScaleToWorstHit_AndCentreIsZero()
        {
            var s = With(0, 0, 5, -5, 50, -50);
            var bins = s.Histogram(5, out double binSize);

            Assert.AreEqual(11, bins.Length);                 // 中央 1 + 左右各 5
            Assert.AreEqual(10.0, binSize, 1e-9);             // ceil(50 / 5)
            Assert.AreEqual(2, bins[5]);                      // 0ms 兩筆落在中央格
            Assert.AreEqual(1, bins[10]);                     // +50ms → 最右
            Assert.AreEqual(1, bins[0]);                      // -50ms → 最左
            int total = 0; foreach (var b in bins) total += b;
            Assert.AreEqual(6, total, "每一擊都要落進某一格");
        }

        [Test]
        public void Histogram_EmptyIsSafe()
        {
            var bins = new HitErrorStats().Histogram(5, out double binSize);
            Assert.AreEqual(11, bins.Length);
            Assert.AreEqual(1.0, binSize, 1e-9);              // 不能是 0（會除以 0）
        }
    }
}
