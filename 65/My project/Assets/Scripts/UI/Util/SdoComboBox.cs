using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Sdo.Localization;

namespace Sdo.UI.Util
{
    /// <summary>
    /// ROOMDLG-style combo dropdown. COLLAPSED = just the current value (white text, or a per-option value sprite)
    /// centred in the baked-art slot, plus a caller-supplied arrow sprite — NO background box. Clicking opens a list
    /// (caller-supplied row art) that expands UP by default, or DOWN when <c>expandDown</c> is set; picking updates the
    /// value and fires onPick(index); outside-click closes. Only one list is open at a time (a full-screen overlay
    /// closes it on any outside click). Callers: song-select (orange ▲ MusicSelDlg196, green ShopDlg rows, up) and the
    /// room 掉落方式 dropdown (▼ ShopDlg13, green LabUnCheck/LabCheck rows, down).
    /// </summary>
    public sealed class SdoComboBox : MonoBehaviour
    {
        private RectTransform _root;
        private string[] _options;
        private string[] _optionKeys;     // 選項字的 localization key(給了就跟著語言重解,見 Relocalize)
        private Sprite[] _valueSprites;   // optional per-option sprite (e.g. 自由模式/普通模式); null -> text
        private bool _chineseValueSprites;// 值圖上的字是烘死的中文 → 只有中文語系用得上(見 UseSpriteValue)
        private bool _listAsText;         // dropdown rows render as text even when _valueSprites is set (collapsed value still uses the sprite)
        private int _index;
        private Sprite _listN, _listH;    // green list-row art (normal / selected)
        private Color _textColor;         // collapsed value text (the baked-slot "default" value)
        private Color _listTextColor;     // expanded green-row text (inside the green box)
        private bool _expandDown;         // list drops DOWN from the slot bottom (ROOM 掉落方式) instead of UP (song-select)
        private Action<int> _onPick;
        private TextMeshProUGUI _label;   // collapsed value text (text mode)
        private Image _labelImg;          // collapsed value sprite (sprite mode)
        private float _x, _y, _w, _h;     // value slot (xml top-left coords)
        private float _listW;             // dropdown-list width; 0 = same as the value slot (_w)
        private float _listX;             // dropdown-list LEFT edge; 0 = same as the value slot (_x)
        private GameObject _popup, _overlay;

        // The collapsed value + ▲ arrow sit 2px high vs the baked slot caption; nudge them up to line up.
        private const float ValueNudgeY = 2f;

        // Expand-UP lists (song-select 模式/隊形/旁觀) lift their whole panel a few px ABOVE the slot's top edge so
        // the bottom green row clears the baked purple slot frame instead of sitting flush on it. Expand-DOWN stays flush.
        private const float ExpandUpGap = 3f;

        public int Index => _index;

        /// <summary>True while the dropdown list is expanded (used by hosts to peel it on ESC before closing themselves).</summary>
        public bool IsOpen => _popup != null;

