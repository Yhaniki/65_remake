using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Sdo.Localization;
using Sdo.UI.Services;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 「房間信息 ROOM INFO」對話框 —— 大廳的房卡按**右鍵**時彈出。
    ///
    /// 版位**逐字取自官方 <c>DATA/UI/STATECOMMUNITYHALL/ROOMINFORMATIONDLG.XML</c>**。
    /// 🔴 那份 XML 的容器鏈是 <c>Screen &gt; WinMainEffect(0,0) &gt; Window(0,0)</c> —— **兩層都是 0,0**,
    ///    所以 XML 裡寫的數字就已經是 800×600 絕對座標,不用累加(與 ROOMCREATEDLG 那份要 +300/+150 不同)。
    ///
    /// 🔴 **四個欄位名都烤在底圖上**(房间名称 / 游戏模式 / 参与人数 + 观战 / 游戏歌曲),
    ///    列表的 4 列格子、捲軸軌道也是烤的 —— 程式只放值。
    ///
    /// 🔴 官方的玩家列表**沒有頭像欄**:ReportList 的 5 個 Column 全是文字欄,
    ///    col0(w=0,alpha=0)是隱藏的 id 欄、col1/col3 各 25 置中、col2 是 5px 的分隔、col4 是 167 靠右的名字。
    ///    不要自己加頭像進去。
    ///
    /// 🔴 大廳房卡的**右鍵怎麼觸發它,官方版面檔裡查不到** —— STATECOMMUNITYHALL.XML 的
    ///    <c>roomchk0..5</c> 六個 CheckBox 三態全是 <c>empty.an</c>,沒有任何 popmenu / 右鍵屬性,
    ///    那是寫在引擎程式碼裡的。所以這邊的右鍵是我們自己接的(見 LobbyScreen 的房卡)。
    /// </summary>
    public sealed class RoomInfoModal : MonoBehaviour
    {
        // ---- ROOMINFORMATIONDLG.XML 的版位(已經是絕對座標)----
        private const float BoardX = 241f, BoardY = 71f;               // stageinfoBG.an 341×423(含右/下的投影)
        private const float ValueX = 364f, ValueW = 170f, ValueH = 14f;
        private const float NameY = 151f, ModeY = 185f, NumY = 217f, MusicY = 251f;
        private const float AudiX = 486f, AudiW = 45f, PlayersW = 45f;
        private const float EnterX = 294f, CancelX = 437f, BtnY = 441f;
        private const float CloseX = 529f, CloseY = 78f;

        // 玩家列表。🔴 列的 y 是**照底圖量的**(285/314/342/371),不是 XML 名目上的 280+28n ——
        //    XML 說列表從 280 起、列高 28,但底圖畫的 4 個格子實際在 285 起,差 5px。照底圖擺才對得上格線。
        private const int RowCount = 4;
        private const float RowY0 = 285f, RowStep = 28.7f, RowH = 24f;
        // 欄位起點 297 = 列表左緣 276 + (264 - 222)/2 的置中留白 21(ReportList align=center,欄寬總和 222)。
        // 驗證方式不是推導而是對圖:col1..col3 的右緣 297+55 = 352,正好是底圖那條「左小格/右大格」分隔線。
        private const float ColLevelX = 297f, ColLevelW = 25f;
        private const float ColSexX = 327f, ColSexW = 25f;
        // 🔴 官方 col4 是 167 寬(右緣 519),但底圖那個右格只到 **514** —— 靠右的名字會壓在捲軸握把(521)上。
        //    收成 162 讓文字右緣正好落在格線上;這 5px 是官方自己畫歪的,不是我們算錯。
        // 🔴 名字**靠左但要縮排**:官方 col4 從 352 起,那正好是底圖那條分隔線 —— 字貼著線看起來像溢出來
        //    (使用者回報「名字太靠左」)。往內縮 8px,與右格的內緣對齊。
        private const float ColNameX = 360f, ColNameW = 154f;
        private const float HeartW = 18f, HeartH = 16f;   // FEMALE/MALE.an = stage.png (…,18,16)

        // 捲軸握把(RoomInfoSB.an 14×28,與大廳好友列表的 Lobby12 是同一個裁切框)。
        // 底圖畫死的軌道細線在 abs x 527..529 → 14 寬的握把置中是 521。
        private const float RailX = 521f, RailTop = 286f, RailH = 109f;

        // 官方原圖取樣(使用者指定):上面四排欄位的值是深藍紫、下面玩家列是暗紅紫。
        private static readonly Color32 ValueCol = new Color32(0x2B, 0x1D, 0x61, 0xFF);
        private static readonly Color32 RowCol = new Color32(0x53, 0x19, 0x5B, 0xFF);
        private const float RowFont = 12f;
        /// <summary>官方的觀戰欄是「0/10」——旁觀席固定 10 個位置。</summary>
        private const int AudienceCapacity = 10;

        private CanvasGroup _cg;
        private TextMeshProUGUI _name, _mode, _players, _audience, _music;
        private readonly List<TextMeshProUGUI> _rowLevel = new List<TextMeshProUGUI>();
        private readonly List<TextMeshProUGUI> _rowName = new List<TextMeshProUGUI>();
        private Action _onEnter;

        public bool IsOpen => _cg != null && _cg.alpha > 0f && _cg.blocksRaycasts;

        private static Sprite An(string n) => LobbyArt.AnSolo(n);
        private static string L(string k) => LocalizationManager.Get(k);

        public void Build(RectTransform parent)
        {
            var root = UIKit.NewRect(parent, "RoomInfoModal");
            UIKit.Stretch(root);
            _cg = root.gameObject.AddComponent<CanvasGroup>();

            // 擋住背後大廳的點擊。**完全透明** —— 同 RoomCreateModal / PlayerInfoModal,官方不壓暗背景。
            var dim = UIKit.AddImage(root, "Dim", new Color(0f, 0f, 0f, 0f), true);
            UIKit.Stretch(dim.rectTransform);

            // 🔴 底圖走 AnRaw(複製到自己的貼圖):stage.png 上這張 crop 的右邊隔 1px 就是一片不透明橘色,
            //    共用圖集會把它拖進框的右緣(見 LobbyArt.AnRaw)。
            UIKit.AddSprite(root, "Board", LobbyArt.AnRaw("stageinfoBG"), BoardX, BoardY);

            _name = AddValue(root, "roomname", ValueX, NameY, ValueW, TextAlignmentOptions.Left);
            _mode = AddValue(root, "gamemode", ValueX, ModeY, ValueW, TextAlignmentOptions.Left);
            _players = AddValue(root, "playersnum", ValueX, NumY, PlayersW, TextAlignmentOptions.Left);
            _audience = AddValue(root, "audinum", AudiX, NumY, AudiW, TextAlignmentOptions.Left);
            _music = AddValue(root, "musicname", ValueX, MusicY, ValueW, TextAlignmentOptions.Left);

            BuildList(root);

            // 捲軸握把。列表固定 4 列、我們最多也只顯示 4 個人 → 捲不動,但官方那根握把永遠在,停在最上面。
            UIKit.AddSprite(root, "ScrollHandle", LobbyArt.AnSoloAA("RoomInfoSB"), RailX, RailTop);

            // 進入 / 取消 / 右上 X。三顆鈕的字都烤在圖上 → 不要疊字。
            // 🔴 close1/2/3.an 是**完全同一個 crop**(官方沒做 hover/pushed 差異),照抄即可。
            AddButton(root, "enter", "btn_OK_1", "btn_OK_2", "btn_OK_3", EnterX, BtnY, Enter);
            AddButton(root, "cancel", "btn_Cancel_1", "btn_Cancel_2", "btn_Cancel_3", CancelX, BtnY, Close);
            AddButton(root, "close", "close1", "close2", "close3", CloseX, CloseY, Close);

            Hide();
        }

        /// <summary>
        /// 4 列玩家。官方是「左邊一個小格 + 右邊一個大格」,對照使用者給的官方畫面:
        /// 左格放**座位編號**(1/2/3…)、右格放名字。
        ///
        /// 🔴 **名字靠左**(使用者指定)。官方 XML 的 col4 寫 align=right,但實機畫面上是靠左的 ——
        ///    以畫面為準。
        /// 🔴 **不畫性別愛心**(使用者指定)。上一版在中間那欄放了房卡那排愛心,官方那格是空的。
        /// </summary>
        private void BuildList(RectTransform root)
        {
            for (int i = 0; i < RowCount; i++)
            {
                float y = RowY0 + i * RowStep;
                _rowLevel.Add(AddValue(root, "row" + i + "_no", ColLevelX, y + 4f, ColLevelW, TextAlignmentOptions.Center));
                _rowName.Add(AddValue(root, "row" + i + "_name", ColNameX, y + 4f, ColNameW, TextAlignmentOptions.Left));
            }
        }

        private static TextMeshProUGUI AddValue(RectTransform parent, string name, float x, float y, float w,
                                                TextAlignmentOptions align)
        {
            // 玩家列與上面四排欄位的字色不同(見 ValueCol / RowCol)——用名字前綴分,列都叫 rowN_*。
            bool isRow = name.StartsWith("row");
            var t = UIKit.AddText(parent, name, "", RowFont, isRow ? RowCol : ValueCol, align);
            // 玩家列的字色偏暗、底又是深紫 —— 加粗才讀得清楚(使用者回報「下面字太深」)。
            if (isRow) t.fontStyle = FontStyles.Bold;
            Place(t.rectTransform, x, y, w, ValueH);
            t.overflowMode = TextOverflowModes.Ellipsis;   // 房名/暱稱沒有長度上限,不截會蓋到隔壁欄
            return t;
        }

        private void AddButton(RectTransform root, string name, string n, string h, string p,
                               float x, float y, Action onClick)
        {
            var btn = UIKit.AddSpriteButton(root, name, An(n), An(h), An(p), x, y);
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

        /// <summary>開框。<paramref name="onEnter"/> 是按「進入」時要做的事(通常就是加入那間房)。</summary>
        public void Open(RoomInfo r, Action onEnter)
        {
            if (r == null) return;
            _onEnter = onEnter;

            // 房名空的時候要顯示「房主名 + 的舞蹈室」—— 與房卡上那行同一份規則,不要各算各的。
            _name.text = RoomLabels.DisplayName(r.Name, r.HostName);
            _mode.text = L(r.Mode == GameMode.Normal ? "songselect.mode_normal" : "songselect.mode_free");
            _players.text = r.Count + "/" + r.Capacity;
            // 旁觀人數:房間快照沒帶旁觀者,官方那格是「0/10」。
            _audience.text = "0/" + AudienceCapacity;
            _music.text = SongLabel(r);

            FillRows(r);

            if (_cg != null) { _cg.alpha = 1f; _cg.blocksRaycasts = true; _cg.interactable = true; }
            transform.SetAsLastSibling();
        }

        /// <summary>
        /// 把座位填進 4 列。空位就整列清空 —— 官方底圖那 4 個格子永遠在,格子裡沒字而已。
        ///
        /// 🔴 性別**不在 <see cref="SeatInfo"/> 裡**,在 <c>RoomInfo.SeatGenders</c>(0=女 1=男,依座位順序)——
        ///    房間列表的封包沒有逐座位資料,那個陣列是 ToRoomInfo 補出來的。資料缺了一律當女生,
        ///    與大廳房卡那排愛心同一個退化規則。
        /// </summary>
        private void FillRows(RoomInfo r)
        {
            var seats = r.Seats;
            int shown = 0;
            for (int i = 0; i < _rowLevel.Count; i++)
            {
                var p = seats != null && i < seats.Count ? seats[i].Player : null;
                if (p == null) { _rowLevel[i].text = ""; _rowName[i].text = ""; continue; }
                // 左格是**座位編號**(官方畫面上是 1/2/3…),不是等級 —— 空位不占號,所以從有人的那幾個往下數。
                _rowLevel[i].text = (++shown).ToString();
                _rowName[i].text = p.DisplayName ?? "";
            }
        }

        /// <summary>
        /// 遊戲歌曲那格。官方是「歌名 (難度級)」,例如「海芋恋 (9級)」。
        ///
        /// 🔴 **難度目前拿不到**:<see cref="RoomInfo"/> 只帶 <c>SongTitle</c> —— 房間列表的封包沒有難度欄位
        ///    (那是進房之後 roomState 才有的)。所以現在只寫歌名,**不硬掛一個「(0級)」**:
        ///    那會讓人以為所有房間的歌都是 0 級,比缺一個括號更糟。
        ///    要補的話得先讓 server 的 roomList 帶上難度,再從這裡接。
        /// </summary>
        private static string SongLabel(RoomInfo r) => Or(r.SongTitle);

        private static string Or(string s) => string.IsNullOrEmpty(s) ? "" : s;

        public void Close() { Hide(); }

        private void Hide()
        {
            if (_cg != null) { _cg.alpha = 0f; _cg.blocksRaycasts = false; _cg.interactable = false; }
        }

        private void Enter()
        {
            var cb = _onEnter;
            Hide();
            if (cb != null) cb();
        }
    }
}
