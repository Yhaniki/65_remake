using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 房間裡「誰的泡蓋住誰的泡」——由**說話者站的位置**決定(使用者需求):站在前面的人的泡蓋住
    /// 站在後面的人的泡,而那個人往前走幾步,他的泡就跟著浮上來。
    ///
    /// 泡整個畫在 UI 裡(疊在房間畫面之上、UI 面板之下),所以兩顆泡的前後**只由畫的順序決定** ——
    /// 這裡把各人的深度換成畫的順序,呼叫端(RoomScreen.SortBubbleOwnerLayers)照名次重排每個人那一層。
    ///
    /// 頭上**名字牌**用同一套名次,但套的地方不同:它已經搬進房間相機(吃深度測試),
    /// 一個人一張 world canvas → 走 <see cref="ApplyFarToNearSorting"/> 改 sortingOrder。
    /// 名次算法只該有一份 —— gameplay 舞台的名牌(<c>NameplateDrawOrder</c>)也是叫 <see cref="FarToNear"/>。
    /// </summary>
    public static class RoomBubbleDrawOrder
    {
        /// <summary>
        /// <paramref name="orderOut"/>[i] = 第 i 個人的名次:0 = 最遠(最先畫、被蓋),n-1 = 最近(最後畫、蓋別人)。
        ///
        /// 🔴 方向寫反的症狀不是「沒效果」,而是**恰好相反** —— 站在最後面的人的泡蓋住所有人,
        /// 而且只有兩顆泡在螢幕上重疊時才看得出來。所以這件事要有測試釘著。
        /// </summary>
        /// <param name="depths">每個人沿相機視線的深度(愈大 = 愈遠)。</param>
        /// <param name="orderOut">輸出;會被清空後填成與 <paramref name="depths"/> 等長。</param>
        public static void FarToNear(List<float> depths, List<int> orderOut)
        {
            if (orderOut == null) return;
            orderOut.Clear();
            if (depths == null) return;

            // 名次 = 「有多少人比我遠」。n 是房間人數(≤6),所以這個 O(n²) 比真的排序還省 ——
            // 而且**一個位元組都不配置**(這是每幀跑的東西;List.Sort 的比較 lambda 會捕獲 depths → 每幀一個 closure)。
            //
            // 🔴 同深度時用 index 當第二鍵(j < i),而且用 CompareTo 而不是 > / == ——
            // 兩者都是為了讓名次是**全序**:少了它,兩個人會拿到同一個名次,誰蓋誰就變成未定義,
            // 症狀是「有時候正常、有時候反過來」。
            int n = depths.Count;
            for (int i = 0; i < n; i++)
            {
                int rank = 0;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    int c = depths[j].CompareTo(depths[i]);
                    if (c > 0 || (c == 0 && j < i)) rank++;
                }
                orderOut.Add(rank);
            }
        }

        /// <summary>
        /// 把 <paramref name="layers"/> 依各自的 <paramref name="depths"/> 重排成「遠的先畫、近的後畫」
        /// (= 站在前面的人蓋住站在後面的人)。兩份 list 等長、同索引配對;
        /// <paramref name="scratchOrders"/> 是呼叫端持有的暫存(每幀跑,不要在這裡配置)。
        ///
        /// 🔴 用 SetAsLastSibling **依名次由遠到近**呼叫,不要用 SetSiblingIndex(rank):
        /// SetSiblingIndex 是「插進第 n 個位置」,前面幾次呼叫會把後面的擠開 —— 逐一指定的結果不是
        /// 算出來的那個排列(而且錯得很安靜:只有兩者在螢幕上重疊時才看得出來)。
        /// </summary>
        public static void ApplyFarToNear(List<RectTransform> layers, List<float> depths, List<int> scratchOrders)
        {
            if (layers == null || depths == null || scratchOrders == null) return;
            if (layers.Count != depths.Count || layers.Count <= 1) return;
            FarToNear(depths, scratchOrders);
            for (int rank = 0; rank < layers.Count; rank++)
                for (int i = 0; i < scratchOrders.Count; i++)
                    if (scratchOrders[i] == rank) { if (layers[i] != null) layers[i].SetAsLastSibling(); break; }
        }

        /// <summary>
        /// 同 <see cref="ApplyFarToNear"/>,但套在 <b>world-space canvas 的 sortingOrder</b> 上 ——
        /// 房間的頭上名字牌走這條。
        ///
        /// 為什麼不能沿用 sibling index:每個人的名字牌各自是一張**獨立的 root canvas**(要各自貼在
        /// 自己那個人的深度平面上),而獨立 canvas 之間的畫序只看 sortingOrder,sibling 順序完全不影響。
        ///
        /// <paramref name="baseOrder"/> 要 &gt; 0:房間場景(牆/家具/角色/玻璃)全在 sortingOrder 0,
        /// 而 sortingOrder 比 renderQueue 優先([[unity-sortingorder-outranks-renderqueue]]) ——
        /// 給 0 的話名字牌會與場景的透明批混在一起排,窗玻璃那類 ZWrite Off 的東西就會蓋在名字上。
        /// 排在透明批之後**不會**讓名字穿透人體:那是深度測試(不透明的人早就寫好深度)在管的,與畫序無關。
        /// </summary>
        public static void ApplyFarToNearSorting(List<Canvas> canvases, List<float> depths,
                                                 List<int> scratchOrders, int baseOrder)
        {
            if (canvases == null || depths == null || scratchOrders == null) return;
            if (canvases.Count != depths.Count) return;
            FarToNear(depths, scratchOrders);
            for (int i = 0; i < canvases.Count; i++)
            {
                if (canvases[i] == null) continue;
                int order = baseOrder + scratchOrders[i];
                if (canvases[i].sortingOrder != order) canvases[i].sortingOrder = order;
            }
        }
    }
}
