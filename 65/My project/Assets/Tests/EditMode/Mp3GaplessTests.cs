using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Mp3Decoder gapless trim — the fix that makes imported osu!/StepMania (mp3) charts line up like they do in
    /// their own game. NLayer decodes every MPEG frame verbatim (no LAME encoder-delay/padding trim), so its output
    /// leads a gapless decoder (osu! BASS / the decode StepMania #OFFSET is calibrated against) by
    /// encoderDelay + 529 samples. These cover the two pure pieces:
    ///   • <see cref="Mp3Decoder.TryParseLameGapless"/> — pull encoder delay + padding out of the LAME tag.
    ///   • <see cref="Mp3Decoder.GaplessTrim"/> — turn those into leading-skip / keep frame counts.
    /// The numbers are ALBIDA.mp3's, verified sample-exact against libsndfile (delay 576, pad 1776 → skip 1105,
    /// keep 5091792 out of 5094144).
    /// </summary>
    public class Mp3GaplessTests
    {
        // Build an mp3 first-frame region: "Info"/"Xing" VBR/CBR header, then the "LAME" extension whose
        // delay+padding 3-byte field sits at LAME+0x15. delay/padding are packed 12 bits each (delay high).
        private static byte[] Frame(string vbrTag, bool withLame, int delay, int padding)
        {
            var b = new byte[64];
            Put(b, 4, vbrTag);                       // Xing/Info near the top of the frame
            if (withLame)
            {
                Put(b, 20, "LAME");                  // LAME extension right after the header
                int o = 20 + 0x15;                   // delay/padding field
                int v = ((delay & 0xFFF) << 12) | (padding & 0xFFF);
                b[o] = (byte)(v >> 16); b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)v;
            }
            return b;
        }

        private static void Put(byte[] b, int at, string s)
        {
            for (int i = 0; i < s.Length; i++) b[at + i] = (byte)s[i];
        }

        [Test]
        public void ParsesLameEncoderDelayAndPadding()
        {
            Assert.IsTrue(Mp3Decoder.TryParseLameGapless(Frame("Info", true, 576, 1776), out int d, out int p));
            Assert.AreEqual(576, d);
            Assert.AreEqual(1776, p);
        }

        [Test]
        public void ParsesXingVariantToo()
        {
            Assert.IsTrue(Mp3Decoder.TryParseLameGapless(Frame("Xing", true, 1024, 300), out int d, out int p));
            Assert.AreEqual(1024, d);
            Assert.AreEqual(300, p);
        }

        [Test]
        public void NoLameTagIsNotGapless()
        {
            // Xing/Info present but no LAME extension → we have no delay data → leave the audio untrimmed.
            Assert.IsFalse(Mp3Decoder.TryParseLameGapless(Frame("Info", false, 0, 0), out _, out _));
            // No VBR/CBR header at all (e.g. a raw stream) → also not gapless.
            Assert.IsFalse(Mp3Decoder.TryParseLameGapless(new byte[64], out _, out _));
            Assert.IsFalse(Mp3Decoder.TryParseLameGapless(null, out _, out _));
        }

        [Test]
        public void TrimMatchesAlbidaGaplessLength()
        {
            // ALBIDA.mp3, verified sample-exact vs libsndfile: NLayer 5094144 frames → skip 1105, keep 5091792.
            Mp3Decoder.GaplessTrim(5094144, 576, 1776, out int skip, out int keep);
            Assert.AreEqual(576 + Mp3Decoder.Mp3DecoderDelay, skip);   // 1105
            Assert.AreEqual(1105, skip);
            Assert.AreEqual(5091792, keep);
            Assert.AreEqual(5094144, skip + keep + (1776 - Mp3Decoder.Mp3DecoderDelay));  // skip + keep + tail == total
        }

        [Test]
        public void PaddingBelowDecoderDelayTrimsNoTail()
        {
            // padding < 529 → tail clamps to 0 (never trims real audio). keep = total − skip only.
            Mp3Decoder.GaplessTrim(10000, 576, 100, out int skip, out int keep);
            Assert.AreEqual(1105, skip);
            Assert.AreEqual(10000 - 1105, keep);
        }

        [Test]
        public void ClampsWhenBufferShorterThanPriming()
        {
            // A buffer shorter than the leading priming must not produce negative/oversized indices.
            Mp3Decoder.GaplessTrim(500, 576, 1776, out int skip, out int keep);
            Assert.AreEqual(500, skip);   // clamped to total
            Assert.AreEqual(0, keep);
        }

        [Test]
        public void EmptyBufferIsNoOp()
        {
            Mp3Decoder.GaplessTrim(0, 576, 1776, out int skip, out int keep);
            Assert.AreEqual(0, skip);
            Assert.AreEqual(0, keep);
        }
    }
}
