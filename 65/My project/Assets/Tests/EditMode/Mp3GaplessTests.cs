using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Mp3Decoder timing — making imported osu!/StepMania (mp3) charts line up at global-offset 0 like they do in their
    /// home game. Both games decode through MAD (StepMania) / BASS (osu, DWI); those keep the LAME encoder-delay priming
    /// (NO gapless trim) and, for a CBR "Info" header frame they can't recognise, EMIT it as ~26 ms of silence rather
    /// than skipping it (RageSoundReader_MP3.cpp: <c>if(type==INFO) return false</c>). NLayer instead skips that frame,
    /// so our decode starts one frame early — <see cref="Mp3Decoder.ApplyBassInfoFrame"/> re-inserts it. These cover the
    /// two pure pieces that decide the fix: is the header an Info tag, and how big is one frame.
    /// Measured on "Be Crazy For Me.mp3" (MPEG-1 L3, Info tag): NLayer-raw onset 0.504 s + 1 frame (1152/44100 ≈ 26 ms)
    /// = 0.530 s ≈ chart beat 4 at 0.537 s; the old gapless trim instead put it at 0.479 s (≈ 51 ms early).
    /// </summary>
    public class Mp3GaplessTests
    {
        private static void Put(byte[] b, int at, string s) { for (int i = 0; i < s.Length; i++) b[at + i] = (byte)s[i]; }

        // First frame header + a Xing/Info tag at the usual offset (after MPEG1-stereo side info).
        private static byte[] Frame(byte hdr1, string vbrTag, int size = 128)
        {
            var b = new byte[size];
            b[0] = 0xFF; b[1] = hdr1;         // frame sync + version/layer
            if (vbrTag != null) Put(b, 36, vbrTag);
            return b;
        }

        [Test]
        public void FrameSamples_ReadsMpegVersion()
        {
            // 0xFB = MPEG-1 Layer III → 1152; 0xF3 = MPEG-2 Layer III → 576; 0xE3 = MPEG-2.5 → 576.
            Assert.AreEqual(1152, Mp3Decoder.FrameSamplesPerChannel(Frame(0xFB, null)));
            Assert.AreEqual(576, Mp3Decoder.FrameSamplesPerChannel(Frame(0xF3, null)));
            Assert.AreEqual(576, Mp3Decoder.FrameSamplesPerChannel(Frame(0xE3, null)));
        }

        [Test]
        public void FrameSamples_ZeroWhenNoSync()
        {
            Assert.AreEqual(0, Mp3Decoder.FrameSamplesPerChannel(new byte[64]));   // all zero → no 0xFFEx sync
            Assert.AreEqual(0, Mp3Decoder.FrameSamplesPerChannel(null));
        }

        [Test]
        public void InfoHeaderFrame_TrueForInfo_FalseForXing()
        {
            // "Info" (CBR) → BASS/DWI emit it as a silence frame → must be re-inserted. This is Be Crazy For Me's case.
            Assert.IsTrue(Mp3Decoder.HasInfoHeaderFrame(Frame(0xFB, "Info")));
            // "Xing" (VBR) → skipped by MAD/BASS too → nothing to re-insert.
            Assert.IsFalse(Mp3Decoder.HasInfoHeaderFrame(Frame(0xFB, "Xing")));
            // No VBR/CBR tag at all → no header frame.
            Assert.IsFalse(Mp3Decoder.HasInfoHeaderFrame(Frame(0xFB, null)));
            Assert.IsFalse(Mp3Decoder.HasInfoHeaderFrame(null));
        }

        [Test]
        public void OsuGapless_TrimsPrimingFromTheFront()
        {
            // osu/BASS drops 576+529 = 1105 frames of priming; verified to align SDO Pack9's osu charts at offset 0.
            Assert.AreEqual(1105, Mp3Decoder.OsuGaplessTrim);
            // stereo: 1105 frames = 2210 interleaved samples removed.
            Assert.AreEqual(100000 - 2210, Mp3Decoder.OsuGaplessKeptLength(100000, 2));
            Assert.AreEqual(100000 - 1105, Mp3Decoder.OsuGaplessKeptLength(100000, 1));   // mono
            // a buffer shorter than the priming is emptied, never negative.
            Assert.AreEqual(0, Mp3Decoder.OsuGaplessKeptLength(1000, 2));
            Assert.AreEqual(0, Mp3Decoder.OsuGaplessKeptLength(0, 2));
        }

        [Test]
        public void InfoHeaderFrame_OnlyLooksInsideTheFirstFrame()
        {
            // "Info" as 4 bytes of real audio data 1200 B in is NOT the header tag → must not be treated as one.
            var b = new byte[2000];
            b[0] = 0xFF; b[1] = 0xFB;
            Put(b, 1200, "Info");
            Assert.IsFalse(Mp3Decoder.HasInfoHeaderFrame(b));
            // The genuine tag near the frame start is still found.
            Put(b, 36, "Info");
            Assert.IsTrue(Mp3Decoder.HasInfoHeaderFrame(b));
        }
    }
}
