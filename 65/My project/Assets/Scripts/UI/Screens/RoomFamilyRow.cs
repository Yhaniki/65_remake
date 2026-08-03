using Sdo.Game;
using Sdo.UI.Util;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 頭上名字牌的「家族列」:小徽章(DATA/EMBLEM)＋ 家族名稱(白字描黑邊),畫在名字的**上方**一行。
    ///
    /// 為什麼抽成一個類別:房間裡**每個人**頭上都有一條 —— 本機自己那條,加上每個遠端玩家/旁觀者
    /// 各一條。以前只有本機那一份(欄位直接掛在 <see cref="RoomScreen"/> 上),於是「別人的家族」
    /// 在房間裡根本看不到(使用者回報)。排版數學(徽章+名稱當一個群組水平置中)只該有一份,
    /// 兩處各寫一次的話遠端那條遲早會偏掉,而那種偏移要兩台機器擺在一起才看得出來。
    ///
    /// 內容空(沒有家族名)→ 整條不顯示;徽章載不到 → 只顯示名稱。
    /// </summary>
    internal sealed class RoomFamilyRow
    {
        // ---- 版面。字級/字距/holder 高都對齊名字列(14px、h=20、trackEm=HeadNameTrackEm),兩行看起來才同一套。 ----

        /// <summary>徽章顯示邊長(design px);原圖 24×24 縮到與 14px 字相稱。</summary>
        public const float EmblemSize = 15f;

        /// <summary>徽章與家族名稱之間的水平間距(design px);調大＝徽章離字遠一點。</summary>
        public const float EmblemGap = 5f;

        /// <summary>家族列 holder 高＝同名字 holder(20):兩行都垂直置中 → 中心距 = <see cref="LinePitch"/>。</summary>
        public const float RowH = 20f;

        /// <summary>家族列與名字「兩行中心」的垂直距離(design px);調小＝兩行靠更近。</summary>
        public const float LinePitch = 15f;

        /// <summary>家族名稱白字的黑邊厚度(canvas px),同頭上名字。</summary>
        public const float NameEdgePx = 1.4f;

        private readonly OutlinedLabel _label;
        private readonly Image _emblem;

        private string _name = "";
        private string _emblemName = "";
        private bool _hasEmblem;      // 徽章圖真的載到了(名稱有填但檔案不在 → false)
        private bool _visible = true; // 呼叫端說「這顆頭現在看得到」

        private RoomFamilyRow(OutlinedLabel label, Image emblem)
        {
            _label = label;
            _emblem = emblem;
        }

        /// <summary>
        /// 建一條家族列。<paramref name="id"/> 只是物件名的後綴(本機用 ""、遠端用 userId),方便在 Hierarchy 裡認。
        /// 建好時是關著的 —— 有內容(<see cref="Set"/>)才會亮。
        /// </summary>
        public static RoomFamilyRow Create(Transform parent, string id)
        {
            // 名稱用「左對齊」:徽章+名稱要當一個群組一起水平置中,左對齊才能讓文字自群組內的固定起點畫出。
            var label = OutlinedLabel.Create(parent, "FamilyName" + id, 0, 0, 160, RowH, 14,
                                             Color.white, Color.black, NameEdgePx, true,
                                             TextAlignmentOptions.Left, trackEm: TextStyles.HeadNameTrackEm);
            label.gameObject.SetActive(false);

            var emblem = UIKit.AddImage(parent, "FamilyEmblem" + id, Color.white);
            var rt = emblem.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);   // 左上錨(同 AddSprite):anchoredPosition=(x,-y)
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(EmblemSize, EmblemSize);
            emblem.raycastTarget = false;
            emblem.gameObject.SetActive(false);

            return new RoomFamilyRow(label, emblem);
        }

        /// <summary>有東西可以畫嗎(家族名非空)。</summary>
        public bool HasContent => _name.Length > 0;

        /// <summary>
        /// 換內容。<paramref name="familyName"/> 空 → 整條(名稱+徽章)不顯示;
        /// <paramref name="emblemName"/> 空或檔案不在 → 只顯示名稱。
        ///
        /// 值一樣時不重設 —— 這條會被每幀/每份快照呼叫,而 <c>SetText</c> 會重算字距與描邊環。
        /// </summary>
        public void Set(string familyName, string emblemName)
        {
            string n = (familyName ?? "").Trim();
            string e = (emblemName ?? "").Trim();
            if (n == _name && e == _emblemName) { Apply(); return; }
            _name = n;
            _emblemName = e;

            if (_label != null && n.Length > 0) _label.SetText(n);

            Sprite sprite = n.Length > 0 ? EmblemArt.Emblem(e) : null;
            _hasEmblem = sprite != null;
            if (_emblem != null && _hasEmblem) _emblem.sprite = sprite;

            Apply();
        }

        /// <summary>這顆頭現在看不看得到(角色轉到鏡頭後面就整條藏起來,同名字牌)。</summary>
        public void SetVisible(bool visible)
        {
            if (_visible == visible) return;
            _visible = visible;
            Apply();
        }

        /// <summary>
        /// 把整條(徽章+名稱)水平置中於頭部,疊在名字上方一行。
        /// <paramref name="centerX"/> = 頭在 800×600 設計座標的水平中心;
        /// <paramref name="nameTop"/> = 名字列 holder 的頂端 y(自畫面上緣往下為正)。
        ///
        /// 徽章與名稱當**一個群組**一起置中(而不是各自置中),否則有沒有徽章會讓名稱整體左右跳動。
        /// </summary>
        public void Place(float centerX, float nameTop)
        {
            if (_label == null || !_label.gameObject.activeSelf) return;

            float rowTop = nameTop - LinePitch;
            float textW = _label.PreferredWidth;
            bool hasEmblem = _emblem != null && _emblem.gameObject.activeSelf;
            float emblemW = hasEmblem ? EmblemSize : 0f;
            float gap = hasEmblem ? EmblemGap : 0f;
            float left = centerX - (emblemW + gap + textW) * 0.5f;

            _label.Rect.anchoredPosition = new Vector2(left + emblemW + gap, -rowTop);   // 左對齊:文字起點 = 群組左緣+徽章+間距
            if (hasEmblem)
                _emblem.rectTransform.anchoredPosition =
                    new Vector2(left, -(rowTop + (RowH - EmblemSize) * 0.5f));           // 徽章垂直置中於家族列
        }

        /// <summary>拆掉(玩家離開房間 / 換畫面)。</summary>
        public void Destroy()
        {
            if (_label != null) Object.Destroy(_label.gameObject);
            if (_emblem != null) Object.Destroy(_emblem.gameObject);
        }

        private void Apply()
        {
            bool show = _visible && HasContent;
            if (_label != null && _label.gameObject.activeSelf != show) _label.gameObject.SetActive(show);
            bool emblemShow = show && _hasEmblem;
            if (_emblem != null && _emblem.gameObject.activeSelf != emblemShow) _emblem.gameObject.SetActive(emblemShow);
        }
    }
}
