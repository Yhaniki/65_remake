using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 官方那個「一疊粉色圓角膠囊」的右鍵選單 —— 對照 <c>UI/ROOM/POPMENU.XML</c> 的 <c>SP_PopMenu</c>:
    /// <code>
    ///   &lt;PopMenu background="SPopMenu6.an"&gt;
    ///     &lt;Button bgnormal="FamilyPop_1.an" bghover="FamilyPop_2.an" bgpushed="FamilyPop_1.an"
    ///             bold="true" color="0xff7a000e" x="0" y="0/27/54/81/108/…"/&gt;
    /// </code>
    /// → 列高 27、列寬 92、列的 x 一律 0(底板沒有內縮),bgpushed = bgnormal(實際只有兩態)。
    ///
    /// 🔴 這份原本長在 <c>RoomScreen</c> 裡(座位右鍵選單)。搬出來是因為官方**同一個選單樣式**至少出現在三處:
    ///    房間座位、大廳玩家名單、大廳「房間信息」的參與者列表。留在畫面類別裡的話另外兩處只能複製一份,
    ///    而下面那些坑(白框、9-slice 的 ppu、圖集滲白)每複製一次就要再踩一次。
    ///
    /// 🔴 **底板 SPopMenu6 不畫**(panel 只是一塊透明的吃點擊面板)。那張圖是 92×21 的框:最外圈 1px 半透明黑、
    ///    再往內 2px 是 <c>ffffff b8</c> 的**白邊**,中間才是青漸層。而列圖是圓角膠囊、四邊的邊緣像素是半透明的 ——
    ///    底板被 9-slice 撐成 w×(27×N) 後,那圈白邊就從膠囊的圓角與半透明邊緣透出來,整個選單外面鑲一個
    ///    **方形白框**(使用者回報)。任何顏色的矩形底板都會留下這一圈(膠囊是圓角,底板是方的),所以是拿掉、
    ///    不是換色。官方其他 PopMenu 本來也就沒有背板(XML 寫 <c>background="empty.an"</c>)。
    /// </summary>
    public static class SdoPopupMenu
    {
        /// <summary>列高。POPMENU.XML 相鄰兩列的 y 差。</summary>
        public const float RowH = 27f;
        /// <summary>最小寬 = FamilyPop_1.an 的原生寬(也是底板 SPopMenu6.png 的寬)。</summary>
        public const float MinW = 92f;
        /// <summary>字級。官方最長是 5 個中文字塞進 92px。</summary>
        public const float FontPx = 13f;
        /// <summary>字距左右緣的內縮(膠囊的圓角大約就這麼寬)。</summary>
        public const float PadX = 7f;
        /// <summary>9-slice 左右保留寬(圓角弧 ~6px,留 12 絕對蓋得住)。</summary>
        private const float SliceX = 12f;

        /// <summary>官方 <c>color="0xff7a000e"</c> 的深紅字。</summary>
        public static readonly Color32 TextColor = new Color32(0x7a, 0x00, 0x0e, 0xff);
        /// <summary>找不到 DATA 時的退路(數值就是從那張列圖中央量到的):選單至少還畫得出來、深紅字還讀得到。</summary>
        private static readonly Color32 RowFallback = new Color32(0x9d, 0x8a, 0xbb, 0xf0);

        private static Sprite _row, _rowHover;
        private static bool _artLoaded;

        /// <summary>
        /// 蓋一個選單出來。<paramref name="root"/> 要是**鋪滿 800×600 設計框**的容器(選單會自己夾進框內),
        /// <paramref name="screenPos"/> 是滑鼠的螢幕座標(通常 <c>PointerEventData.position</c>)。
        /// 回傳的 GameObject 就是整個選單 —— 呼叫端負責在關掉時 <c>Destroy</c> 它。
        /// </summary>
        public static GameObject Build(RectTransform root, string name, Vector2 screenPos, Camera uiCam,
                                       int count, Func<int, string> labelOf, Action<int> onPick)
        {
            if (root == null || count <= 0) return null;
            EnsureArt();

            var labels = new string[count];
            float w = MinW;
            for (int i = 0; i < count; i++)
            {
                labels[i] = (labelOf != null ? labelOf(i) : null) ?? "";
                w = Mathf.Max(w, TextWidth(labels[i], FontPx) + PadX * 2f);
            }
            w = Mathf.Ceil(w);          // 半像素寬會讓 9-slice 的邊落在像素中間 → 邊框糊掉
            float h = RowH * count;

            Vector2 tl = ScreenToDesign(root, screenPos, uiCam);
            // 夾進 800×600 —— 在畫面右緣/下緣右鍵時整個選單往內推,而不是讓後面幾列被切到框外。
            var rect = root.rect;
            float frameW = rect.width > 0f ? rect.width : 800f;
            float frameH = rect.height > 0f ? rect.height : 600f;
            float x = Mathf.Clamp(tl.x, 0f, Mathf.Max(0f, frameW - w));
            float y = Mathf.Clamp(tl.y, 0f, Mathf.Max(0f, frameH - h));

            // 透明但 raycastTarget=true:選單自己要吃掉點擊(才不會穿透到後面的座位/3D 房間),
            // 但一個像素都不畫(見類別註解的白框)。Image 的 raycast 與 color.a 無關 → alpha 0 照樣擋得住。
            var panel = UIKit.AddImage(root, name, new Color(0f, 0f, 0f, 0f), raycast: true);
            Place(panel.rectTransform, x, y, w, h);
            panel.transform.SetAsLastSibling();

            for (int i = 0; i < count; i++)
            {
                int idx = i;
                var row = UIKit.AddImage(panel.rectTransform, "Row" + i, Color.white, raycast: true);
                SetSliced(row, _row, RowFallback);
                Place(row.rectTransform, 0f, RowH * i, w, RowH);

                var btn = row.gameObject.AddComponent<Button>();
                btn.targetGraphic = row;
                // 官方 bgpushed = bgnormal,所以 pressed 也給 normal ——
                // 自己補一個「按下變暗」等於多出官方沒有的第三態。
                btn.transition = Selectable.Transition.SpriteSwap;
                var st = btn.spriteState;
                st.highlightedSprite = _rowHover;
                st.pressedSprite = _row;
                st.selectedSprite = _row;
                btn.spriteState = st;
                UiSfx.AttachClick(btn);
                UiHoverSfx.Attach(btn);
                if (onPick != null) btn.onClick.AddListener(() => onPick(idx));

                var t = UIKit.AddText(row.rectTransform, "Label", labels[i], FontPx, TextColor,
                                      TextAlignmentOptions.Center);
                t.fontStyle = FontStyles.Bold;                 // 官方每一列都 bold="true"
                // 高度少 1px:膠囊的上下框各佔 1px,字框跟著縮才會落在**內側**的視覺中心。
                Place(t.rectTransform, PadX, 0f, w - PadX * 2f, RowH - 1f);
            }
            return panel.gameObject;
        }

        /// <summary>滑鼠螢幕座標 → 設計座標(左上原點、y 往下)。取不到就回 (0,0)(選單會落在左上角,不會不見)。</summary>
        public static Vector2 ScreenToDesign(RectTransform root, Vector2 screenPos, Camera uiCam)
        {
            if (root != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(root, screenPos, uiCam, out var lp))
            {
                var r = root.rect;
                return new Vector2(lp.x - r.xMin, r.yMax - lp.y);
            }
            return Vector2.zero;
        }

        /// <summary>選單開著時點到選單外面 → 該關掉了嗎?(彈出的那一幀由呼叫端自己擋,見各畫面的 popupFrame)</summary>
        public static bool ClickedOutside(GameObject menu, Vector2 screenPos, Camera uiCam)
        {
            if (menu == null) return false;
            var rt = menu.transform as RectTransform;
            return rt == null || !RectTransformUtility.RectangleContainsScreenPoint(rt, screenPos, uiCam);
        }

        /// <summary>
        /// 選單的兩張列圖(normal / hover),都轉成 **9-slice**(左右各留 <see cref="SliceX"/> 不拉伸)。
        ///
        /// 為什麼不是 Simple 直接拉:這兩張是 92px 寬的圓角膠囊,左右各只有 ~6px 的弧再加 1px 深藍外框。
        /// 日文的「プレイヤー情報」、英文的 "Remove Friend" 在 13px 字級下要 100px 以上,Simple 會把那段弧
        /// 連同外框一起橫向拉扁 → 圓角變橢圓、框線變糊。9-slice 只拉中段,而中段是**純垂直漸層,水平方向
        /// 逐像素量過最多差 3/255**(等於看不出來)→ 拉到任何寬度都跟原圖一樣銳利。
        ///
        /// 🔴 走 <see cref="AtlasCropper"/> 而不是 <c>RoomUiArt.AtlasCrop</c>:後者是**直接在共用圖集上開 rect**,
        ///    而 EXPRESSIONINFO.PNG 在這兩塊膠囊的圓角外留的是 <c>ffffff0a</c>(工具的白 matte)、四周又緊貼別的圖 ——
        ///    雙線性取樣會把那圈白拖進圓角,每一列鑲一道白邊。AtlasCropper.Crop 會把 rect 複製到自己的貼圖、
        ///    把透明像素的 RGB 換成鄰近的不透明色(BleedTransparent)再 Clamp → 沒有白 matte 也沒有鄰居可滲。
        ///
        /// 為什麼還要自己重造 sprite:Crop 造出來的 sprite 沒有 border,而 <c>Image.Type.Sliced</c> 遇到 border 全 0
        /// 會靜靜地退化成 Simple(不會報錯,只是圓角被拉扁 —— 正是我們要避免的那個結果)。
        /// </summary>
        private static void EnsureArt()
        {
            if (_artLoaded) return;
            // FamilyPop_1/2.an 沒有被單獨切出來,兩張都在 ExpressionInfo 圖集裡(座標為官方 .an 的 top-left)。
            _row = Slice(AtlasCropper.Crop(RoomUiArt.Dir, "EXPRESSIONINFO.PNG", 420, 139, 92, 27), SliceX);
            _rowHover = Slice(AtlasCropper.Crop(RoomUiArt.Dir, "EXPRESSIONINFO.PNG", 420, 169, 92, 27), SliceX);
            // 只有真的拿到圖才把結果封存起來。第一次右鍵有可能發生在 DATA 根還沒解析成功的時候
            // (RoomUiArt.Dir 走 catch 分支 → 兩張全 null),先把旗標立起來等於**永久**退回純色 ——
            // 之後就算路徑好了也再也不會重載。RoomUiArt 自己的快取也是同一個寫法(null 不算數)。
            _artLoaded = _row != null;
        }

        /// <summary>同一張圖、同一塊 rect,只是補上 9-slice 的左右 border。來源缺圖 → 回 null(呼叫端有退路色)。</summary>
        private static Sprite Slice(Sprite src, float sideX)
        {
            if (src == null || src.texture == null) return null;
            return Sprite.Create(src.texture, src.rect, new Vector2(0.5f, 0.5f), src.pixelsPerUnit, 0,
                                 SpriteMeshType.FullRect, new Vector4(sideX, 0f, sideX, 0f));
        }

        /// <summary>
        /// 套 9-slice 圖。**不能用 <c>UIKit.ApplySprite</c>** —— 它會把 sizeDelta 設回 sprite 的原生尺寸,
        /// 選單就永遠是 92×27 一格。尺寸一律由 <see cref="Place"/> 決定。
        ///
        /// 🔴 <c>pixelsPerUnitMultiplier</c> 不是可有可無的裝飾。UGUI 畫 Sliced 時是拿
        ///    <c>sprite.border ÷ (sprite.pixelsPerUnit / canvas.referencePixelsPerUnit)</c> 當邊寬 ——
        ///    這個專案的圖一律 ppu=1(<c>SdoExtracted</c> 的 Sprite.Create 全寫死 1),而 CanvasScaler 給的
        ///    參考值是 UGUI 預設的 100 → 除數是 0.01,border 12 會被當成 **1200** 單位。UGUI 遇到
        ///    「左右邊加起來比整個 rect 還寬」只好等比夾成各半 → 整條膠囊變成兩個被橫向拉爛的圓角、
        ///    中段完全不見。乘回 refPPU/spritePPU 之後 border 才剛好等於我們量的那幾個像素。
        /// </summary>
        private static void SetSliced(Image img, Sprite s, Color32 fallback)
        {
            if (img == null) return;
            img.sprite = s;
            img.type = Image.Type.Sliced;
            img.fillCenter = true;
            if (s != null)
            {
                var canvas = img.canvas;                                      // 建立時就已 parent 進 root → 找得到
                float refPpu = canvas != null ? canvas.referencePixelsPerUnit : 100f;   // 拿不到就用 UGUI 預設值
                float spritePpu = s.pixelsPerUnit > 0f ? s.pixelsPerUnit : 1f;
                img.pixelsPerUnitMultiplier = Mathf.Max(0.01f, refPpu / spritePpu);
            }
            img.color = s != null ? (Color)Color.white : (Color)fallback;
        }

        /// <summary>
        /// 一個中文字約 1em、半形約 0.55em 的粗估寬度。
        ///
        /// 為什麼不問 TMP 要 preferredWidth:那要先把物件建出來、跑一次排版才有值,而寬度是**建之前**
        /// 就要決定的(整個選單每列等寬)。粗估寬一點沒有壞處 —— 底圖是 9-slice,多幾 px 不會糊。
        /// </summary>
        public static float TextWidth(string s, float fontSize)
        {
            if (string.IsNullOrEmpty(s)) return 0f;
            float w = 0f;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool wide = (c >= 0x1100 && c <= 0x115F)      // 韓文字母
                            || (c >= 0x2E80 && c <= 0xA4CF)   // CJK 部首 / 假名 / 注音 / 漢字
                            || (c >= 0xAC00 && c <= 0xD7A3)   // 韓文音節
                            || (c >= 0xF900 && c <= 0xFAFF)   // CJK 相容漢字
                            || (c >= 0xFE30 && c <= 0xFE4F)   // CJK 相容形式
                            || (c >= 0xFF00 && c <= 0xFF60);  // 全形英數/標點
                w += wide ? fontSize : fontSize * 0.55f;
            }
            return w;
        }

        /// <summary>把 rect 擺到設計座標的 (x,y)(左上原點、y 向下)。</summary>
        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }
    }
}
