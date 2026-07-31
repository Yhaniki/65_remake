using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.Localization;
using Sdo.Settings;
using Sdo.UI.Core;
using Sdo.UI.Services;
using Sdo.UI.Util;
// 只借名單那一個型別。整包 `using Sdo.Net` 會讓 RoomStatus 變成歧義
// (Sdo.Net 與 Sdo.UI.Services 各有一個,房卡繫結那邊用的是後者)。
using NetUserListEntry = Sdo.Net.NetUserListEntry;

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
    /// 🔴 **官方有的東西一律照擺,即使這個重製版還沒有那份資料**(使用者要求)。這推翻了本檔早期的兩條原則
    ///    (「畫一個永遠是 0 的欄位比不畫更糟」「按了沒反應的鈕直接不放」):版面對不上官方比欄位是 0 更難認,
    ///    而缺一整排鈕會讓大廳看起來像半成品。所以:
    ///      • 愛慕值 / 金葉子 固定顯示 0(同 <c>WardrobeScreen</c> 的金葉子,那邊也是使用者指定固定 0);
    ///      • 沒做的功能**照官方版位放鈕,按了安靜地什麼都不做** —— handler 傳 null。
    ///
    /// 🔴 **大廳一律不彈 Toast,也不寫聊天區**(使用者要求)。玩家該知道的事只靠畫面本身表達
    ///    (鈕換圖、名單多一列、進不去就是還留在大廳);其餘一律 <c>Debug.Log</c>。
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

        // 左側的 3D 角色(官方 AvtShow)。與選角色畫面共用 GenderPreview3D。
        //
        // 🔴 XML 的 AvtShow 是一顆**按鈕**(x=206 y=379,Lobby157/158/159 的小人頭圖示),不是角色的畫框 ——
        //    角色是 3D 直接畫在畫面上、不受任何 UI 框限制。照那顆鈕旁邊的 285×430 擺會小掉一大圈
        //    (實機量到的角色是「頭頂 y≈32、腳底 y≈580」,約 550px 高、身體中線 x≈205)。
        //    這裡改成把 GenderPreview3D 的 2:3 槽位(400×600,與選角色畫面同一組投影)整個攤在左半邊,
        //    角色的落點與大小就由 <see cref="AvatarFillFrac"/> 與 <see cref="ShowAvatar"/> 裡歸零的兩個偏移決定。
        //
        // 🔴 **W:H 必須永遠是 2:3。** <c>GenderPreview3D.SlotW/SlotH</c> 是 const 400/600,同時決定 RT 像素數
        //    **與**釘死的 <c>_cam.aspect</c>。RawImage 的 rect 比例一旦不等於那個 aspect,角色立刻變形
        //    (而且是所有視窗都變形,不只非 4:3 的)。要改大小請動 <see cref="AvatarFillFrac"/>,不要動 W/H 的比例。
        //
        // 落點驗算(見 AvatarFillFrac):角色高 600×0.913 = 548;頭頂 y = 4 + (600-548)/2 = 30;腳底 = 578;
        // 身體中線 x = 5 + 200 = 205 —— 對上官方實機量到的「頭頂 30 / 腳底 578 / 中線 205」。
        private const float AvatarX = 5f, AvatarY = 4f, AvatarW = 400f, AvatarH = 600f;

        /// <summary>
        /// 角色佔預覽高度的比例。選角色畫面用 0.68(那邊的框留白多),大廳的角色幾乎頂天立地。
        ///
        /// 🔴 這個值只有在 <see cref="ShowAvatar"/> 把 <c>avatarYOffset</c> 與 <c>verticalBias</c> **一起歸零**
        ///    之後才算得準。那兩個偏移(官方 LOBBYSEL 的 -5 與 +2)會讓角色相對取景窗往下偏 7 個 model unit
        ///    ≈ 0.123×角色像素高 —— 0.9 配上那個偏移時腳底會算到 y=642,**掉出畫面被切掉 36px**
        ///    (使用者回報「人變得太大」的真正成因),而且在 400×600 的槽位裡 fillFrac 上限只到 0.803。
        ///    歸零之後才做得到官方那個 548px。
        ///
        /// 事後微調:高度不對 → <c>fillFrac = 目標高度 / AvatarH</c>;整體上下位移 → 直接加減 <see cref="AvatarY"/>(1:1 px)。
        /// </summary>
        private const float AvatarFillFrac = 0.913f;

        // 房間列表底板(NormalBG = LobbyChannelBG,506×364)+ 捲軸
        //
        // 🔴 HandleH 是**握把圖的實際高度**,不是隨便一個數:LOBBY38.AN = stage.png (843,590,14,28) → 14×28。
        //    以前寫 42 → 拉到底時握把底緣停在 y=341,離軌道底 355 差 14px。
        // RailX 也不是 XML 的 760:那是 ScrollBarV 整條(w=25)的左緣,而底板烘死的軌道凹槽實測在絕對 x 760-781、
        //    中央深溝 769-772 → 14 寬的握把要壓在溝上是 x≈764。
        private const float ListBgX = 286f, ListBgY = 46f;
        private const float RailX = 764f, RailTop = 35f, RailH = 320f, HandleH = 28f;

        // 右下角那一排功能鈕。創建/快速/篩選同一個 y(363);活動查詢與夥伴在 365(官方就差這 2px)。
        private const float ActionY = 363f, SideActionY = 365f;
        private const float ActX = 306f, PartnerX = 373f, CreateX = 526f, QuickX = 615f, FilterX = 703f;

        // 上方(win2):頻道名底圖 + 右上角那排圓鈕。
        //
        // 五顆鈕的 .an 全部裁自 My3dHouseSmall.png 同一列 (y=274),圖案由左到右是:
        //   hall23(x=95) 六角 → hall10(x=0) 放大鏡 → hall13(x=31) NEW筆 → hall16(x=126) 返回箭頭,
        //   另外 hall26(x=62) 是**問號**。
        // 版位對照官方實機截圖:六角(651) / 放大鏡(688) / NEW筆(722) / 返回(759),而問號那顆(723,44)
        // 官方**根本沒畫**(使用者也要求拿掉)→ 這裡不放,設定改從放大鏡那顆的下拉選單進去(見 BuildHallMenu)。
        private const float ChannelBgX = 285f, ChannelBgY = 7f;
        private const float ServerX = 292f, ChannelX = 389f, TopLabelY = 9f;
        private const float TopWeddingX = 651f, TopHouseX = 688f, TopRankX = 722f, TopIconY = 8f;
        private const float TopLogoutX = 759f, TopLogoutY = 8f;

        // 放大鏡那顆(TopHouseX)拉開的下拉選單 —— 版位逐字取自官方 POPMENU.XML 的 Formal_Pop_Menu:
        // 五個項目在選單內的 (14, 13/39/65/91/117),每項 135×26,**pushed = normal**(官方只給兩態)。
        // 選單原點是靠右對齊算出來的:項目寬 135、右緣貼齊畫面 → 651;y 讓第一項落在按鈕列正下方 → 27。
        private const float HallMenuX = 651f, HallMenuY = 27f;
        private const float HallMenuItemX = 14f, HallMenuRow0Y = 13f, HallMenuRowStep = 26f;

        // 左下「當前」拉開的頻道選單 —— 逐字取自官方 LOBBYPOPMENU.XML 的 chatmodemenu (21,466)。
        private const float ChatMenuX = 21f, ChatMenuY = 466f;

        // 下方(win4)。聊天顯示區官方是可開關的浮動面板(recordchatmode/closerecordchatmode 一對開關鈕),
        // 它的 XML 位置 (21,296) 會壓在第三列房卡上 —— 這裡當常駐聊天區用,所以下移到輸入列正上方。
        private const float ChatBgX = 21f, ChatBgY = 437f;
        private const float ChatX = 34f, ChatY = 447f, ChatW = 408f, ChatH = 110f;
        private const float ChatInputX = 156f, ChatInputY = 570f, ChatInputW = 250f, ChatInputH = 20f;
        private const float ChanX = 23f, ChanY = 570f;            // chatmode「當前」
        private const float RecordChatX = 75f, RecordChatY = 570f;// recordchatmode「聊天記錄」開關
        private const float ExprX = 458f, ExprY = 566f;           // expression 表情
        private const float SendX = 493f, SendY = 566f;           // ChatSendButton
        private const float LoudX = 525f, LoudY = 566f;           // LoudSpeaker 大聲公
        private const float PetX = 558f, PetY = 565f;             // Pet 寵物
        private const float HelpX = 751f, HelpY = 566f;           // help「?」
        private const float UserListX = 206f, UserListY = 378f;   // ListShow / AvtShow(三人頭 ↔ 單人頭,疊同一格)
        private const float DetailX = 244f, DetailY = 378f;       // 个人资料
        private const float ItemsX = 595f, NotesX = 671f, BottomBtnY = 570f;

        // 自己的角色資料(win4 的 char* 標籤;背板已經把「等級/經驗值/G幣/M幣/P幣」與
        // 「超舞戰績/知名度/勝率/愛慕值/金葉子」兩排標題烤進圖裡了 → 每一格只放數值)
        private const float SelfNameX = 492f, SelfNameY = 446f, SelfNameW = 130f, SelfNameH = 16f;
        private const float LevelX = 513f, LevelY = 467f;
        private const float ExpX = 522f, ExpY = 489f, ExpW = 86f, ExpH = 14f;
        private const float PointY = 504f, CoinY = 522f, BonusY = 539f, MoneyX = 513f, MoneyW = 128f, StatH = 10f;
        // 🔴 右排那五行的**烤字**(實測 STAGE.PNG 的 Lobby53 那塊)由上而下是
        //    「超舞战绩 / 知名度 / 胜率 / 爱慕值 / 金叶子」。XML 的 label 名字會誤導
        //    (charduanwei「段位」烤的其實是愛慕值、charrank「排名」烤的是金葉子、AUcharperformance 烤的是知名度)——**以底圖為準**。
        //    這裡以前把「命中率」放在知名度那格,是照著一段錯註解做的;命中率在個人資料頁已經有了,直接拿掉。
        private const float RecordX = 674f, RecordY = 470f, PerfW = 90f, PerfH = 10f;
        private const float FameX = 674f, FameY = 487f;            // AUcharperformance → 知名度「LV n (m)」
        private const float WinX = 672f, WinY = 505f;
        private const float LoveX = 672f, LoveY = 521f;            // charduanwei → 愛慕值(固定 0)
        private const float LeafX = 676f, LeafY = 538f, LeafW = 100f;   // charrank → 金葉子(固定 0)

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
        private Image _chatBgImg;             // 聊天記錄的底框:跟捲動區一起被「聊天記錄」鈕收合
        private Button _recordChatBtn;
        private Image _recordChatImg;
        private bool _chatLogHidden;

        // 兩個下拉選單(右上角功能選單 / 左下角頻道選單)。都是 lazily build、再按一次收起來,
        // 而且**互斥** —— 開一個就把另一個收掉(照 RoomScreen 的 chatmode ↔ expression 那個模式)。
        private RectTransform _hallMenu, _chatMenu;
        private Button _chatChannelBtn;
        private Image _chatChannelImg;
        private ChatChannel _chatChannel = ChatChannel.Current;

        // 玩家名單(官方 win3)。開關鈕在下方面板(ListShow ↔ AvtShow 同一格輪換)。
        private LobbyUserPanel _userPanel;
        private Button _userListBtn;
        private Image _userListImg;
        /// <summary>離線時餵給名單的那一列(只有自己)。重複用同一個 List —— 名單每 4 秒刷一次,每次配置會一直跳 GC。</summary>
        private readonly List<NetUserListEntry> _offlineUsers = new List<NetUserListEntry>();

        private TextMeshProUGUI _selfName, _selfLevel, _selfWin, _selfFame, _selfRecord, _selfCoins, _selfPoints, _selfBonus;

        // 左側 3D 角色(官方 AvtShow)。與選角色畫面同一套 GenderPreview3D:它自己開一台相機
        // 渲到 RenderTexture,顯示時要把那個 layer 從前端 UI 相機的 cullingMask 遮掉,OnHide 還原。
        private GenderPreview3D _preview;
        private RawImage _previewImg;
        private Camera _maskedCam;
        private int _savedMask;

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

            // 左側 3D 角色的畫布(貼圖在 OnShow 接上 —— RenderTexture 那時才存在)。
            // 🔴 加在 2D UI **之後** = 疊在它們之上,因為官方的角色是直接畫在畫面上的 3D:
            //    實機那雙鞋子明顯壓在下方紫色面板的上緣。放在最底層的話腳會被面板切掉半截。
            //    RawImage 不吃射線(見 AddRaw),所以蓋過去也不會擋住底下那些鈕。
            _previewImg = AddRaw("AvatarView", AvatarX, AvatarY, AvatarW, AvatarH);
            _previewImg.color = new Color(1f, 1f, 1f, 0f);   // 還沒有 RT 之前不要畫一塊白

            // 捲軸握把(在角色之後 —— 兩者不重疊,只是維持「列表零件疊在最上」)。
            // 走 AnSoloAA 而不是共用圖集:握把在 stage.png 裡左邊 x=840-842 是完全不透明的鄰居,
            // 共用圖集取樣會把那片拖進邊緣變成白邊(見 SpriteBtn 的註解)。
            _handle = UIKit.AddSprite(Root, "ScrollHandle", LobbyArt.AnSoloAA("Lobby38"), RailX, RailTop);

            // 玩家名單(官方 win3)最後建 = 疊在最上面,展開時蓋住角色與左半邊 —— 官方就是這樣。
            BuildUserPanel();
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

            // 右上角那排圓鈕(版位與圖案的對照見上方常數區)。實機還藏著拼圖/彩虹/BOSS/權益那幾顆
            // (要開活動才亮),不放。
            //
            // 🔴 六角與 NEW筆 這個重製版沒有對應功能 → 鈕照擺、**按了安靜地什麼都不做**(handler 傳 null)。
            //    以前這裡會彈一句「這個功能還沒做」的 Toast,使用者要求大廳一律不要 Toast。
            TopIcon("Wedding", "hall23", "hall24", "hall25", TopWeddingX, null);
            // 放大鏡 = 官方那顆拉開功能選單的鈕(家族/奖励兑换/情侣密友证/排行榜/设置),見 BuildHallMenu。
            TopIcon("MyHouse", "hall10", "hall11", "hall12", TopHouseX, ToggleHallMenu);
            TopIcon("Rank", "hall13", "hall14", "hall15", TopRankX, null);

            // returnlubbysel(回頻道選擇)= 我們的「登出」:斷線退回單機並回選角色畫面。
            TopIcon("Logout", "hall16", "hall17", "hall18", TopLogoutX, OnLogout);
        }

        // ---- 右上角放大鏡拉開的功能選單(官方 POPMENU.XML 的 Formal_Pop_Menu) ----

        /// <summary>五個項目。normal / hover 兩態(官方沒給 pushed,pushed 直接用 normal)。
        /// 只有「设置」接得上東西(與房間同一個 OptionDlg);其餘四項是官方有、這裡還沒做的功能 → handler 為 null。</summary>
        private static readonly string[,] HallMenuItems =
        {
            { "FamilyPopMenu1",  "FamilyPopMenu2"  },   // 家族
            { "ChangePopMenu1",  "ChangePopMenu2"  },   // 奖励兑换
            { "WeddingPopMenu1", "WeddingPopMenu2" },   // 情侣密友证
            { "RankPopMenu1",    "RankPopMenu2"    },   // 排行榜
            { "SetPopMenu1",     "SetPopMenu2"     },   // 设置
        };

        private void ToggleHallMenu()
        {
            if (_hallMenu == null) BuildHallMenu();
            bool show = !_hallMenu.gameObject.activeSelf;
            HideChatMenu();   // 兩個選單互斥
            _hallMenu.gameObject.SetActive(show);
        }

        private void HideHallMenu()
        {
            if (_hallMenu != null) _hallMenu.gameObject.SetActive(false);
        }

        private void BuildHallMenu()
        {
            _hallMenu = UIKit.NewRect(Root, "hallmenu");
            PlaceTopLeft(_hallMenu, HallMenuX, HallMenuY, 163f, 143f);   // 14+135 寬、13+5×26 高

            for (int i = 0; i < HallMenuItems.GetLength(0); i++)
            {
                // 官方項目沒有 pushed 態 → 三個參數餵同一組(normal/hover/normal)。
                var b = UIKit.AddSpriteButton(_hallMenu, "hallmenu" + i,
                    LobbyArt.AnSoloAA(HallMenuItems[i, 0]), LobbyArt.AnSoloAA(HallMenuItems[i, 1]),
                    LobbyArt.AnSoloAA(HallMenuItems[i, 0]),
                    HallMenuItemX, HallMenuRow0Y + i * HallMenuRowStep);
                UiHoverSfx.Attach(b, UiSfx.Menufloat);
                UiSfx.AttachClick(b);
                // 最後一項是「设置」→ 開房間那個 OptionDlg;其餘四項按了只把選單收起來(沒有功能)。
                bool isSettings = i == HallMenuItems.GetLength(0) - 1;
                b.onClick.AddListener(() =>
                {
                    HideHallMenu();
                    if (isSettings) Nav.OpenSettings?.Invoke();
                });
            }
            _hallMenu.gameObject.SetActive(false);
        }

        /// <summary>右上角那排 34px 圓盤鈕:圓形去白邊 + 命中判定貼齊可見圓(透明四角不吃點擊)。</summary>
        private void TopIcon(string name, string normal, string hover, string pushed, float x,
                             UnityEngine.Events.UnityAction onClick)
        {
            var b = SpriteBtn(name, normal, hover, pushed, x, TopIconY, onClick, circle: true);
            UIKit.SetAlphaHit(b.targetGraphic);
        }

        // ---- 右下角那一排功能鈕 ----

        private void BuildActionBar()
        {
            // 活動查詢(actandprize)與夥伴(partner):這個重製版兩個系統都沒有 → 鈕照官方版位擺,
            // 按了安靜地什麼都不做。版位是官方的(306,365)/(373,365),之後真的做出來只要換掉 handler。
            SpriteBtn("ActAndPrize", "huodong1", "huodong2", "huodong3", ActX, SideActionY, null);
            SpriteBtn("Partner", "partner0", "partner1", "partner2", PartnerX, SideActionY, null);

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
            _chatBgImg = UIKit.AddSprite(Root, "ChatBg", An("RecordChatBG"), ChatBgX, ChatBgY);

            // 聊天記錄。背板已經畫好框了 → ScrollRect 自己不要再上底色。
            _chatScroll = UIKit.AddVerticalScroll(Root, "ChatScroll", out _chatContent, 1f, 2, new Color(0f, 0f, 0f, 0f));
            PlaceTopLeft(_chatScroll.GetComponent<RectTransform>(), ChatX, ChatY, ChatW, ChatH);
            _chatClip = _chatScroll.gameObject.AddComponent<ChatLineClip>();   // 只露整行,不留半截字

            // 頻道切換(chatmode「當前」)。按了拉開四選一的頻道選單,行為與房間畫面同一套。
            _chatChannelBtn = SpriteBtn("ChatChannel", "Lobby57", "Lobby58", "Lobby59", ChanX, ChanY, ToggleChatMenu);
            _chatChannelImg = _chatChannelBtn.targetGraphic as Image;

            // 聊天記錄(recordchatmode / closerecordchatmode 一對開關)。官方那是把聊天記錄面板叫出來/收回去,
            // 我們的聊天區是常駐的 → 這顆就是它的收合開關(收起來才看得到底下角色的腳與星空)。
            _recordChatBtn = SpriteBtn("RecordChat", "RecordChatBtn_1", "RecordChatBtn_2", "RecordChatBtn_3",
                                       RecordChatX, RecordChatY, ToggleChatLog);
            _recordChatImg = _recordChatBtn.targetGraphic as Image;

            // 輸入框:**placeholder 傳空字串**。以前放「輸入訊息後按 Enter…」的提示字,官方那格是空的,
            // 而且提示字在沒有游標的情況下會讓人以為那不是輸入框(使用者回報)。設定整套照搬房間那顆
            // (見 RoomScreen.ConfigureRoomChatInput),游標與 IME 行為兩邊才會一致。
            // (loc key lobby.chat_hint 從此沒人用,留在表裡不影響。)
            _chatInput = UIKit.AddInputField(Root, "ChatInput", "", 13f);
            PlaceTopLeft(_chatInput.GetComponent<RectTransform>(), ChatInputX, ChatInputY, ChatInputW, ChatInputH);
            if (_chatInput.targetGraphic != null)
                _chatInput.targetGraphic.color = new Color(1f, 1f, 1f, 0f);   // 底圖已經有凹槽了,不要再蓋一層灰
            ConfigureChatInput();

            // 表情(expression)。大廳沒有 3D 角色的表情動作(那是房間的功能)→ 鈕照擺,按了不做事。
            SpriteBtn("Expression", "Lobby102", "Lobby117", "Lobby118", ExprX, ExprY, null, circle: true);

            // 送出鈕很小,不套 alpha 命中判定 —— 那麼小的鈕給滿一個矩形反而好按(見 UIKit.SetAlphaHit 的註解)。
            // ⚠️ 官方這顆的 normal/hover 是**對調**的(bgnormal=Lobby100、bghover=Lobby99),照抄。
            SpriteBtn("ChatSend", "Lobby100", "Lobby99", "Lobby101", SendX, SendY, SendChat);

            // 大聲公(全服廣播)與寵物:兩個系統都沒有 → 鈕照擺,按了不做事。
            SpriteBtn("LoudSpeaker", "LoudSpeaker_1", "LoudSpeaker_2", "LoudSpeaker_3", LoudX, LoudY, null, circle: true);
            SpriteBtn("Pet", "Lobby147", "Lobby148", "Lobby149", PetX, PetY, null, circle: true);

            // 道具包(ItemsButton)。官方這顆是道具包,不是衣櫥 —— 以前接到儲物櫃是接錯了(使用者回報)。
            // 儲物櫃的入口在房間(RoomScreen 的 ClosetButton),沒有因此少掉。
            SpriteBtn("Items", "Lobby94", "Lobby95", "Lobby96", ItemsX, BottomBtnY, null);
            // 个人资料(DetailButton)→ 自己的資料頁。房間畫面是「點自己的頭貼」進去的,大廳沒有頭貼可點,
            // 這顆美術字面本來就寫著「个人资料」→ 正好是同一個去處,不接的話大廳就沒有入口。
            SpriteBtn("Detail", "Lobby61", "Lobby62", "Lobby72", DetailX, DetailY,
                      () => Nav.OpenSelfInfo?.Invoke());
            // NotesButton(郵件)。這個重製版沒有信箱 → 按了不做事。
            // 🔴 這顆以前借去開「音符外觀選擇器」,使用者要求拿掉 → **NoteSkinPicker 從此沒有任何入口**
            //    (全專案只有這一處接過 Nav.OpenNoteSkinPicker)。那支選擇器本身還在,之後要接的話
            //    最合理的去處是設定視窗(OptionDlg),不是這顆美術上寫著「郵件」的鈕。
            SpriteBtn("NoteSkin", "Lobby63", "Lobby64", "Lobby73", NotesX, BottomBtnY, null);
            // 右下角的「?」(help)。沒有說明系統 → 鈕照擺,按了不做事。
            var help = SpriteBtn("Help", "Lobby65", "Lobby66", "Lobby74", HelpX, HelpY, null, circle: true);
            UIKit.SetAlphaHit(help.targetGraphic);

            // 玩家名單開關(ListShow 三人頭 ↔ AvtShow 單人頭,官方疊在同一格輪換)。
            // 展開時鈕換成 AvtShow(= 按了切回看角色),見 ToggleUserPanel / ApplyUserPanelSprites。
            _userListBtn = SpriteBtn("UserList", "Lobby160", "Lobby161", "Lobby162", UserListX, UserListY, ToggleUserPanel);
            _userListImg = _userListBtn.targetGraphic as Image;

            BuildSelfInfo();
        }

        private void BuildSelfInfo()
        {
            _selfName = Label(Root, "SelfName", SelfNameX, SelfNameY, SelfNameW, SelfNameH, 14f,
                              SelfNameColor, TextAlignmentOptions.MidlineLeft);
            _selfName.fontStyle = FontStyles.Bold;
            _selfName.overflowMode = TextOverflowModes.Ellipsis;   // 名字長度沒有上限,不截會蓋到右邊的欄位

            // 右排五行(烤字順序見常數區):超舞戰績 / 知名度 / 勝率 / 愛慕值 / 金葉子。
            _selfRecord = Label(Root, "SelfRecord", RecordX, RecordY, PerfW, PerfH, 11f, StatColor, TextAlignmentOptions.Midline);
            _selfFame = Label(Root, "SelfFame", FameX, FameY, PerfW, PerfH, 11f, StatColor, TextAlignmentOptions.Midline);

            // 愛慕值 / 金葉子:這個重製版沒有這兩套系統,但官方那兩行**固定顯示 0**(使用者要求照擺)。
            // 同 WardrobeScreen 的金葉子 —— 那邊也是使用者指定固定 0。
            Label(Root, "SelfLove", LoveX, LoveY, MoneyW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft).text = "0";
            Label(Root, "SelfLeaf", LeafX, LeafY, LeafW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft).text = "0";

            _selfLevel = Label(Root, "SelfLevel", LevelX, LevelY, MoneyW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft);
            _selfWin = Label(Root, "SelfWin", WinX, WinY, MoneyW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft);
            _selfPoints = Label(Root, "SelfPoints", MoneyX, PointY, MoneyW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft);
            _selfCoins = Label(Root, "SelfCoins", MoneyX, CoinY, MoneyW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft);
            _selfBonus = Label(Root, "SelfBonus", MoneyX, BonusY, MoneyW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft);

            // 經驗條(exp_progress:Lobby137 底 + Lobby60 前景)。這個重製版沒有經驗值 →
            // 只畫底槽、不畫填充,那格看起來就是「還沒開始累積」而不是一個假的進度。
            UIKit.AddSprite(Root, "ExpBar", An("Lobby137"), ExpX, ExpY);
        }

        // ================================================================ 生命週期

        public override void OnShow()
        {
            Subscribe();
            Ctx.Chat?.SetScope(ChatScope.Lobby);   // 大廳作用域:只顯示大廳訊息(密語跨場另計)

            ShowAvatar();
            _scroll = 0;
            RefreshSelf();
            ReloadRooms();
            RequestOnlineUsers();   // 名單開著就回來的情況(從房間回大廳)—— 自己 no-op 掉收起來的那種
            RebuildChat();
            _nextPoll = Time.unscaledTime + PollSeconds;
        }

        public override void OnHide()
        {
            _listGen++;   // 還在路上的 roomList 回呼作廢
            Unsubscribe();
            HideAvatar();
            // 兩個下拉選單不能掛著離開:回來時它們會還開著,而且蓋在剛滑入的房卡上。
            HideHallMenu();
            HideChatMenu();
        }

        /// <summary>
        /// 左側的 3D 角色(官方 AvtShow)。做法與選角色畫面**完全相同**:GenderPreview3D 自己開一台相機
        /// 把角色渲到 RenderTexture,我們只是把那張貼圖掛到 RawImage 上。
        ///
        /// 🔴 一定要把預覽的 layer 從前端 UI 相機的 cullingMask 遮掉 —— 那台相機幾乎什麼都照,
        ///    沒遮的話角色會被它用正交投影再畫一次(扁扁的疊在畫面上)。OnHide 還原。
        ///
        /// 大廳只顯示**目前登入的那個角色**,所以 Build 之後照 session 的性別切一次。
        /// (GenderPreview3D 內部男女兩隻都建起來、靠 SetGender 切換顯示,這裡沿用同一套 API。)
        /// </summary>
        private void ShowAvatar()
        {
            int gender = Ctx != null && Ctx.Session != null && Ctx.Session.Gender == 1 ? 1 : 0;
            string[] fParts = PartsForGender(0), mParts = PartsForGender(1);

            if (_preview == null)
            {
                var go = new GameObject("LobbyAvatar3D");
                _preview = go.AddComponent<GenderPreview3D>();
                // 🔴 取景參數要在 Build **之前**設:Build 最後會 SetGender → FrameTo,那一刻就把相機距離定下來了。
                //    (avatarYOffset 更早 —— BuildAvatar 擺位時就讀它。)
                //
                // 🔴 那兩個偏移一定要**歸零**:官方 LOBBYSEL 的 avatarYOffset=-5 是「那個 400×600 預覽框內」的偏移,
                //    verticalBias=+2 也是為那個框校的。大廳沒有那個框,兩者加起來會讓角色相對取景窗往下偏 7 個
                //    model unit ≈ 0.123×角色像素高 —— fillFrac 0.9 時腳底會算到 y=642 直接掉出畫面
                //    (使用者回報的「人變得太大」),而且在 400×600 的槽位裡 fillFrac 上限被壓到 0.803、
                //    永遠做不到官方那個 548px。歸零之後版位改由 AvatarY 吸收,男女落點也才會一致
                //    (那個偏移與身高成反比,男角比女角高 8% → 不歸零的話兩性會差 5px)。
                _preview.avatarYOffset = 0f;
                _preview.verticalBias = 0f;
                _preview.fillFrac = AvatarFillFrac;
                _preview.Build(gender, fParts, mParts, BodyIndexForGender(0), BodyIndexForGender(1));
            }
            else
            {
                // 回到大廳時穿搭可能已經變了(去商城買了東西、或換過帳號)→ 每次顯示都重套一次。
                _preview.SetOutfits(gender, fParts, mParts, BodyIndexForGender(0), BodyIndexForGender(1));
            }
            _preview.SetGender(gender);

            if (_previewImg != null && _preview.PreviewTexture != null)
            {
                _previewImg.texture = _preview.PreviewTexture;
                _previewImg.color = Color.white;
            }

            var ui = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            if (ui != null) { _maskedCam = ui; _savedMask = ui.cullingMask; ui.cullingMask &= ~(1 << GenderPreview3D.PreviewLayer); }
        }

        private void HideAvatar()
        {
            if (_maskedCam != null) { _maskedCam.cullingMask = _savedMask; _maskedCam = null; }
            if (_previewImg != null) { _previewImg.texture = null; _previewImg.color = new Color(1f, 1f, 1f, 0f); }
            if (_preview != null) { Destroy(_preview.gameObject); _preview = null; }
        }

        // 取某性別對應 profile(女 00000000 / 男 00000001)的「實際穿戴」部位;找不到 → null(用預設整套)。
        // 從 id-based equippedItems 經 catalog 現算(含合成的翅膀/表情/項鍊),而不是讀可能過時的
        // equippedParts 快取 —— 與選角色畫面同一條路,兩邊看到的自己才會一樣。
        private static string[] PartsForGender(int gender)
        {
            string id = ProfileManager.SeededIdForGender(gender);
            foreach (var p in ProfileManager.List())
                if (p != null && p.id == id)
                    return WardrobeStore.ResolveEquippedParts(p, gender, cid => AvatarItemCatalog.Instance.ById(cid));
            return null;
        }

        // 取某性別對應 profile 自己的體型(胖瘦)index 0..4;找不到 → 0(瘦)。
        private static int BodyIndexForGender(int gender)
        {
            string id = ProfileManager.SeededIdForGender(gender);
            foreach (var p in ProfileManager.List())
                if (p != null && p.id == id)
                    return p.bodyShapeIndex;
            return 0;
        }

        private RawImage AddRaw(string name, float x, float y, float w, float h)
        {
            var rt = UIKit.NewRect(Root, name);
            var ri = rt.gameObject.AddComponent<RawImage>();
            ri.raycastTarget = false;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return ri;
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

            // 玩家名單同理(沒有上下線推播)。只在名單開著時才問 —— 收起來的時候沒人看,不必占頻寬。
            RequestOnlineUsers();
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
            if (FakeLobbyData) AddFakeRooms();
            RefreshRows();
        }

        /// <summary>
        /// 大廳的測試假資料開關(假房間 + 假玩家)。
        ///
        /// 🔴 **editor 預設開、打包版預設關** —— 開發時要看得到滿版的房卡與名單才能校版位,
        ///    而打包出去的版本絕不能出現假人。要在 editor 關掉就設環境變數(或 EditorPrefs)
        ///    <c>SDO_LOBBY_FAKE=0</c>;要在打包版臨時打開就設 <c>=1</c>。
        ///    讀法照本專案的 dev 慣例(<see cref="Sdo.Game.ScreenGameplay.DevVar"/>:環境變數優先、editor 退回 EditorPrefs)。
        /// </summary>
        private static bool FakeLobbyData
        {
            get
            {
                var v = Sdo.Game.ScreenGameplay.DevVar("SDO_LOBBY_FAKE");
                if (!string.IsNullOrEmpty(v)) return v != "0";
#if UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>官方風格的假房名(「XXX的舞蹈室」)。用真的像房名的字,「測試1測試2」看不出版位對不對。</summary>
        private static readonly string[] FakeRoomHosts =
            { "煙煙羅", "寶貝BOY", "邦·瑋", "不知道", "勇敢的心", "月光之城", "櫻花雨", "小舞", "風之舞", "舞星" };

        private static readonly string[] FakeSongs =
            { "Butterfly", "Dancing Queen", "極彩色", "Sandstorm", "Bad Apple", "月與蓮", "" };

        private void AddFakeRooms()
        {
            // 十間 —— 一頁只放得下 6 張卡,多出來的才會讓捲軸握把有得跑(這正是要測的東西之一)。
            for (int i = 0; i < FakeRoomHosts.Length; i++)
            {
                string host = FakeRoomHosts[i];
                var room = new RoomInfo
                {
                    Id = 90000 + i,          // 假房號不會與本機那間(RoomEntry 給的)撞號
                    Seq = i + 1,
                    Name = host + "的舞蹈室",
                    HostName = host,
                    Status = (i % 4 == 3) ? RoomStatus.InGame : RoomStatus.Waiting,
                    Capacity = 6,
                    SongTitle = FakeSongs[i % FakeSongs.Length],
                };
                // 🔴 RoomInfo.Count 是**由 Seats 算出來的唯讀屬性**,不能直接指派 —— 人數要靠真的塞座位。
                int taken = 1 + (i % 5);
                for (int s = 0; s < room.Capacity; s++)
                    room.Seats.Add(new SeatInfo { Player = s < taken ? new PlayerProfile("", host, 1 + i) : null });
                _rooms.Add(room);
            }
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

        /// <summary>
        /// 把握把放到軌道上對應的位置。
        ///
        /// 🔴 **永遠顯示**(使用者要求):以前沒東西可捲時整顆 <c>enabled = false</c>,結果房間少於七間
        /// 就看不到滑桿頭,看起來像忘了做。沒得捲時 t=0 → 停在軌道最上面,那就是官方的樣子。
        /// </summary>
        private void PlaceHandle(int max)
        {
            if (_handle == null) return;
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
                    // 失敗只寫 log(大廳不彈 Toast)。玩家看得到的是「按了沒進房、還留在大廳」,
                    // 而失敗的細節(server 回的協定代碼)本來就只有查問題的人需要。
                    Debug.LogWarning("[lobby] createRoom 失敗:" + result);
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
                        // 同 OnCreate:大廳不彈 Toast,失敗只寫 log(畫面上就是「沒進去、還在大廳」)。
                        Debug.Log("[lobby] joinRoom 失敗:" + result + " —— " + L(JoinErrorText.KeyFor(result)));
                    },
                    // 座位滿了會自動改用旁觀身分進去。這件事只寫 log —— 使用者要求大廳不彈 Toast,
                    // 也不要寫進聊天區。玩家看得出來的線索是進房之後那顆「旁觀 / 進入」鈕的狀態。
                    trigger => Debug.Log("[lobby] " + L("lobby.joined_as_spectator")));
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
            // 沒有可加入的房 → 只寫 log(大廳不彈 Toast)。列表本來就攤在眼前,有沒有等待中的房看得出來。
            if (pick == null) { Debug.Log("[lobby] " + L("lobby.no_quick_room")); return; }
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
            => Reskin(_filterBtn, _filterImg,
                      _waitingOnly ? "Lobby103" : "Lobby51",
                      _waitingOnly ? "Lobby104" : "Lobby52",
                      _waitingOnly ? "Lobby105" : "Lobby83");

        /// <summary>把一顆已經建好的鈕整組換皮(三態一起換)。官方常把「兩個狀態」做成兩顆疊在同一格的鈕,
        /// 我們用一顆換圖表達 —— 換皮要走與 <see cref="SpriteBtn"/> 相同的 solo 載入,否則換完那一顆又長回白邊。</summary>
        private static void Reskin(Button btn, Image img, string normal, string hover, string pushed)
        {
            if (btn == null) return;
            var n = LobbyArt.AnSoloAA(normal);
            var h = LobbyArt.AnSoloAA(hover);
            var p = LobbyArt.AnSoloAA(pushed);
            UIKit.ApplySprite(img, n);
            var st = btn.spriteState;
            st.highlightedSprite = h != null ? h : n;
            st.pressedSprite = p != null ? p : (h != null ? h : n);
            st.selectedSprite = n;
            btn.spriteState = st;
        }

        // ---- 左下角「當前」拉開的頻道選單(官方 LOBBYPOPMENU.XML 的 chatmodemenu) ----

        /// <summary>
        /// 四個頻道,由上而下 家族 / 好友 / 當前 / 回复(官方在選單內的 y 是 5/29/53/77)。
        ///
        /// 🔴 「回复」那一項官方**借的是房間的素材**(Room206/207/208)—— 大廳這一包裡沒有對應的圖,
        ///    所以那一項要走 <see cref="RoomUiArt"/> 而不是 <see cref="LobbyArt"/>。
        /// </summary>
        private static readonly ChatChannel[] ChatMenuOrder =
            { ChatChannel.Family, ChatChannel.Friend, ChatChannel.Current, ChatChannel.Reply };

        private static readonly float[] ChatMenuRowY = { 5f, 29f, 53f, 77f };

        private void ToggleChatMenu()
        {
            if (_chatMenu == null) BuildChatMenu();
            bool show = !_chatMenu.gameObject.activeSelf;
            HideHallMenu();   // 兩個選單互斥
            _chatMenu.gameObject.SetActive(show);
        }

        private void HideChatMenu()
        {
            if (_chatMenu != null) _chatMenu.gameObject.SetActive(false);
        }

        private void BuildChatMenu()
        {
            _chatMenu = UIKit.NewRect(Root, "chatmodemenu");
            PlaceTopLeft(_chatMenu, ChatMenuX, ChatMenuY, 45f, 104f);

            for (int i = 0; i < ChatMenuOrder.Length; i++)
            {
                var ch = ChatMenuOrder[i];
                ChatChannelArt(ch, out var n, out var h, out var p, out bool fromRoom);
                System.Func<string, Sprite> res = fromRoom
                    ? (System.Func<string, Sprite>)RoomUiArt.AnSolo
                    : LobbyArt.AnSoloAA;
                var b = UIKit.AddSpriteButton(_chatMenu, "chatmode" + i, res(n), res(h), res(p), 2f, ChatMenuRowY[i]);
                UiHoverSfx.Attach(b, UiSfx.Menufloat);
                UiSfx.AttachClick(b);
                b.onClick.AddListener(() => SetChatChannel(ch));
            }
            _chatMenu.gameObject.SetActive(false);
        }

        /// <summary>
        /// 換頻道。🔴 **只換圖,不改真正的送出行為** —— 大廳的作用域固定是 <see cref="ChatScope.Lobby"/>
        /// (見 <see cref="OnShow"/>),這個重製版的大廳只有一條公頻。官方那四個頻道背後是家族/好友系統,
        /// 我們沒有;硬把訊息標成別的頻道只會變成「送出去自己看不到」。
        /// 密語仍然走 <c>/w 名字</c>,不佔頻道。
        /// </summary>
        private void SetChatChannel(ChatChannel channel)
        {
            _chatChannel = channel;
            ChatChannelArt(channel, out var n, out var h, out var p, out bool fromRoom);
            if (fromRoom)
            {
                // 「回复」用的是房間的素材 → 換皮不能走 Reskin(它固定吃 LobbyArt)。
                UIKit.ApplySprite(_chatChannelImg, RoomUiArt.AnSolo(n));
                var st = _chatChannelBtn.spriteState;
                st.highlightedSprite = RoomUiArt.AnSolo(h);
                st.pressedSprite = RoomUiArt.AnSolo(p);
                st.selectedSprite = RoomUiArt.AnSolo(n);
                _chatChannelBtn.spriteState = st;
            }
            else Reskin(_chatChannelBtn, _chatChannelImg, n, h, p);
            HideChatMenu();
            if (_chatInput != null) _chatInput.ActivateInputField();
        }

        private static void ChatChannelArt(ChatChannel channel, out string nrm, out string hov, out string psh,
                                           out bool fromRoom)
        {
            fromRoom = false;
            switch (channel)
            {
                case ChatChannel.Family: nrm = "Lobby127"; hov = "Lobby128"; psh = "Lobby129"; break;
                case ChatChannel.Friend: nrm = "Lobby106"; hov = "Lobby107"; psh = "Lobby108"; break;
                case ChatChannel.Reply: nrm = "Room206"; hov = "Room207"; psh = "Room208"; fromRoom = true; break;
                default: nrm = "Lobby57"; hov = "Lobby58"; psh = "Lobby59"; break;
            }
        }

        /// <summary>
        /// 輸入框的設定 —— **與房間那顆完全同一套**(見 <c>RoomScreen.ConfigureRoomChatInput</c>)。
        /// 以前大廳只設了 onSubmit,結果沒有游標、又留著一行提示字,看起來不像可以打字的地方(使用者回報)。
        ///
        /// 其中兩項不是美觀而是行為:
        ///   • <c>onFocusSelectAll = false</c> —— 預設 true 時每次重新取得 focus 都會整行反白、游標跳回最前面。
        ///   • <c>richText = true</c> —— TMP 只有在 richText 開著時才會把 IME 組字串包成 &lt;u&gt;…&lt;/u&gt;,
        ///     也就是注音選字階段那條底線。送出走的是 raw text,所以不影響內容。
        /// </summary>
        private void ConfigureChatInput()
        {
            if (_chatInput == null) return;
            _chatInput.characterLimit = 50;
            _chatInput.onFocusSelectAll = false;
            _chatInput.customCaretColor = true;
            _chatInput.caretColor = Color.white;
            _chatInput.caretWidth = 2;
            _chatInput.caretBlinkRate = 0.85f;
            _chatInput.richText = true;
            if (_chatInput.textComponent != null) _chatInput.textComponent.richText = true;
            _chatInput.selectionColor = new Color(1f, 1f, 1f, 0.28f);
            _chatInput.onSubmit.AddListener(_ => SendChat());

            if (_chatInput.textViewport != null)
            {
                _chatInput.textViewport.offsetMin = new Vector2(5f, 4f);
                _chatInput.textViewport.offsetMax = new Vector2(-5f, -4f);
            }
            if (_chatInput.textComponent != null)
            {
                _chatInput.textComponent.color = Color.white;
                _chatInput.textComponent.fontSize = 13f;
                _chatInput.textComponent.alignment = TextAlignmentOptions.MidlineLeft;
                _chatInput.textComponent.margin = Vector4.zero;
            }
        }

        // ---- 玩家名單(官方 win3) ----

        private void BuildUserPanel()
        {
            _userPanel = Root.gameObject.AddComponent<LobbyUserPanel>();
            _userPanel.Build(Root);
            // 名單滑進來時把左邊的 3D 角色藏起來、滑出去時放回來(使用者要求)。
            // 🔴 只關 RawImage,**不要**拆掉 GenderPreview3D —— 那是一整隻 3D 角色 + 相機 + RT,
            //    每開關一次名單就重建會卡一下,而且穿搭要重新解析。
            _userPanel.VisibilityChanged = on =>
            {
                if (_previewImg != null) _previewImg.enabled = !on;
            };
        }

        /// <summary>三人頭(ListShow)開名單、單人頭(AvtShow)收回去看角色 —— 官方兩顆疊在同一格輪換。</summary>
        private void ToggleUserPanel()
        {
            if (_userPanel == null) return;
            bool show = !_userPanel.Visible;
            _userPanel.SetVisible(show);
            // 展開時鈕換成 AvtShow(單人頭)= 「按了回去看角色」,收起來時換回 ListShow(三人頭)。
            Reskin(_userListBtn, _userListImg,
                   show ? "Lobby157" : "Lobby160",
                   show ? "Lobby158" : "Lobby161",
                   show ? "Lobby159" : "Lobby162");
            if (show) RequestOnlineUsers();   // 開的那一刻先要一份新的,不必等下一個輪詢節拍
        }

        /// <summary>
        /// 跟 server 要一份線上名單。與房間列表同一個模式:沒有上下線推播,只能自己回頭問
        /// (所以 <see cref="Update"/> 的輪詢也順手要一次)。
        ///
        /// 離線時 server 不存在 → 名單裡只有自己那一列。空著會讓人以為是壞了,而「只有你自己在線上」
        /// 正是單機模式的事實。
        /// </summary>
        private void RequestOnlineUsers()
        {
            if (_userPanel == null || !_userPanel.Visible) return;

            var net = Ctx != null ? Ctx.Net : null;
            if (net == null || !net.IsConnected) { _userPanel.SetUsers(OfflineSelfOnly(), 0, SelfName(), SelfGuild()); return; }

            int gen = _listGen;
            net.RequestUserList(users =>
            {
                // 同房間列表:回呼可能在離開大廳/登出之後才到,那份資料屬於上一次的畫面。
                if (this == null || gen != _listGen || _userPanel == null) return;
                _userPanel.SetUsers(users, net.UserId, SelfName(), SelfGuild());
            });
        }

        private List<NetUserListEntry> OfflineSelfOnly()
        {
            _offlineUsers.Clear();
            var p = ProfileManager.Active;
            _offlineUsers.Add(new NetUserListEntry
            {
                UserId = 0,
                Name = SelfName(),
                Guild = SelfGuild(),
                Level = ProfileFields.PlayerLevelValue(p),
                Gender = Ctx != null && Ctx.Session != null && Ctx.Session.Gender == 1 ? 1 : 0,
                RoomSeq = 0,
            });
            if (FakeLobbyData) AddFakeUsers();
            return _offlineUsers;
        }

        /// <summary>
        /// 假玩家(<see cref="FakeLobbyData"/> 開著時才有)。四個分頁都要有東西可看,所以這批人刻意做成:
        ///   • 十幾個 → 名單塞得滿、捲軸握把有得跑;
        ///   • 前三個名字與**本機好友清單**比對得上(FriendList 認的是名字)→「好友」分頁不會是空的;
        ///   • 中間三個家族設成與自己相同 →「家族」分頁不會是空的;
        ///   • 位置一半在大廳(RoomSeq=0)、一半在房裡 → 那一欄的兩種樣子都看得到。
        /// </summary>
        private void AddFakeUsers()
        {
            var owner = ProfileManager.Active;
            var friends = FriendList.Names(owner);
            string myGuild = SelfGuild();

            for (int i = 0; i < FakeUserNames.Length; i++)
            {
                // 前三個優先借用真的好友名字(好友清單有多少就借多少),其餘用假名字池。
                string name = i < friends.Length && i < 3 ? friends[i] : FakeUserNames[i];
                _offlineUsers.Add(new NetUserListEntry
                {
                    UserId = 1000 + i,
                    Name = name,
                    // 中間三個掛自己的家族(自己沒家族時就留空 —— 那時「家族」分頁本來就該是空的)。
                    Guild = (i >= 3 && i <= 5) ? myGuild : "",
                    Level = 1 + i * 7,
                    Gender = i % 2,
                    RoomSeq = (i % 2 == 0) ? 0 : (i / 2) + 1,
                });
            }
        }

        private static readonly string[] FakeUserNames =
        {
            "ゲ鱼鳞ゲ", "司狼†v撩冰", "派头姑娘つ", "ㄘ--宪◎岁", "ㄣЕ†n.ㄋ月光", "..·狞 摞..",
            "Elodie yin", "一呸,臭狗ト的。", "大熊舞咪匠★", "櫻花", "舞星", "風之舞", "Neo",
        };

        private string SelfName()
        {
            string name = Ctx != null && Ctx.Session != null ? Ctx.Session.LocalPlayerName : null;
            if (string.IsNullOrEmpty(name))
            {
                var p = ProfileManager.Active;
                if (p != null) name = p.name;
            }
            return name ?? "";
        }

        private static string SelfGuild() => ProfileFields.FamilyName(ProfileManager.Active);

        /// <summary>「聊天記錄」鈕(官方 recordchatmode ↔ closerecordchatmode):收合/展開左下角的聊天顯示區。
        /// 輸入列不跟著收 —— 收起來之後照樣講得了話(訊息會飄到房間/大廳的其他人那裡)。</summary>
        private void ToggleChatLog()
        {
            _chatLogHidden = !_chatLogHidden;
            if (_chatBgImg != null) _chatBgImg.enabled = !_chatLogHidden;
            if (_chatScroll != null) _chatScroll.gameObject.SetActive(!_chatLogHidden);
            Reskin(_recordChatBtn, _recordChatImg,
                   _chatLogHidden ? "RecordChatCloseBtn_1" : "RecordChatBtn_1",
                   _chatLogHidden ? "RecordChatCloseBtn_2" : "RecordChatBtn_2",
                   _chatLogHidden ? "RecordChatCloseBtn_3" : "RecordChatBtn_3");
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

            // 勝率那格背板已經寫著「胜率」→ 只放數字。
            var st = p != null ? p.stats : null;
            _selfWin.text = st != null ? One(st.WinRate) + "%" : "";
            _selfRecord.text = st != null ? LocalizationManager.Get("lobby.record", st.wins, st.losses) : "";

            // 知名度:購物累加的那個值,顯示成官方的「LV 2 (15)」——等級由累計值查表(FameLevel)。
            // (以前這一格放的是命中率,那是照著一段錯註解擺的;命中率在個人資料頁本來就有。)
            _selfFame.text = p != null ? FameLevel.Label(p.fame) : "";
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

        /// <summary>
        /// 官方三態鈕(normal/hover/pushed)+ 點擊音效,座標是 XML 的左上角像素。
        ///
        /// 🔴 三態一律走 <see cref="LobbyArt.AnSoloAA"/>(自貼圖 + 超取樣)而不是 <see cref="LobbyArt.An"/>:
        ///    大廳這一包幾乎整包裁自同一張 STAGE.PNG,鈕與鈕在圖裡是**貼著**的 → 共用圖集取樣時會把隔壁那顆鈕
        ///    的像素拖進邊緣,每顆鈕就鑲了一圈白邊(使用者回報的 #3)。切到自己的貼圖上就沒有鄰居可滲。
        ///
        /// <paramref name="circle"/> = 真正的圓盤鈕(右上角那排 hall*、下排的表情/喇叭/寵物/幫助):
        ///    它們的圓邊是寬軟 AA 邊,AnSoloAA 的 α&lt;128→0 硬裁會把它裁成 1-bit 圓 → 邊緣破碎;
        ///    改走 CircleMask 平滑圓邊。長方形/膠囊的鈕不要開(圓遮罩會把兩端裁掉)。
        /// </summary>
        private Button SpriteBtn(string name, string normal, string hover, string pushed, float x, float y,
                                 UnityEngine.Events.UnityAction onClick, bool circle = false)
        {
            System.Func<string, Sprite> res = circle ? (System.Func<string, Sprite>)LobbyArt.AnSoloCircleAA
                                                     : LobbyArt.AnSoloAA;
            var btn = UIKit.AddSpriteButton(Root, name, res(normal), res(hover), res(pushed), x, y);
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
