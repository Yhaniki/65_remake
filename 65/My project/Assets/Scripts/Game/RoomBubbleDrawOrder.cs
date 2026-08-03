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
    }
}
