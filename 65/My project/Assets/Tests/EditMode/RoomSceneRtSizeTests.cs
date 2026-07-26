using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Pins <see cref="RoomScene3D.SceneRtSize"/> — the room backdrop's render-target size. The bug this locks down:
    /// the RT used to be sized 4:3 (<c>height×4/3</c>), but the backdrop RawImage fills the whole WINDOW and
    /// AspectController runs in Stretch mode, so on any window wider than 4:3 the RT was narrower than the pixels it
    /// was stretched across → the room/avatar looked soft while the head portrait (which renders ABOVE its slot size)
    /// stayed crisp. The RT must now be window-shaped and oversampled.
    /// </summary>
    public class RoomSceneRtSizeTests
    {
        [Test]
        public void Rt_Is_Never_Narrower_Than_The_Window_On_A_Wide_Screen()
        {
            // 1067×722 (the reported window, ≈1.48:1). The old formula gave 963 wide — 104px SHORT of the window, i.e.
            // a horizontal upscale. Whatever the supersample, the RT must cover at least the window's own pixels.
            RoomScene3D.SceneRtSize(1067, 722, 1f, out int w, out int h);
            Assert.GreaterOrEqual(w, 1067, "RT narrower than the window → horizontal magnification (the original bug)");
            Assert.GreaterOrEqual(h, 722);

            RoomScene3D.SceneRtSize(1920, 1080, 1f, out w, out h);
            Assert.GreaterOrEqual(w, 1920);
            Assert.GreaterOrEqual(h, 1080);
        }

        [Test]
        public void Rt_Follows_The_Window_Shape_Not_A_Fixed_43()
        {
            RoomScene3D.SceneRtSize(1920, 1080, 1f, out int w, out int h);
            Assert.AreEqual(1920f / 1080f, (float)w / h, 0.01f, "RT should match the window aspect, not 4:3");
            Assert.AreNotEqual(1440, w, "1440 = the old height×4/3 result");
        }

        [Test]
        public void Supersample_Multiplies_Both_Axes()
        {
            RoomScene3D.SceneRtSize(1067, 722, 1.5f, out int w, out int h);
            Assert.AreEqual(1600, w);   // 1067 × 1.5 = 1600.5 → Mathf.RoundToInt is banker's rounding → 1600
            Assert.AreEqual(1083, h);   // 722 × 1.5
        }

        [Test]
        public void Supersample_Is_Clamped_To_1x_2x()
        {
            RoomScene3D.SceneRtSize(1000, 800, 0.25f, out int lowW, out int lowH);
            RoomScene3D.SceneRtSize(1000, 800, 1f, out int oneW, out int oneH);
            Assert.AreEqual(oneW, lowW, "below 1× must clamp to window-native, never render under the window size");
            Assert.AreEqual(oneH, lowH);

            RoomScene3D.SceneRtSize(1000, 800, 99f, out int hiW, out int hiH);
            Assert.AreEqual(2000, hiW);   // capped at 2×
            Assert.AreEqual(1600, hiH);
        }

        [Test]
        public void Floors_At_The_Official_800x600_Frame()
        {
            // A tiny window must not render the room below the original SDO frame resolution.
            RoomScene3D.SceneRtSize(320, 240, 1f, out int w, out int h);
            Assert.AreEqual(800, w);
            Assert.AreEqual(600, h);
        }

        [Test]
        public void Caps_Each_Axis_So_A_4K_Window_Cannot_Blow_Up_The_Rt()
        {
            RoomScene3D.SceneRtSize(3840, 2160, 1.5f, out int w, out int h);
            Assert.AreEqual(RoomScene3D.SceneRtMaxDim, w);   // 5760 → capped at 4096
            Assert.AreEqual(3240, h);                        // still under the cap
            Assert.LessOrEqual(h, RoomScene3D.SceneRtMaxDim);
        }

        [Test]
        public void Degenerate_Window_Sizes_Do_Not_Produce_An_Invalid_Rt()
        {
            // Screen.width/height can be 0 in a headless/minimised frame; an RT must never be 0-sized.
            RoomScene3D.SceneRtSize(0, 0, 1.5f, out int w, out int h);
            Assert.Greater(w, 0);
            Assert.Greater(h, 0);
        }

        [Test]
        public void Projection_Stays_The_Official_43_Regardless_Of_Rt_Shape()
        {
            // The RT is window-shaped now, so the 4:3 framing survives only because UpdateCamera pins Camera.aspect
            // to this constant. If this changes, the room's composition changes with the window.
            Assert.AreEqual(800f / 600f, RoomScene3D.ProjectionAspect, 1e-6f);
        }
    }
}
