using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Sdo.UI.Util
{
    /// <summary>
    /// ROOMDLG combo dropdown. COLLAPSED = just the current value (white text, or a per-option value sprite)
    /// centred in the baked-art slot, plus the orange ▲ arrow (MusicSelDlg196) — NO background box. Clicking opens
    /// a GREEN list (ShopDlg strips) that expands UPWARD; picking updates the value and fires onPick(index);
    /// outside-click closes. Only one list is open at a time (a full-screen overlay closes it on any outside click).
    /// </summary>
    public sealed class SdoComboBox : MonoBehaviour
    {
        private RectTransform _root;
        private string[] _options;
        private Sprite[] _valueSprites;   // optional per-option sprite (e.g. 自由模式/普通模式); null -> text
        private bool _listAsText;         // dropdown rows render as text even when _valueSprites is set (collapsed value still uses the sprite)
        private int _index;
        private Sprite _listN, _listH;    // green list-row art (normal / selected)
        private Color _textColor;         // collapsed value text (the baked-slot "default" value)
        private Color _listTextColor;     // expanded green-row text (inside the green box)
        private Action<int> _onPick;
        private TextMeshProUGUI _label;   // collapsed value text (text mode)
        private Image _labelImg;          // collapsed value sprite (sprite mode)
        private float _x, _y, _w, _h;     // value slot (xml top-left coords)
        private GameObject _popup, _overlay;

        // The collapsed value + ▲ arrow sit 2px high vs the baked slot caption; nudge them up to line up.
        private const float ValueNudgeY = 2f;

        public int Index => _index;

        /// <summary>
        /// Build a collapsed dropdown: a value slot at (slotX,slotY,slotW,slotH) with the ▲ arrow at arrowX.
        /// <paramref name="valueSprites"/> (optional) renders each option as a sprite instead of text.
        /// </summary>
        public static SdoComboBox Create(RectTransform root, string name,
            float slotX, float slotY, float slotW, float slotH, float arrowX,
            Sprite arrowSprite, Sprite listN, Sprite listH,
            string[] options, Sprite[] valueSprites, int start, Color textColor, Color listTextColor, Action<int> onPick,
            bool listAsText = false)
        {
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
            combo._listAsText = listAsText;
            combo._x = slotX; combo._y = slotY; combo._w = slotW; combo._h = slotH;

            // value display, centred in the slot — nudged up 2px so it lines up with the baked-slot caption (the
            // popup geometry below still keys off the un-nudged _y, so only the visible value/arrow move).
            float valueY = slotY - ValueNudgeY;
            if (valueSprites != null)
            {
                combo._labelImg = UIKit.AddImage(root, name + "_val", Color.white);
                combo._labelImg.preserveAspect = true;
                combo._labelImg.raycastTarget = false;
                Place(combo._labelImg.rectTransform, slotX, valueY, slotW, slotH);
            }
            else
            {
                combo._label = UIKit.AddText(root, name + "_val", "", 14, textColor, TextAlignmentOptions.Center);
                Place(combo._label.rectTransform, slotX, valueY, slotW, slotH);
            }
            combo.RefreshValue();

            // orange ▲ arrow art (static visual; the slot is the click target) — nudged up with the value.
            if (arrowSprite != null)
            {
                var arrow = UIKit.AddImage(root, name + "_arr", Color.white);
                arrow.sprite = arrowSprite;
                arrow.raycastTarget = false;
                float aw = arrowSprite.rect.width, ah = arrowSprite.rect.height;
                Place(arrow.rectTransform, arrowX, valueY + (slotH - ah) / 2f, aw, ah);
            }

            var btn = slot.gameObject.AddComponent<Button>();
            btn.targetGraphic = slot; btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(combo.Toggle);
            return combo;
        }

        private void RefreshValue()
        {
            if (_labelImg != null)
            {
                var s = (_valueSprites != null && _index < _valueSprites.Length) ? _valueSprites[_index] : null;
                _labelImg.sprite = s;
                _labelImg.color = s != null ? Color.white : new Color(1f, 1f, 1f, 0f);
                if (s != null) _labelImg.rectTransform.sizeDelta = s.rect.size;   // native size, centred
            }
            else if (_label != null)
            {
                _label.text = (_index >= 0 && _index < _options.Length) ? _options[_index] : "";
            }
        }

        private void Toggle() { if (_popup != null) Close(); else Open(); }

        private void Open()
        {
            int n = _options.Length;
            if (n == 0) return;
            float rowH = _h;
            float panelH = rowH * n;
            float top = _y - panelH;   // expand up: panel bottom edge == slot top edge

            _overlay = UIKit.AddImage(_root, "ComboOverlay", new Color(0f, 0f, 0f, 0.001f), raycast: true).gameObject;
            UIKit.Stretch((RectTransform)_overlay.transform);
            _overlay.transform.SetAsLastSibling();
            var ob = _overlay.AddComponent<Button>();
            ob.targetGraphic = _overlay.GetComponent<Image>(); ob.transition = Selectable.Transition.None;
            ob.onClick.AddListener(Close);

            var panel = UIKit.NewRect(_root, "ComboPopup");
            Place(panel, _x, top, _w, panelH);
            panel.SetAsLastSibling();
            _popup = panel.gameObject;

            for (int i = 0; i < n; i++)
            {
                int idx = i;
                var row = UIKit.AddImage(panel, "row" + i, Color.white, raycast: true);
                row.sprite = (i == _index) ? _listH : _listN;   // green row (selected uses the hover art)
                var rt = row.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
                rt.sizeDelta = new Vector2(_w, rowH); rt.anchoredPosition = new Vector2(0f, -rowH * i);

                if (!_listAsText && _valueSprites != null && i < _valueSprites.Length && _valueSprites[i] != null)
                {
                    var im = UIKit.AddImage(row.transform, "s", Color.white);
                    im.sprite = _valueSprites[i]; im.preserveAspect = true; im.raycastTarget = false;
                    UIKit.Stretch(im.rectTransform, 4, 2, 4, 2);
                }
                else
                {
                    var txt = UIKit.AddText(row.transform, "t", _options[i], 14, _listTextColor, TextAlignmentOptions.Center);
                    UIKit.Stretch(txt.rectTransform, 4, 0, 4, 0);
                }

                var rb = row.gameObject.AddComponent<Button>();
                rb.targetGraphic = row; rb.transition = Selectable.Transition.SpriteSwap;
                var st = rb.spriteState; st.highlightedSprite = _listH; st.pressedSprite = _listH; rb.spriteState = st;
                rb.onClick.AddListener(() => Pick(idx));
                UiSfx.AttachClick(rb);   // button press -> SE_0001
            }
        }

        private void Pick(int i)
        {
            _index = i;
            RefreshValue();
            Close();
            _onPick?.Invoke(i);
        }

        private void Close()
        {
            if (_popup != null) { Destroy(_popup); _popup = null; }
            if (_overlay != null) { Destroy(_overlay); _overlay = null; }
        }

        private void OnDisable() => Close();

        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = new Vector2(x, -y);
        }
    }
}
