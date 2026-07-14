using System;
using System.Collections.Generic;

namespace Sdo.Osu
{
    /// <summary>
    /// 音樂波形：把 PCM 取樣壓成「每 <see cref="BucketMs"/> 一格」的兩條曲線，供編輯器在音符板旁邊畫波形條。
    /// 純邏輯、無引擎相依（見 WaveformPeaksTests）——Unity 端只負責把 <c>AudioClip.GetData</c> 讀到的 float 陣列
    /// 分批餵進 <see cref="Builder"/>（一首 4 分鐘立體聲有 ~2100 萬個取樣，一次算完會卡一大幀）。
    ///
    /// 兩條都要，缺一不可：
    ///   • <see cref="Rms"/>（音量包絡）＝波形的「身體」。只畫峰值的話，壓縮過的舞曲每一格的峰值都逼近全曲最大值
    ///     → 整條變成一根實心柱，什麼結構都看不出來（實測過）。
    ///   • <see cref="Peak"/>（每格 |sample| 最大值）＝瞬態。RMS 會把單一下鼓點平均掉，但編譜最需要看的就是打點在哪，
    ///     所以另外留一條，畫成 RMS 外面那圈淡淡的暈。
    /// 兩條各自依「自己那條的全曲最大值」正規化到 0..1，安靜的歌也看得見。
    /// </summary>
    public sealed class WaveformPeaks
    {
        /// <summary>每格代表幾毫秒。</summary>
        public double BucketMs { get; }

        /// <summary>每格的峰值振幅（0..1）。</summary>
        public float[] Peak { get; }

        /// <summary>每格的 RMS 音量（0..1）。</summary>
        public float[] Rms { get; }

        public int Count => Rms.Length;

        /// <summary>波形涵蓋的長度（毫秒）。</summary>
        public double DurationMs => Count * BucketMs;

        private WaveformPeaks(double bucketMs, float[] peak, float[] rms)
        {
            BucketMs = bucketMs;
            Peak = peak ?? Array.Empty<float>();
            Rms = rms ?? Array.Empty<float>();
        }

        private float At(float[] a, double ms)
        {
            if (ms < 0.0 || a.Length == 0) return 0f;
            int i = (int)(ms / BucketMs);
            return (i < 0 || i >= a.Length) ? 0f : a[i];
        }

        /// <summary>該毫秒位置的峰值；界外為 0。</summary>
        public float PeakAtMs(double ms) => At(Peak, ms);

        /// <summary>該毫秒位置的 RMS 音量；界外為 0。</summary>
        public float RmsAtMs(double ms) => At(Rms, ms);

        /// <summary>一次算完（測試 / 短音檔用）。<paramref name="interleaved"/> 為交錯排列的取樣。</summary>
        public static WaveformPeaks Build(float[] interleaved, int channels, int sampleRate, double bucketMs = 10.0)
        {
            var b = new Builder(channels, sampleRate, bucketMs);
            if (interleaved != null) b.Feed(interleaved, interleaved.Length);
            return b.Finish();
        }

        /// <summary>分批建構：<see cref="Feed"/> 可呼叫多次（每幀一塊），最後 <see cref="Finish"/>。</summary>
        public sealed class Builder
        {
            private readonly double _bucketMs;
            private readonly int _samplesPerBucket;   // 交錯取樣數 = sampleRate × channels × bucket 秒數
            private readonly List<float> _peak = new List<float>();
            private readonly List<float> _rms = new List<float>();
            private float _curPeak;
            private double _curSumSq;
            private int _filled;                      // 目前這一格已吃進幾個取樣
            private float _maxPeak, _maxRms;          // 各自的全曲最大值（正規化用）

            public Builder(int channels, int sampleRate, double bucketMs = 10.0)
            {
                _bucketMs = bucketMs > 0.0 ? bucketMs : 10.0;
                int ch = Math.Max(1, channels);
                int sr = Math.Max(1, sampleRate);
                _samplesPerBucket = Math.Max(1, (int)Math.Round(sr * ch * _bucketMs / 1000.0));
            }

            /// <summary>餵進 <paramref name="count"/> 個交錯取樣（<paramref name="chunk"/> 比 count 長時，尾端多的忽略）。</summary>
            public void Feed(float[] chunk, int count)
            {
                if (chunk == null) return;
                int n = Math.Min(count, chunk.Length);
                for (int i = 0; i < n; i++)
                {
                    float v = chunk[i];
                    float a = v < 0f ? -v : v;
                    if (a > _curPeak) _curPeak = a;
                    _curSumSq += (double)v * v;
                    if (++_filled >= _samplesPerBucket) Flush();
                }
            }

            private void Flush()
            {
                float rms = (float)Math.Sqrt(_curSumSq / Math.Max(1, _filled));
                if (_curPeak > _maxPeak) _maxPeak = _curPeak;
                if (rms > _maxRms) _maxRms = rms;
                _peak.Add(_curPeak);
                _rms.Add(rms);
                _curPeak = 0f;
                _curSumSq = 0.0;
                _filled = 0;
            }

            public WaveformPeaks Finish()
            {
                if (_filled > 0) Flush();   // 收尾：最後不滿一格的也算一格
                var p = _peak.ToArray();
                var r = _rms.ToArray();
                if (_maxPeak > 1e-6f) for (int i = 0; i < p.Length; i++) p[i] /= _maxPeak;
                if (_maxRms > 1e-6f) for (int i = 0; i < r.Length; i++) r[i] /= _maxRms;
                return new WaveformPeaks(_bucketMs, p, r);
            }
        }
    }
}