        /// <summary>
        /// Build a collapsed dropdown: a value slot at (slotX,slotY,slotW,slotH) with the ▲ arrow at arrowX.
        /// <paramref name="valueSprites"/> (optional) renders each option as a sprite instead of text.
        ///
        /// 多語系:給 <paramref name="optionKeys"/>(這時 <paramref name="options"/> 可以傳 null)選項字就綁在 key 上,
        /// 換語言時自己重解 —— 下拉是「建版面當下解一次」的話,從 OPTION 中途換語言它會停在舊語言(見 Relocalize)。
        /// <paramref name="chineseValueSprites"/> 標記「值圖上的字是烘死的中文」(選歌的 LABEL_SDO 模式名):
        /// 中文語系照用官方圖,英文/日文改畫翻譯後的文字,否則那格永遠是中文。
        /// </summary>
        public static SdoComboBox Create(RectTransform root, string name,
            float slotX, float slotY, float slotW, float slotH, float arrowX,
            Sprite arrowSprite, Sprite listN, Sprite listH,
            string[] options, Sprite[] valueSprites, int start, Color textColor, Color listTextColor, Action<int> onPick,
            bool listAsText = false, bool expandDown = false, float listWidth = 0f, float listX = 0f,
            float valueOffsetY = 0f, string[] optionKeys = null, bool chineseValueSprites = false)
        {
            if (optionKeys != null && (options == null || options.Length == 0))
            {
                options = new string[optionKeys.Length];
                for (int i = 0; i < optionKeys.Length; i++) options[i] = LocalizationManager.Get(optionKeys[i]);
            }
            options = options ?? new string[0];
            start = Mathf.Clamp(start, 0, Mathf.Max(0, options.Length - 1));

            float arrowW = arrowSprite != null ? arrowSprite.rect.width : 25f;
            float total = (arrowX + arrowW) - slotX;

            // transparent clickable slot covering the value + arrow (no green box when collapsed).
            var slot = UIKit.AddImage(root, name, new Color(1f, 1f, 1f, 0f), raycast: true);
            Place(slot.rectTransform, slotX, slotY, total, slotH);

            var combo = slot.gameObject.AddComponent<SdoComboBox>();
            combo._root = root; combo._options = options; combo._valueSprites = valueSprites; combo._index = start;
            combo._listN = listN; combo._listH = listH; combo._textColor = textColor; combo._listTextColor = listTextColor; combo._onPick = onPick;
            combo._listAsText = listAsText; combo._expandDown = expandDown;
            combo._optionKeys = optionKeys; combo._chineseValueSprites = chineseValueSprites;
            combo._x = slotX; combo._y = slotY; combo._w = slotW; combo._h = slotH;
            combo._listW = listWidth; combo._listX = listX;

            // value display, centred in the slot — nudged up 2px so it lines up with the baked-slot caption (the
            // popup geometry below still keys off the un-nudged _y, so only the visible value/arrow move).
            // valueOffsetY nudges ONLY the value text up (positive = up) without moving the arrow.
            float valueY = slotY - ValueNudgeY;
            float labelY = valueY - valueOffsetY;
            if (valueSprites != null)
            {
                combo._labelImg = UIKit.AddImage(root, name + "_val", Color.white);
                combo._labelImg.preserveAspect = true;
                combo._labelImg.raycastTarget = false;
                Place(combo._labelImg.rectTransform, slotX, labelY, slotW, slotH);
            }
            // 文字值:沒有值圖時是唯一的值;有值圖但那些圖是烘死的中文時,英日語系改用它(兩個都建好,
            // RefreshValue 依當下語言決定顯示哪一個 —— 換語言不必重建版面)。
            if (valueSprites == null || chineseValueSprites)
            {
                // 名字:純文字值沿用 _val(既有測試依這個名字找它);與值圖並存時另取 _valtxt,免得兩個物件同名。
                combo._label = UIKit.AddText(root, name + (valueSprites != null ? "_valtxt" : "_val"), "", 14, textColor, TextAlignmentOptions.Center);
                combo._label.fontStyle |= FontStyles.Bold;   // 收合值與清單列同步加粗
                Place(combo._label.rectTransform, slotX, labelY, slotW, slotH);
            }
            combo.RefreshValue();

            // orange ▲ arrow art (static visual; the slot is the click target) — keyed off the un-nudged valueY.
            if (arrowSprite != null)
            {
                var arrow = UIKit.AddImage(root, name + "_arr", Color.white);
                // ApplySprite(不是直接指派 .sprite):premult 貼圖(RoomDlgArt.AnPremult 的去白邊 ▲)必須配 premult 材質才畫得對,
                // 那個配對只發生在 ApplySprite 裡。它順帶設的 sizeDelta 由下面的 Place 覆蓋成同一個值(pad:0 → 原生尺寸)。
                UIKit.ApplySprite(arrow, arrowSprite);
                arrow.raycastTarget = false;
                float aw = arrowSprite.rect.width, ah = arrowSprite.rect.height;
                Place(arrow.rectTransform, arrowX, valueY + (slotH - ah) / 2f, aw, ah);
            }

            var btn = slot.gameObject.AddComponent<Button>();
            btn.targetGraphic = slot; btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(combo.Toggle);
            return combo;
        }

