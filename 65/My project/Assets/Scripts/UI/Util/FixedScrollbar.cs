using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 點軌道 → 握把**跳到滑鼠那個位置**(使用者要求)。
    ///
    /// 🔴 這個元件是必要的,因為 Unity 內建的 <see cref="Scrollbar"/> 點軌道是「分頁跳」——
    ///    它的 <c>ClickRepeat</c> 協程每幀把 value 加減一個 <c>size</c>,按住就一頁一頁爬,
    ///    不是跳到你點的地方。
    ///
    /// 🔴 **一定要在 Scrollbar 之後 AddComponent**:同一個 GameObject 上的多個 handler 會照
    ///    <c>GetComponents</c>(= 加入)的順序執行,排在後面才能覆蓋掉 Scrollbar 剛做的那次分頁跳。
    ///    跳完之後握把就在滑鼠底下,Scrollbar 的協程下一幀發現游標在握把內就自己停了。
    ///
    /// 順帶一提,跳完可以直接繼續拖:Scrollbar 在 PointerDown 當下算的 m_Offset 是 0(當時點在握把外),
    /// 正好等於「握把中心跟著游標」,與我們跳過去的位置一致。
    /// </summary>
    public sealed class ScrollbarTrackJump : MonoBehaviour, IPointerDownHandler
    {
        public Scrollbar Bar;

        public void OnPointerDown(PointerEventData e)
        {
            if (Bar == null || Bar.handleRect == null) return;
            if (e.button != PointerEventData.InputButton.Left) return;
            // 點在握把上就是要拖它,不要瞬移(不然按下去的瞬間握把會自己置中,手感很怪)。
            if (RectTransformUtility.RectangleContainsScreenPoint(Bar.handleRect, e.position, e.pressEventCamera)) return;

            var rt = (RectTransform)transform;
            Vector2 lp;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, e.position, e.pressEventCamera, out lp)) return;

            // 握把有厚度 → 它的中心只能在「上下各縮進半個握把」的範圍內跑。把游標的 y 映射到這段區間。
            var r = rt.rect;
            float half = r.height * Bar.size * 0.5f;
            float bottom = r.yMin + half, top = r.yMax - half;
            if (top - bottom <= 0f) return;
            Bar.value = Mathf.Clamp01(Mathf.InverseLerp(bottom, top, lp.y));   // BottomToTop:1 = 最上
        }
    }

    /// <summary>
    /// 官方那種「握把固定大小」的捲軸 —— 用 Unity 內建的 <see cref="Scrollbar"/> 做,但**不接**
    /// <c>ScrollRect.verticalScrollbar</c>,而是兩邊手動同步。
    ///
    /// 🔴 為什麼要用內建的 Scrollbar:自己在一張 Image 上接拖曳連續三輪都被回報「滑鼠左鍵拉不動,
    ///    只有滾輪能動」。Scrollbar 是 Selectable + IBeginDragHandler,拖曳、**點軌道跳到那個位置**
    ///    (使用者後來追加的要求)、鍵盤操作全都是它內建的,一行都不用自己寫。
    ///
    /// 🔴 為什麼不直接接 <c>ScrollRect.verticalScrollbar</c>:那樣 ScrollRect 每幀會依內容長度覆寫
    ///    <see cref="Scrollbar.size"/>,握把會被拉成「內容越長越扁」。官方那根握把是**固定 28px** 的圖,
    ///    所以 size 由我們自己算(握把高 ÷ 軌道高)並固定住,只借它的輸入處理。
    /// </summary>
    public static class FixedScrollbar
    {
        /// <summary>
        /// 建一條垂直捲軸。<paramref name="x"/>/<paramref name="y"/> 是**握把**的左上角(800×600 設計座標,
        /// 與原本自畫握把時用的座標完全一樣),<paramref name="handleW"/>/<paramref name="handleH"/> 是握把圖的
        /// 大小,<paramref name="trackH"/> 是軌道可跑的總長(= 握把從最上到最下所掃過的範圍 + 握把高)。
        ///
        /// <paramref name="hitPad"/> 把**命中範圍**左右各加寬幾 px:14px 寬的軌道用滑鼠很難點準,
        /// 加寬之後視覺不變(握把圖仍是 14 寬),但好按得多。
        /// </summary>
        public static Scrollbar Create(Transform parent, string name, Sprite handle,
                                       float x, float y, float handleW, float handleH, float trackH,
                                       float hitPad = 6f)
        {
            // ── 軌道 = Scrollbar 本體 ──────────────────────────────────────────
            var barRt = UIKit.NewRect(parent, name);
            barRt.anchorMin = barRt.anchorMax = new Vector2(0f, 1f);
            barRt.pivot = new Vector2(0f, 1f);
            barRt.anchoredPosition = new Vector2(x - hitPad, -y);
            barRt.sizeDelta = new Vector2(handleW + hitPad * 2f, trackH);

            // 軌道要吃得到射線,「點軌道跳位」才有東西可點 —— 但畫面上不能看到它(官方軌道烤在底圖上)。
            var track = barRt.gameObject.AddComponent<Image>();
            track.color = new Color(0f, 0f, 0f, 0f);
            track.raycastTarget = true;

            // ── 握把 ──────────────────────────────────────────────────────────
            // 🔴 handleRect **會被 Scrollbar 撐滿軌道寬度**(它每幀改 anchorMin/Max,x 方向固定 0..1)。
            //    所以 handleRect 自己保持透明、只負責吃拖曳,真正的圖放在它底下、寬度寫死 handleW 並置中,
            //    否則握把會跟著 hitPad 一起被拉寬(加寬命中區 = 握把變胖)。
            var handleRt = UIKit.NewRect(barRt, "Handle");
            // 🔴 sizeDelta 一定要歸零:Scrollbar 只改 anchorMin/Max,**不動 sizeDelta** ——
            //    RectTransform 的實際大小 = anchor 撐出的大小 + sizeDelta。留著預設的 100×100
            //    握把就會被拉成一根貫穿整條軌道的長條(踩過)。
            handleRt.pivot = new Vector2(0.5f, 0.5f);
            handleRt.anchoredPosition = Vector2.zero;
            handleRt.sizeDelta = Vector2.zero;
            var handleHit = handleRt.gameObject.AddComponent<Image>();
            handleHit.color = new Color(0f, 0f, 0f, 0f);
            handleHit.raycastTarget = true;

            var artRt = UIKit.NewRect(handleRt, "Art");
            artRt.anchorMin = new Vector2(0.5f, 0f);
            artRt.anchorMax = new Vector2(0.5f, 1f);
            artRt.pivot = new Vector2(0.5f, 0.5f);
            artRt.anchoredPosition = Vector2.zero;
            artRt.sizeDelta = new Vector2(handleW, 0f);   // 寬度寫死,高度跟著 handleRect
            var art = artRt.gameObject.AddComponent<Image>();
            art.sprite = handle;
            art.raycastTarget = false;                     // 拖曳交給 handleRect,這層只是畫面
            art.enabled = handle != null;                  // 缺圖時不要留一塊白

            // ── 接線 ──────────────────────────────────────────────────────────
            var sb = barRt.gameObject.AddComponent<Scrollbar>();
            sb.direction = Scrollbar.Direction.BottomToTop;   // 值 1 = 最上,與 ScrollRect 的 vNP 同向
            sb.handleRect = handleRt;
            sb.targetGraphic = handleHit;
            sb.transition = Selectable.Transition.None;       // 握把的圖由我們給,不要被 UGUI 換掉
            sb.navigation = new Navigation { mode = Navigation.Mode.None };   // 不要 focus 外框

            // 握把佔軌道的比例。Scrollbar 用它算握把 rect,也用它把「拖幾像素」換成「值變多少」——
            // 固定住才能維持官方那根 28px 的握把。
            sb.size = trackH > 0f ? Mathf.Clamp01(handleH / trackH) : 0.2f;
            sb.SetValueWithoutNotify(1f);   // 預設停在最上面(官方就是這樣)

            // 點軌道跳位。順序很重要 —— 必須在 Scrollbar 之後掛,見 ScrollbarTrackJump 的註解。
            barRt.gameObject.AddComponent<ScrollbarTrackJump>().Bar = sb;
            return sb;
        }

        /// <summary>
        /// 把捲軸與一個 <see cref="ScrollRect"/> 雙向綁起來(不用 ScrollRect.verticalScrollbar,理由見類別註解)。
        /// <c>SetValueWithoutNotify</c> 是必要的 —— 兩邊互相通知會無限迴圈。
        /// </summary>
        public static void Bind(Scrollbar bar, ScrollRect scroll)
        {
            if (bar == null || scroll == null) return;
            bar.onValueChanged.AddListener(v => { if (Scrollable(scroll)) scroll.verticalNormalizedPosition = v; });
            scroll.onValueChanged.AddListener(_ => Sync(bar, scroll));
            Sync(bar, scroll);
        }

        /// <summary>把 ScrollRect 目前的位置寫回握把。內容比視窗短時 Unity 的 vNP 不可信 → 一律停在最上面。</summary>
        public static void Sync(Scrollbar bar, ScrollRect scroll)
        {
            if (bar == null || scroll == null) return;
            bar.SetValueWithoutNotify(Scrollable(scroll) ? Mathf.Clamp01(scroll.verticalNormalizedPosition) : 1f);
        }

        /// <summary>內容真的比視窗高才算捲得動 —— 差一點點(0.5px)當作沒有,避免邊界抖動。</summary>
        private static bool Scrollable(ScrollRect scroll)
        {
            var c = scroll.content; var v = scroll.viewport;
            return c != null && v != null && c.rect.height > v.rect.height + 0.5f;
        }
    }
}
