using NUnit.Framework;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 自畫游標在文字區裡的 x(<see cref="InputCaretMetrics.CaretX"/>)。
    /// 症狀:字打到比輸入框還寬時,TMP 把整段文字往左推,自畫的游標卻照舊按「字寬」往右跑 → 跑出框外、和字對不上。
    /// </summary>
    public class InputCaretMetricsTests
    {
        // 房間左下輸入框:文字區 183 寬、第一個字的左緣 2、游標 2px 寬。
        private const float X0 = 2f, ViewW = 183f, CaretW = 2f;

        [Test]
        public void Short_Text_Puts_The_Caret_Right_After_The_Last_Glyph()
        {
            Assert.AreEqual(2f, InputCaretMetrics.CaretX(X0, 0f, 0f, ViewW, CaretW), 1e-4f);      // 空字串
            Assert.AreEqual(62f, InputCaretMetrics.CaretX(X0, 60f, 0f, ViewW, CaretW), 1e-4f);
        }

        [Test]
        public void Scrolled_Text_Drags_The_Caret_Back_Into_The_Box()
        {
            // 打了 400px 寬的字,TMP 把文字往左推了 240 → 游標仍留在框內(2 + 400 - 240 = 162),不是跑到 402。
            Assert.AreEqual(162f, InputCaretMetrics.CaretX(X0, 400f, -240f, ViewW, CaretW), 1e-4f);
        }

        [Test]
        public void Moving_The_Caret_Back_Left_Follows_The_Text_Shifting_Back()
        {
            // 游標往回移到第 100px 處、TMP 把文字推回原位 → 游標也回到 102,而不是黏在右緣。
            Assert.AreEqual(102f, InputCaretMetrics.CaretX(X0, 100f, 0f, ViewW, CaretW), 1e-4f);
        }

        [Test]
        public void Caret_Is_Clamped_Inside_The_Text_Area()
        {
            // 我們量字寬(GetPreferredValues)與 TMP 自己排版之間會有零點幾 px 的誤差,
            // 不夾的話游標會探出遮罩、在框外閃一根白線。
            Assert.AreEqual(ViewW - CaretW, InputCaretMetrics.CaretX(X0, 400f, 0f, ViewW, CaretW), 1e-4f);
            Assert.AreEqual(0f, InputCaretMetrics.CaretX(X0, 0f, -50f, ViewW, CaretW), 1e-4f);
        }

        [Test]
        public void Unknown_Viewport_Width_Skips_The_Clamp()
        {
            // 還沒排版到、量不到寬度時不要亂夾(夾成 0 會讓游標整根黏在最左邊)。
            Assert.AreEqual(402f, InputCaretMetrics.CaretX(X0, 400f, 0f, 0f, CaretW), 1e-4f);
        }
    }
}
