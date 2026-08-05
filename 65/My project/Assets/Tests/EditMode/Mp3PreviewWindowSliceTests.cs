using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// <see cref="Mp3Decoder.WindowSlice"/> —— 選歌試聽窗切在已解碼 PCM 的哪一段。
    ///
    /// 為什麼會有這條路徑:選歌試聽平常是用 NLayer 串流(邊播邊解、開得快),但 NLayer 的 seek 對
    /// <b>MPEG-2 / MPEG-2.5</b> 的 mp3(取樣率 ≤ 24 kHz、每幀 576 樣本)會算出界丟例外 —— 實例是
    /// Over the Ocean[Blue] 的 mp3(22050 Hz、64 kbps):從 0 循序讀完全正常(所以遊戲裡放得出來),
    /// 但 seek 到它的 #SAMPLESTART 51.7 s 就爆,試聽因此整段無聲。那種檔改用 libmad 整檔解完再切窗,
    /// 切窗這半段的算術就是這裡測的東西。
    /// </summary>
    public class Mp3PreviewWindowSliceTests
    {
        private const int Sr = 22050;   // Over the Ocean[Blue] 的取樣率
        private const int Ch = 2;

        private static int Samples(double seconds) => (int)(seconds * Sr) * Ch;

        [Test]
        public void HonoursAnExplicitStart()
        {
            // 129 秒的歌、#SAMPLESTART 51.7 s、#SAMPLELENGTH 12 s
            Assert.IsTrue(Mp3Decoder.WindowSlice(Samples(129.0), Ch, Sr, 51.7f, 12f, 0.4f,
                                                 out int from, out int count));
            Assert.AreEqual((int)(51.7 * Sr) * Ch, from);
            Assert.AreEqual(12 * Sr * Ch, count);
        }

        [Test]
        public void NegativeStartUsesTheAutomaticRatio()
        {
            Assert.IsTrue(Mp3Decoder.WindowSlice(Samples(100.0), Ch, Sr, -1f, 20f, 0.4f,
                                                 out int from, out int count));
            Assert.AreEqual((int)(40.0 * Sr) * Ch, from);
            Assert.AreEqual(20 * Sr * Ch, count);
        }

        [Test]
        public void WindowIsClampedInsideTheTrack()
        {
            // 起點離結尾只剩 5 秒 → 窗往前退到「結尾減窗長」,絕不切出界。
            Assert.IsTrue(Mp3Decoder.WindowSlice(Samples(60.0), Ch, Sr, 55f, 20f, 0.4f,
                                                 out int from, out int count));
            Assert.AreEqual((int)(40.0 * Sr) * Ch, from);
            Assert.AreEqual(20 * Sr * Ch, count);
            Assert.LessOrEqual(from + count, Samples(60.0));
        }

        [Test]
        public void WindowLongerThanTheTrackTakesTheWholeTrack()
        {
            int total = Samples(8.0);
            Assert.IsTrue(Mp3Decoder.WindowSlice(total, Ch, Sr, -1f, 20f, 0.4f,
                                                 out int from, out int count));
            Assert.AreEqual(0, from);
            Assert.AreEqual(total, count);
        }

        [Test]
        public void StartPastTheEndFallsBackToTheBeginning()
        {
            // 表頭寫的試聽點比音檔還長(改過音檔卻沒改譜的歌)—— ResolveStart 判定為未指定,
            // 用自動比例,再被夾在音檔內。
            Assert.IsTrue(Mp3Decoder.WindowSlice(Samples(30.0), Ch, Sr, 999f, 12f, 0.4f,
                                                 out int from, out int count));
            Assert.AreEqual((int)(12.0 * Sr) * Ch, from);
            Assert.AreEqual(12 * Sr * Ch, count);
        }

        [Test]
        public void ZeroLengthMeansTheDefaultTwentySeconds()
        {
            Assert.IsTrue(Mp3Decoder.WindowSlice(Samples(100.0), Ch, Sr, 10f, 0f, 0.4f,
                                                 out int from, out int count));
            Assert.AreEqual(10 * Sr * Ch, from);
            Assert.AreEqual(20 * Sr * Ch, count);
        }

        [Test]
        public void SliceIsAlwaysWholeFrames()
        {
            // 交錯樣本:切點一定落在聲道邊界上,否則左右聲道會對調。
            Assert.IsTrue(Mp3Decoder.WindowSlice(Samples(77.0), Ch, Sr, 13.37f, 7.77f, 0.4f,
                                                 out int from, out int count));
            Assert.AreEqual(0, from % Ch);
            Assert.AreEqual(0, count % Ch);
        }

        [Test]
        public void MonoWorks()
        {
            Assert.IsTrue(Mp3Decoder.WindowSlice(60 * Sr, 1, Sr, 10f, 5f, 0.4f,
                                                 out int from, out int count));
            Assert.AreEqual(10 * Sr, from);
            Assert.AreEqual(5 * Sr, count);
        }

        [Test]
        public void InvalidInputSlicesNothing()
        {
            Assert.IsFalse(Mp3Decoder.WindowSlice(0, Ch, Sr, 0f, 12f, 0.4f, out _, out _));
            Assert.IsFalse(Mp3Decoder.WindowSlice(Samples(10.0), 0, Sr, 0f, 12f, 0.4f, out _, out _));
            Assert.IsFalse(Mp3Decoder.WindowSlice(Samples(10.0), Ch, 0, 0f, 12f, 0.4f, out _, out _));
            Assert.IsFalse(Mp3Decoder.WindowSlice(1, Ch, Sr, 0f, 12f, 0.4f, out _, out _));   // 不足一個 frame
        }
    }
}
