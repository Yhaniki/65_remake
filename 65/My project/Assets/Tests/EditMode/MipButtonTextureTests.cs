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

        // 下排功能鈕(win3 工具列:泡泡/表情/喇叭/大聲公/寵物/翅膀/衣櫥/手環/信件)是同一種 31~33px 紫色圓盤,盤緣同樣是軟
        // AA 邊(半徑剖面 α 237→138→10 — α≈138 那圈正好卡在 clip 門檻上,硬裁會把它 binarise → 鋸齒)。它們跟 head-bar
        // 走同一條 circle 路徑;這裡逐顆守門,免得日後有人把某顆改回 AnSoloAA。
        private static readonly string[] BottomBarDiscs =
        {
            "OpenRecord_a", "BtnExpression_1", "BtnSpeaker_1", "LoudSpeaker_1", "BtnPet_1",
            "RoomWing", "RoomCloset001", "Bangle0", "Emai0",
        };

        [Test]
        public void LoadAnSoloCircleMip_BottomBarDiscs_KeepSoftRim_AndStayLogicalSize()
        {
            var dir = RoomDir();
            if (dir == null) Assert.Ignore("ROOM art not present in this environment.");
            int ss = SdoExtracted.ButtonSupersample;

            foreach (var an in BottomBarDiscs)
            {
                var solo = SdoExtracted.LoadAnSolo(dir, an, 0);
                var circ = SdoExtracted.LoadAnSoloCircleMip(dir, an, 0);
                var clip = SdoExtracted.LoadAnSoloMip(dir, an, 0);
                if (solo == null || circ == null || clip == null) { Assert.Ignore(an + " crop not present."); return; }

                Assert.Greater(circ.texture.mipmapCount, 1, an + ": circular-AA texture must carry a mip chain");
                Assert.AreEqual(FilterMode.Trilinear, circ.texture.filterMode, an + ": must sample trilinear across mips");
                Assert.AreEqual(solo.rect.width * ss, circ.texture.width, an + ": texture width should be SS× the crop");
                Assert.AreEqual((float)ss, circ.pixelsPerUnit, an + ": ppu = SS so it displays at the logical size");

                int Mid(Sprite s)
                {
                    int n = 0;
                    foreach (var c in s.texture.GetPixels32()) if (c.a >= 40 && c.a <= 215) n++;
                    return n;
                }
                Assert.Greater(Mid(circ), Mid(clip), an + ": smoothstep round rim must be softer than the binarised clip edge");

                // …and the round mask must sit ON the disc, not eat into it: the opaque core survives (a mask fitted to the
                // wrong radius would carve the button smaller — 衣櫥鈕的盤緣 α 最低,是這裡最敏感的一顆).
                int Opaque(Sprite s)
                {
                    int n = 0;
                    foreach (var c in s.texture.GetPixels32()) if (c.a >= 250) n++;
                    return n;
                }
                Assert.GreaterOrEqual(Opaque(circ), Opaque(clip) * 0.9f, an + ": circular mask must not shrink the visible disc");
            }
        }

        // 大顆實心圓球(開始 Room15 / 準備 Room12 / 取消 c_ready0 / 旁觀 BtnLook_1 / 進入 Room92)是 55~73px 手繪圓盤:α 幾乎是二元的
        // (0 或 255 佔圓周 ~2/3),深色描邊也一格寬一格窄 → clip+SS 忠實重現那圈階梯,放大就是一圈黑缺口(使用者回報
        // 「開始和旁觀的大圓 邊緣還是鋸齒很破碎」)。CircleMask 治不了:遮罩只會減,補不回缺口,而且階梯同時在 RGB 描邊上。
        // LoadAnSoloSmoothDiscMip 改在極座標把描邊沿圓周低通(階梯是角向高頻、真美術幾乎不變)+ alpha 用解析圓重建。
        private static readonly string[] BigDiscs = { "Room15", "Room12", "c_ready0", "BtnLook_1", "Room92" };

        [Test]
        public void LoadAnSoloSmoothDiscMip_BigDiscs_AreSupersampledMipmappedTrilinear()
        {
            var dir = RoomDir();
            if (dir == null) Assert.Ignore("ROOM art not present in this environment.");
            int ss = SdoExtracted.ButtonSupersample;
            foreach (var an in BigDiscs)
            {
                var solo = SdoExtracted.LoadAnSolo(dir, an, 0);
                var s = SdoExtracted.LoadAnSoloSmoothDiscMip(dir, an, 0);
                if (solo == null || s == null) { Assert.Ignore(an + " crop not present."); return; }
                Assert.Greater(s.texture.mipmapCount, 1, an + ": smooth-disc texture must carry a mip chain");
                Assert.AreEqual(FilterMode.Trilinear, s.texture.filterMode, an + ": must sample trilinear across mips");
                Assert.AreEqual(solo.rect.width * ss, s.texture.width, an + ": texture width should be SS× the crop");
                Assert.AreEqual((float)ss, s.pixelsPerUnit, an + ": ppu = SS so it displays at the logical size");
                Assert.IsTrue(s.texture.isReadable, an + ": must stay readable — the big discs use UIKit.SetAlphaHit");
            }
        }

        [Test]
        public void LoadAnSoloSmoothDiscMip_BigDiscs_RimIsDeStairedAndNotShrunk()
        {
            var dir = RoomDir();
            if (dir == null) Assert.Ignore("ROOM art not present in this environment.");
            foreach (var an in BigDiscs)
            {
                var clip = SdoExtracted.LoadAnSoloMip(dir, an, 0);
                var disc = SdoExtracted.LoadAnSoloSmoothDiscMip(dir, an, 0);
                if (clip == null || disc == null) { Assert.Ignore(an + " crop not present."); return; }

                float clipHf = RimHighFreq(clip.texture), discHf = RimHighFreq(disc.texture);
                Assert.Less(discHf, clipHf * 0.7f,
                    $"{an}: rim should be de-staired — high-frequency energy along the circumference {discHf:F2} vs clip {clipHf:F2} " +
                    "(measured 0.44-0.55× when the polar low-pass is in place)");

                int Opaque(Sprite s)
                {
                    int n = 0;
                    foreach (var c in s.texture.GetPixels32()) if (c.a >= 250) n++;
                    return n;
                }
                Assert.GreaterOrEqual(Opaque(disc), Opaque(clip) * 0.9f, an + ": the disc must not come out smaller");
            }
        }

        /// <summary>HIGH-FREQUENCY energy of the rim colour walking around the circumference (three rings just inside the
        /// edge, bilinear samples, minus a 15-sample moving average so the sphere's own shading doesn't count). A
        /// staircase outline jumps every few samples — that's the 破碎 look; a de-staired rim is nearly constant.
        /// Centre/radius are fitted from the texture itself (α≥128 centroid + area) so it works on any loader path.
        /// Plain |Δ| between neighbours does NOT separate the two (measured 0.86-1.09×) — the staircase has to be
        /// isolated from the artwork's own gradient first.</summary>
        private static float RimHighFreq(Texture2D tex, int samples = 720)
        {
            var px = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            double sx = 0, sy = 0; int n = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (px[y * w + x].a >= 128) { sx += x; sy += y; n++; }
            if (n < 16) return 0f;
            float cx = (float)(sx / n), cy = (float)(sy / n);
            float R = Mathf.Sqrt(n / Mathf.PI);

            float total = 0f; int rings = 0;
            foreach (float k in new[] { 0.94f, 0.97f, 1.00f })
            {
                var lum = new float[samples];
                for (int i = 0; i < samples; i++)
                {
                    float t = i * 2f * Mathf.PI / samples;
                    lum[i] = SampleLuminance(px, w, h, cx + R * k * Mathf.Cos(t), cy + R * k * Mathf.Sin(t));
                }
                total += DetrendedStd(lum); rings++;
            }
            return total / rings;
        }

        private static float DetrendedStd(float[] v)
        {
            int n = v.Length, k = 15;
            double acc = 0;
            for (int i = 0; i < n; i++)
            {
                double s = 0;
                for (int j = -k / 2; j <= k / 2; j++) s += v[((i + j) % n + n) % n];
                double d = v[i] - s / k;
                acc += d * d;
            }
            return Mathf.Sqrt((float)(acc / n));
        }

        private static float SampleLuminance(Color32[] px, int w, int h, float x, float y)
        {
            x = Mathf.Clamp(x, 0f, w - 1.001f); y = Mathf.Clamp(y, 0f, h - 1.001f);
            int x0 = (int)x, y0 = (int)y, x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
            float fx = x - x0, fy = y - y0;
            float L(Color32 c) => (c.r * 0.299f + c.g * 0.587f + c.b * 0.114f) * (c.a / 255f);
            return Mathf.Lerp(Mathf.Lerp(L(px[y0 * w + x0]), L(px[y0 * w + x1]), fx),
                              Mathf.Lerp(L(px[y1 * w + x0]), L(px[y1 * w + x1]), fx), fy);
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
