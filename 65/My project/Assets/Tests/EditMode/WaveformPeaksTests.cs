using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 編輯器波形：PCM → 每格的 RMS（波形的身體）與峰值（瞬態）。重點是「格子的時間長度」要正確（不然波形跟音符
    /// 對不齊）、分批餵（Unity 端一幀讀一塊 AudioClip.GetData）要跟一次餵完全一樣，以及 RMS 與峰值真的不同
    /// ——只畫峰值的話壓縮過的舞曲會變成一根實心柱（實測過），所以身體一定要用 RMS。
    /// </summary>
    public class WaveformPeaksTests
    {
        [Test]
        public void BucketSize_FollowsSampleRateAndChannels()
        {
            // 1000 Hz、單聲道、每格 100ms → 一格 100 個取樣。餵 300 個 → 3 格。
            var samples = new float[300];
            for (int i = 0; i < 100; i++) samples[i] = 0.5f;      // 第 0 格
            for (int i = 100; i < 200; i++) samples[i] = 1.0f;    // 第 1 格
            for (int i = 200; i < 300; i++) samples[i] = 0.25f;   // 第 2 格

            var w = WaveformPeaks.Build(samples, channels: 1, sampleRate: 1000, bucketMs: 100.0);

            Assert.AreEqual(3, w.Count);
            Assert.AreEqual(300.0, w.DurationMs, 1e-9);
            // 定值訊號的 RMS == 峰值；兩條都依全曲最大值正規化（最大 = 1.0）
            Assert.AreEqual(0.5f, w.Peak[0], 1e-6f);
            Assert.AreEqual(1.0f, w.Peak[1], 1e-6f);
            Assert.AreEqual(0.25f, w.Peak[2], 1e-6f);
            Assert.AreEqual(0.5f, w.Rms[0], 1e-6f);
            Assert.AreEqual(1.0f, w.Rms[1], 1e-6f);
        }

        [Test]
        public void Stereo_BucketCoversBothChannels()
        {
            // 立體聲：交錯排列，一格 = sampleRate × 2 × 秒數。1000Hz/2ch/100ms → 200 個交錯取樣。
            var samples = new float[400];
            samples[3] = -0.8f;    // 第 0 格（負值取絕對值）
            samples[250] = 0.4f;   // 第 1 格
            var w = WaveformPeaks.Build(samples, channels: 2, sampleRate: 1000, bucketMs: 100.0);

            Assert.AreEqual(2, w.Count);
            Assert.AreEqual(1.0f, w.Peak[0], 1e-6f);   // 0.8 是全曲最大 → 正規化成 1
            Assert.AreEqual(0.5f, w.Peak[1], 1e-6f);   // 0.4 / 0.8
        }

        [Test]
        public void Rms_IsBody_Peak_KeepsTransient()
        {
            // 一格裡只有一個尖峰、其餘為 0：峰值要保住它；RMS 會被平均掉（這正是波形身體該有的行為）。
            var samples = new float[200];
            samples[7] = 1.0f;         // 第 0 格：單一瞬態
            for (int i = 100; i < 200; i++) samples[i] = 0.2f;   // 第 1 格：持續的小音量

            var w = WaveformPeaks.Build(samples, 1, 1000, 100.0);

            Assert.AreEqual(1.0f, w.Peak[0], 1e-6f);   // 瞬態沒被吃掉
            Assert.AreEqual(0.2f, w.Peak[1], 1e-6f);
            // RMS：第 0 格 = sqrt(1/100) = 0.1；第 1 格 = 0.2 → 第 1 格才是比較「大聲」的
            Assert.Less(w.Rms[0], w.Rms[1], "單一尖峰的 RMS 應該小於持續音量的 RMS");
            Assert.AreEqual(1.0f, w.Rms[1], 1e-6f);    // 正規化後最大的那格 = 1
            Assert.AreEqual(0.5f, w.Rms[0], 1e-6f);    // 0.1 / 0.2
        }

        [Test]
        public void ChunkedFeed_MatchesSingleShot()
        {
            var samples = new float[1000];
            for (int i = 0; i < samples.Length; i++) samples[i] = (i % 37) / 37f * ((i & 1) == 0 ? 1f : -1f);

            var once = WaveformPeaks.Build(samples, 1, 1000, 20.0);

            var b = new WaveformPeaks.Builder(1, 1000, 20.0);    // 分批：刻意用不對齊格子的塊大小
            for (int off = 0; off < samples.Length; off += 33)
            {
                int n = System.Math.Min(33, samples.Length - off);
                var chunk = new float[n];
                System.Array.Copy(samples, off, chunk, 0, n);
                b.Feed(chunk, n);
            }
            var chunked = b.Finish();

            Assert.AreEqual(once.Count, chunked.Count);
            for (int i = 0; i < once.Count; i++)
            {
                Assert.AreEqual(once.Peak[i], chunked.Peak[i], 1e-6f, "peak bucket " + i);
                Assert.AreEqual(once.Rms[i], chunked.Rms[i], 1e-6f, "rms bucket " + i);
            }
        }

        [Test]
        public void AtMs_MapsTimeToBucket_AndClampsOutOfRange()
        {
            var samples = new float[300];
            for (int i = 200; i < 300; i++) samples[i] = 1.0f;   // 只有第 2 格（200~300ms）有聲音
            var w = WaveformPeaks.Build(samples, 1, 1000, 100.0);

            Assert.AreEqual(0f, w.RmsAtMs(50.0), 1e-6f);
            Assert.AreEqual(1f, w.RmsAtMs(250.0), 1e-6f);
            Assert.AreEqual(1f, w.PeakAtMs(250.0), 1e-6f);
            Assert.AreEqual(0f, w.PeakAtMs(-10.0), 1e-6f);       // 界外（音樂起點前的無聲數拍）
            Assert.AreEqual(0f, w.PeakAtMs(999999.0), 1e-6f);    // 界外（歌尾之後）
        }

        [Test]
        public void Silence_DoesNotDivideByZero()
        {
            var w = WaveformPeaks.Build(new float[500], 1, 1000, 100.0);
            Assert.AreEqual(5, w.Count);
            foreach (var p in w.Peak) Assert.AreEqual(0f, p, 1e-6f);
            foreach (var r in w.Rms) Assert.AreEqual(0f, r, 1e-6f);
        }
    }
}
