using NUnit.Framework;
using Sdo.UI.Screens;

namespace Sdo.Tests
{
    /// <summary>商城捲動時「每一步都把整頁 8 張 3D 縮圖砍掉重建」是卡頓的主因之一 —— 捲一列其實只有 2 件商品換掉,
    /// 其餘 6 件只是換個格子。<see cref="ShopScreen.MapRetainedSlots"/> 就是那張對照表:新格子要用哪個舊格既有的縮圖。</summary>
    public class ShopCardRetainTests
    {
        private const long N = ShopScreen.NoCardKey;

        [Test]
        public void ScrollOneRow_KeepsEverythingButTheNewRow()
        {
            // 2 欄 × 4 列的一頁,往下捲一列:1,2 捲出去、7,8 捲進來。
            var have = new long[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var want = new long[] { 3, 4, 5, 6, 7, 8, 9, 10 };
            var src = ShopScreen.MapRetainedSlots(have, want);
            Assert.AreEqual(new[] { 2, 3, 4, 5, 6, 7, -1, -1 }, src, "只有真的捲進來的 9/10 要重建");
        }

        [Test]
        public void ScrollBackUp_AlsoReuses()
        {
            var have = new long[] { 3, 4, 5, 6 };
            var want = new long[] { 1, 2, 3, 4 };
            Assert.AreEqual(new[] { -1, -1, 0, 1 }, ShopScreen.MapRetainedSlots(have, want));
        }

        [Test]
        public void EmptySlots_NeverClaimAPreview()
        {
            var have = new long[] { 1, 2, N, N };
            var want = new long[] { 1, N, N, 2 };
            Assert.AreEqual(new[] { 0, -1, -1, 1 }, ShopScreen.MapRetainedSlots(have, want));
        }

        [Test]
        public void DifferentPageEntirely_RebuildsEverything()
        {
            var have = new long[] { 1, 2, 3, 4 };
            var want = new long[] { 90, 91, 92, 93 };
            Assert.AreEqual(new[] { -1, -1, -1, -1 }, ShopScreen.MapRetainedSlots(have, want));
        }

        [Test]
        public void OneOldSlotIsClaimedOnlyOnce()
        {
            // 防呆:萬一同一鍵在 want 裡出現兩次 (收合失效),第二格必須自己重建,不能兩格共用同一份人形/RT
            // (共用會讓兩張卡的取景/旋轉互相打架)。
            var have = new long[] { 7, N };
            var want = new long[] { 7, 7 };
            Assert.AreEqual(new[] { 0, -1 }, ShopScreen.MapRetainedSlots(have, want));
        }
    }
}
