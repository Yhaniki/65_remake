using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 泡與泡之間的前後 = **人站的位置**(使用者需求)。
    ///
    /// 泡整個畫在 UI 裡(蓋過整張房間畫面,但被上排頭貼框與其他面板擋住),所以「誰蓋誰」
    /// 只剩泡與泡之間,而那只由畫的順序決定 —— 也就是 <see cref="RoomBubbleDrawOrder.FarToNear"/>
    /// 算出來的名次(RoomScreen.SortBubbleOwnerLayers 照它重排每個人那一層)。
    ///
    /// 這裡釘住的是「方向」與「往前走會翻轉」:寫反的話症狀恰好相反(站最後面的人的泡蓋住所有人),
    /// 而且只有兩顆泡在螢幕上重疊時才看得出來 —— 用眼睛很難抓。
    /// </summary>
    public class RoomBubbleSortOrderTests
    {
        private static List<int> Order(params float[] depths)
        {
            var outv = new List<int>();
            RoomBubbleDrawOrder.FarToNear(new List<float>(depths), outv);
            return outv;
        }

        [Test]
        public void The_Nearer_Persons_Bubble_Draws_Last()
        {
            // 0 號站得近(深度 100)、1 號站得遠(300) → 近的偏移要比較大(最後畫 = 蓋住遠的)。
            var o = Order(100f, 300f);
            Assert.AreEqual(2, o.Count);
            Assert.Greater(o[0], o[1], "站在前面的人的泡沒有蓋在後面那個人的泡上面(排序方向反了)");
            Assert.AreEqual(1, o[0]);
            Assert.AreEqual(0, o[1]);
        }

        [Test]
        public void Walking_Forward_Flips_Which_Bubble_Is_On_Top()
        {
            // 使用者的話:「後面這個人如果往前站到前面,那這個氣泡會再變成靠前」。
            var before = Order(100f, 300f);   // 1 號在後面
            var after = Order(100f, 50f);     // 1 號走到 0 號前面
            Assert.Greater(before[0], before[1]);
            Assert.Greater(after[1], after[0], "走到別人前面之後泡沒有跟著浮上來");
        }

        [Test]
        public void Order_Is_A_Permutation_Of_Zero_To_N_Minus_One()
        {
            // 每顆泡都要拿到自己的一個名次:重複的話兩顆泡同 sortingOrder,誰蓋誰就變成未定義
            // (Unity 內部順序),而那是「有時候正常、有時候反過來」這種最難查的症狀。
            var o = Order(500f, 120f, 300f, 120f, 900f);
            var seen = new HashSet<int>(o);
            Assert.AreEqual(o.Count, seen.Count, "有兩顆泡拿到同一個 sortingOrder");
            for (int i = 0; i < o.Count; i++) Assert.IsTrue(seen.Contains(i), "名次不連續(缺 " + i + ")");
            // 最遠(900)排 0、最近(120)排最後兩名。
            Assert.AreEqual(0, o[4]);
            Assert.AreEqual(4, o[3]);
        }

        [Test]
        public void Equal_Depths_Keep_A_Stable_Order()
        {
            // 兩個人站在完全一樣的深度(剛進房、同一排座位)—— 每幀排出來必須一樣,
            // 否則兩顆泡會每幀互換 = 重疊處閃爍。
            var a = Order(200f, 200f, 200f);
            var b = Order(200f, 200f, 200f);
            CollectionAssert.AreEqual(a, b);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, a, "同深度時沒有用 index 當第二鍵");
        }

        [Test]
        public void A_Bubble_With_No_Known_Depth_Sits_At_The_Back()
        {
            // RoomScreen 對「這一幀還沒擺出來」的泡填 float.MaxValue —— 它該排最遠,
            // 不是最近(冒出來蓋住所有人)。
            var o = Order(100f, float.MaxValue, 300f);
            Assert.AreEqual(0, o[1]);
        }

        [Test]
        public void Empty_And_Single_Inputs_Are_Handled()
        {
            Assert.AreEqual(0, Order().Count);
            CollectionAssert.AreEqual(new[] { 0 }, Order(42f));

            // null 進來不該炸(呼叫端每幀跑一次,寧可什麼都不排)。
            var outv = new List<int> { 7 };
            RoomBubbleDrawOrder.FarToNear(null, outv);
            Assert.AreEqual(0, outv.Count);
        }
    }
}
