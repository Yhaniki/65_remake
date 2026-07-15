using System;
using System.Collections.Generic;

namespace Sdo.Ruleset
{
    /// <summary>
    /// 打擊誤差統計（校時用）。誤差 <c>delta = 打擊時間 − 音符時間</c>（毫秒）——與判定引擎、osu 同號：
    /// <b>負 = 太早、正 = 太晚</b>。
    ///
    /// 純邏輯、無引擎相依（見 HitErrorStatsTests）。作法對齊 osu（HitEventExtensions / BeatmapOffsetControl /
    /// HitEventTimingDistributionGraph）：
    ///   • <see cref="UnstableRate"/> = 10 × 標準差（osu 的 UR 定義）。
    ///   • <see cref="SuggestedOffset"/> 用**中位數**而不是平均（少數幾次亂打不會把建議值拉走），
    ///     而且打得越亂（UR 越高）建議值越保守：UR ≥ 90 之後乘上 exp(-0.0116 × (UR−90))。
    ///   • <see cref="Histogram"/> 的 bin 寬度依「最差的那一擊」自動縮放，並取整數毫秒。
    /// </summary>
    public sealed class HitErrorStats
    {
        /// <summary>osu 的門檻：UR 低於此值不衰減建議值。</summary>
        public const double UrDampCutoff = 90.0;
        private const double UrDampFactor = 0.0116;

        private readonly List<double> _deltas = new List<double>();

        /// <summary>所有有判定到的誤差（毫秒，依打擊順序）。miss／空打不列入。</summary>
        public IReadOnlyList<double> Deltas => _deltas;

        public int Count => _deltas.Count;

        public void Add(double deltaMs)
        {
            if (double.IsNaN(deltaMs) || double.IsInfinity(deltaMs)) return;
            _deltas.Add(deltaMs);
        }

        public void Clear() => _deltas.Clear();

        /// <summary>平均誤差（毫秒）。沒有資料回 0。</summary>
        public double Mean
        {
            get
            {
                if (_deltas.Count == 0) return 0.0;
                double s = 0.0;
                foreach (var d in _deltas) s += d;
                return s / _deltas.Count;
            }
        }

        /// <summary>中位數誤差（毫秒）。偶數筆取中間兩筆的平均。沒有資料回 0。</summary>
        public double Median
        {
            get
            {
                int n = _deltas.Count;
                if (n == 0) return 0.0;
                var a = _deltas.ToArray();
                Array.Sort(a);
                return (n & 1) == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) / 2.0;
            }
        }

        /// <summary>母體標準差（毫秒）。</summary>
        public double StdDev
        {
            get
            {
                int n = _deltas.Count;
                if (n == 0) return 0.0;
                double m = Mean, ss = 0.0;
                foreach (var d in _deltas) { double e = d - m; ss += e * e; }
                return Math.Sqrt(ss / n);
            }
        }

        /// <summary>osu 的 unstable rate = 10 × 標準差。越小越穩。</summary>
        public double UnstableRate => 10.0 * StdDev;

        /// <summary>
        /// 建議的 global offset（毫秒）。玩家整體偏早（中位數為負）→ 建議值往上加，把判定時鐘往後挪，
        /// 誤差就會被拉回 0。打得太亂（UR 高）時建議值會被指數衰減，避免亂打把 offset 帶歪。
        /// </summary>
        public double SuggestedOffset(double currentOffsetMs, int minSamples = 10)
        {
            if (_deltas.Count < minSamples) return currentOffsetMs;
            double adj = Median;
            double ur = UnstableRate;
            if (ur >= UrDampCutoff) adj *= Math.Exp(-UrDampFactor * (ur - UrDampCutoff));
            return currentOffsetMs - adj;
        }

        /// <summary>
        /// osu 式直方圖：中央一格 + 左右各 <paramref name="binsPerSide"/> 格（共 2n+1 格）。
        /// bin 寬度 = ceil(最大絕對誤差 / n) 的整數毫秒（至少 1ms），所以軸會自己貼合這次打的範圍。
        /// </summary>
        public int[] Histogram(int binsPerSide, out double binSizeMs)
        {
            int n = Math.Max(1, binsPerSide);
            var bins = new int[n * 2 + 1];
            double maxAbs = 0.0;
            foreach (var d in _deltas) { double a = Math.Abs(d); if (a > maxAbs) maxAbs = a; }
            binSizeMs = Math.Max(1.0, Math.Ceiling(maxAbs / n));
            foreach (var d in _deltas)
            {
                int i = n + (int)Math.Round(d / binSizeMs, MidpointRounding.AwayFromZero);
                if (i >= 0 && i < bins.Length) bins[i]++;
            }
            return bins;
        }
    }
}
