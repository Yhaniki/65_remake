using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Sdo.Localization;
using Sdo.Settings;
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
        //    (使用者回報「名字太靠左」)。使用者分兩次比對:先縮 8px、再往右 10px,共 +18 → 370。
        //    右緣仍收在 514(底圖右格的格線),所以寬度跟著減。
        private const float ColNameX = 370f, ColNameW = 144f;
        private const float HeartW = 18f, HeartH = 16f;   // FEMALE/MALE.an = stage.png (…,18,16)

        // 捲軸握把(RoomInfoSB.an 14×28,與大廳好友列表的 Lobby12 是同一個裁切框)。
        // 底圖畫死的軌道細線在 abs x 527..529 → 14 寬的握把置中是 521。
        private const float RailX = 521f, RailTop = 286f, RailH = 109f;
        private const float HandleW = 14f, HandleH = 28f;

        // 滾輪的收訊範圍 = 那 4 個格子整片(左緣 276 見 ColLevelX 的註解,右緣收在格線 514)。
        private const float ListX = 276f, ListW = 238f;

        // 官方原圖取樣(使用者指定):上面四排欄位的值是深藍紫、下面玩家列是暗紅紫。
        private static readonly Color32 ValueCol = new Color32(0x2B, 0x1D, 0x61, 0xFF);
        private static readonly Color32 RowCol = new Color32(0x53, 0x19, 0x5B, 0xFF);
        private const float RowFont = 12f;
        /// <summary>官方的觀戰欄是「0/10」——旁觀席固定 10 個位置。</summary>
        private const int AudienceCapacity = 10;

        private CanvasGroup _cg;
        private RectTransform _root;
        private TextMeshProUGUI _name, _mode, _players, _audience, _music;
        private readonly List<TextMeshProUGUI> _rowLevel = new List<TextMeshProUGUI>();
        private readonly List<TextMeshProUGUI> _rowName = new List<TextMeshProUGUI>();
        private Action _onEnter;

        /// <summary>連線中嗎 —— 右鍵選單靠它決定要不要畫「私聊 / 好友 / 黑名單」那幾項(見 <see cref="PlayerContextMenu"/>)。
        /// 由 <c>FrontendApp</c> 綁(這個框自己拿不到 AppContext,而 <c>Ctx.Net</c> 在登入成功時會換人 → 一定要是委派)。</summary>
        public Func<bool> IsOnline;

        // 右鍵選單。彈出的那一幀不能被「點外面就關」自己關掉(觸發它的正是那一次點擊)。
        private GameObject _popup;
        private int _popupFrame = -1;

        // 捲動狀態。列表是**整數分頁**(一次捲一列),所以存的是「第一列是名單的第幾個人」,
        // 不是像素位移 —— 4 個格子是烤在底圖上的,列只能對齊格線。
        private Scrollbar _bar;
        private readonly List<Person> _people = new List<Person>();
        private int _scroll;

        /// <summary>名單上的一個人。**不只存 <see cref="PlayerProfile"/>** —— 右鍵選單的「玩家信息」要
        /// 性別(畫他的 3D 角色)與 server userId(查名片),那兩個欄位都不在 PlayerProfile 裡:
        /// 性別在 <c>RoomInfo.SeatGenders</c>(依人的順序,不是座位順序)、userId 在 <c>SeatInfo</c>。</summary>
        private struct Person
        {
            public PlayerProfile Profile;
            public int Gender;    // 0=女 1=男
            public int UserId;    // server 這次連線給的編號;0 = 離線/不知道
        }

        /// <summary>捲得動的列數(0 = 人數塞得進 4 列,握把停在最上面不動)。</summary>
        private int MaxScroll => Mathf.Max(0, _people.Count - RowCount);

        public bool IsOpen => _cg != null && _cg.alpha > 0f && _cg.blocksRaycasts;

        private static Sprite An(string n) => LobbyArt.AnSolo(n);
        private static string L(string k) => LocalizationManager.Get(k);

        public void Build(RectTransform parent)
        {
            var root = UIKit.NewRect(parent, "RoomInfoModal");
            UIKit.Stretch(root);
            _root = root;
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

            // 捲軸。列表固定 4 列、房間坐得下 6 個 → 滿房時真的捲得動。
            // 🔴 用 FixedScrollbar 而不是自畫一張握把圖接拖曳:握把要維持官方那個固定 28px
            //    (接 ScrollRect.verticalScrollbar 會被拉成「內容越長越扁」),而拖曳/點軌道跳位
            //    自己接過的下場見 FixedScrollbar 的類別註解。大廳那三條捲軸走的是同一條路。
            // 🔴 **永遠顯示**:沒得捲時停在軌道最上面 —— 官方那根握把是常駐的,藏起來看起來像忘了做。
            _bar = FixedScrollbar.Create(root, "ScrollBar", LobbyArt.AnSoloAA("RoomInfoSB"),
                                         RailX, RailTop, HandleW, HandleH, RailH);
            _bar.onValueChanged.AddListener(OnBarChanged);

            // 進入 / 取消 / 右上 X。三顆鈕的字都烤在圖上 → 不要疊字。
            // 🔴 close1/2/3.an 是**完全同一個 crop**(官方沒做 hover/pushed 差異),照抄即可。
            AddButton(root, "enter", "btn_OK_1", "btn_OK_2", "btn_OK_3", EnterX, BtnY, Enter);
            AddButton(root, "cancel", "btn_Cancel_1", "btn_Cancel_2", "btn_Cancel_3", CancelX, BtnY, Close);
            AddButton(root, "close", "close1", "close2", "close3", CloseX, CloseY, Close);

            Hide();
        }

        /// <summary>
        /// 4 列玩家。官方是「左邊一個小格 + 右邊一個大格」:左格放**等級**、右格放名字
        /// (欄名就叫 col_level —— 見上面的版位常數)。
        ///
        /// 🔴 曾經把左格寫成「座位編號」(1/2/3…):房裡只有一個人的時候剛好也印「1」,
        ///    看起來像等級 1,實際上是 11 等 —— 使用者回報的就是這個。編號沒有資訊量,官方那格是等級。
        ///
        /// 🔴 **名字靠左**(使用者指定)。官方 XML 的 col4 寫 align=right,但實機畫面上是靠左的 ——
        ///    以畫面為準。
        /// 🔴 **不畫性別愛心**(使用者指定)。上一版在中間那欄放了房卡那排愛心,官方那格是空的。
        /// </summary>
        private void BuildList(RectTransform root)
        {
            // 滾輪的收訊板 —— 全透明但吃射線。<c>UIKit.AddText</c> 出來的字 raycastTarget 是 false,
            // 而 UGUI 的 scroll 事件是從「射線打到的東西」往上冒泡的:不鋪這張,游標壓在名單上滾會直接
            // 穿到底下的 Dim,什麼都不會發生。先建 = 在字底下(它全透明,擋不到畫面)。
            var catcher = UIKit.AddImage(root, "ListWheel", new Color(0f, 0f, 0f, 0f), true);
            Place(catcher.rectTransform, ListX, RowY0, ListW, RowCount * RowStep);
            catcher.gameObject.AddComponent<WheelScroll>().Scrolled = OnWheel;

            for (int i = 0; i < RowCount; i++)
            {
                float y = RowY0 + i * RowStep;
                // 整列的右鍵收訊盤(全透明但吃射線)。官方在這個框裡右鍵參與者會跳「添加好友 / 玩家信息 /
                // 玩家私聊 / …」的選單(使用者提供的實機截圖)。
                // 🔴 一定要另外鋪一塊:上面那兩個 TMP 是 raycastTarget=false,而滾輪收訊板(catcher)是**整片**的,
                //    掛在它身上分不出點的是第幾列。
                // 🔴 而且必須是 catcher 的**子物件**,不是它的兄弟:UGUI 的滾輪事件是從「射線打到的東西」
                //    往**父層**冒泡的。鋪成兄弟的話這塊會擋在 catcher 前面,而 WheelScroll 在兄弟身上收不到 ——
                //    症狀是「名單多於 4 人時滾輪突然不能捲了」。當子物件則照樣冒泡上去。
                var hit = UIKit.AddImage(catcher.rectTransform, "row" + i + "_hit", new Color(0f, 0f, 0f, 0f), raycast: true);
                Place(hit.rectTransform, 0f, i * RowStep, ListW, RowStep);
                int rowIndex = i;
                var rc = hit.gameObject.AddComponent<RightClickProxy>();
                rc.Clicked = () => OnRowRightClick(rowIndex);

                _rowLevel.Add(AddValue(root, "row" + i + "_level", ColLevelX, y + 4f, ColLevelW, TextAlignmentOptions.Center));
                _rowName.Add(AddValue(root, "row" + i + "_name", ColNameX, y + 4f, ColNameW, TextAlignmentOptions.Left));
            }
        }

        // ---------------------------------------------------------------- 參與者右鍵選單

        /// <summary>
        /// 右鍵第 <paramref name="rowIndex"/> 列 → 那個人的社交選單(項目由 <see cref="PlayerContextMenu"/> 決定)。
        /// 空列不彈(那格沒有人,不是一個可以私聊/加好友的對象)。
        ///
        /// 🔴 位置用**滑鼠現在的螢幕座標**而不是那一列的座標:官方選單就是從游標處長出來的,而且四列在框裡很擠,
        ///    貼列頭會讓選單蓋掉隔壁兩列的名字。
        /// </summary>
        private void OnRowRightClick(int rowIndex)
        {
            ClosePopup();
            int idx = _scroll + rowIndex;
            if (idx < 0 || idx >= _people.Count) return;
            var person = _people[idx];
            string who = person.Profile != null ? (person.Profile.DisplayName ?? "").Trim() : "";
            if (who.Length == 0) return;

            var me = ProfileManager.Active;
            bool online = IsOnline != null && IsOnline();
            bool isSelf = me != null && string.Equals(who, (me.name ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            var actions = PlayerContextMenu.For(online, isSelf, FriendList.IsFriend(me, who), BlockList.IsBlocked(me, who));
            if (actions.Length == 0) return;

            var cam = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            _popup = SdoPopupMenu.Build(_root, "RoomInfoPopup", Input.mousePosition, cam, actions.Length,
                                        i => PlayerMenuLabels.Of(actions[i]),
                                        i =>
                                        {
                                            PlayerMenuActions.Run(actions[i], who, person.Profile, person.Gender,
                                                                  person.UserId, isSelf);
                                            ClosePopup();
                                        });
            _popupFrame = Time.frameCount;
        }

        private void ClosePopup()
        {
            if (_popup != null) { Destroy(_popup); _popup = null; }
        }

        /// <summary>選單開著時點到選單外面 → 關掉(彈出那一幀不算,否則會被觸發它的那次點擊自己關掉)。</summary>
        private void Update()
        {
            if (_popup == null) return;
            if (Time.frameCount == _popupFrame) return;
            if (!Input.GetMouseButtonDown(0) && !Input.GetMouseButtonDown(1)) return;
            var cam = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            if (SdoPopupMenu.ClickedOutside(_popup, Input.mousePosition, cam)) ClosePopup();
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

            TakePeople(r);
            _scroll = 0;              // 每次開框都從頭看起(上一間房捲到哪與這間無關)
            RefreshRows();
            PlaceHandle();

            if (_cg != null) { _cg.alpha = 1f; _cg.blocksRaycasts = true; _cg.interactable = true; }
            transform.SetAsLastSibling();
        }

        /// <summary>
        /// 房裡有誰 —— 把座位壓成一份**名單**。
        ///
        /// 🔴 **空位不佔位**:這是「房裡有誰」,不是座位圖。座位 0 空、座位 1 有人的房間,
        ///    那個人是名單的第一個,不是第二個(不然捲動時會捲到一片空白)。
        /// 🔴 性別**不在 <see cref="SeatInfo"/> 裡**,在 <c>RoomInfo.SeatGenders</c>(0=女 1=男)——
        ///    而且它的索引是**這份壓平後的名單**,不是座位號(見上面「空位不佔位」)。那個框不畫性別
        ///    (使用者指定),但右鍵選單的「玩家信息」要拿它去畫對方的 3D 角色,所以照樣收進來。
        /// </summary>
        private void TakePeople(RoomInfo r)
        {
            _people.Clear();
            var seats = r.Seats;
            if (seats == null) return;
            var genders = r.SeatGenders;
            for (int i = 0; i < seats.Count; i++)
            {
                if (seats[i] == null || seats[i].Player == null) continue;
                int n = _people.Count;
                _people.Add(new Person
                {
                    Profile = seats[i].Player,
                    Gender = genders != null && n < genders.Length ? genders[n] : 0,
                    UserId = seats[i].UserId,
                });
            }
        }

        /// <summary>
        /// 把名單第 <see cref="_scroll"/> 個起的 4 個人畫進那 4 列(左=等級、右=名字)。
        /// 名單不夠長的列清空 —— 官方底圖那 4 個格子永遠在,格子裡沒字而已。
        ///
        /// 🔴 等級 <b>0 = 不知道</b>(對方是**舊版 server**,roomList 不送 members)—— 這時整格留白。
        ///    寫「0」或補一個假數字都會讓人以為那是真的等級。
        /// </summary>
        private void RefreshRows()
        {
            for (int i = 0; i < _rowLevel.Count; i++)
            {
                int idx = _scroll + i;
                var p = idx >= 0 && idx < _people.Count ? _people[idx].Profile : null;
                _rowLevel[i].text = p != null && p.Level > 0 ? p.Level.ToString() : "";
                _rowName[i].text = p != null ? (p.DisplayName ?? "") : "";
            }
        }

        /// <summary>
        /// 捲軸被拖(或被點軌道)→ 換算成「第一列是第幾個人」。列是**整數分頁**的(4 個格子烤在底圖上,
        /// 列只能對齊格線),所以 0..1 要乘上可捲列數再四捨五入。
        /// <c>value</c> 是 BottomToTop:1 = 最上 = 第 0 個。
        ///
        /// 🔴 只在真的換列時才重畫 —— 拖曳時每幀都會回呼。
        /// </summary>
        private void OnBarChanged(float value)
        {
            int max = MaxScroll;
            // 沒得捲(人數 <= 4)還被拖動 → 彈回最上面。放著不管的話握把會停在半路,
            // 看起來像「捲了但列表沒跟上」。
            if (max <= 0) { PlaceHandle(); return; }

            int row = Mathf.Clamp(Mathf.RoundToInt((1f - value) * max), 0, max);
            if (row == _scroll) return;
            _scroll = row;
            RefreshRows();
            PlaceHandle();   // 對齊格線 —— 列只能整列跳,握把也跟著吸在那一格上
        }

        private void OnWheel(float dy)
        {
            if (Mathf.Approximately(dy, 0f)) return;
            int max = MaxScroll;
            if (max <= 0) return;
            int row = Mathf.Clamp(_scroll + (dy > 0f ? -1 : 1), 0, max);
            if (row == _scroll) return;
            _scroll = row;
            RefreshRows();
            PlaceHandle();
        }

        /// <summary>
        /// 把握把挪到目前這一列。
        /// 🔴 一定要用 <c>SetValueWithoutNotify</c>:這裡是「資料 → 捲軸」方向,發通知會反過來又叫一次
        /// <see cref="OnBarChanged"/>。
        /// </summary>
        private void PlaceHandle()
        {
            if (_bar == null) return;
            int max = MaxScroll;
            _bar.SetValueWithoutNotify(max > 0 ? 1f - _scroll / (float)max : 1f);
        }

        /// <summary>
        /// 遊戲歌曲那格,官方格式是「歌名 (難度級)」,例如「海芋恋 (9級)」。
        ///
        /// 🔴 難度 <b>0 = 不知道</b>(沒歌、譜面沒標難度,或對方是**舊版 server** 不送 songLevel)——
        ///    這時只寫歌名、**不掛「(0級)」**:那會讓整排房間看起來都是 0 級,比缺一個括號更糟。
        /// </summary>
        private static string SongLabel(RoomInfo r)
        {
            string title = Or(r.SongTitle);
            if (title.Length == 0) return "";
            return r.SongLevel > 0 ? title + " (" + r.SongLevel + "級)" : title;
        }

        private static string Or(string s) => string.IsNullOrEmpty(s) ? "" : s;

        public void Close() { Hide(); }

        private void Hide()
        {
            ClosePopup();   // 選單是這個框的子物件,但 CanvasGroup 關掉只是看不見 —— 留著下次開框會直接浮在那裡
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
