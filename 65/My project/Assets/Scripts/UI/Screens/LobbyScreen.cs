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
        // 落點(數字是**量出來的**,不是算出來的 —— 見 AvatarFillFrac 與 LobbyAvatarFramingTests):
        //   角色高 548、頭頂 y=30、腳底 y=578、身體中線 x=205,對上官方實機。
        // 🔴 AvatarX 用「RT 中心 = 官方身體中線」回推(205 − 400/2 = 5),**不要**照 alpha bounding box 的中心去校:
        //    相機正對角色原點,所以身體中線恆在 RT 正中;bounding box 的中心會隨當下抽到的 idle 姿勢
        //    (手臂張開、抬腳、甩裙擺)左右跳三四十 px —— 照那個調會越調越偏。
        private const float AvatarX = -30f, AvatarY = -91f, AvatarW = 400f, AvatarH = 600f;

        /// <summary>
        /// 角色佔預覽高度的比例。選角色畫面用 0.68(那邊的框留白多),大廳的角色幾乎頂天立地。
        ///
        /// 🔴 這個值只有在 <see cref="ShowAvatar"/> 把 <c>avatarYOffset</c> 與 <c>verticalBias</c> **一起歸零**
        ///    之後才算得準。那兩個偏移(官方 LOBBYSEL 的 -5 與 +2)會讓角色相對取景窗往下偏 7 個 model unit
        ///    ≈ 0.123×角色像素高 —— 0.9 配上那個偏移時腳底會算到 y=642,**掉出畫面被切掉 36px**
        ///    (使用者回報「人變得太大」的真正成因),而且在 400×600 的槽位裡 fillFrac 上限只到 0.803。
        ///    歸零之後才做得到官方那個 548px。
        ///
        /// 🔴 **fillFrac 不等於「角色佔畫面的比例」,不要照那個直覺算。** <c>FrameTo</c> 是拿
        ///    <c>bodyTop = (頭骨 y − 腳底 y) × (1 + framePadTop)</c> 去填 fillFrac 的,而**實際看得到的人**
        ///    比那個 bodyTop 還高約 9%(髮型高過頭骨、idle 動作會抬腳/甩裙擺)。照「fillFrac = 548/600 = 0.913」
        ///    算下去,實測是角色高 **599px、整個填滿畫布**(頭頂 y=4、腳底 y=603)——正是使用者回報的「太大」。
        ///
        /// 這個值是**量出來的**:0.913 → 實測 599px;0.835 → 571px;0.80 → 546px。
        ///
        /// 🔴 **目標值是直接從官方實機截圖量出來的**(800×630 的視窗截圖,標題列 26px,畫布從 y=26 起):
        ///    髮髻頂 y≈30 → 畫布 y≈4;白鞋底 y≈437 → 畫布 y≈411(正好落在房卡列表框的下緣 410);
        ///    身體中線 x≈170。**高 ≈410px。**
        ///    前三次(599 / 571 / 546)都太大,是因為我拿截圖「目測」而不是逐點量,而且每次只縮 9% 一直在原地打轉。
        ///    這一版連**落點**也一起修:官方那隻是「頂到畫面最上緣、腳落在列表框下緣」,以前整個往下掉了 80px。
        ///    要再調就跑 <c>Assets/Tests/PlayMode/LobbyAvatarFramingTests.cs</c>,它會把實測的
        ///    頭頂/腳底/高度印出來(Debug.Log 前綴 <c>[lobby-avatar]</c>);高度與 fillFrac 是線性的,
        ///    想要 N px 就寫 <c>目前的 fillFrac × N ÷ 目前實測高度</c>。
        /// 要再調就跑 <c>Assets/Tests/PlayMode/LobbyAvatarFramingTests.cs</c>,它會把實際的
        /// 頭頂/腳底/高度/中線印出來(Debug.Log 前綴 <c>[lobby-avatar]</c>),照那個數字換算就好 ——
        /// 別再用幾何推導,那條路已經錯過兩次。
        /// </summary>
        private const float AvatarFillFrac = 0.605f;

        // 「按住拖動轉身」的命中區。
        // 🔴 **下緣一定要停在 370**:再往下就會蓋住三人頭(<see cref="UserListX"/> 206,378)與个人资料(244,378)
        //    那兩顆鈕 —— 上一版開到 430,結果玩家家的名單按鈕整顆按不動(使用者回報)。
        // 🔴 **右緣停在 240**:再往右會碰到个人资料鈕(244);更右邊的房卡列表(286 起)本來就不能碰。
        // 角色實測橫向 x≈110-230、縱向 y≈6-419 —— 拖不到腳,但上半身涵蓋得到,轉身照樣拖得動。
        private const float AvatarDragX = 90f, AvatarDragY = 0f, AvatarDragW = 150f, AvatarDragH = 370f;

        // 房間列表底板(NormalBG = LobbyChannelBG,506×364)+ 捲軸
        //
        // 🔴 HandleH 是**握把圖的實際高度**,不是隨便一個數:LOBBY38.AN = stage.png (843,590,14,28) → 14×28。
        //    以前寫 42 → 拉到底時握把底緣停在 y=341,離軌道底 355 差 14px。
        // RailX / RailTop 都**不是** XML 的那組(760,35):XML 給的是 ScrollBarV 整條(25 寬)的框,
        //    而握把要坐進「底板烘死的凹槽」裡。實測 LobbyChannelBG 貼在 (286,46) 之後,
        //    凹槽在絕對 x 760-781(中央深溝 769-772)、y 49-349 —— 所以 14 寬的握把置中是 x=764,
        //    軌道從 y=49 起、可跑 300(349-49)。照 XML 的 35 會讓握把浮在列表框上緣外面(使用者回報「拉桿太高」)。
        private const float ListBgX = 286f, ListBgY = 46f;
        private const float RailX = 764f, RailTop = 49f, RailH = 300f, HandleH = 28f;

        // 左下聊天記錄的捲軸(官方 win4 的 TextList AllChatList,Handle 也是 Lobby12)。
        // RecordChatBG 貼在 (21,437),它烘死的細溝實測在絕對 x 429-431 → 14 寬的握把置中 x=423;
        // 軌道跟著聊天區(ChatY..ChatY+ChatH)。
        private const float ChatRailX = 423f, ChatRailTop = 447f, ChatRailH = 110f;

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
        // 🔴 官方 XML 是 288/385,但實機那排字整體再往右 4px(使用者實機比對) → 292/389 再 +4。
        private const float ServerX = 296f, ChannelX = 393f, TopLabelY = 9f;
        private const float TopWeddingX = 651f, TopHouseX = 688f, TopRankX = 722f, TopIconY = 8f;
        private const float TopLogoutX = 759f, TopLogoutY = 8f;

        // 放大鏡那顆(TopHouseX)拉開的下拉選單 —— 版位逐字取自官方 POPMENU.XML 的 Formal_Pop_Menu:
        // 五個項目在選單內的 (14, 13/39/65/91/117),每項 135×26,**pushed = normal**(官方只給兩態)。
        // 選單原點是靠右對齊算出來的:項目寬 135、右緣貼齊畫面 → 651;y 讓第一項落在按鈕列正下方 → 27。
        private const float HallMenuX = 651f, HallMenuY = 27f;
        private const float HallMenuItemX = 14f, HallMenuRow0Y = 13f, HallMenuRowStep = 26f;

        // 左下「當前」拉開的頻道選單 —— 逐字取自官方 LOBBYPOPMENU.XML 的 chatmodemenu (21,466)。
        private const float ChatMenuX = 21f, ChatMenuY = 466f;

        // 表情盤(官方 LOBBYPOPMENU.XML 的 expression PopMenu,165×152)。XML 給的是選單自己的 (0,0),
        // 實際位置對齊表情鈕:水平置中(458+16.5−82.5=392)、底邊貼鈕的上緣(566−152=414)。
        private const float ExprMenuX = 392f, ExprMenuY = 414f;

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
        // 🔴 金葉子與愛慕值要**同一個 x**:官方 XML 寫 676/672,但那 4px 差在實機看起來就是沒對齊
        //    (兩行上下相鄰、都是靠左的數字)。統一取 672。
        private const float LeafX = 672f, LeafY = 538f, LeafW = 100f;   // charrank → 金葉子(固定 0)

        // XML 的顏色(0xAARRGGBB)
        private static readonly Color32 RoomNameColor = new Color32(0x82, 0x14, 0x38, 0xff);   // roomname
        private const float RoomNameEdgePx = 1.2f;   // 房名的白邊厚度(12px 字,再厚就糊成一團)
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
        private Image _chatHandle;            // 聊天記錄的捲軸握把(官方 AllChatList 的 Handle)
        private Image _chatCaret;             // 自畫的輸入游標(TMP 內建的在這裡畫不出來,見 ConfigureChatInput)
        private readonly Vector3[] _caretCorners = new Vector3[4];   // 餵給 IME 候選視窗的座標暫存(每幀用,不要每次配置)
        private Button _recordChatBtn;
        private Image _recordChatImg;
        private bool _chatLogHidden;

        // 兩個下拉選單(右上角功能選單 / 左下角頻道選單)。都是 lazily build、再按一次收起來,
        // 而且**互斥** —— 開一個就把另一個收掉(照 RoomScreen 的 chatmode ↔ expression 那個模式)。
        private RectTransform _hallMenu, _chatMenu, _exprMenu;
        private int _exprPage;
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

            // 在角色身上按住拖動 → 轉身 / 抬頭(與商城左側那隻同一組官方參數,見 GenderPreview3D.Orbit)。
            // 🔴 命中區**不能**用 AvatarView 本身:那張 RawImage 是 400×600、右緣蓋到 x=370,
            //    會把房卡列表(x 從 286 起)整片吃掉。這裡另外開一塊只涵蓋角色的透明區
            //    (實測角色橫向落在 x≈110-230、縱向 0-420),留一點餘裕又不碰到房卡。
            var drag = UIKit.AddImage(Root, "AvatarDrag", new Color(0f, 0f, 0f, 0f), raycast: true);
            PlaceTopLeft(drag.rectTransform, AvatarDragX, AvatarDragY, AvatarDragW, AvatarDragH);
            var trig = drag.gameObject.AddComponent<EventTrigger>();
            var entry = new EventTrigger.Entry { eventID = EventTriggerType.Drag };
            entry.callback.AddListener(ev =>
            {
                if (_preview != null && ev is PointerEventData p) _preview.Orbit(p.delta);
            });
            trig.triggers.Add(entry);

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
            HideChatMenu();          // 三個選單互斥
            HideExpressionMenu();
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
                // 🔴 走 <see cref="LobbyArt.AnSolo"/> 而**不是** AnSoloAA:官方的 PopMenu 沒有背板
                //    (XML 寫 background="empty.an"),那個「整片粉色選單框」其實是五條 135×26 的項目圖
                //    **無縫疊起來**的效果。AnSoloAA 會把外圈的透明/低 alpha 邊裁掉 → 每條變窄一點,
                //    疊起來就出現一條條裂縫、整體還會位移(使用者回報「沒把官方底圖做出來」)。
                //    AnSolo 是 pad:0 的自貼圖裁切,尺寸與原圖完全一致 → 條與條才接得起來。
                // 官方項目沒有 pushed 態 → 三個參數餵同一組(normal/hover/normal)。
                var b = UIKit.AddSpriteButton(_hallMenu, "hallmenu" + i,
                    LobbyArt.AnSolo(HallMenuItems[i, 0]), LobbyArt.AnSolo(HallMenuItems[i, 1]),
                    LobbyArt.AnSolo(HallMenuItems[i, 0]),
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

            // 🔴 房名是**置中 + 白色描邊**(照官方實機):XML 只寫 color=0xff821438 與 bold,
            //    但那個深紅字直接壓在粉紫標題列上根本讀不清 —— 官方實機那行字有一圈白邊、而且置中。
            //    用 OutlinedLabel(與房間畫面的頭上名字同一套描邊)才做得出來。
            row.Name = OutlinedLabel.Create(root, "Name", NameX, NameY, NameW, NameH, 12f,
                                            RoomNameColor, Color.white, RoomNameEdgePx, true,
                                            TextAlignmentOptions.Midline);
            // 房名是玩家自訂的,長的話會整條蓋過右邊的圖示 → 截斷加省略號(官方的欄寬也是硬邊界)。
            row.Name.Face.overflowMode = TextOverflowModes.Ellipsis;

            row.CountD = UIKit.AddSprite(root, "Count", null, CountX, CountY);
            row.Slash = UIKit.AddSprite(root, "Slash", An("slash"), SlashX, CountY);
            row.CapD = UIKit.AddSprite(root, "Cap", null, CapX, CountY);

            // 密碼房的鑰匙(password = Lobby93)。這個重製版還沒有房間密碼 → 永遠隱藏,
            // 但位置先擺好,之後接上密碼房只要把它 SetActive(true)。
            row.Key = UIKit.AddSprite(root, "Key", null, KeyX, KeyY);

            row.Heads = new Image[6];
            for (int h = 0; h < 6; h++)
                row.Heads[h] = UIKit.AddSprite(root, "Head" + h, null, HeadX + h * HeadStep, HeadY);

            // 官方 XML 把這格叫 roommusic,但實機顯示的是**遊戲模式**(見 Bind)。
            row.Song = Label(root, "Song", SongX, SongY, SongW, SongH, 11f,
                             SongColor, TextAlignmentOptions.MidlineLeft);
            row.Song.overflowMode = TextOverflowModes.Ellipsis;

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
            // 🔴 官方是**一整張** Lobby53(stage.png 0,364,787×169)貼在 (8,435) —— XML 只有這一個 Label。
            //    以前拆成四張(Lobby53..56 各 256 寬)貼在 (5,433) 起,而 Lobby54/55/56 根本是**別的圖**
            //    (各自獨立的 Lobby54.png…,不是背板的續接),整條 bar 因此位置偏了、右段還是拼接的。
            //    bar 位置一偏,照官方座標擺的那排鈕看起來就沒對齊(使用者回報)。
            UIKit.AddSprite(Root, "BottomPanel", An("Lobby53"), 8f, 435f);

            // 聊天顯示區的底框(RecordChatBG,437×130)。
            _chatBgImg = UIKit.AddSprite(Root, "ChatBg", An("RecordChatBG"), ChatBgX, ChatBgY);

            // 聊天記錄。背板已經畫好框了 → ScrollRect 自己不要再上底色。
            _chatScroll = UIKit.AddVerticalScroll(Root, "ChatScroll", out _chatContent, 1f, 2, new Color(0f, 0f, 0f, 0f));
            PlaceTopLeft(_chatScroll.GetComponent<RectTransform>(), ChatX, ChatY, ChatW, ChatH);
            _chatClip = _chatScroll.gameObject.AddComponent<ChatLineClip>();   // 只露整行,不留半截字

            // 聊天記錄的捲軸握把(官方 AllChatList 的 Handle,與另外兩條同一張 Lobby12)。
            // 建在捲動區之後 = 疊在它上面;永遠顯示,沒得捲時停在最上面(同房間列表那條)。
            _chatHandle = UIKit.AddSprite(Root, "ChatScrollHandle", LobbyArt.AnSoloAA("Lobby12"), ChatRailX, ChatRailTop);
            // ScrollRect 自己動(滾輪/拖曳/自動捲到底)時沒人會通知我們 → 這條是唯一即時的來源。
            _chatScroll.onValueChanged.AddListener(_ => PlaceChatHandle());

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

            // 表情(expression):按了拉開 6×4 的表情盤,點一格就把那個表情送進聊天(與房間同一套表情系統)。
            SpriteBtn("Expression", "Lobby102", "Lobby117", "Lobby118", ExprX, ExprY, ToggleExpressionMenu, circle: true);

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
            HideExpressionMenu();
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
            // 重新進大廳(或換過性別/穿搭)→ 轉身角度歸零,回到官方那個朝左 30° 的預設姿。
            _preview.ResetOrbit();

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

            // 🔴 游標要**每幀**更新(閃爍 + 跟著字尾跑),所以擺在下面那個 4 秒節拍的早期返回**之前**。
            UpdateChatCaret();

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

        /// <summary>假房間的座位性別樣式(0=女 1=男)。刻意不規則 —— 真實房間就是誰先進來坐誰的。</summary>
        private static readonly int[] FakeSeatGender = { 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 1, 0, 0, 1 };

        private static readonly string[] FakeSongs =
            { "Butterfly", "Dancing Queen", "極彩色", "Sandstorm", "Bad Apple", "月與蓮", "" };

        private void AddFakeRooms(List<RoomInfo> into)
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
                // 假房間也給性別 —— 不然那排愛心永遠全是粉紅,校不出藍色那顆對不對。
                // 🔴 **不要男女交叉**:真實房間是「第 N 個座位坐的是誰就畫誰的性別」,
                //    交叉排列會讓人以為那排愛心有固定規律。用一個不規則但固定的樣式。
                room.SeatGenders = new int[taken];
                for (int s = 0; s < taken; s++) room.SeatGenders[s] = FakeSeatGender[(i * 6 + s) % FakeSeatGender.Length];
                into.Add(room);
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
            // dev 假房間補在**檢視層**(_view)而不是資料來源(_rooms):
            //   • 線上/離線都看得到 —— 上一版只在離線那條路加,結果連著 server 時整排卡還是空的;
            //   • 不會被下一次輪詢洗掉,也不會混進「快速進入」的挑選(那邊讀的是 _rooms)。
            if (FakeLobbyData) AddFakeRooms(_view);
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
            // 🔴 卡片底的兩張圖是「**常態 vs 滑過**」,不是「空房 vs 有人」——
            //    XML 兩個 Label 疊在同一格:emptyroom0 = Lobby98(**紫**,stage.png 506,0)、
            //    room_chk0 = Lobby28(**粉紅**,506,89)。名字裡的 chk 就是 check/hover 的意思。
            //    以前照字面把「有人」畫成粉紅、「空房」畫成紫,結果整排卡變粉紅色(使用者回報
            //    「官方本來是紫色為什麼做成粉紅色」)。官方是一律紫、滑鼠移上去才變粉紅。
            row.Hover.SetSkin(An("Lobby98"), has ? An("Lobby28") : null);
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

            row.Name.SetText(has ? RoomLabels.DisplayName(r.Name, r.HostName) : "");
            // 🔴 這一格(官方 roommusic,卡片右側)顯示的是**遊戲模式**,不是歌名 ——
            //    官方實機那裡寫的是「自由模式 / 普通模式」。以前放歌名,結果多數房間是空白
            //    (大廳的房間列表封包本來就常常沒帶歌名),整格看起來像壞掉。
            row.Song.text = has ? ModeLabel(r.Mode) : "";

            int count = has ? Mathf.Clamp(r.Count, 0, 9) : 0;
            int cap = has ? Mathf.Clamp(r.Capacity, 0, 9) : 0;
            UIKit.ApplySprite(row.CountD, has ? LobbyArt.Digit("LobbyNum2", count) : null);
            UIKit.ApplySprite(row.CapD, has ? LobbyArt.Digit("LobbyNum2", cap) : null);
            if (row.Slash != null) row.Slash.enabled = has;

            // 密碼房的鑰匙:這個重製版還沒有房間密碼 → 永遠不畫(版位已經留好)。
            if (row.Key != null) row.Key.enabled = false;

            // 🔴 官方那排是**六顆愛心**,不是人頭,而且**六格永遠都畫**,顏色分三種:
            //    女 → FEMALE.AN(粉紅,stage.png 1000,89)、男 → MALE.AN(藍,982,89)、空位 → MAN.AN(灰,1001,105)。
            //    以前只畫有人的那幾格、而且用灰心去畫「有人」—— 三個都錯。
            //    性別由 server 的 roomList 逐座位送過來(見 RoomInfo.SeatGenders);舊版 server 不送 → 退回粉紅。
            var female = An("female");
            var male = An("male");
            var free = An("man");
            for (int h = 0; h < row.Heads.Length; h++)
            {
                Sprite heart = null;
                if (has) heart = h < r.Count ? (SeatIsMale(r, h) ? male : female) : free;
                UIKit.ApplySprite(row.Heads[h], heart);
            }

            UIKit.ApplySprite(row.Keyboard, has ? An("Lobby97") : null);
        }

        /// <summary>
        /// 把握把放到軌道上對應的位置。
        ///
        /// 🔴 **永遠顯示**(使用者要求):以前沒東西可捲時整顆 <c>enabled = false</c>,結果房間少於七間
        /// 就看不到滑桿頭,看起來像忘了做。沒得捲時 t=0 → 停在軌道最上面,那就是官方的樣子。
        /// </summary>
        /// <summary>
        /// 聊天記錄的握把。與房間列表那條**驅動方式不同**:房卡是整數分頁(_scroll / max),
        /// 這裡是真的 ScrollRect,位置要從 <c>verticalNormalizedPosition</c> 換算(1=最上、0=最下,方向相反)。
        /// 內容比視窗短時 Unity 的回傳值不可信 → 自己判,直接停在最上面。
        /// </summary>
        private void PlaceChatHandle()
        {
            if (_chatHandle == null || _chatScroll == null) return;
            float t = 0f;
            var content = _chatScroll.content;
            var viewport = _chatScroll.viewport;
            if (content != null && viewport != null && content.rect.height > viewport.rect.height + 0.5f)
                t = Mathf.Clamp01(1f - _chatScroll.verticalNormalizedPosition);
            _chatHandle.rectTransform.anchoredPosition =
                new Vector2(ChatRailX, -(ChatRailTop + (ChatRailH - HandleH) * t));
        }

        /// <summary>房卡上第 <paramref name="index"/> 顆愛心的主人是不是男生。
        /// 資料缺了(舊版 server / 離線)一律當女生 —— 那是官方唯一那顆彩色心的顏色,退化得最不突兀。</summary>
        private static bool SeatIsMale(RoomInfo r, int index)
        {
            var g = r.SeatGenders;
            return g != null && index >= 0 && index < g.Length && g[index] == 1;
        }

        /// <summary>房卡上那格的模式字。與選歌畫面/房間畫面共用同一組 loc key —— 同一件事在三個地方
        /// 不該有三種講法。(<c>GameMode</c> 只有 Free / Normal 兩個值;ShowTime 是房間裡另外一個開關,
        /// 大廳的房間列表帶不到,所以這裡不分。)</summary>
        private static string ModeLabel(GameMode mode)
            => L(mode == GameMode.Normal ? "songselect.mode_normal" : "songselect.mode_free");

        /// <summary>
        /// 拖握把 → 捲聊天記錄。<paramref name="dy"/> 是滑鼠這一幀的垂直位移(Unity:往上為正)。
        /// 握把往下拖 = 內容往後捲,所以 <c>verticalNormalizedPosition</c>(1=最上、0=最下)要跟著減。
        /// 可跑的軌道長度是 <c>ChatRailH - HandleH</c> —— 用它把「移動幾像素」換成「捲了幾成」。
        /// </summary>
        private void DragChatHandle(float dy)
        {
            if (_chatScroll == null) return;
            float travel = ChatRailH - HandleH;
            if (travel <= 0f) return;
            _chatScroll.verticalNormalizedPosition =
                Mathf.Clamp01(_chatScroll.verticalNormalizedPosition + dy / travel);
            PlaceChatHandle();
        }

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
            HideHallMenu();          // 三個選單互斥
            HideExpressionMenu();
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
                // 同 BuildHallMenu:選單項目要用 AnSolo(尺寸不變),AnSoloAA 會裁掉透明邊讓四條之間裂開。
                System.Func<string, Sprite> res = fromRoom
                    ? (System.Func<string, Sprite>)RoomUiArt.AnSolo
                    : LobbyArt.AnSolo;
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

        // ---- 表情盤(官方 LOBBYPOPMENU.XML 的 expression PopMenu,165×152) ----

        private void ToggleExpressionMenu()
        {
            if (_exprMenu == null) BuildExpressionMenu();
            bool show = !_exprMenu.gameObject.activeSelf;
            HideHallMenu();
            HideChatMenu();
            _exprMenu.gameObject.SetActive(show);
            if (show) RebuildExpressionMenu();   // 換過頁之後再打開要回到當下那一頁的內容
        }

        private void HideExpressionMenu()
        {
            if (_exprMenu != null) _exprMenu.gameObject.SetActive(false);
        }

        /// <summary>
        /// 表情盤的框。官方 XML 給的是 PopMenu 自己的 (0,0) 165×152,實際位置要對齊表情鈕:
        /// 水平置中於鈕(鈕 33 寬 → 中心 458+16.5=474.5 → 選單左緣 474.5−82.5=392)、
        /// 底邊貼著鈕的上緣(566−152=414)。與房間那盤同一個擺法(見 RoomScreen.BuildExpressionMenu)。
        ///
        /// 🔴 素材走 <see cref="RoomUiArt"/> 而不是 LobbyArt:表情盤的圖(EXPRESSIONINFO.PNG)在 ROOMPOPMENU
        ///    那一包,大廳與房間**共用同一份**(表情系統本來就是同一套,連 id 都是同一組)。
        /// </summary>
        private void BuildExpressionMenu()
        {
            _exprMenu = UIKit.NewRect(Root, "expression");
            PlaceTopLeft(_exprMenu, ExprMenuX, ExprMenuY, 165f, 152f);
            _exprMenu.gameObject.SetActive(false);
            RebuildExpressionMenu();
        }

        private void RebuildExpressionMenu()
        {
            if (_exprMenu == null) return;
            UIKit.Clear(_exprMenu);

            UIKit.AddSprite(_exprMenu, "ExpressionInfo", RoomUiArt.ExpressionInfoPage(_exprPage), 0f, 20f);
            UIKit.AddSprite(_exprMenu, "NormalExp", RoomUiArt.ExpressionNormalTab(selected: true), 5f, 3f);

            var left = RoomUiArt.ExpressionPageArrowFrames(left: true);
            var right = RoomUiArt.ExpressionPageArrowFrames(left: false);
            var prev = UIKit.AddSpriteButton(_exprMenu, "preexp", left[0], left[1], left[2], 103f, 131f);
            UiSfx.AttachClick(prev);
            prev.onClick.AddListener(() => StepExpressionPage(-1));
            var next = UIKit.AddSpriteButton(_exprMenu, "nextexp", right[0], right[1], right[2], 146f, 131f);
            UiSfx.AttachClick(next);
            next.onClick.AddListener(() => StepExpressionPage(1));

            int pages = Mathf.Max(1, RoomChatCommand.TotalExpressionPages);
            var pageColor = new Color32(0xBB, 0x20, 0x77, 0xFF);   // XML 的 0xffbb2077
            var cur = UIKit.AddText(_exprMenu, "CurrentPage", Mathf.Clamp(_exprPage + 1, 1, pages).ToString(),
                                    12f, pageColor, TextAlignmentOptions.Center);
            PlaceTopLeft(cur.rectTransform, 118f, 133f, 12f, 12f);
            var sep = UIKit.AddText(_exprMenu, "PageSlash", "/", 12f, pageColor, TextAlignmentOptions.Center);
            PlaceTopLeft(sep.rectTransform, 127f, 133f, 10f, 12f);
            var total = UIKit.AddText(_exprMenu, "TotalPage", pages.ToString(), 12f, pageColor, TextAlignmentOptions.Center);
            PlaceTopLeft(total.rectTransform, 136f, 133f, 12f, 12f);

            // 6×4 的格子。圖是烤在背板上的 → 每一格只放一塊透明的命中區(官方 XML 的 BtExpSel_* 也是 empty.an)。
            for (int slot = 0; slot < RoomChatCommand.ExpressionsPerPage; slot++)
            {
                int id = RoomChatCommand.ExpressionAtMenuSlot(_exprPage, slot);
                if (id <= 0) continue;
                var hit = UIKit.AddImage(_exprMenu, "BtExpSel_" + slot, new Color(1f, 1f, 1f, 0.001f), raycast: true);
                PlaceTopLeft(hit.rectTransform, 4f + (slot % 6) * 26f, 24f + (slot / 6) * 26f, 24f, 24f);
                var b = hit.gameObject.AddComponent<Button>();
                b.targetGraphic = hit;
                b.transition = Selectable.Transition.None;
                UiSfx.AttachClick(b);
                int captured = id;
                b.onClick.AddListener(() =>
                {
                    // 🔴 點表情是**把它的指令字插進打字框**,不是直接送出(使用者要求,與房間一致):
                    //    玩家常常要「表情 + 一句話」一起送,直接送出就沒機會補字了。
                    //    送出時 MockChatService / OnlineChatService 會把那段指令解析回 expressionId
                    //    (見 RoomChatCommand.TryParseExpression),所以走這條路與直接送的結果相同。
                    InsertIntoChatInput(RoomChatCommand.ExpressionDisplayText(captured));
                    HideExpressionMenu();
                });
            }
        }

        /// <summary>把一段文字插到打字框目前游標的位置(沒有 focus 就接在最後),然後把 focus 交回輸入框。</summary>
        private void InsertIntoChatInput(string text)
        {
            if (_chatInput == null || string.IsNullOrEmpty(text)) return;
            string cur = _chatInput.text ?? "";
            int at = Mathf.Clamp(_chatInput.stringPosition, 0, cur.Length);
            // 前面已經有字時補一個空白,免得表情與前一個字黏在一起變成別的指令。
            string sep = at > 0 && !char.IsWhiteSpace(cur[at - 1]) ? " " : "";
            _chatInput.text = cur.Substring(0, at) + sep + text + " " + cur.Substring(at);
            _chatInput.stringPosition = at + sep.Length + text.Length + 1;
            _chatInput.ActivateInputField();
        }

        private void StepExpressionPage(int delta)
        {
            int pages = Mathf.Max(1, RoomChatCommand.TotalExpressionPages);
            _exprPage = (_exprPage + delta) % pages;
            if (_exprPage < 0) _exprPage += pages;
            RebuildExpressionMenu();
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

            // 🔴 **自畫游標**。TMP 內建的 caret 在這個專案的執行期組合(執行期載入的 CJK 字型 + world-space canvas)
            //    算不出可見的寬高,畫不出來 —— 使用者回報「打字時沒有光標」就是它。房間畫面早就踩過同一個坑
            //    (見 RoomScreen 的 _chatCaret),這裡照抄:把內建 caret 設成全透明,自己擺一根白色細長條。
            _chatInput.caretColor = new Color(1f, 1f, 1f, 0f);
            if (_chatInput.textViewport != null)
            {
                _chatCaret = UIKit.AddImage(_chatInput.textViewport, "TypingCaret", Color.white, raycast: false);
                var caretRt = _chatCaret.rectTransform;
                caretRt.anchorMin = caretRt.anchorMax = new Vector2(0f, 0.5f);
                caretRt.pivot = new Vector2(0f, 0.5f);
                caretRt.sizeDelta = new Vector2(2f, 15f);
                caretRt.anchoredPosition = new Vector2(2f, 0f);
                _chatCaret.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// 自畫游標的每幀更新(擺在目前輸入位置、閃爍、順便告訴系統 IME 候選視窗該出現在哪)。
        /// 與 <c>RoomScreen.UpdateChatCaret</c> 同一套,但大廳沒有「頭上泡打字模式」→ 少一組狀態判斷。
        /// </summary>
        private void UpdateChatCaret()
        {
            if (_chatCaret == null || _chatInput == null) return;
            if (!_chatInput.isFocused)
            {
                if (_chatCaret.gameObject.activeSelf) _chatCaret.gameObject.SetActive(false);
                return;
            }

            // 游標擺在「已上屏的字(到 stringPosition 為止)+ IME 組字串」的尾端 —— 往回移或中間刪都跟得上。
            string committed = _chatInput.text ?? "";
            int caretPos = Mathf.Clamp(_chatInput.stringPosition, 0, committed.Length);
            string comp = Input.compositionString ?? "";
            string upTo = committed.Substring(0, caretPos) + comp;
            float w = (_chatInput.textComponent != null && upTo.Length > 0)
                ? _chatInput.textComponent.GetPreferredValues(upTo).x : 0f;
            _chatCaret.rectTransform.anchoredPosition = new Vector2(2f + w, 0f);
            if (!_chatCaret.gameObject.activeSelf) _chatCaret.gameObject.SetActive(true);

            // 閃爍:0.55 秒亮、0.45 秒暗(與房間同拍)。
            bool on = Mathf.Repeat(Time.unscaledTime, 1f) < 0.55f;
            var c = _chatCaret.color; c.a = on ? 1f : 0f; _chatCaret.color = c;

            // 自製輸入框要自己告訴系統「文字游標在螢幕哪裡」,注音/拼音的候選視窗才會跟著游標跑。
            var canvas = _chatCaret.rectTransform.GetComponentInParent<Canvas>();
            Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;
            _chatCaret.rectTransform.GetWorldCorners(_caretCorners);
            Input.compositionCursorPos = RectTransformUtility.WorldToScreenPoint(cam, _caretCorners[0]);
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
                // dev 假玩家線上也要補(同 RefreshRows 的假房間)—— 不然連著 server 時名單只有自己一列,
                // 四個分頁的版位一樣校不了。
                _offlineUsers.Clear();
                _offlineUsers.AddRange(users);
                if (FakeLobbyData) AddFakeUsers();
                _userPanel.SetUsers(_offlineUsers, net.UserId, SelfName(), SelfGuild());
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
            public OutlinedLabel Name;
            public TextMeshProUGUI Song;
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
