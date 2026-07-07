using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    public class ScreenshotCropTests
    {
        [Test]
        public void FullFrame_Needs_No_Crop()
        {
            bool crop = ScreenshotCrop.PixelRect(0f, 0f, 1f, 1f, 1920, 1080, out int x, out int y, out int w, out int h);
            Assert.IsFalse(crop);           // covers whole image → encode as-is
            Assert.AreEqual((0, 0, 1920, 1080), (x, y, w, h));
        }

        [Test]
        public void Pillarbox_On_16by9_Crops_Left_Right_Only()
        {
            // 4:3 centred in 1920×1080: content width = (4/3)/(16/9) = 0.75 → 1440px, full height.
            const float w43 = (800f / 600f) / (1920f / 1080f);   // 0.75
            const float x0 = (1f - w43) * 0.5f;                  // 0.125
            bool crop = ScreenshotCrop.PixelRect(x0, 0f, w43, 1f, 1920, 1080, out int x, out int y, out int w, out int h);
            Assert.IsTrue(crop);
            Assert.AreEqual(240, x);        // 0.125 * 1920
            Assert.AreEqual(0, y);
            Assert.AreEqual(1440, w);       // 0.75 * 1920 — bars (240px each side) removed
            Assert.AreEqual(1080, h);       // full height untouched
        }

        [Test]
        public void Letterbox_On_Tall_Window_Crops_Top_Bottom_Only()
        {
            // 4:3 centred in 1200×1000 (taller than 4:3): content height = (12/10)/(4/3) = 0.9 → 900px, full width.
            const float h43 = (1200f / 1000f) / (800f / 600f);   // 0.9
            const float y0 = (1f - h43) * 0.5f;                  // 0.05
            bool crop = ScreenshotCrop.PixelRect(0f, y0, 1f, h43, 1200, 1000, out int x, out int y, out int w, out int h);
            Assert.IsTrue(crop);
            Assert.AreEqual(0, x);
            Assert.AreEqual(50, y);         // 0.05 * 1000
            Assert.AreEqual(1200, w);       // full width untouched
            Assert.AreEqual(900, h);        // 0.9 * 1000
        }

        [Test]
        public void Result_Always_Stays_In_Bounds()
        {
            // Slightly-out-of-range inputs must clamp, never exceed the image.
            ScreenshotCrop.PixelRect(0.9f, 0.9f, 0.5f, 0.5f, 800, 600, out int x, out int y, out int w, out int h);
            Assert.LessOrEqual(x + w, 800);
            Assert.LessOrEqual(y + h, 600);
            Assert.GreaterOrEqual(w, 1);
            Assert.GreaterOrEqual(h, 1);
        }
    }
}
