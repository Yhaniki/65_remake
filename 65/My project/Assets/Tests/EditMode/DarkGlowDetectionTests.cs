using System.IO;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// REAL-DATA guard for <see cref="DdsLoader.LooksLikeDarkGlowDxt1"/> (which frame-sets get DE-BACKED) and
    /// <see cref="DdsLoader.LoadDxt1BlackKeyed"/> (the de-back itself). SDO ships flying-wing animation frames as DXT1
    /// (no alpha) on a black field; drawn straight, the alpha-less black becomes a solid rectangle / a muddy 1-bit edge
    /// (使用者「fly pink butterfly f 邊緣去背沒修好」). The fix DERIVES an alpha from brightness so the black keys to
    /// transparent and the coloured wing stays SOLID, then draws it alpha-blend (an earlier additive attempt was WRONG —
    /// it made the whole wing translucent: 使用者「整個變得半透明」). The detector originally keyed on a DARK BORDER, which
    /// 008448 fails because it decorates its edges with sparkle stars (borderMean≈55); a dark-BACKGROUND fraction alone
    /// also mis-fires (a dark opaque shoe has as much black), so it keys on a BIMODAL "lots of black AND lots of bright".
    /// These rows pin all of that against the shipped bytes so it can't silently drift back and re-break the butterfly.
    /// </summary>
    public class DarkGlowDetectionTests
    {
        private static string Avatar(string file)
        {
            var p = SdoAvatarBuilder.ResolveAvatarFile("AVATAR/" + file);
            return !string.IsNullOrEmpty(p) && File.Exists(p) ? p : null;
        }

        // Read back a (GPU) decoded texture's pixels via a blit (DdsLoader textures aren't CPU-readable).
        private static Color32[] Readback(Texture2D t)
        {
            var trt = RenderTexture.GetTemporary(t.width, t.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(t, trt);
            var prev = RenderTexture.active; RenderTexture.active = trt;
            var rd = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false);
            rd.ReadPixels(new Rect(0, 0, t.width, t.height), 0, 0); rd.Apply();
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(trt);
            var px = rd.GetPixels32(); Object.DestroyImmediate(rd); return px;
        }

        [Test]
        public void ButterflyGlowFrame_DXT1OnBlack_IsDetectedAsGlow()
        {
            var p = Avatar("008448_WOMAN_CHIBANG_1.DDS");   // FLY Pink Butterfly f — a texanim frame (DXT1, glow-on-black)
            if (p == null) Assert.Ignore("AVATAR data root not found");
            Assert.IsTrue(DdsLoader.LooksLikeDarkGlowDxt1(File.ReadAllBytes(p)),
                "008448 wing frame must route to additive (its sparkle-lit edges give a bright border → dark-BORDER test " +
                "misses it; the dark-BACKGROUND fraction must still catch it)");
        }

        [Test]
        public void ButterflyBaseFrame_DXT3WithAlpha_IsNotGlow()
        {
            var p = Avatar("008448_WOMAN_CHIBANG_.DDS");   // the base is DXT3 (carries alpha) → the alpha path handles it
            if (p == null) Assert.Ignore("AVATAR data root not found");
            Assert.IsFalse(DdsLoader.LooksLikeDarkGlowDxt1(File.ReadAllBytes(p)), "DXT3 (has alpha) is never the glow path");
        }

        [Test]
        public void OpaqueDxt1_SolidTexture_IsNotGlow()
        {
            var p = Avatar("024977_woman_shoes.dds");   // a plain opaque DXT1 (no dominant black field) — must stay alpha/opaque
            if (p == null) Assert.Ignore("AVATAR data root not found");
            Assert.IsFalse(DdsLoader.LooksLikeDarkGlowDxt1(File.ReadAllBytes(p)),
                "a solid opaque DXT1 has no dark background → must not be mistaken for a glow");
        }

        [Test]
        public void BlackKeyedButterflyFrame_CutsTheBlack_KeepsTheWingSolid()
        {
            var p = Avatar("008448_WOMAN_CHIBANG_1.DDS");
            if (p == null) Assert.Ignore("AVATAR data root not found");
            var tex = DdsLoader.LoadDxt1BlackKeyed(File.ReadAllBytes(p));
            Assert.IsNotNull(tex, "a DXT1 frame must decode");
            var px = Readback(tex);
            int transparent = 0, opaque = 0;
            foreach (var c in px) { if (c.a == 0) transparent++; else if (c.a == 255) opaque++; }
            double tf = transparent / (double)px.Length, of = opaque / (double)px.Length;
            // the de-back must BOTH cut the black background (a big transparent population) AND keep the wing SOLID (a big
            // fully-opaque population). Additive/1-bit failures show up as one of these collapsing.
            Assert.Greater(tf, 0.15, "the black background must key OUT to transparent (α=0)");
            Assert.Greater(of, 0.30, "the coloured wing must stay SOLID (α=255), not go translucent");
            Object.DestroyImmediate(tex);
        }
    }
}
