using NUnit.Framework;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    public class ChatLineClipTests
    {
        // 訊息欄實際尺寸：視窗 104px、行高 16px、下 padding 3px。捲到底時可視高度 = 104-3 = 101 = 6 行 + 5px，
        // 第 7 行的下緣 5px 會露在框頂 → 就是那截一直留著的半個字。
        private const float ViewBottom = 0f, ViewTop = 104f;

        [Test]
        public void Whole_Line_Inside_The_Viewport_Is_Visible()
        {
            Assert.IsTrue(ChatLineClip.IsLineVisible(3f, 19f, ViewBottom, ViewTop));    // 最底下那行
            Assert.IsTrue(ChatLineClip.IsLineVisible(83f, 99f, ViewBottom, ViewTop));   // 最上面那整行
        }

        [Test]
        public void Line_Sticking_Out_Of_The_Top_Is_Hidden()
        {
            // 第 7 行：99..115，只有下面 5px 在框內 → 整行不畫（原本會留下半截字）
            Assert.IsFalse(ChatLineClip.IsLineVisible(99f, 115f, ViewBottom, ViewTop));
        }

        [Test]
        public void Line_Sticking_Out_Of_The_Bottom_Is_Hidden()
        {
            Assert.IsFalse(ChatLineClip.IsLineVisible(-5f, 11f, ViewBottom, ViewTop));
        }

        [Test]
        public void Line_Flush_With_An_Edge_Survives_Rounding()
        {
            Assert.IsTrue(ChatLineClip.IsLineVisible(0f, 16f, ViewBottom, ViewTop));
            Assert.IsTrue(ChatLineClip.IsLineVisible(88f, 104f, ViewBottom, ViewTop));
            Assert.IsTrue(ChatLineClip.IsLineVisible(-0.2f, 15.8f, ViewBottom, ViewTop));   // 排版誤差內
        }

        [Test]
        public void Line_Taller_Than_The_Viewport_Falls_Back_To_Pixel_Clipping()
        {
            // 折了很多行的長訊息永遠塞不進視窗；若照規則藏起來，整條訊息會直接不見 → 交還給 RectMask2D。
            Assert.IsTrue(ChatLineClip.IsLineVisible(-20f, 120f, ViewBottom, ViewTop));
        }
    }
}
