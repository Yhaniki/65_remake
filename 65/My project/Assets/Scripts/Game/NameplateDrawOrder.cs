using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>
    /// 遊戲中頭上名字牌「誰蓋誰」的畫序配置。
    ///
    /// 名牌是 alpha-blend 且 <c>ZWrite Off</c>(<c>Sdo/DepthText</c>),所以**名牌對名牌**的前後只由
    /// sortingOrder 決定 —— 深度緩衝只擋得住「被不透明的人擋住」,擋不住另一面名牌畫上來。
    ///
    /// 🔴 全場共用同一組畫序(字面 2、黑邊 1)會**把描邊整片抹掉**:sortingOrder 是跨物件的全域分層,
    /// 於是真正的畫序變成「所有人的黑邊 → 所有人的字面」,站在後面那個人的白字就蓋在站在前面那個人的
    /// 黑邊上,兩串名字糊成一團(使用者回報)。所以每個人要吃**自己的一段** order:
    /// 一個人兩層(黑邊 + 字面),依他站的深度由遠到近往上疊,近的人整組(連黑邊)都在遠的人字面之上。
    ///
    /// 最遠那位拿到的正是舊的那組值(字面 2、黑邊 1),所以「名牌排在場景透明批 0 之上」這個
    /// 既有性質不變(見 <see cref="HeadMarker.WorldNameOrder"/>)。
    /// </summary>
    public static class NameplateDrawOrder
    {
        /// <summary>最遠那位的字面畫序。必須 &gt; 0 —— 場景的透明物(噴水池的水、光柱)都在 0。</summary>
        public const int FirstFaceOrder = 2;

        /// <summary>一個人吃幾層:字面一層 + 它下面的黑邊/箭頭一層。</summary>
        public const int Stride = 2;

        /// <summary>名次 <paramref name="rank"/>(0 = 最遠)的字面畫序。</summary>
        public static int FaceOrder(int rank) => FirstFaceOrder + rank * Stride;

        /// <summary>名次 <paramref name="rank"/> 的黑邊畫序 —— 永遠在**自己的**字面之下、
        /// 在**比自己遠的人**的字面之上。</summary>
        public static int EdgeOrder(int rank) => FaceOrder(rank) - 1;

        /// <summary>
        /// 把每個人的深度(愈大 = 愈遠;量不到就填 <see cref="float.MaxValue"/> → 排最遠)換成各自的字面畫序。
        /// <paramref name="ordersOut"/> 與 <paramref name="depths"/> 同索引配對;
        /// <paramref name="scratchRanks"/> 是呼叫端持有的暫存(每幀跑,不要在這裡配置)。
        /// </summary>
        public static void FaceOrders(List<float> depths, List<int> ordersOut, List<int> scratchRanks)
        {
            if (ordersOut == null) return;
            ordersOut.Clear();
            if (depths == null) return;
            var ranks = scratchRanks ?? new List<int>();
            RoomBubbleDrawOrder.FarToNear(depths, ranks);   // 名次:0 = 最遠(與房間名字牌/泡同一套規則)
            for (int i = 0; i < ranks.Count; i++) ordersOut.Add(FaceOrder(ranks[i]));
        }
    }
}
