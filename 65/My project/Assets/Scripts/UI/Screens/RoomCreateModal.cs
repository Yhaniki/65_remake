using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Sdo.Localization;
using Sdo.UI.Services;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 「創建遊戲房間」對話框 —— 大廳按「創建舞台」時彈出。
    ///
    /// 版位**逐字取自官方 <c>DATA/UI/LOBBYDLG/ROOMCREATEDLG/ROOMCREATEDLG.XML</c>**。
    /// 🔴 那份 XML 只有一層容器 <c>&lt;Window name="win_createroom" x="300" y="150"&gt;</c>,
    ///    所有元素都是它的直接子節點 → **絕對座標 = 元素自己的 x/y + (300,150)**,下面的常數都加好了。
    ///    (Window 上那個 w=800 h=600 是官方複製貼上的殘留,視窗本體只有 302×288,不要拿來當尺寸。)
    ///
    /// 🔴 **標題與三個欄位名都烤在底圖上**(「创建游戏房间」/「房间名称」/「密　码」/「游戏模式」),
    ///    兩張房型縮圖也各自烤了「普通房间」「爱的小屋」—— 程式一個字都不要畫,畫了就疊字。
    ///    這與 <see cref="PlayerInfoModal"/> 的分頁底圖是同一個模式。
    ///
    /// 🔴 官方**沒有**「設定密碼」勾選框(全樹掃過 CheckBox/password/lock,這份 XML 只有房型那兩個 CheckBox)。
    ///    規則就是「密碼留白 = 不上鎖」,不要自己加開關。
    /// </summary>
    public sealed class RoomCreateModal : MonoBehaviour
    {
        // ---- ROOMCREATEDLG.XML 的版位(已加上 win_createroom 的 (300,150) 偏移)----
        private const float BoardX = 301f, BoardY = 151f;              // background.an (0,0,302,288)
        private const float NameX = 406f, NameY = 202f, FieldW = 157f, NameH = 14f;
        private const float PassX = 405f, PassY = 230f, PassH = 17f;
        private const float ModeX = 409f, ModeY = 259f;                // currmode(顯示目前模式的那個 Label)
        private const float ArrowX = 544f, ArrowY = 256f;              // mode ▼ 鈕 22×21
        private const float RoomTypeY = 292f, RoomTypeW = 84f, RoomTypeH = 85f;
        private const float NormalRoomX = 345f, CohabitRoomX = 467f;
        private const float OkX = 362f, CancelX = 466f, BtnY = 388f;

        // 🔴 currmode 官方寫 w=167(409+167=576),會鑽到 ▼ 鈕(544..566)底下 —— 官方自己畫歪的。
        //    夾到 135 讓它停在箭頭鈕左緣。
        private const float ModeW = 135f, ModeH = 14f;

        // 下拉清單:ChoseState1/3 是 166×26,官方 interval=-1 → step 25。
        // 🔴 用 25 不是 26:那兩張圖的第 0 列與第 25 列 alpha 只有 102/153(羽化邊),重疊 1px 才接得無縫;
        //    照 26 排兩列之間會多一條半透明接縫。
        private const float ListX = 401f, ListY = 279f, ListW = 166f, RowH = 26f;

        // ---- 從官方原圖取樣的顏色 ----
        /// <summary>兩個輸入框與模式字的深棕金(官方 XML 三個欄位都寫 color=0xff815105)。</summary>
        private static readonly Color32 FieldText = new Color32(0x81, 0x51, 0x05, 255);

        /// <summary>官方 EditBox 的 limittext。房名 24、密碼 20。</summary>
        private const int NameLimit = 24, PassLimit = 20;

        private CanvasGroup _cg;
        private TMP_InputField _nameInput, _passInput;
        private Image _normalBox, _cohabitBox;
        private bool _cohabit;                      // false = 普通房間(官方預設)
        private int _mode;                          // 0 = 自由模式、1 = 普通模式(見 BuildMode)
        private Action<string, string, GameMode> _onConfirm;

        public bool IsOpen => _cg != null && _cg.alpha > 0f && _cg.blocksRaycasts;

        private static string L(string k) => LocalizationManager.Get(k);

        public void Build(RectTransform parent)
        {
            var root = UIKit.NewRect(parent, "RoomCreateModal");
            UIKit.Stretch(root);
            _cg = root.gameObject.AddComponent<CanvasGroup>();

            // 擋住背後大廳的點擊。🔴 **完全透明** —— 官方開這個框時背後的大廳是原本亮度,
            //    而且壓暗的黑幕貼在框邊會被看成「框被切壞」(見 PlayerInfoModal 那一輪的教訓)。
            var dim = UIKit.AddImage(root, "Dim", new Color(0f, 0f, 0f, 0f), true);
            UIKit.Stretch(dim.rectTransform);

            UIKit.AddSprite(root, "Board", RoomCreateArt.Board, BoardX, BoardY);

            _nameInput = BuildField(root, "roomname", NameX, NameY, NameH, NameLimit, false);
            _passInput = BuildField(root, "password", PassX, PassY, PassH, PassLimit, true);

            BuildMode(root);
            BuildRoomType(root);

            // 確定 / 取消。兩顆鈕的「确 定」「取 消」都烤在圖上 → 不要疊字。
            AddButton(root, "Dialog-OK", RoomCreateArt.OkN, RoomCreateArt.OkH, RoomCreateArt.OkP, OkX, Confirm);
            AddButton(root, "Dialog-Cancel", RoomCreateArt.CancelN, RoomCreateArt.CancelH, RoomCreateArt.CancelP,
                      CancelX, Close);

            Hide();
        }

        /// <summary>
        /// 一個輸入框。底圖已經把凹槽畫好了(房名槽 y196..220、密碼槽 225..249,水平內緣都是 406..563)——
        /// 所以 <c>AddInputField</c> 預設那層半透明白底要關掉,不然凹槽裡會多一塊亮片。
        /// </summary>
        private TMP_InputField BuildField(RectTransform root, string name, float x, float y, float h,
                                          int limit, bool password)
        {
            var f = UIKit.AddInputField(root, name, "", 12f);
            var rt = (RectTransform)f.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(FieldW, h);

            var bg = f.GetComponent<Image>(); if (bg != null) bg.color = new Color(1f, 1f, 1f, 0f);
            f.textComponent.color = FieldText;
            f.characterLimit = limit;
            if (password) f.contentType = TMP_InputField.ContentType.Password;
            // 🔴 關掉 onFocusSelectAll:它預設為 true,而 SelectAll 被延到 LateUpdate 才跑,會蓋掉
            //    MoveTextEnd → 重新聚焦時游標跳到最前面(見 JoinRoomModal / RoomScreen 聊天輸入的同一個坑)。
            f.onFocusSelectAll = false;
            return f;
        }

        /// <summary>
        /// 遊戲模式下拉。官方是四項(第 2/3/4 項當年掛著 New 標記),但**四個模式的名字 XML 裡沒有** ——
        /// 是執行期由 client 塞的,整份 DATA 找不到字串表。這個重製版的 <see cref="GameMode"/> 只有
        /// 自由 / 普通兩個值,所以就放兩項,用與選歌畫面/房卡同一組 loc key(同一件事不該有三種講法)。
        /// </summary>
        private void BuildMode(RectTransform root)
        {
            // 🔴 slot 的 y 要給**箭頭**的 256,不是文字的 259:SdoComboBox 把 ▼ 鈕與值放在同一個 y,
            //    而官方這兩者差 3px(mode 256 / currmode 259)。差額由 valueOffsetY 補回去,
            //    照 259 傳的話箭頭會整顆掉出凹槽下緣。
            SdoComboBox.Create(root, "gamemode",
                ModeX, ArrowY, ModeW, ModeH, ArrowX,
                RoomCreateArt.ArrowN, RoomCreateArt.RowN, RoomCreateArt.RowH,
                new[] { L("songselect.mode_free"), L("songselect.mode_normal") }, null,
                _mode, FieldText, FieldText,
                i => _mode = i,
                listAsText: true, expandDown: true, listWidth: ListW, listX: ListX,
                valueOffsetY: ModeY - ArrowY);
        }

        /// <summary>
        /// 房型二選一。官方把縮圖(Label)與勾選框(CheckBox)擺在**完全重合**的座標上,疊法是:
        ///   底層 = NormalRoom / CohabitRoom 縮圖(永遠畫)
        ///   上層 = 選中時才顯示的 choosebox(空心橘框)—— 對應的 unchoose.an 量過是全透明,所以未選就是不畫。
        ///
        /// 🔴 這是**二選一**不是兩個獨立開關,互斥要自己做。
        /// 🔴 愛的小屋這個重製版沒有(要有伴侶),照官方「不可選就換灰階圖」的做法用 CohabitRoomGray,
        ///    並且**按了安靜地什麼都不做**(大廳一律不彈 Toast、不寫聊天區)。
        /// </summary>
        private void BuildRoomType(RectTransform root)
        {
            UIKit.AddSprite(root, "NormalRoom", RoomCreateArt.NormalRoom, NormalRoomX, RoomTypeY);
            UIKit.AddSprite(root, "CohabitRoom", RoomCreateArt.CohabitRoomGray, CohabitRoomX, RoomTypeY);

            _normalBox = UIKit.AddSprite(root, "NorHouseSel", RoomCreateArt.ChooseBox, NormalRoomX, RoomTypeY);
            _cohabitBox = UIKit.AddSprite(root, "CohHouseSel", RoomCreateArt.ChooseBox, CohabitRoomX, RoomTypeY);

            // 命中區疊在縮圖上(縮圖與高亮框都不吃射線,不然高亮框一出現就把點擊擋在外面)。
            AddTypeHit(root, "NorHouse", NormalRoomX, () => SetCohabit(false));
            AddTypeHit(root, "CohHouse", CohabitRoomX, null);   // 愛的小屋:沒有這套系統 → 點了不做事

            SetCohabit(false);
        }

        private void AddTypeHit(RectTransform root, string name, float x, Action onClick)
        {
            var hit = UIKit.AddImage(root, name, new Color(0f, 0f, 0f, 0f), raycast: true);
            Place(hit.rectTransform, x, RoomTypeY, RoomTypeW, RoomTypeH);
            var btn = hit.gameObject.AddComponent<Button>();
            btn.targetGraphic = hit;
            btn.transition = Selectable.Transition.None;
            if (onClick != null) btn.onClick.AddListener(() => onClick());
        }

        private void SetCohabit(bool cohabit)
        {
            _cohabit = cohabit;
            if (_normalBox != null) _normalBox.enabled = !cohabit;
            if (_cohabitBox != null) _cohabitBox.enabled = cohabit;
        }

        private void AddButton(RectTransform root, string name, Sprite n, Sprite h, Sprite p, float x, Action onClick)
        {
            var btn = UIKit.AddSpriteButton(root, name, n, h, p, x, BtnY);
            if (btn != null && onClick != null) btn.onClick.AddListener(() => onClick());
            UiSfx.AttachClick(btn);
        }

        /// <summary>把 rect 擺到 800×600 設計座標的 (x,y)(左上原點、y 向下)。</summary>
        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }

        // ---------------------------------------------------------------- 開闔

        /// <summary>開框。<paramref name="onConfirm"/> 收到 (房名, 密碼, 模式)。</summary>
        public void Open(Action<string, string, GameMode> onConfirm)
        {
            _onConfirm = onConfirm;
            if (_nameInput != null) _nameInput.text = "";
            if (_passInput != null) _passInput.text = "";
            SetCohabit(false);
            if (_cg != null) { _cg.alpha = 1f; _cg.blocksRaycasts = true; _cg.interactable = true; }
            transform.SetAsLastSibling();
            if (_nameInput != null) _nameInput.ActivateInputField();
        }

        public void Close() { Hide(); }

        private void Hide()
        {
            if (_cg != null) { _cg.alpha = 0f; _cg.blocksRaycasts = false; _cg.interactable = false; }
        }

        private void Confirm()
        {
            var cb = _onConfirm;
            string name = _nameInput != null ? _nameInput.text : "";
            string pass = _passInput != null ? _passInput.text : "";
            // 模式索引照 BuildMode 的順序:0 = 自由、1 = 普通。
            var mode = _mode == 1 ? GameMode.Normal : GameMode.Free;
            Hide();
            if (cb != null) cb(name, pass, mode);
        }
    }
}
