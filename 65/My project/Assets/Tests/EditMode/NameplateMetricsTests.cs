using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// Pure-math tests for <see cref="NameplateMetrics"/> — the fake-outline nameplate helpers: the
    /// 16-direction offset ring, the design→physical scale of the 800×600 frame, the horizontal stretch
    /// compensation (4:3 stretched onto 16:9), and the legacy-TextMesh raster/characterSize pairing that
    /// keeps the on-screen height constant while rasterizing at the physical pixel size.
    /// </summary>
    public class NameplateMetricsTests
    {
        private static readonly Rect FullRect = new Rect(0f, 0f, 1f, 1f);              // Stretch mode
        private static readonly Rect Pillar169 = new Rect(0.125f, 0f, 0.75f, 1f);      // 4:3 centred on 16:9

        // ---- Ring ----

        [Test]
        public void Ring_Has_Count_And_Radius()
        {
            var r = NameplateMetrics.Ring(1.4f, 16);
            Assert.AreEqual(16, r.Length);
            foreach (var o in r) Assert.AreEqual(1.4f, o.magnitude, 1e-4f);
        }

        [Test]
        public void Ring_Multiple_Of_4_Hits_Exact_Cardinals()
        {
            var r = NameplateMetrics.Ring(2f, 16);
            Assert.AreEqual(0f, r[0].y, 1e-5f); Assert.AreEqual(2f, r[0].x, 1e-5f);    // +X first
            Assert.AreEqual(0f, r[4].x, 1e-5f); Assert.AreEqual(2f, r[4].y, 1e-5f);    // +Y at a quarter turn
            Assert.AreEqual(-2f, r[8].x, 1e-5f);
            Assert.AreEqual(-2f, r[12].y, 1e-5f);
        }

        [Test]
        public void Ring_Is_Point_Symmetric()   // every offset has its negation → the ring is centred
        {
            var r = NameplateMetrics.Ring(1f, 16);
            foreach (var o in r)
            {
                bool found = false;
                foreach (var p in r) if ((p + o).magnitude < 1e-4f) { found = true; break; }
                Assert.IsTrue(found, $"no negation for {o}");
            }
        }

        // ---- ScaleY / AnisotropyX ----

        [Test]
        public void ScaleY_Fullscreen_1080p_Is_1_8()
            => Assert.AreEqual(1.8f, NameplateMetrics.ScaleY(1080f, FullRect), 1e-4f);

        [Test]
        public void ScaleY_Design_Window_Is_1()
            => Assert.AreEqual(1f, NameplateMetrics.ScaleY(600f, FullRect), 1e-4f);

        [Test]
        public void Anisotropy_Stretch_On_16x9_Is_4_Thirds()   // 1920/800 ÷ 1080/600 = 2.4/1.8
            => Assert.AreEqual(4f / 3f, NameplateMetrics.AnisotropyX(1920f, 1080f, FullRect), 1e-4f);

        [Test]
        public void Anisotropy_Pillarbox_Is_1()                // 4:3 sub-rect on a 16:9 screen → undistorted
            => Assert.AreEqual(1f, NameplateMetrics.AnisotropyX(1920f, 1080f, Pillar169), 1e-4f);

        [Test]
        public void Anisotropy_4x3_Window_Is_1()
            => Assert.AreEqual(1f, NameplateMetrics.AnisotropyX(800f, 600f, FullRect), 1e-4f);

        [Test]
        public void Anisotropy_Degenerate_Screen_Falls_Back_To_1()
            => Assert.AreEqual(1f, NameplateMetrics.AnisotropyX(0f, 0f, FullRect), 1e-4f);

        [Test]
        public void Compensate_Divides_X_Only()
        {
            var o = NameplateMetrics.Compensate(new Vector2(1.4f, 1.4f), 4f / 3f);
            Assert.AreEqual(1.05f, o.x, 1e-4f);
            Assert.AreEqual(1.4f, o.y, 1e-4f);
        }

        // ---- FontPxFor / CharacterSizeFor ----

        [Test]
        public void FontPx_Is_Design_Size_At_Design_Resolution()
            => Assert.AreEqual(22, NameplateMetrics.FontPxFor(22f, 1f));

        [Test]
        public void FontPx_Scales_To_Physical_Pixels()          // 22 design px at 1080p → 40 physical px
            => Assert.AreEqual(40, NameplateMetrics.FontPxFor(22f, 1.8f));

        [Test]
        public void FontPx_Clamps_Both_Ends()
        {
            Assert.AreEqual(8, NameplateMetrics.FontPxFor(2f, 1f));
            Assert.AreEqual(200, NameplateMetrics.FontPxFor(22f, 100f));
        }

        [Test]
        public void CharSize_Matches_Legacy_Calibration_At_64()  // the old hardcoded pair: fontSize 64, px × 0.11
            => Assert.AreEqual(22f * 0.11f, NameplateMetrics.CharacterSizeFor(22f, 64), 1e-4f);

        [Test]
        public void CharSize_Keeps_Onscreen_Height_Invariant()
        {
            // TextMesh height ∝ fontSize × characterSize — the product must not depend on the raster size.
            float at64 = 64 * NameplateMetrics.CharacterSizeFor(22f, 64);
            float at22 = 22 * NameplateMetrics.CharacterSizeFor(22f, 22);
            float at40 = 40 * NameplateMetrics.CharacterSizeFor(22f, 40);
            Assert.AreEqual(at64, at22, 1e-3f);
            Assert.AreEqual(at64, at40, 1e-3f);
        }

        // ---- EmDesignPx / RasterPxForEm (HUD 底列 歌名/LV/時間) ----

        [Test]
        public void EmDesignPx_Legacy_Is_1_28_Times_Design()
            // fontSize 64 × (designPx × 0.2) × 0.1 = 1.28 × designPx —— designPx 11 的標籤其實站 14.08 設計 px 高。
            => Assert.AreEqual(14.08f, NameplateMetrics.EmDesignPx(11f, NameplateMetrics.PxToCharSizeLegacyAt64), 1e-3f);

        [Test]
        public void EmDesignPx_Cjk_Is_0_704_Times_Design()
            => Assert.AreEqual(0.704f * 22f, NameplateMetrics.EmDesignPx(22f, NameplateMetrics.PxToCharSizeAt64), 1e-3f);

        [Test]
        public void RasterPx_Never_Undersamples_The_Em_Box()
        {
            // 迴歸鎖：光柵尺寸不得小於「字實際佔的實體 px 高」，否則字圖被放大 → 就是這次的「歌名很模糊」。
            const float c = NameplateMetrics.PxToCharSizeLegacyAt64;
            foreach (float sy in new[] { 1f, 1.28f, 1.8f, 2.25f, 3.6f })
            {
                float needed = NameplateMetrics.EmDesignPx(11f, c) * sy;          // 螢幕上真正要畫的高度
                int raster = NameplateMetrics.RasterPxForEm(11f, sy, c);
                Assert.GreaterOrEqual(raster, needed, $"sy={sy} 欠取樣：光柵 {raster}px < 顯示 {needed}px");
            }
        }

        [Test]
        public void RasterPx_Is_Em_Height_Times_Supersample()
        {
            const float c = NameplateMetrics.PxToCharSizeLegacyAt64;
            // 1080p 全螢幕：em 14.08 設計 px × 1.8 = 25.34 實體 px，×2 超取樣 → 51。
            Assert.AreEqual(51, NameplateMetrics.RasterPxForEm(11f, 1.8f, c));
            // 800×600 視窗：14.08 × 1 × 2 → 28。
            Assert.AreEqual(28, NameplateMetrics.RasterPxForEm(11f, 1f, c));
        }

        [Test]
        public void RasterPx_Beats_The_Old_FontPxFor_Sizing()
        {
            // 舊寫法拿 designPx(11) 當高度算，比實際 em(14.08) 小 1.28 倍 → 字圖被放大。新函式必須大於它。
            const float c = NameplateMetrics.PxToCharSizeLegacyAt64;
            Assert.Less(NameplateMetrics.FontPxFor(11f, 1.8f), NameplateMetrics.EmDesignPx(11f, c) * 1.8f);
            Assert.Greater(NameplateMetrics.RasterPxForEm(11f, 1.8f, c), NameplateMetrics.FontPxFor(11f, 1.8f));
        }

        [Test]
        public void RasterPx_Gives_Two_Raster_Pixels_Per_Screen_Pixel()
        {
            // 真正決定糊不糊的不變式，且跟「字有多高」怎麼估完全無關：TextMesh 的一個光柵像素恆等於
            // characterSize × 0.1 世界單位(Unity 寫死的縮放)，世界單位 == 設計 px，所以
            //   螢幕px / 光柵px = characterSize × 0.1 × scaleY
            // 要等於 1/超取樣(=0.5)。>1 就是字圖被放大 → 糊；<<1 只是多花 atlas。
            const float c = NameplateMetrics.PxToCharSizeLegacyAt64;
            foreach (float sy in new[] { 1f, 1.8f, 2.25f, 3.6f })
            {
                int fp = NameplateMetrics.RasterPxForEm(11f, sy, c);
                float screenPxPerRasterPx = 0.1f * NameplateMetrics.CharacterSizeFor(11f, fp, c) * sy;
                Assert.AreEqual(1f / NameplateMetrics.HudTextSupersample, screenPxPerRasterPx, 0.02f, $"sy={sy}");
            }
        }

        [Test]
        public void Old_Sizing_Magnified_The_Bitmap()
        {
            // 根因存檔：舊的 FontPxFor 走法在 1080p 每個光柵像素要拉成 1.28 個螢幕像素（放大取樣）。
            const float c = NameplateMetrics.PxToCharSizeLegacyAt64;
            int oldPx = NameplateMetrics.FontPxFor(11f, 1.8f);
            float screenPxPerRasterPx = 0.1f * NameplateMetrics.CharacterSizeFor(11f, oldPx, c) * 1.8f;
            Assert.Greater(screenPxPerRasterPx, 1.2f);
        }

        [Test]
        public void RasterPx_Clamps_Both_Ends()
        {
            const float c = NameplateMetrics.PxToCharSizeLegacyAt64;
            Assert.AreEqual(8, NameplateMetrics.RasterPxForEm(1f, 0.5f, c));       // 極小視窗仍給得出可讀的光柵
            Assert.AreEqual(200, NameplateMetrics.RasterPxForEm(11f, 100f, c));    // 不讓動態字型 atlas 爆掉
        }

        [Test]
        public void RasterPx_Keeps_Onscreen_Height_Invariant()
        {
            // 換光柵尺寸只能改解析度，不能改字的大小：fontSize × characterSize 的乘積要跟舊的 64 × (11×0.2) 一樣。
            const float c = NameplateMetrics.PxToCharSizeLegacyAt64;
            float old = 64f * (11f * 0.2f);
            foreach (float sy in new[] { 1f, 1.8f, 3.6f })
            {
                int fp = NameplateMetrics.RasterPxForEm(11f, sy, c);
                Assert.AreEqual(old, fp * NameplateMetrics.CharacterSizeFor(11f, fp, c), 1e-3f);
            }
        }

        [Test]
        public void CharSize_Legacy_Calibration_Matches_Old_Hud_Pair()
        {
            // 遊戲 HUD 底列(歌名/LV/時間)舊寫法 = fontSize 64 + characterSize designPx×0.2；改走實體 px 光柵後，
            // 顯示大小必須「一模一樣」，否則歌名會位移/變大。
            const float c = NameplateMetrics.PxToCharSizeLegacyAt64;
            Assert.AreEqual(11f * 0.2f, NameplateMetrics.CharacterSizeFor(11f, 64, c), 1e-4f);
            float at64 = 64 * NameplateMetrics.CharacterSizeFor(11f, 64, c);
            float at11 = 11 * NameplateMetrics.CharacterSizeFor(11f, 11, c);   // 800×600 視窗
            float at20 = 20 * NameplateMetrics.CharacterSizeFor(11f, 20, c);   // 1080p 全螢幕
            Assert.AreEqual(at64, at11, 1e-3f);
            Assert.AreEqual(at64, at20, 1e-3f);
        }
    }
}
