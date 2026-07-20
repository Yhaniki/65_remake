using System.IO;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>Guards the fix for 使用者回報「room 開始/旁觀 圓鈕邊緣鋸齒/破碎」. Root cause (found by capturing the button
    /// through a real Unity camera at several scales): the room buttons render at &lt;native size, and bilinear
    /// MINIFICATION without mipmaps aliases the near 1-bit round edge. <see cref="SdoExtracted.LoadAnSoloMip"/> gives the
    /// button texture a mipmap chain + Trilinear filtering so the GPU box-filters the downsample. Integration test: loads
    /// the real 開始 (Room15) crop and asserts the texture is mipmapped + trilinear. Skipped (not failed) when the game
    /// data tree isn't present in this environment.</summary>
    public class MipButtonTextureTests
    {
        private static string RoomDir()
        {
            // Prefer the resolved data root; fall back to the local clean pack used by the dev machine.
            foreach (var d in new[] { Path.Combine(SdoExtracted.Root, "UI", "ROOM"), @"H:/65_remake_clean/DATA/UI/ROOM" })
                if (!string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "Room15.an"))) return d;
            return null;
        }

        [Test]
        public void LoadAnSoloMip_Room15_IsMipmappedAndTrilinear()
        {
            var dir = RoomDir();
            if (dir == null) Assert.Ignore("ROOM art (Room15.an) not present in this environment.");

            var sprite = SdoExtracted.LoadAnSoloMip(dir, "Room15", 0);
            Assert.IsNotNull(sprite, "LoadAnSoloMip returned null for a present crop");
            var tex = sprite.texture;
            Assert.Greater(tex.mipmapCount, 1, "button texture must carry a mip chain (else minification aliases the edge)");
            Assert.AreEqual(FilterMode.Trilinear, tex.filterMode, "button texture must sample trilinear across mips");
        }

        [Test]
        public void LoadAnSoloMip_Room15_HasNoBrightOuterGlow()
        {
            // The art bakes a jagged low-alpha bright-cyan glow outside the dark rim; the loader must clip it so no
            // partially-visible bright-cyan texel survives (使用者回報「描邊之外的異常亮光」). The softened edge ramp is the
            // dark-rim colour, so it never trips the bright-cyan test.
            var dir = RoomDir();
            if (dir == null) Assert.Ignore("ROOM art (Room15.an) not present in this environment.");

            var sprite = SdoExtracted.LoadAnSoloMip(dir, "Room15", 0);
            var px = sprite.texture.GetPixels32();
            int glow = 0;
            foreach (var c in px)
                if (c.a > 10 && c.a < 128 && c.r > 150 && c.g > 200 && c.b > 200) glow++;
            Assert.AreEqual(0, glow, "no partially-transparent bright-cyan texels should remain (outer glow must be clipped)");
        }

        [Test]
        public void LoadAnSolo_Room15_HasNoMipmaps()
        {
            // The plain solo path (used where 1:1 is guaranteed) stays single-level bilinear — only the button path opts in.
            var dir = RoomDir();
            if (dir == null) Assert.Ignore("ROOM art (Room15.an) not present in this environment.");

            var sprite = SdoExtracted.LoadAnSolo(dir, "Room15", 0);
            Assert.IsNotNull(sprite);
            Assert.AreEqual(1, sprite.texture.mipmapCount, "plain AnSolo texture should have no mip chain");
        }
    }
}