        /// <summary>值圖上的字是中文烘死的話,只有中文語系用得上;其餘語言退回(翻譯過的)文字值。</summary>
        private bool UseSpriteValue =>
            _valueSprites != null && (!_chineseValueSprites || IsChinese(LocalizationManager.Current));

        private static bool IsChinese(Language l) =>
            l == Language.TraditionalChinese || l == Language.SimplifiedChinese;

        private void RefreshValue()
        {
            bool useSprite = UseSpriteValue;
            if (_labelImg != null)
            {
                _labelImg.gameObject.SetActive(useSprite);
                // ApplySprite:值圖(選歌的 LABEL_SDO 模式名切片)是 premult 貼圖,材質配對只發生在這裡；它同時代掉了
                // 「null → 透明」與「sizeDelta = 原生尺寸置中」這兩件原本手寫的事。
                if (useSprite)
                    UIKit.ApplySprite(_labelImg, (_valueSprites != null && _index < _valueSprites.Length) ? _valueSprites[_index] : null);
            }
            if (_label != null)
            {
                _label.gameObject.SetActive(!useSprite);
                if (!useSprite) _label.text = (_index >= 0 && _index < _options.Length) ? _options[_index] : "";
            }
        }

        /// <summary>
        /// 換語言:選項字重解、收合值重畫(順帶在「中文圖 ↔ 翻譯文字」之間切換)。
        /// 清單正開著的話整份重建 —— 那些列是 Open() 當下用 _options 畫出來的。
        /// </summary>
        private void Relocalize()
        {
            if (_optionKeys != null)
            {
                if (_options == null || _options.Length < _optionKeys.Length) _options = new string[_optionKeys.Length];
                for (int i = 0; i < _optionKeys.Length; i++)
                    if (!string.IsNullOrEmpty(_optionKeys[i])) _options[i] = LocalizationManager.Get(_optionKeys[i]);
            }
            RefreshValue();
            if (IsOpen) { Close(); Open(); }
        }

        private void OnEnable()
        {
            LocalizationManager.LanguageChanged += Relocalize;
            // 藏著的時候換的語言收不到事件(房間那幾塊 UI 會整個 SetActive 收起來)→ 重新顯示時補解一次。
            // AddComponent 當下 OnEnable 就會跑一遍,那時欄位都還沒設 —— Relocalize 對全 null 是安全的。
            Relocalize();
        }

        private void Toggle() { if (_popup != null) Close(); else Open(); }

