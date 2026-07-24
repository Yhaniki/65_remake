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

        // The top head-bar round ICON buttons (設定/邀請/返回/交易/天使) are 34px CommonButtonNew discs whose rim is a WIDE
        // SOFT AA edge (α 145→87→30 over ~3 rings). Feeding them through the plain-mip α<128→0 clip BINARISES that rim into
        // a 1-bit circle → the edge reads jagged/破碎 (使用者回報「右上角圓形按鈕邊緣非常破碎」). LoadAnSoloCircleMip runs
        // CircleMask (smoothstep round edge, halo trimmed) and supersamples WITHOUT clipping, so the soft rim survives.
        private const string HeadBarDisc = "BtnHeadOption_1";   // the gear/settings disc (a CommonButtonNew 34px round button)

        [Test]
        public void LoadAnSoloCircleMip_HeadBar_IsSupersampledMipmappedTrilinear()
        {
            var dir = RoomDir();
            if (dir == null) Assert.Ignore("ROOM art not present in this environment.");
            var solo = SdoExtracted.LoadAnSolo(dir, HeadBarDisc, 0);
            var sprite = SdoExtracted.LoadAnSoloCircleMip(dir, HeadBarDisc, 0);
            if (solo == null || sprite == null) Assert.Ignore("head-bar disc crop not present.");
            int ss = SdoExtracted.ButtonSupersample;
            Assert.Greater(sprite.texture.mipmapCount, 1, "circular-AA texture must carry a mip chain");
            Assert.AreEqual(FilterMode.Trilinear, sprite.texture.filterMode, "must sample trilinear across mips");
            Assert.AreEqual(solo.rect.width * ss, sprite.texture.width, "texture width should be SS× the crop");
            Assert.AreEqual((float)ss, sprite.pixelsPerUnit, "ppu = SS so it displays at the logical size");
        }

        [Test]
        public void LoadAnSoloCircleMip_HeadBar_KeepsSoftRim_NotBinarised()
        {
            // The FIX: the circular path must preserve a SOFT anti-aliased rim, unlike the clip path which binarises it.
            // Count transition (mid-alpha) texels on each SS'd texture — the smoothstep rim yields far more than a
            // clipped-then-upsampled 1-bit edge. Strictly more mid-alpha ⇒ the round edge is smooth, not stair-stepped.
            var dir = RoomDir();
            if (dir == null) Assert.Ignore("ROOM art not present in this environment.");
            var circ = SdoExtracted.LoadAnSoloCircleMip(dir, HeadBarDisc, 0);
            var clip = SdoExtracted.LoadAnSoloMip(dir, HeadBarDisc, 0);
            if (circ == null || clip == null) Assert.Ignore("head-bar disc crop not present.");

            int Mid(Sprite s)
            {
                int n = 0;
                foreach (var c in s.texture.GetPixels32()) if (c.a >= 40 && c.a <= 215) n++;
                return n;
            }
            int circMid = Mid(circ), clipMid = Mid(clip);
            Assert.Greater(circMid, 0, "circular path must keep a soft AA rim (mid-alpha texels present)");
            Assert.Greater(circMid, clipMid, "circular smoothstep rim must be softer (more transition texels) than the binarised clip edge");
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
