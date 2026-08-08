using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// IME「還沒選字上屏」的那一段一律畫底線 —— 房間頭上泡與大廳/房間輸入框走 TMP 的 &lt;u&gt; 標籤,
    /// 遊戲畫面右下那條是 legacy TextMesh 只能自己擺線,兩邊共用這裡的規則。
    /// </summary>
    public class ImeComposeTests
    {
        [Test]
        public void Underline_WrapsTheComposition()
        {
            Assert.AreEqual("<u>ㄊㄞ</u>", ImeCompose.Underline("ㄊㄞ"));
        }

        [Test]
        public void Underline_EmptyCompositionEmitsNoTags()
        {
            Assert.AreEqual("", ImeCompose.Underline(""));
            Assert.AreEqual("", ImeCompose.Underline(null));
        }

        [Test]
        public void ShownStart_CompositionSitsAtTheTail()
        {
            // "你好" + 組字 "ㄊㄞ" → 顯示 4 個字,底線從第 2 個字開始。
            Assert.AreEqual(2, ImeCompose.ShownStart(4, 2));
        }

        [Test]
        public void ShownStart_NoCompositionMeansNothingToUnderline()
        {
            Assert.AreEqual(5, ImeCompose.ShownStart(5, 0));
            Assert.AreEqual(5, ImeCompose.ShownStart(5, -1));
        }

        [Test]
        public void ShownStart_ClippedDraftUnderlinesEverythingLeft()
        {
            // 輸入框砍掉開頭只留尾巴 → 看得見的全是組字。
            Assert.AreEqual(0, ImeCompose.ShownStart(3, 8));
            Assert.AreEqual(0, ImeCompose.ShownStart(3, 3));
        }

        [Test]
        public void ShownStart_EmptyDraftIsSafe()
        {
            Assert.AreEqual(0, ImeCompose.ShownStart(0, 0));
            Assert.AreEqual(0, ImeCompose.ShownStart(0, 4));
        }
    }
}
