using System.IO;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>Guards the fix for 使用者回報「room 開始/旁觀 圓鈕邊緣鋸齒/破碎/往外糊/描邊外亮光」. Root cause (found by
    /// capturing the button through a real Unity camera at 1:1 and fullscreen): the ~73px near 1-bit disc shows ~1:1 at
    /// the default 800×600 window, where a hard edge is jagged and a blur is mushy. <see cref="SdoExtracted.LoadAnSoloMip"/>
    /// SUPERSAMPLES it — clips the baked outer glow, upsamples <see cref="SdoExtracted.ButtonSupersample"/>× onto a
    /// mipmapped texture, and returns it at ppu = SS so it displays at the logical size (crisp ~1px AA edge). Integration
    /// test; skipped (not failed) when the game data tree isn't present.</summary>
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
        public void LoadAnSoloMip_Room15_IsSupersampledMipmappedTrilinear()
        {
            var dir = RoomDir();
            if (dir == null) Assert.Ignore("ROOM art (Room15.an) not present in this environment.");

            var solo = SdoExtracted.LoadAnSolo(dir, "Room15", 0);
            var sprite = SdoExtracted.LoadAnSoloMip(dir, "Room15", 0);
            Assert.IsNotNull(sprite, "LoadAnSoloMip returned null for a present crop");
            var tex = sprite.texture;
            int ss = SdoExtracted.ButtonSupersample;

            Assert.Greater(tex.mipmapCount, 1, "supersampled texture must carry a mip chain (GPU downsamples it to display size)");
            Assert.AreEqual(FilterMode.Trilinear, tex.filterMode, "must sample trilinear across mips");
            // texture is SS× the crop, but pixelsPerUnit = SS so it DISPLAYS at the logical size (rect.size / ppu).
            Assert.AreEqual(solo.rect.width * ss, tex.width, "texture width should be SS× the crop");
            Assert.AreEqual((float)ss, sprite.pixelsPerUnit, "sprite must report ppu = SS so ApplySprite sizes it to logical px");
            Assert.AreEqual(solo.rect.width, sprite.rect.width / sprite.pixelsPerUnit, 0.5f, "logical display size == native crop");
        }

        [Test]
        public void LoadAnSoloMip_Room15_HasNoBrightOuterGlow()
        {
            // The art bakes a jagged low-alpha bright-cyan glow outside the dark rim; the loader clips it so no
            // partially-visible bright-cyan texel survives (使用者回報「描邊之外的異常亮光」).
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
        public void LoadAnSolo_Room15_IsPlainNativeBilinear()
        {
            // The plain solo path stays single-level bilinear at ppu 1 — only the button path opts into supersampling.
            var dir = RoomDir();
            if (dir == null) Assert.Ignore("ROOM art (Room15.an) not present in this environment.");

            var sprite = SdoExtracted.LoadAnSolo(dir, "Room15", 0);
            Assert.IsNotNull(sprite);
            Assert.AreEqual(1, sprite.texture.mipmapCount, "plain AnSolo texture should have no mip chain");
            Assert.AreEqual(1f, sprite.pixelsPerUnit, "plain AnSolo stays ppu 1 (displays at native size)");
        }
    }
}
