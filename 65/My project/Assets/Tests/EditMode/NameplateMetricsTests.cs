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
    }
}
