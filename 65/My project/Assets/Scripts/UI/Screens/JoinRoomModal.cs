using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Sdo.Localization;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 「輸入房號」對話框 —— 選男女畫面按下「加入」時彈出。
    ///
    /// 框體是官方線上版的密碼輸入框(<c>UI/LOBBYDLG/PASSWORDDLG</c>:一列輸入欄 + 確認/取消)。
    /// 官方那個框做的事與我們要的一模一樣(進別人的房間前先輸入一串東西),所以直接沿用它的美術與版位,
    /// 只把烘死的簡體字抹掉、執行期疊上動態 TMP(<c>tools/make_join_room_dialog.py</c> 產 JOINDLG0..3)。
    /// 座標逐字取自 <c>PASSWORDDLG.XML</c>:框 (256,241)、輸入欄 (338,290,148×20)、確認 (327,333)、取消 (425,333)。
    ///
    /// 這個框**不自己做加入房間的動作** —— 它只負責「拿到一個房號」與「把失敗原因顯示在框裡」。
    /// 送出後留在畫面上等結果:房號打錯是最常見的情況,關掉框再重打一次很煩。
    /// </summary>
    public sealed class JoinRoomModal : MonoBehaviour
    {
        // ---- PASSWORDDLG.XML 的版位(800×600、左上原點)----
        private const float PanelX = 256f, PanelY = 241f, PanelW = 290f;
        private const float FieldX = 338f, FieldY = 290f, FieldW = 148f, FieldH = 20f;
        private const float OkX = 327f, CancelX = 425f, BtnY = 333f;

        // ---- 從官方原圖取樣的顏色 ----
        private static readonly Color32 TitleFace = new Color32(0xE5, 0x83, 0x09, 255);   // 標題橘
        private static readonly Color32 TitleEdge = new Color32(0xFF, 0xFF, 0xFF, 255);   // 標題外圍的白暈
        private static readonly Color32 LabelFace = new Color32(0xFF, 0xD1, 0x17, 255);   // 欄位標籤的奶油色
        private static readonly Color32 LabelEdge = new Color32(0x2E, 0x24, 0x28, 255);
        private static readonly Color BtnIdle = new Color32(0x18, 0x24, 0x45, 255);       // 鈕上的字:平常深藍黑
        private static readonly Color BtnHot = new Color32(0x5F, 0xB0, 0x02, 255);        //           滑過/按下轉綠

        /// <summary>房號位數。<see cref="Sdo.Net.NetLimits"/> 的房號池是 10000..99999。</summary>
        private const int CodeDigits = 5;

        private CanvasGroup _cg;
        private TMP_InputField _input;
        private OutlinedLabel _title, _fieldLabel, _error;
        private Action<int> _onSubmit;

        public bool IsOpen => _cg != null && _cg.alpha > 0f && _cg.blocksRaycasts;

        private static Sprite An(string n) => LobbySelArt.An(n);
        private static string L(string k) => LocalizationManager.Get(k);

        public void Build(RectTransform parent)
        {
            var root = UIKit.NewRect(parent, "JoinRoomModal");
            UIKit.Stretch(root);
            _cg = root.gameObject.AddComponent<CanvasGroup>();

            // 半透明黑幕:除了擋點擊,也把下面那張很花的選男女畫面壓暗,讓框跳出來。
            var dim = UIKit.AddImage(root, "Dim", new Color(0f, 0f, 0f, 0.55f), true);
            UIKit.Stretch(dim.rectTransform);

            UIKit.AddSprite(root, "Panel", An("JoinDlg0"), PanelX, PanelY);

            // 標題:官方的墨水框是 x=112..176 y=6..19(框內座標)→ 置中排在同一條基線上。
            _title = OutlinedLabel.Create(root, "Title", PanelX + 40f, PanelY + 3f, PanelW - 80f, 18f,
                                          14f, TitleFace, TitleEdge, 1f, bold: true);

            // 欄位標籤:官方是「密　码」那顆膠囊,內部 x=17..77 y=48..64(框內座標)。
            _fieldLabel = OutlinedLabel.Create(root, "FieldLabel", PanelX + 17f, PanelY + 47f, 61f, 18f,
                                               12f, LabelFace, LabelEdge, 1f, bold: true);
            SetTexts();

            _input = UIKit.AddInputField(root, "CodeInput", "", 14f);
            var irt = (RectTransform)_input.transform;
            irt.anchorMin = irt.anchorMax = new Vector2(0f, 1f);
            irt.pivot = new Vector2(0f, 1f);
            irt.anchoredPosition = new Vector2(FieldX, -FieldY);
            irt.sizeDelta = new Vector2(FieldW, FieldH);
            // 官方的輸入欄是畫在粉紅底上的,所以這裡不要再疊一層半透明白(AddInputField 的預設底)。
            var bg = _input.GetComponent<Image>(); if (bg != null) bg.color = new Color(1f, 1f, 1f, 0f);
            _input.textComponent.color = new Color32(0x2E, 0x24, 0x28, 255);
            _input.characterLimit = CodeDigits;
            _input.contentType = TMP_InputField.ContentType.IntegerNumber;
            // 🔴 關掉 onFocusSelectAll:它預設為 true,而 SelectAll 被延到 LateUpdate 才跑,會蓋掉
            // MoveTextEnd → 重新聚焦時游標跳到最前面(見 RoomScreen 聊天輸入的同一個坑)。
            _input.onFocusSelectAll = false;

            // 送出/取消。官方三態的 pushed 是「整顆往下 1px」,所以字也要跟著往下 1px(BtnLabel 處理)。
            AddButton(root, "Ok", OkX, "room.join_confirm", () => Submit());
            AddButton(root, "Cancel", CancelX, "common.cancel", Close);

            // 失敗原因就寫在框裡(不用 Toast):房號打錯是最常見的情況,訊息要留在眼睛已經在看的地方。
            // 位置是框內唯一的空檔:洋紅色欄位列到 y=79、鈕從 y=92 開始 → 中間那條 13px 高的白帶。
            // 字級 11 是被這 13px 逼出來的,所以訊息一律寫短(「房間已滿」四個字那種長度)。
            _error = OutlinedLabel.Create(root, "Error", PanelX + 8f, PanelY + 80f, PanelW - 16f, 13f,
                                          11f, new Color32(0xC0, 0x1A, 0x5E, 255), Color.white, 1f, bold: true);
            _error.SetText("");

            SetVisible(false);
        }

        private void AddButton(RectTransform root, string name, float x, string locKey, Action onClick)
        {
            var btn = UIKit.AddSpriteButton(root, name, An("JoinDlg1"), An("JoinDlg2"), An("JoinDlg3"), x, BtnY);
            btn.onClick.AddListener(() => onClick());
            UiSfx.AttachClick(btn);
            var label = UIKit.AddText(btn.transform, "Label", L(locKey), 13f, BtnIdle, TextAlignmentOptions.Center);
            UIKit.Stretch(label.rectTransform);
            var tint = btn.gameObject.AddComponent<BtnLabelTint>();
            tint.Bind(label);
        }

        // ---- 開/關 ----

        /// <summary>開框。<paramref name="onSubmit"/> 收到玩家輸入的房號(已檢查是 5 位數)。</summary>
        public void Open(Action<int> onSubmit)
        {
            _onSubmit = onSubmit;
            if (_error != null) _error.SetText("");
            SetTexts();
            if (_input != null) { _input.text = ""; _input.ActivateInputField(); }
            SetVisible(true);
        }

        public void Close()
        {
            if (_input != null) _input.DeactivateInputField();
            SetVisible(false);
            _onSubmit = null;
        }

        /// <summary>送出失敗:把原因留在框裡,框不關(讓玩家直接改房號重試)。</summary>
        public void ShowError(string message)
        {
            if (_error != null) _error.SetText(message ?? "");
            if (_input != null) _input.ActivateInputField();
        }

        private void SetVisible(bool on)
        {
            if (_cg == null) return;
            _cg.alpha = on ? 1f : 0f;
            _cg.blocksRaycasts = on;
            _cg.interactable = on;
        }

        // 標題與欄位標籤是 OutlinedLabel(不是 LocalizedText),所以換語言不會自動跟 —— 每次 Open() 重寫一次。
        private void SetTexts()
        {
            if (_title != null) _title.SetText(L("room.join_title"));
            if (_fieldLabel != null) _fieldLabel.SetText(L("room.join_code"));
        }

        private void Submit()
        {
            int code;
            string raw = (_input != null ? _input.text : "").Trim();
            if (raw.Length != CodeDigits || !int.TryParse(raw, out code))
            {
                ShowError(L("room.join_bad_code"));
                return;
            }
            var cb = _onSubmit;
            if (cb != null) cb(code);
        }

        private void Update()
        {
            if (!IsOpen) return;
            if (Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) Submit();
        }

        /// <summary>
        /// 鈕上的字:平常深藍黑、滑過與按下轉綠,按下時再往下 1px。
        ///
        /// 為什麼要這個小元件:官方那三張圖的差別**只有字的顏色**(hover 的底盤與 normal 逐位元組相同),
        /// 而我們把字抹掉了改用 TMP → 顏色變化就得自己接。順便處理「按下整顆往下 1px」那個位移,
        /// 底盤的位移在圖裡、字的位移在這裡,兩者要一起動才不會錯開。
        /// </summary>
        private sealed class BtnLabelTint : MonoBehaviour,
            IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
        {
            private TextMeshProUGUI _label;
            private Vector2 _home;
            private bool _hover, _down;

            public void Bind(TextMeshProUGUI label)
            {
                _label = label;
                _home = label.rectTransform.anchoredPosition;
                Apply();
            }

            public void OnPointerEnter(PointerEventData e) { _hover = true; Apply(); }
            public void OnPointerExit(PointerEventData e) { _hover = false; _down = false; Apply(); }
            public void OnPointerDown(PointerEventData e) { _down = true; Apply(); }
            public void OnPointerUp(PointerEventData e) { _down = false; Apply(); }

            private void Apply()
            {
                if (_label == null) return;
                _label.color = (_hover || _down) ? BtnHot : BtnIdle;
                _label.rectTransform.anchoredPosition = _down ? _home + new Vector2(0f, -1f) : _home;
            }
        }
    }
}
