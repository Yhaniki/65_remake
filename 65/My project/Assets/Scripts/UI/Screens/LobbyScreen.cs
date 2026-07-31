using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Sdo.Localization;
using Sdo.Settings;
using Sdo.UI.Core;
using Sdo.UI.Services;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 大廳 —— 忠實重製官方 <c>CStateCommunityHall</c> 的 <c>STATECOMMUNITYHALL.XML</c>:
    /// 星空底(LobbyBG)、上方頻道名牌、中間**兩欄三列**的房卡、右下一排「創建舞台 / 快速進入 / 等待舞台」、
    /// 下方聊天欄 + 自己的角色資料。座標逐字取自那份 XML(800×600、左上原點、y 向下),
    /// 所以 <c>UIKit.AddSprite(parent, name, sprite, x, y)</c> 直接餵原座標就對位。
    ///
    /// 🔴 **素材是 UI/STATECOMMUNITYHALL,不是 UI/LOBBY。** 兩個資料夾都叫「大廳」、.an 檔名還大量重複
    ///    (Lobby26/28/47/98…),但版面完全不同:UI/LOBBY 是單欄六列的青色版,這一套才是玩家實際看到的
    ///    紫色兩欄版(幾乎整包裁自同一張 STAGE.PNG)。照檔名分不出來,只能看它裁的是哪張圖。
    ///
    /// 🔴 **XML 的 window y 是動畫起點,不是版位**:win2 寫 y=-450、win4 寫 y=200,各自有一組 TransForm
    ///    把它滑到 (0,0) —— 所以裡面那些座標就是最終的絕對座標,不要再加 window 的 y。
    ///
    /// 官方有、但這個重製版沒有對應資料的東西一律不畫(知名度、段位、愛慕值、金葉子、經驗值、活動、寵物、
    /// 結婚、小屋、排行)—— 畫一個永遠是 0 的欄位比不畫更糟;按了沒反應的鈕同理,直接不放。
    ///
    /// 🔴 **版面不依 <c>Ctx.Net</c> 分歧。** <c>Build(ctx)</c> 是開機時跑的,那時一定還是單機
    /// (連線由選角色畫面按「登入」才發動),所以在 <see cref="BuildUI"/> 判斷連線只會永遠走到離線那半邊。
    /// 線上/離線的差別全部收在 <see cref="OnShow"/> 之後的資料來源上。
    /// </summary>
    public sealed class LobbyScreen : UIScreenBase
    {
        public override ScreenId Id => ScreenId.Lobby;

        // ---------------- STATECOMMUNITYHALL.XML 版位(全部是 800×600 絕對座標) ----------------
        //
        // 🔴 XML 裡 win2 / win4 的 y 是**動畫起點**(win2 y=-450、win4 y=200),各自有一組 TransForm
        //    把它滑到 (0,0) —— 所以裡面那些座標**就是**最終的絕對座標,不要再加 window 的 y。

        // 房卡:兩欄三列。卡片絕對位置直接取自 XML 裡那組裝飾空卡(window "normal" x=1 y=-14,
        // 內含 6 個 Lobby28 標在 (301,78)(530,78)(301,173)(530,173)(301,268)(530,268))——
        // 與六個 room window 自己算出來的落點一致(room0 x=53 y=25、卡片在 window 內 (248,38) → 301,63)。
        // ⚠️ 官方 XML 的 room1 座標與 room0 重複(x 都是 53),照 room2..5 的規律那顯然是右欄的筆誤。
        private const int VisibleRows = 6;
        private const float ColLX = 301f, ColRX = 530f;          // 左欄 / 右欄
        private const float Row0Y = 64f, RowStep = 95f;          // 三列
        private const float CardW = 226f, CardH = 89f;           // Lobby28 / Lobby98

        // 卡片內的元素 —— XML 是相對 room window 的,這裡換算成「相對卡片左上角 (248,38)」。
        private const float NameX = 16f, NameY = 11f, NameW = 190f, NameH = 14f;   // roomname
        private const float SongX = 139f, SongY = 40f, SongW = 76f, SongH = 16f;   // roommusic
        private const float NumX = 8f, NumY = 57f, NumStep = 17f;                  // roomnum:LobbyNum1,3 位(16×20)
        private const float CountX = 95f, SlashX = 107f, CapX = 116f, CountY = 45f;// roomuser / slash / roommaxuser:LobbyNum2
        private const float KeyX = 73f, KeyY = 42f;                                // password(Lobby93 鑰匙)
        // 狀態是**兩層**:Lobby26/27 的圓底(50×50)+ waiting/playing 的綠字牌(63×18)疊在上面。
        private const float StateX = 14f, StateY = 35f;                            // roomstate_waitting / _playing
        private const float BadgeX = 6f, BadgeY = 39f;                             // waiting.an / playing.an
        private const float HeadX = 66f, HeadY = 65f, HeadStep = 19f;              // roomplayer0..5(man.an)
        private const float KeysX = 183f, KeysY = 60f;                             // jianpan(Lobby97)

        // 房間列表底板(NormalBG = LobbyChannelBG,506×364)+ 捲軸
        private const float ListBgX = 286f, ListBgY = 46f;
        private const float RailX = 760f, RailTop = 35f, RailH = 320f, HandleH = 42f;

        // 右下角那一排功能鈕(全部同一個 y)
        private const float ActionY = 363f;
        private const float ActX = 306f, CreateX = 526f, QuickX = 615f, FilterX = 703f;

        // 上方(win2):頻道名底圖 + 右上角圓鈕
        private const float ChannelBgX = 285f, ChannelBgY = 7f;
        private const float ServerX = 292f, ChannelX = 389f, TopLabelY = 9f;
        private const float TopSettingX = 723f, TopSettingY = 44f;
        private const float TopLogoutX = 759f, TopLogoutY = 8f;

        // 下方(win4)。聊天顯示區官方是可開關的浮動面板(recordchatmode/closerecordchatmode 一對開關鈕),
        // 它的 XML 位置 (21,296) 會壓在第三列房卡上 —— 這裡當常駐聊天區用,所以下移到輸入列正上方。
        private const float ChatBgX = 21f, ChatBgY = 437f;
        private const float ChatX = 34f, ChatY = 447f, ChatW = 408f, ChatH = 110f;
        private const float ChatInputX = 156f, ChatInputY = 570f, ChatInputW = 250f, ChatInputH = 20f;
        private const float ChanX = 23f, ChanY = 570f;            // chatmode「當前」
        private const float SendX = 493f, SendY = 566f;           // ChatSendButton
        private const float DetailX = 244f, DetailY = 378f;       // 个人资料
        private const float ItemsX = 595f, NotesX = 671f, BottomBtnY = 570f;

        // 自己的角色資料(win4 的 char* 標籤;背板已經把「等級/經驗值/G幣/M幣/P幣」與
        // 「超舞戰績/知名度/勝率/愛慕值/金葉子」兩排標題烤進圖裡了 → 每一格只放數值)
        private const float SelfNameX = 492f, SelfNameY = 446f, SelfNameW = 130f, SelfNameH = 16f;
        private const float LevelX = 513f, LevelY = 467f;
        private const float ExpX = 522f, ExpY = 489f, ExpW = 86f, ExpH = 14f;
        private const float PointY = 504f, CoinY = 522f, BonusY = 539f, MoneyX = 513f, MoneyW = 128f, StatH = 10f;
        private const float RecordX = 674f, RecordY = 470f, PerfW = 90f, PerfH = 10f;
        private const float AccX = 674f, AccY = 487f;
        private const float WinX = 672f, WinY = 505f;

        // XML 的顏色(0xAARRGGBB)
        private static readonly Color32 RoomNameColor = new Color32(0x82, 0x14, 0x38, 0xff);   // roomname
        private static readonly Color32 SongColor = new Color32(0xed, 0xec, 0xa0, 0xff);       // roommusic
        private static readonly Color32 SelfNameColor = new Color32(0xf2, 0x86, 0x4b, 0xff);   // charname
        private static readonly Color32 StatColor = new Color32(0xff, 0xff, 0xff, 0xff);

        /// <summary>線上房間列表的輪詢間隔。server 沒有「房間列表變了」的推播,只能自己回頭問。</summary>
        private const float PollSeconds = 4f;

        // ---------------- 狀態 ----------------

        private readonly RoomRow[] _rows = new RoomRow[VisibleRows];
        private readonly List<RoomInfo> _rooms = new List<RoomInfo>();   // 來源(線上=server 回的;離線=Ctx.Rooms)
        private readonly List<RoomInfo> _view = new List<RoomInfo>();    // 套用「只顯示等待中」之後的
        private int _scroll;
        private bool _waitingOnly;
        private Image _handle;

        private Button _filterBtn;
        private Image _filterImg;

        private RectTransform _chatContent;
        private TMP_InputField _chatInput;
        private ScrollRect _chatScroll;
        private ChatLineClip _chatClip;

        private TextMeshProUGUI _selfName, _selfLevel, _selfWin, _selfAcc, _selfRecord, _selfCoins, _selfPoints, _selfBonus;

        /// <summary>OnShow 當下訂到的那兩份服務。登入/登出會**就地換掉** Ctx.Rooms / Ctx.Chat,
        /// 用 Ctx 的現值去退訂會退到新的那份身上 → 舊的那份永遠留著一條死訂閱。</summary>
        private IRoomService _subRooms;
        private IChatService _subChat;
        private bool _subscribed;

        /// <summary>房間列表請求的世代。回呼可能在離開大廳(或登出)之後才回來 —— 那份資料屬於上一次的畫面。</summary>
        private int _listGen;
        private float _nextPoll;

        private static string L(string k) => LocalizationManager.Get(k);
        private static Sprite An(string n) => LobbyArt.An(n);

        // ================================================================ 版面

        protected override void BuildUI()
        {
            // 官方的大廳底是一張 800×600 的星空圖(LobbyBG)。缺圖時退回不透明底色 ——
            // 沒有它的話 UI 之間的空隙會直接看到上一個畫面的殘影。
            var bg = An("LobbyBG");
            if (bg != null) UIKit.AddSprite(Root, "Bg", bg, 0f, 0f);
            else UIKit.Stretch(UIKit.AddImage(Root, "Bg", UITheme.Bg).rectTransform);

            BuildRoomList();     // 底板 + 兩欄三列房卡
            BuildTopBar();
            BuildActionBar();
            BuildBottomPanel();

            // 捲軸握把最後加(= 疊在最上面)。
            _handle = UIKit.AddSprite(Root, "ScrollHandle", An("Lobby38"), RailX, RailTop);
        }

        // ---- 上方:頻道名 + 右上角圓鈕 ----

        private void BuildTopBar()
        {
            // 頻道名牌(ChannelName.an,198×30)。官方在上面疊「伺服器名」與「頻道號」兩個置中標籤。
            UIKit.AddSprite(Root, "ChannelPlate", An("ChannelName"), ChannelBgX, ChannelBgY);

            // server / channel:官方是「浓情蜜意 / 沈阳」那種分區名,我們只有一台伺服器一個頻道
            // → 沿用房間畫面同一組字,兩邊講的是同一件事。
            var server = Label(Root, "Server", ServerX, TopLabelY, 72f, 28f, 13f, StatColor, TextAlignmentOptions.Midline);
            server.text = RoomLabels.ServerName(1);
            var channel = Label(Root, "Channel", ChannelX, TopLabelY, 90f, 28f, 13f, StatColor, TextAlignmentOptions.Midline);
            channel.text = RoomLabels.Channel(1);

            // 右上角:設定(hall26/27/28)。官方那一排還有結婚/小屋/排行/拼圖/彩虹/BOSS ——
            // 這個重製版一個都沒有 → 不放(按了沒反應的鈕比缺一顆更糟)。
            var setting = SpriteBtn("Setting", "hall26", "hall27", "hall28", TopSettingX, TopSettingY,
                                    () => Nav.OpenSettings?.Invoke());
            UIKit.SetAlphaHit(setting.targetGraphic);   // 圓鈕,透明四角不該吃點擊

            // returnlubbysel(回頻道選擇)= 我們的「登出」:斷線退回單機並回選角色畫面。
            var logout = SpriteBtn("Logout", "hall16", "hall17", "hall18", TopLogoutX, TopLogoutY, OnLogout);
            UIKit.SetAlphaHit(logout.targetGraphic);
        }

        // ---- 右下角那一排功能鈕 ----

        private void BuildActionBar()
        {
            // 活動查詢(actandprize)。這個重製版沒有活動系統 → 只有在素材真的存在時才畫,
            // 而且不接 handler …… 不,那就是「按了沒反應的鈕」。直接不放。
            //   → 官方版位 ActX 留著當常數,之後真的做活動時就有地方擺。

            // 創建舞台 —— 大廳的主要動作。素材沒複製到時整個大廳就廢了,
            // 所以額外給一個純色 + 文字的 fallback(其餘的鈕缺圖就讓它缺)。
            var create = SpriteBtn("CreateRoom", "Lobby47", "Lobby48", "Lobby81", CreateX, ActionY, OnCreate);
            if (An("Lobby47") == null) FallbackButtonSkin(create, "lobby.create_room", 81f, 40f);

            SpriteBtn("QuickJoin", "Lobby49", "Lobby50", "Lobby82", QuickX, ActionY, OnQuickJoin);

            // 等待舞台 / 全部舞台:官方是兩個疊在同一格(x=703)的 CheckBox,
            // 這裡用一顆鈕換圖表達同一件事。
            _filterBtn = SpriteBtn("RoomFilter", "Lobby51", "Lobby52", "Lobby83", FilterX, ActionY, OnToggleFilter);
            _filterImg = _filterBtn.targetGraphic as Image;
            ApplyFilterSprites();
        }

        // ---- 六列房卡 ----

        private void BuildRoomList()
        {
            var listRoot = UIKit.NewRect(Root, "RoomList");
            UIKit.Stretch(listRoot);

            // 列表底板(NormalBG = LobbyChannelBG,506×364)。官方在 XML 裡把它排在房卡**之後**
            // = 畫在房卡後面,所以這裡先加。
            UIKit.AddSprite(listRoot, "ListBg", An("LobbyChannelBG"), ListBgX, ListBgY);

            // 整片列表區的透明接盤:滾輪事件會沿著階層往上冒泡,所以掛在這裡的話,
            // 游標壓在任何一張卡上滾都收得到;而卡與卡之間的縫也不會漏掉。
            var catcher = UIKit.AddImage(listRoot, "WheelCatcher", new Color(0f, 0f, 0f, 0f), raycast: true);
            var crt = catcher.rectTransform;
            crt.anchorMin = crt.anchorMax = new Vector2(0f, 1f);
            crt.pivot = new Vector2(0f, 1f);
            crt.sizeDelta = new Vector2(ColRX - ColLX + CardW, 3f * RowStep);
            crt.anchoredPosition = new Vector2(ColLX, -Row0Y);

            var wheel = listRoot.gameObject.AddComponent<WheelScroll>();
            wheel.Scrolled = OnWheel;

            for (int i = 0; i < VisibleRows; i++) _rows[i] = BuildRow(listRoot, i);
        }

        /// <summary>第 <paramref name="index"/> 格房卡的絕對位置。**兩欄三列**:偶數在左欄、奇數在右欄。</summary>
        private static Vector2 CardPos(int index)
        {
            float x = (index % 2 == 0) ? ColLX : ColRX;
            float y = Row0Y + (index / 2) * RowStep;
            return new Vector2(x, y);
        }

        private RoomRow BuildRow(Transform parent, int index)
        {
            var pos = CardPos(index);

            var root = UIKit.NewRect(parent, "Room" + index);
            root.anchorMin = root.anchorMax = new Vector2(0f, 1f);
            root.pivot = new Vector2(0f, 1f);
            root.sizeDelta = new Vector2(CardW, CardH);
            root.anchoredPosition = new Vector2(pos.x, -pos.y);

            var row = new RoomRow();

            // 卡片本體是**單張** 226×89(Lobby98 空房 / Lobby28 有人),不像另一套是左右兩張拼的。
            row.Card = UIKit.AddSprite(root, "Card", An("Lobby98"), 0f, 0f, raycast: true);

            // 狀態圓底:等待(Lobby26)/ 遊戲中(Lobby27)/ 空房(LobbyRoomNone),三張疊同一格。
            row.State = UIKit.AddSprite(root, "State", null, StateX, StateY);
            // 疊在圓底上的 WAITING / PLAYING 綠字牌(官方是分開的兩個 Label)。
            row.Badge = UIKit.AddSprite(root, "Badge", null, BadgeX, BadgeY);

            row.Digits = new Image[3];
            for (int d = 0; d < 3; d++)
                row.Digits[d] = UIKit.AddSprite(root, "Num" + d, null, NumX + d * NumStep, NumY);

            row.Name = Label(root, "Name", NameX, NameY, NameW, NameH, 12f,
                             RoomNameColor, TextAlignmentOptions.MidlineLeft);
            row.Name.fontStyle = FontStyles.Bold;   // XML: bold="1"
            // 房名是玩家自訂的,長的話會整條蓋過右邊的圖示 → 截斷加省略號(官方的欄寬也是硬邊界)。
            row.Name.overflowMode = TextOverflowModes.Ellipsis;

            row.CountD = UIKit.AddSprite(root, "Count", null, CountX, CountY);
            row.Slash = UIKit.AddSprite(root, "Slash", An("slash"), SlashX, CountY);
            row.CapD = UIKit.AddSprite(root, "Cap", null, CapX, CountY);

            // 密碼房的鑰匙(password = Lobby93)。這個重製版還沒有房間密碼 → 永遠隱藏,
            // 但位置先擺好,之後接上密碼房只要把它 SetActive(true)。
            row.Key = UIKit.AddSprite(root, "Key", null, KeyX, KeyY);

            row.Heads = new Image[6];
            for (int h = 0; h < 6; h++)
                row.Heads[h] = UIKit.AddSprite(root, "Head" + h, null, HeadX + h * HeadStep, HeadY);

            row.Song = Label(root, "Song", SongX, SongY, SongW, SongH, 11f,
                             SongColor, TextAlignmentOptions.MidlineLeft);
            row.Song.overflowMode = TextOverflowModes.Ellipsis;   // 歌名比欄位長是常態

            // 鍵盤圖示:這個重製版只有鍵盤能玩,所以它永遠成立(不是「沒資料的裝飾」)。
            row.Keyboard = UIKit.AddSprite(root, "Keys", null, KeysX, KeysY);

            // 點擊/hover 都掛在 root 上 —— UGUI 的 click 與 pointerEnter 都會從命中的子物件往上找,
            // 所以一顆 Button 就能涵蓋左右兩張圖,而 hover 換圖也能兩張一起換(分開掛會出現「只亮一半」)。
            row.Btn = root.gameObject.AddComponent<Button>();
            row.Btn.transition = Selectable.Transition.None;
            int captured = index;
            row.Btn.onClick.AddListener(() => OnRowClicked(captured));
            UiSfx.AttachClick(row.Btn);

            row.Hover = root.gameObject.AddComponent<RowHover>();
            row.Hover.Left = row.Card;
            return row;
        }

        // ---- 下方面板(聊天 + 自己的資料) ----

        private void BuildBottomPanel()
        {
            // 下方整條背板(Lobby53..56,各 256 寬 + 一片 21 寬的收尾)。
            // 官方把「等級 / 經驗值 / G幣 / M幣 / P幣」與「超舞戰績 / 知名度 / 勝率 / 愛慕值 / 金葉子」
            // 兩排**標題字烤死在圖裡**,所以下面每一格只放數值,標題一律不重畫。
            UIKit.AddSprite(Root, "Bottom0", An("Lobby53"), 5f, 433f);
            UIKit.AddSprite(Root, "Bottom1", An("Lobby54"), 261f, 433f);
            UIKit.AddSprite(Root, "Bottom2", An("Lobby55"), 517f, 433f);
            UIKit.AddSprite(Root, "Bottom3", An("Lobby56"), 773f, 433f);

            // 聊天顯示區的底框(RecordChatBG,437×130)。
            UIKit.AddSprite(Root, "ChatBg", An("RecordChatBG"), ChatBgX, ChatBgY);

            // 聊天記錄。背板已經畫好框了 → ScrollRect 自己不要再上底色。
            _chatScroll = UIKit.AddVerticalScroll(Root, "ChatScroll", out _chatContent, 1f, 2, new Color(0f, 0f, 0f, 0f));
            PlaceTopLeft(_chatScroll.GetComponent<RectTransform>(), ChatX, ChatY, ChatW, ChatH);
            _chatClip = _chatScroll.gameObject.AddComponent<ChatLineClip>();   // 只露整行,不留半截字

            // 頻道指示(chatmode「當前」)。官方那是可切換當前/好友/家族的鈕;大廳這裡只有公頻,
            // 所以畫成**純圖示**而不是按鈕 —— 一顆按了沒反應的鈕比一個看得懂的指示更糟。
            UIKit.AddSprite(Root, "ChatChannel", An("Lobby57"), ChanX, ChanY);

            _chatInput = UIKit.AddInputField(Root, "ChatInput", L("lobby.chat_hint"), 13f);
            PlaceTopLeft(_chatInput.GetComponent<RectTransform>(), ChatInputX, ChatInputY, ChatInputW, ChatInputH);
            if (_chatInput.targetGraphic != null)
                _chatInput.targetGraphic.color = new Color(1f, 1f, 1f, 0f);   // 底圖已經有凹槽了,不要再蓋一層灰
            _chatInput.onSubmit.AddListener(_ => SendChat());

            // 送出鈕很小,不套 alpha 命中判定 —— 那麼小的鈕給滿一個矩形反而好按(見 UIKit.SetAlphaHit 的註解)。
            // ⚠️ 官方這顆的 normal/hover 是**對調**的(bgnormal=Lobby100、bghover=Lobby99),照抄。
            SpriteBtn("ChatSend", "Lobby100", "Lobby99", "Lobby101", SendX, SendY, SendChat);

            // 道具包 → 儲物櫃。
            SpriteBtn("Items", "Lobby94", "Lobby95", "Lobby96", ItemsX, BottomBtnY, () => Nav.OpenWardrobe?.Invoke());
            // 个人资料(DetailButton)→ 自己的資料頁。房間畫面是「點自己的頭貼」進去的,大廳沒有頭貼可點,
            // 這顆美術字面本來就寫著「个人资料」→ 正好是同一個去處,不接的話大廳就沒有入口。
            SpriteBtn("Detail", "Lobby61", "Lobby62", "Lobby72", DetailX, DetailY,
                      () => Nav.OpenSelfInfo?.Invoke());
            // NotesButton 的美術寫的是「郵件」,但這個重製版沒有信箱,而**音符外觀選擇器只有大廳進得去**
            // (拿掉就變成死程式)。借它的版位放音符選擇 —— 版位名稱本來就叫 Notes。
            SpriteBtn("NoteSkin", "Lobby63", "Lobby64", "Lobby73", NotesX, BottomBtnY,
                      () => Nav.OpenNoteSkinPicker?.Invoke());

            BuildSelfInfo();
        }

        private void BuildSelfInfo()
        {
            _selfName = Label(Root, "SelfName", SelfNameX, SelfNameY, SelfNameW, SelfNameH, 14f,
                              SelfNameColor, TextAlignmentOptions.MidlineLeft);
            _selfName.fontStyle = FontStyles.Bold;
            _selfName.overflowMode = TextOverflowModes.Ellipsis;   // 名字長度沒有上限,不截會蓋到右邊的欄位

            // charperformance(超舞戰績)放勝敗場、AUcharperformance(勁舞戰績)放命中率 ——
            // 兩格的烤字都只是「战绩」,分不出是哪一種,所以這兩格的值自己帶單位(見 RefreshSelf)。
            _selfRecord = Label(Root, "SelfRecord", RecordX, RecordY, PerfW, PerfH, 11f, StatColor, TextAlignmentOptions.Midline);
            _selfAcc = Label(Root, "SelfAcc", AccX, AccY, PerfW, PerfH, 11f, StatColor, TextAlignmentOptions.Midline);

            _selfLevel = Label(Root, "SelfLevel", LevelX, LevelY, MoneyW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft);
            _selfWin = Label(Root, "SelfWin", WinX, WinY, MoneyW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft);
            _selfPoints = Label(Root, "SelfPoints", MoneyX, PointY, MoneyW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft);
            _selfCoins = Label(Root, "SelfCoins", MoneyX, CoinY, MoneyW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft);
            _selfBonus = Label(Root, "SelfBonus", MoneyX, BonusY, MoneyW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft);

            // 經驗條(exp_progress:Lobby137 底 + Lobby60 前景)。這個重製版沒有經驗值 →
            // 只畫底槽、不畫填充,那格看起來就是「還沒開始累積」而不是一個假的進度。
            UIKit.AddSprite(Root, "ExpBar", An("Lobby137"), ExpX, ExpY);

            // 知名度 / 愛慕值 / 金葉子 / 段位:這個重製版沒有那些資料 → 背板的字留著,數值不填。
        }

        // ================================================================ 生命週期

        public override void OnShow()
        {
            Subscribe();
            Ctx.Chat?.SetScope(ChatScope.Lobby);   // 大廳作用域:只顯示大廳訊息(密語跨場另計)

            _scroll = 0;
            RefreshSelf();
            ReloadRooms();
            RebuildChat();
            _nextPoll = Time.unscaledTime + PollSeconds;
        }

        public override void OnHide()
        {
            _listGen++;   // 還在路上的 roomList 回呼作廢
            Unsubscribe();
        }

        private void Subscribe()
        {
            if (_subscribed) return;
            _subRooms = Ctx.Rooms;
            _subChat = Ctx.Chat;
            if (_subRooms != null) _subRooms.RoomsChanged += OnRoomsChanged;
            if (_subChat != null) _subChat.MessageReceived += OnChatMessage;
            Ctx.OnlineChanged += OnOnlineChanged;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            if (_subRooms != null) _subRooms.RoomsChanged -= OnRoomsChanged;
            if (_subChat != null) _subChat.MessageReceived -= OnChatMessage;
            Ctx.OnlineChanged -= OnOnlineChanged;
            _subRooms = null;
            _subChat = null;
            _subscribed = false;
        }

        /// <summary>登入/登出會就地換掉 Ctx.Rooms / Ctx.Chat → 訂閱要跟著搬,資料來源也要重讀。</summary>
        private void OnOnlineChanged()
        {
            _listGen++;
            Unsubscribe();
            if (!Visible) return;
            Subscribe();
            // 換掉的是**另一個** IChatService 實例,作用域要重設一次 —— 不設的話它還停在自己的預設值,
            // 我們送出的大廳訊息可能被標成別的作用域,結果自己送的字自己看不到。
            Ctx.Chat?.SetScope(ChatScope.Lobby);
            _scroll = 0;
            ReloadRooms();
            RebuildChat();
        }

        private void Update()
        {
            if (!Visible) return;
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + PollSeconds;

            // 商城/儲物櫃是**疊在大廳上的 modal**,關掉不會重跑 OnShow —— 買完東西回來錢包不會自己更新,
            // 所以跟房間列表同一個節拍順手重讀一次。
            RefreshSelf();

            // server 沒有「房間列表變了」的推播,只能自己回頭問(離線那份靠 RoomsChanged 事件就夠了)。
            if (Ctx != null && Ctx.Net != null) RequestOnlineRooms();
        }

        // ================================================================ 房間列表

        /// <summary>依現在的模式重讀一次列表:線上跟 server 要,離線讀本機那份。</summary>
        private void ReloadRooms()
        {
            if (Ctx != null && Ctx.Net != null) { RequestOnlineRooms(); return; }
            LoadOfflineRooms();
        }

        private void LoadOfflineRooms()
        {
            _rooms.Clear();
            var src = Ctx != null && Ctx.Rooms != null ? Ctx.Rooms.GetRooms() : null;
            if (src != null) for (int i = 0; i < src.Count; i++) _rooms.Add(src[i]);
            RefreshRows();
        }

        private void RequestOnlineRooms()
        {
            var net = Ctx != null ? Ctx.Net : null;
            if (net == null || !net.IsConnected) return;
            int gen = _listGen;
            net.RequestRoomList(entries =>
            {
                // 回呼可能在離開大廳/登出之後才回來 —— 那份資料屬於上一次的畫面,丟掉。
                if (this == null || gen != _listGen) return;
                _rooms.Clear();
                _rooms.AddRange(NetRoomMapping.ToRoomInfos(entries));
                RefreshRows();
            });
        }

        /// <summary>離線的房間服務自己會喊「列表變了」;線上那份不會(它的列表是我們問來的)。</summary>
        private void OnRoomsChanged()
        {
            if (Ctx != null && Ctx.Net != null) return;
            LoadOfflineRooms();
        }

        private void RefreshRows()
        {
            _view.Clear();
            for (int i = 0; i < _rooms.Count; i++)
            {
                var r = _rooms[i];
                if (r == null) continue;
                if (_waitingOnly && r.Status != RoomStatus.Waiting) continue;
                _view.Add(r);
            }

            int max = Mathf.Max(0, _view.Count - VisibleRows);
            _scroll = Mathf.Clamp(_scroll, 0, max);

            for (int i = 0; i < VisibleRows; i++)
            {
                int idx = _scroll + i;
                Bind(_rows[i], idx < _view.Count ? _view[idx] : null, idx);
            }
            PlaceHandle(max);
        }

        private void Bind(RoomRow row, RoomInfo r, int absoluteIndex)
        {
            row.Data = r;

            bool has = r != null;
            // 卡片是單張 226×89:有人 → Lobby28(粉紫標題列)、空位 → Lobby98(紫色標題列)。
            // 官方沒有另外的 hover 圖(那一套只有兩張卡底),所以 hover 不換圖 —— 傳 null 當 hover。
            row.Hover.SetSkin(An(has ? "Lobby28" : "Lobby98"), null);
            row.Btn.interactable = has;

            // 狀態:圓底 + 綠字牌兩層。有人 → 等待(Lobby26 + waiting)/ 遊戲中(Lobby27 + playing);
            // 空位 → 只有 LobbyRoomNone 的圓底,沒有字牌。
            bool waiting = has && r.Status == RoomStatus.Waiting;
            UIKit.ApplySprite(row.State, !has
                ? An("LobbyRoomNone")
                : An(waiting ? "Lobby26" : "Lobby27"));
            UIKit.ApplySprite(row.Badge, !has ? null : An(waiting ? "waiting" : "playing"));

            // 🔴 門牌是 **3 位數的 Seq**,不是 5 位數的 Id。Id 是「加入房間的鑰匙」,
            //    官方大廳從來不顯示它(要進房就在這裡點那張卡)。
            //    線上的 roomList 封包沒有帶 seq(NetRoomListEntry 只有 code),那就退回用列表位置當門牌 ——
            //    那正是官方那個數字的意思:「第幾間房」。
            int door = !has ? 0 : (r.Seq > 0 ? r.Seq : absoluteIndex + 1);
            door = Mathf.Clamp(door, 0, 999);
            for (int d = 0; d < row.Digits.Length; d++)
            {
                int digit = d == 0 ? door / 100 : (d == 1 ? (door / 10) % 10 : door % 10);
                UIKit.ApplySprite(row.Digits[d], has ? LobbyArt.Digit("LobbyNum1", digit) : null);
            }

            row.Name.text = has ? RoomLabels.DisplayName(r.Name, r.HostName) : "";
            row.Song.text = has && !string.IsNullOrEmpty(r.SongTitle) ? r.SongTitle : "";

            int count = has ? Mathf.Clamp(r.Count, 0, 9) : 0;
            int cap = has ? Mathf.Clamp(r.Capacity, 0, 9) : 0;
            UIKit.ApplySprite(row.CountD, has ? LobbyArt.Digit("LobbyNum2", count) : null);
            UIKit.ApplySprite(row.CapD, has ? LobbyArt.Digit("LobbyNum2", cap) : null);
            if (row.Slash != null) row.Slash.enabled = has;

            // 密碼房的鑰匙:這個重製版還沒有房間密碼 → 永遠不畫(版位已經留好)。
            if (row.Key != null) row.Key.enabled = false;

            // 人頭:列表封包只帶得到「幾個人」,帶不到每個人的性別 → 一律用 man.an 這隻通用剪影。
            var head = An("man");
            for (int h = 0; h < row.Heads.Length; h++)
                UIKit.ApplySprite(row.Heads[h], has && h < r.Count ? head : null);

            UIKit.ApplySprite(row.Keyboard, has ? An("Lobby97") : null);
        }

        private void PlaceHandle(int max)
        {
            if (_handle == null) return;
            bool show = max > 0;
            _handle.enabled = show;
            if (!show) return;
            float t = max > 0 ? _scroll / (float)max : 0f;
            _handle.rectTransform.anchoredPosition = new Vector2(RailX, -(RailTop + (RailH - HandleH) * t));
        }

        private void OnWheel(float dy)
        {
            if (Mathf.Approximately(dy, 0f)) return;
            int max = Mathf.Max(0, _view.Count - VisibleRows);
            if (max == 0) return;
            _scroll = Mathf.Clamp(_scroll + (dy > 0f ? -1 : 1), 0, max);
            RefreshRows();
        }

        // ================================================================ 動作

        private void OnCreate()
        {
            if (ScreenTransition.Busy) return;
            var net = Ctx.Net;
            if (net != null)
            {
                // 從大廳按建房時理論上不在任何房裡;真的還掛著就先送 leaveRoom,把**本機**的認知
                // (OnlineRoomService._current / Session.CurrentRoomId)一起清掉。
                // 這不是進房的前提 —— server 的 RoomRegistry.TryCreate/TryJoin 本來就會隱式離房,
                // 而且是房主也照離(那間房會轉手或關掉),所以這裡不必分房主/客人。
                if (net.InRoom) net.LeaveRoom();
                net.CreateRoom("", (result, code) =>
                {
                    if (this == null) return;
                    if (result == Sdo.Net.NetProto.JoinOk) { EnterRoom(); return; }
                    // server 回的是協定代碼(full/…)。直接貼上去玩家只會看到半句英文,
                    // 所以翻成人話,原始代碼留給 log。
                    Debug.LogWarning("[lobby] createRoom 失敗:" + result);
                    Toast.Show(L(result == Sdo.Net.NetProto.JoinFull ? "room.create_failed_full" : "room.create_failed"));
                });
                return;
            }

            Ctx.Rooms.CreateRoom(GameMode.Normal);
            EnterRoom();
        }

        private void OnRowClicked(int rowIndex)
        {
            var r = _rows[rowIndex].Data;
            if (r == null) return;   // 空位:官方也是點不動的
            JoinRoom(r);
        }

        private void JoinRoom(RoomInfo r)
        {
            if (r == null || ScreenTransition.Busy) return;
            var net = Ctx.Net;
            if (net != null)
            {
                if (net.InRoom) net.LeaveRoom();   // 同 OnCreate:只是把本機認知清掉,server 自己會隱式離房
                int code = r.Id;   // 🔴 進房的鑰匙是 5 位數房號,不是卡片上那個 3 位數門牌
                net.JoinOrSpectate(code,
                    (result, asSpectator) =>
                    {
                        if (this == null) return;
                        if (result == Sdo.Net.NetProto.JoinOk) { EnterRoom(); return; }
                        Toast.Show(L(JoinErrorText.KeyFor(result)));
                    },
                    // 座位滿了會自動改用旁觀身分進去。玩家點的是「加入」卻變成旁觀 ——
                    // 這件事畫面上看不出來(進去就是房間畫面),所以一定要說一聲。
                    trigger => Toast.Show(L("lobby.joined_as_spectator")));
                return;
            }

            switch (Ctx.Rooms.JoinRoom(r.Id))
            {
                // 進不去的三種原因**只寫 log,不彈 toast**:房卡上就寫著人數與「PLAYING」,
                // 滿了/開打了在按下去之前就看得到,再跳一句只是把畫面弄髒。
                case JoinResult.Ok: EnterRoom(); break;
                case JoinResult.Full: Debug.Log("[lobby] " + L("join.full")); break;
                case JoinResult.InGame: Debug.Log("[lobby] " + L("join.ingame")); break;
                default: Debug.Log("[lobby] " + L("join.notfound")); break;
            }
        }

        /// <summary>進房間轉場:漸黑 → 切畫面 → 漸亮時房間 UI 從四邊滑入(與選角色畫面同一條路)。</summary>
        private void EnterRoom()
            => ScreenTransition.Run(() => GoTo(ScreenId.Room), onReveal: Nav.PlayRoomEntrance);

        private void OnQuickJoin()
        {
            RoomInfo pick = null;
            for (int i = 0; i < _rooms.Count; i++)
            {
                var r = _rooms[i];
                if (r == null || r.Status != RoomStatus.Waiting || r.IsFull) continue;
                // 挑**人最多**的那間,不是第一間 —— 測試留下的空房會排在前面,挑到那間的話
                // 玩家會進到一間只有自己的房,症狀跟「加入失敗」長得一樣(FrontendApp 的 dev hook 踩過同一個坑)。
                if (pick == null || r.Count > pick.Count) pick = r;
            }
            if (pick == null) { Toast.Show(L("lobby.no_quick_room")); return; }
            JoinRoom(pick);
        }

        private void OnToggleFilter()
        {
            _waitingOnly = !_waitingOnly;
            ApplyFilterSprites();
            _scroll = 0;
            RefreshRows();
        }

        /// <summary>官方是兩個疊在同一格的 CheckBox:現在顯示全部 → 鈕上寫「等待舞台」(按了就篩);反之亦然。</summary>
        private void ApplyFilterSprites()
        {
            if (_filterBtn == null) return;
            var n = An(_waitingOnly ? "Lobby103" : "Lobby51");
            var h = An(_waitingOnly ? "Lobby104" : "Lobby52");
            var p = An(_waitingOnly ? "Lobby105" : "Lobby83");
            UIKit.ApplySprite(_filterImg, n);
            var st = _filterBtn.spriteState;
            st.highlightedSprite = h != null ? h : n;
            st.pressedSprite = p != null ? p : (h != null ? h : n);
            st.selectedSprite = n;
            _filterBtn.spriteState = st;
        }

        private void OnLogout()
        {
            if (ScreenTransition.Busy) return;
            // AppContext.Logout 會斷線、把房間/聊天換回單機那份,並發 OnlineChanged。
            // 之後才轉場 —— 反過來的話畫面已經切走,重畫的是一個看不見的大廳。
            Ctx.Logout("userLogout");
            ScreenTransition.Run(() => GoTo(ScreenId.GenderSel));
        }

        // ================================================================ 自己的角色資料

        private void RefreshSelf()
        {
            var p = ProfileManager.Active;

            string name = Ctx != null && Ctx.Session != null ? Ctx.Session.LocalPlayerName : null;
            if (string.IsNullOrEmpty(name) && p != null) name = p.name;
            _selfName.text = name ?? "";

            // 等級走 ProfileFields(config.ini 是 Default、角色自己設過就以角色的為準);沒設 → 這一格留白。
            // 這裡刻意**不**用 LevelLabel:那個會回「LV:11」,而背板左邊已經烤了「等级」→ 會變成「等级 LV:11」。
            _selfLevel.text = ProfileFields.PlayerLevel(p);

            // 背板左排烤的是「等级 / 经验值 / G币 / M币 / P币」→ 每一格只放數值。
            var w = p != null ? p.wallet : null;
            _selfPoints.text = w != null ? w.points.ToString(CultureInfo.InvariantCulture) : "";   // G幣
            _selfCoins.text = w != null ? w.coins.ToString(CultureInfo.InvariantCulture) : "";     // M幣
            _selfBonus.text = w != null ? w.bonus.ToString(CultureInfo.InvariantCulture) : "";     // P幣

            // 勝率那格背板已經寫著「胜率」→ 只放數字;命中率那格是自由格(背板沒字)→ 由 loc key 自己帶標題。
            var st = p != null ? p.stats : null;
            _selfWin.text = st != null ? One(st.WinRate) + "%" : "";
            _selfAcc.text = st != null ? LocalizationManager.Get("lobby.accuracy", One(st.Accuracy)) : "";
            _selfRecord.text = st != null ? LocalizationManager.Get("lobby.record", st.wins, st.losses) : "";
        }

        /// <summary>小數一位。用 InvariantCulture —— 跟著系統地區走的話,同一個畫面會出現「62.5%」與「62,5%」兩種寫法。</summary>
        private static string One(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        // ================================================================ 聊天

        private void RebuildChat()
        {
            UIKit.Clear(_chatContent);
            var hist = Ctx != null && Ctx.Chat != null ? Ctx.Chat.History : null;
            if (hist != null) foreach (var m in hist) AddChatLine(m);
            ScrollChatToBottom();
        }

        private void OnChatMessage(ChatMessage m)
        {
            AddChatLine(m);
            ScrollChatToBottom();
        }

        private void AddChatLine(ChatMessage m)
        {
            if (m == null) return;
            // 密語跨大廳/房間 → 大廳也顯示(青色單行)。
            if (m.Whisper != WhisperKind.None)
            {
                var w = UIKit.AddText(_chatContent, "whisper",
                    "<color=#1EFEFE>" + Esc(ChatDisplay.WhisperText(m)) + "</color>", 13, UITheme.Text,
                    TextAlignmentOptions.TopLeft, true);
                w.richText = true;
                UIKit.Layout(w.gameObject, 15);
                return;
            }
            // 進出舞台廣播只屬房間;一般/系統訊息只顯示大廳作用域(隔離房間訊息)。
            if (m.Stage != StageEventKind.None) return;
            if (m.Scope != ChatScope.Lobby) return;
            string line = m.System ? "<color=#F0C24A>" + Esc(m.Text) + "</color>"
                                   : "<color=#7FB6FF>" + Esc(m.Sender) + "</color>: " + Esc(m.Text);
            var t = UIKit.AddText(_chatContent, "line", line, 13, UITheme.Text, TextAlignmentOptions.TopLeft, true);
            t.richText = true;
            UIKit.Layout(t.gameObject, 15);
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private void ScrollChatToBottom()
        {
            if (_chatScroll == null) return;
            Canvas.ForceUpdateCanvases();
            _chatScroll.verticalNormalizedPosition = 0f;
            if (_chatClip != null) _chatClip.Refresh();
        }

        private void SendChat()
        {
            var txt = _chatInput.text;
            if (string.IsNullOrWhiteSpace(txt)) return;
            Ctx.Chat.Send(txt);
            _chatInput.text = "";
            _chatInput.ActivateInputField();
        }

        // ================================================================ 小工具

        /// <summary>官方三態鈕(normal/hover/pushed)+ 點擊音效,座標是 XML 的左上角像素。</summary>
        private Button SpriteBtn(string name, string normal, string hover, string pushed, float x, float y,
                                 UnityEngine.Events.UnityAction onClick)
        {
            var btn = UIKit.AddSpriteButton(Root, name, An(normal), An(hover), An(pushed), x, y);
            if (onClick != null) btn.onClick.AddListener(onClick);
            UiSfx.AttachClick(btn);
            return btn;
        }

        /// <summary>素材缺了的保險:把鈕變成純色塊 + 一行字,至少還按得下去。只給「創建舞台」用。</summary>
        private static void FallbackButtonSkin(Button btn, string locKey, float w, float h)
        {
            var img = btn.targetGraphic as Image;
            if (img == null) return;
            img.color = UITheme.Primary;
            img.rectTransform.sizeDelta = new Vector2(w, h);
            var label = UIKit.AddLocText(img.rectTransform, "Label", locKey, 12f, UITheme.OnPrimary,
                                         TextAlignmentOptions.Center);
            UIKit.Stretch(label, 2f, 0f, 2f, 0f);
        }

        /// <summary>XML 的 Label:左上角 (x,y)、大小 (w,h)、y 向下。</summary>
        private static TextMeshProUGUI Label(Transform parent, string name, float x, float y, float w, float h,
                                             float size, Color color, TextAlignmentOptions align)
        {
            var t = UIKit.AddText(parent, name, "", size, color, align);
            PlaceTopLeft(t.rectTransform, x, y, w, h);
            return t;
        }

        private static void PlaceTopLeft(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, -y);
        }

        // ================================================================ 內部型別

        /// <summary>一列房卡的所有零件。六列在 <see cref="BuildUI"/> 時就建好,之後只換圖/換字 ——
        /// 線上每 4 秒刷一次,每次都重建的話 hover 狀態會被吃掉,而且 GC 會一直跳。</summary>
        private sealed class RoomRow
        {
            public Image Card, State, Badge, Keyboard, CountD, CapD, Slash, Key;
            public Image[] Digits, Heads;
            public TextMeshProUGUI Name, Song;
            public Button Btn;
            public RowHover Hover;
            public RoomInfo Data;
        }

        /// <summary>
        /// 房卡的 hover 換圖。掛在「整張卡的 root」上 —— UGUI 的 pointerEnter/Exit 會沿著階層通知每一層,
        /// 所以游標壓在卡片上任何一個子元素(房名、人頭、狀態牌…)都收得到,不會出現「壓在字上就不亮」。
        /// </summary>
        private sealed class RowHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            public Image Left;
            private Sprite _normal, _hover;
            private bool _hot;

            public void SetSkin(Sprite normal, Sprite hover)
            {
                _normal = normal; _hover = hover;
                Apply();
            }

            public void OnPointerEnter(PointerEventData _) { _hot = true; Apply(); }
            public void OnPointerExit(PointerEventData _) { _hot = false; Apply(); }

            private void Apply()
            {
                UIKit.ApplySprite(Left, _hot && _hover != null ? _hover : _normal);
            }
        }

        /// <summary>滾輪轉發。UGUI 的 scroll 事件會往上冒泡,所以掛在列表容器上就能收到任何一張卡上的滾動。</summary>
        private sealed class WheelScroll : MonoBehaviour, IScrollHandler
        {
            public System.Action<float> Scrolled;
            public void OnScroll(PointerEventData e) { if (Scrolled != null) Scrolled(e.scrollDelta.y); }
        }
    }
}
