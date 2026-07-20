using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>Guards <see cref="SdoExtracted.BlurAlphaGaussian"/>, the alpha-edge softener used by
    /// <see cref="SdoExtracted.LoadAnSoloMip"/> to fix 使用者回報「room 圓鈕邊緣鋸齒/破碎」: the room buttons render at ~0.8×,
    /// where the GPU samples mostly mip0, so mip0 itself needs a real anti-aliased edge. These verify the softener widens
    /// a hard 0↔255 boundary into a graded ramp, preserves the deep interior/exterior, leaves RGB alone, and is a no-op
    /// when sigma ≤ 0.</summary>
    public class BlurAlphaGaussianTests
    {
        private static Color32[] Fill(int w, int h, byte a)
        {
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(10, 20, 30, a);
            return px;
        }

        [Test]
        public void HardEdge_GainsGradedRamp()
        {
            // left half opaque, right half transparent → hard vertical edge at x=10 (wide enough that the ends sit
            // beyond the ~σ·3 blur radius, so they stay fully opaque / fully transparent).
            int w = 24, h = 6;
            var px = Fill(w, h, 0);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < 12; x++) px[y * w + x].a = 255;
            SdoExtracted.BlurAlphaGaussian(px, w, h, 1.8f);

            int row = 3 * w;
            Assert.AreEqual(255, px[row + 0].a, "deep interior stays opaque");
            Assert.AreEqual(0, px[row + w - 1].a, "deep exterior stays transparent");
            // a genuine ramp now spans several texels across the old hard step
            int graded = 0;
            for (int x = 0; x < w; x++) { int a = px[row + x].a; if (a > 8 && a < 248) graded++; }
            Assert.That(graded, Is.GreaterThanOrEqualTo(3), "edge should span multiple partial-alpha texels (σ≈1.8)");
            // monotonic non-increasing across the edge
            for (int x = 0; x < w - 1; x++)
                Assert.That(px[row + x].a, Is.GreaterThanOrEqualTo(px[row + x + 1].a));
        }

        [Test]
        public void StrongerSigma_WidensRamp()
        {
            int w = 16, h = 4;
            Color32[] Make() { var p = Fill(w, h, 0); for (int y = 0; y < h; y++) for (int x = 0; x < 8; x++) p[y * w + x].a = 255; return p; }
            int Graded(Color32[] p) { int c = 0, row = 1 * w; for (int x = 0; x < w; x++) { int a = p[row + x].a; if (a > 8 && a < 248) c++; } return c; }
            var soft = Make(); SdoExtracted.BlurAlphaGaussian(soft, w, h, 1.0f);
            var wide = Make(); SdoExtracted.BlurAlphaGaussian(wide, w, h, 2.6f);
            Assert.That(Graded(wide), Is.GreaterThan(Graded(soft)), "larger sigma → wider anti-aliased band");
        }

        [Test]
        public void RgbChannels_Untouched()
        {
            var px = Fill(8, 8, 0);
            for (int i = 0; i < 24; i++) px[i].a = 255;
            SdoExtracted.BlurAlphaGaussian(px, 8, 8, 1.8f);
            foreach (var c in px) { Assert.AreEqual(10, c.r); Assert.AreEqual(20, c.g); Assert.AreEqual(30, c.b); }
        }

        [Test]
        public void ZeroOrNegativeSigma_IsNoOp()
        {
            var px = Fill(6, 6, 0);
            for (int i = 0; i < 18; i++) px[i].a = 255;
            var before = (Color32[])px.Clone();
            SdoExtracted.BlurAlphaGaussian(px, 6, 6, 0f);
            for (int i = 0; i < px.Length; i++) Assert.AreEqual(before[i].a, px[i].a);
        }

        [Test]
        public void Degenerate_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => SdoExtracted.BlurAlphaGaussian(null, 6, 6, 1.8f));
            Assert.DoesNotThrow(() => SdoExtracted.BlurAlphaGaussian(new Color32[2], 6, 6, 1.8f));   // undersized array
        }
    }
}
