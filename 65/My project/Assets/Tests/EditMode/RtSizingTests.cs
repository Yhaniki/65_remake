using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Pins <see cref="RtSizing"/> — how every "render 3D to an RT, show it in the UI" surface picks its pixel size
    /// (room backdrop, gameplay stage backdrop, gender-select dancer, shop/wardrobe previews).
    ///
    /// The bug being locked out: those RTs used to be sized from their LOGICAL aspect (e.g. Screen.height×4/3), but the
    /// 800×600 UI frame is stretched NON-UNIFORMLY across the window, so each axis has its own logical→screen scale.
    /// An RT sized to the logical aspect is then narrower (or shorter) than the pixels it's stretched over and the
    /// RawImage magnifies it — soft 3D, which is exactly what "the room player is blurry" was.
    /// </summary>
    public class RtSizingTests
    {
        // ---- SlotRtSize: full-screen backdrops --------------------------------------------------------------------

        [Test]
        public void FullScreen_Slot_Covers_At_Least_The_Window_Pixels()
        {
            // 1067×722 (≈1.48:1, the reported window). Old formula: 722×4/3 = 963 wide → 104px short → magnified.
            RtSizing.SlotRtSize(1067, 722, RtSizing.LogicalW, RtSizing.LogicalH, 1f, out int w, out int h);
            Assert.GreaterOrEqual(w, 1067, "RT narrower than the window → horizontal magnification (the original bug)");
            Assert.GreaterOrEqual(h, 722);
            Assert.AreNotEqual(963, w, "963 = the old height×4/3 result");
        }

        [Test]
        public void FullScreen_Slot_Follows_The_Window_Shape()
        {
            RtSizing.SlotRtSize(1920, 1080, RtSizing.LogicalW, RtSizing.LogicalH, 1f, out int w, out int h);
            Assert.AreEqual(1920, w);
            Assert.AreEqual(1080, h);
        }

        // ---- SlotRtSize: sub-slots (the previews) -----------------------------------------------------------------

        [Test]
        public void Sub_Slot_Scales_Each_Axis_By_Its_Own_Factor()
        {
            // The 400×600 gender-select slot in a 1600×600 window: x scales ×2, y scales ×1. A logical-aspect RT would
            // have kept 2:3 and been half the width it needed.
            RtSizing.SlotRtSize(1600, 600, 400f, 600f, 1f, out int w, out int h);
            Assert.AreEqual(800, w);    // 400 × (1600/800)
            Assert.AreEqual(600, h);    // 600 × (600/600)
        }

        [Test]
        public void Sub_Slot_Is_Floored_At_Its_Own_Logical_Size()
        {
            // Tiny window: never render a 220×320 wardrobe preview below the art's own resolution.
            RtSizing.SlotRtSize(320, 240, 220f, 320f, 1f, out int w, out int h);
            Assert.AreEqual(220, w);
            Assert.AreEqual(320, h);
        }

        // ---- supersample + caps ----------------------------------------------------------------------------------

        [Test]
        public void Supersample_Multiplies_Both_Axes()
        {
            RtSizing.SlotRtSize(1000, 800, RtSizing.LogicalW, RtSizing.LogicalH, 1.5f, out int w, out int h);
            Assert.AreEqual(1500, w);
            Assert.AreEqual(1200, h);
        }

        [Test]
        public void Supersample_Is_Clamped_To_1x_2x()
        {
            RtSizing.SlotRtSize(1000, 800, RtSizing.LogicalW, RtSizing.LogicalH, 0.25f, out int lowW, out int lowH);
            Assert.AreEqual(1000, lowW, "below 1× must clamp to window-native — never render under the window size");
            Assert.AreEqual(800, lowH);

            RtSizing.SlotRtSize(1000, 800, RtSizing.LogicalW, RtSizing.LogicalH, 99f, out int hiW, out int hiH);
            Assert.AreEqual(2000, hiW);
            Assert.AreEqual(1600, hiH);
        }

        [Test]
        public void Caps_Each_Axis_At_MaxDim()
        {
            RtSizing.SlotRtSize(3840, 2160, RtSizing.LogicalW, RtSizing.LogicalH, 1.5f, out int w, out int h);
            Assert.AreEqual(RtSizing.MaxDim, w);   // 5760 → capped
            Assert.AreEqual(3240, h);
            Assert.LessOrEqual(h, RtSizing.MaxDim);
        }

        [Test]
        public void Degenerate_Window_Sizes_Stay_Valid()
        {
            // Screen.width/height read 0 in a headless or minimised frame; an RT may never be 0-sized.
            RtSizing.SlotRtSize(0, 0, RtSizing.LogicalW, RtSizing.LogicalH, 1.5f, out int w, out int h);
            Assert.Greater(w, 0);
            Assert.Greater(h, 0);
        }

        // ---- RtResizeTracker (debounce) --------------------------------------------------------------------------

        [Test]
        public void Tracker_Stays_Quiet_While_The_Window_Is_Unchanged()
        {
            var t = new RtResizeTracker();
            t.Reset(1000, 800);
            Assert.IsFalse(t.Tick(1000, 800, 0f));
            Assert.IsFalse(t.Tick(1000, 800, 100f));
        }

        [Test]
        public void Tracker_Fires_Once_After_The_New_Size_Settles()
        {
            var t = new RtResizeTracker();
            t.Reset(1000, 800);
            Assert.IsFalse(t.Tick(1200, 800, 10f), "the frame the size changes must not re-allocate yet");
            Assert.IsFalse(t.Tick(1200, 800, 10.1f), "still inside the settle window");
            Assert.IsTrue(t.Tick(1200, 800, 10.2f), "settled → re-allocate");
            Assert.IsFalse(t.Tick(1200, 800, 11f), "and only once");
        }

        [Test]
        public void Tracker_Never_Fires_Mid_Drag()
        {
            // Dragging a window edge changes the size every frame; re-allocating a big MSAA RT per frame would stutter.
            var t = new RtResizeTracker();
            t.Reset(1000, 800);
            for (int i = 1; i <= 20; i++)
                Assert.IsFalse(t.Tick(1000 + i, 800, i), "fired mid-drag at frame " + i);
            Assert.IsTrue(t.Tick(1020, 800, 21f), "…then fires once the drag stops");
        }

        [Test]
        public void Apply_Is_A_No_Op_When_The_Size_Is_Unchanged()
        {
            var rt = new UnityEngine.RenderTexture(64, 32, 0);
            try
            {
                Assert.IsFalse(RtSizing.Apply(rt, 64, 32));
                Assert.IsTrue(RtSizing.Apply(rt, 128, 64));
                Assert.AreEqual(128, rt.width);
                Assert.AreEqual(64, rt.height);
            }
            finally { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
        }

        [Test]
        public void Apply_Tolerates_A_Null_Rt()
        {
            Assert.IsFalse(RtSizing.Apply(null, 100, 100));
        }
    }
}