        private void Open()
        {
            int n = _options.Length;
            if (n == 0) return;
            float rowH = _h;
            float panelH = rowH * n;
            float listW = _listW > 0f ? _listW : _w;   // list can be narrower than the value slot (0 = match slot)
            float listX = _listX != 0f ? _listX : _x;  // list left edge; 0 = align with the value slot left (_x)
            // expand DOWN: panel top edge == slot bottom edge; expand UP: panel bottom edge sits ExpandUpGap px ABOVE
            // the slot top edge (clears the baked purple slot frame instead of overlapping it).
            float top = _expandDown ? _y + _h : _y - panelH - ExpandUpGap;

            _overlay = UIKit.AddImage(_root, "ComboOverlay", new Color(0f, 0f, 0f, 0.001f), raycast: true).gameObject;
            UIKit.Stretch((RectTransform)_overlay.transform);
            _overlay.transform.SetAsLastSibling();
            var ob = _overlay.AddComponent<Button>();
            ob.targetGraphic = _overlay.GetComponent<Image>(); ob.transition = Selectable.Transition.None;
            ob.onClick.AddListener(Close);

            var panel = UIKit.NewRect(_root, "ComboPopup");
            Place(panel, listX, top, listW, panelH);
            panel.SetAsLastSibling();
            _popup = panel.gameObject;

            for (int i = 0; i < n; i++)
            {
                int idx = i;
                var row = UIKit.AddImage(panel, "row" + i, Color.white, raycast: true);
                // ApplySprite 而非直接指派:列圖若走 premult 路徑要配 premult 材質(材質是 Image 層級的,所以下面
                // SpriteSwap 換 hover 圖時仍然對)。它設的 sizeDelta 由下一行的 listW×rowH 覆蓋。
                UIKit.ApplySprite(row, (i == _index) ? _listH : _listN);   // green row (selected uses the hover art)
                var rt = row.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
                rt.sizeDelta = new Vector2(listW, rowH); rt.anchoredPosition = new Vector2(0f, -rowH * i);

                if (!_listAsText && UseSpriteValue && i < _valueSprites.Length && _valueSprites[i] != null)
                {
                    var im = UIKit.AddImage(row.transform, "s", Color.white);
                    UIKit.ApplySprite(im, _valueSprites[i]);   // premult 值圖要配 premult 材質；尺寸交給下面的 Stretch
                    im.preserveAspect = true; im.raycastTarget = false;
                    UIKit.Stretch(im.rectTransform, 4, 2, 4, 2);
                }
                else
                {
                    var txt = UIKit.AddText(row.transform, "t", _options[i], 14, _listTextColor, TextAlignmentOptions.Center);
                    txt.fontStyle |= FontStyles.Bold;   // 官方清單字較粗;faux-bold 加粗提升可讀性
                    UIKit.Stretch(txt.rectTransform, 4, 0, 4, 0);
                }

                var rb = row.gameObject.AddComponent<Button>();
                rb.targetGraphic = row; rb.transition = Selectable.Transition.SpriteSwap;
                var st = rb.spriteState; st.highlightedSprite = _listH; st.pressedSprite = _listH; rb.spriteState = st;
                rb.onClick.AddListener(() => Pick(idx));
                UiSfx.AttachClick(rb);     // button press -> SE_0001
                UiHoverSfx.Attach(rb);     // pointer slides onto a row -> Menufloat (original menu hover SE)
            }
        }

        private void Pick(int i)
        {
            _index = i;
            RefreshValue();
            Close();
            _onPick?.Invoke(i);
        }

        /// <summary>Force-close the open list from outside (e.g. when the host panel collapses/slides away).</summary>
        public void CloseList() => Close();

        /// <summary>
        /// 從外部把顯示值改成第 <paramref name="index"/> 個,**不觸發 onPick**。
        ///
        /// 為什麼需要它:選歌面板只 BuildUI 一次,那幾個下拉的值是建版面當下從 session 讀的。
        /// session 之後被別的地方改掉(線上收到房間設定、自己開新房重設)時,下拉會停在舊值 ——
        /// 症狀是「房主把 ShowTime 的房主讓給我,我打開選歌選單卻寫自由模式」。
        /// 不通知是因為呼叫端是拿 session 的值來對齊 UI,回頭再寫一次 session 只會多一次
        /// 持久化/推送(場景那個 onPick 會 RoomConfig.Save + SyncIfHost)。
        /// </summary>
        public void SetIndexWithoutNotify(int index)
        {
            int last = Mathf.Max(0, (_options != null ? _options.Length : 1) - 1);
            int i = Mathf.Clamp(index, 0, last);
            if (i == _index) return;
            _index = i;
            RefreshValue();
            if (IsOpen) { Close(); Open(); }   // 展開中 → 重畫,高亮才會跟著移到新的那列
        }

        private void Close()
        {
            if (_popup != null) { Destroy(_popup); _popup = null; }
            if (_overlay != null) { Destroy(_overlay); _overlay = null; }
        }

        private void OnDisable()
        {
            LocalizationManager.LanguageChanged -= Relocalize;
            Close();
        }

        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = new Vector2(x, -y);
        }
    }
}
