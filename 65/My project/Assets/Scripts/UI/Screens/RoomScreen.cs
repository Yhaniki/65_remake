using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.Localization;
using Sdo.Net;
using Sdo.Settings;
using Sdo.UI.Catalog;
using Sdo.UI.Core;
using Sdo.UI.Services;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 開房間的大廳 (waiting room): the real 3D SCNCHIRSROOM scene rendered behind a faithful ROOM (DDRROOM) UI overlay.
    /// The local player's avatar walks the room with the arrow keys (RoomScene3D); six head-portrait slots line the top
    /// (slot 0 = the live 3D head of the local player, the rest show the empty-seat close cover); leave/ready/start/
    /// select-song stay wired to the room service. The 3D scene + head portrait are spawned in OnShow and torn down in
    /// OnHide (this screen never leaves the front-end, so it owns its own 3D lifecycle — it does NOT use the gameplay
    /// teardown path). Layout coords are verbatim from DDRROOM.XML (window resting target + child offset, 800×600 4:3).
    /// </summary>
    public sealed class RoomScreen : UIScreenBase
    {
        public override ScreenId Id => ScreenId.Room;

        // DDRROOM window resting targets; child coordinates are already relative to each window.
        private static readonly Vector2 Win1 = new Vector2(0f, 1f);     // top head panel
        private static readonly Vector2 Win2 = new Vector2(649f, 177f); // right song/scene/mode panel
        private static readonly Vector2 Win3 = new Vector2(0f, 481f);   // bottom chat + ready/start bar
        /// <summary>聊天訊息區的上緣（官方 win4 是 445）。見 <c>BuildRoomChatLog</c>：刻意比官方低 10px，
        /// 讓最後一行貼近下面那條輸入框。</summary>
        private const float ChatLogY = 455f;
        private const int HeadLayer = 11;

        // win2 文字色（取自線上 DDRROOM.XML）：歌名/難度·BPM字幕 0xff835ce1、難度·BPM數字 0xffc969e3、
        // 速度值 0xfffff5a4、模式名 0xff9d6ac9。
        private static readonly Color32 SongNameColor = new Color32(0x83, 0x5c, 0xe1, 0xff);
        private static readonly Color32 InfoValueColor = new Color32(0xc9, 0x69, 0xe3, 0xff);
        private static readonly Color32 SpeedColor = new Color32(0xff, 0xf5, 0xa4, 0xff);
        private static readonly Color32 ModeColor = new Color32(0x9d, 0x6a, 0xc9, 0xff);
        // 自由模式/歌名/難度/BPM 的白色描邊(位移複製,不靠 SDF) 厚度(canvas px)。要更粗就調大。
        private const float Win2EdgePx = 1.1f;

        // 自由模式「難度設置」框裡的 EASY/NORMAL/HARD：官方 FMLvlChoose color="0xfff9f891"(淡黃)。
        private static readonly Color32 FmLevelColor = new Color32(0xf9, 0xf8, 0x91, 0xff);
        // FMdif.an 上「难度设置」那幾個烘死的簡體字所佔的像素範圍(.an 座標,左上原點)——見 EraseFmDifTitle。
        // 逐列量出來的字框是 x 36..88 / y 2..14;這裡各留一點餘裕,但 y 絕不碰 15(那是下面值框的上緣)。
        private const int FmTitleClearX0 = 33, FmTitleClearX1 = 92, FmTitleClearY0 = 2, FmTitleClearY1 = 15;
        private const int FmTitleCleanX = 20;   // 同一列拿來當「乾淨底色」的取樣 x(在框內、字的左邊)

        // note 種類(hit-effect)可選預覽圖，取自線上 DDRROOM.XML 的 hiteft 清單（索引 = GameSession.NoteType；-1=隨機）。
        // 每個 .an 是多幀動畫（如 hiteft2 = jz00..jz07 八幀），預覽框用 SpriteSeqAnim 循環撥放。
        // 只收「實際可選的特效皮」12 項（index 0..11），循環是 隨機 → 0..11 → 回隨機。
        // 排除 XML 上另兩項：free_small（=「自由/無」，隨機格已改用靜態 FREE.PNG）、sixhiteft1（六鍵特效不是獨立資料夾，
        // 是包在各 EFT_N 內的 SIX_*；打六鍵譜時引擎在所選皮裡自動換，不該當成獨立選項）。
        private static readonly string[] NoteEftArt =
        {
            "hiteft2", "hiteft5", "hiteft8", "hiteft9", "hiteft10", "hiteft11",
            "hiteft3",   // EFT_3: hit burst = EFT_7 JZ0x, board = NOTEIMAGE_5, combo = EFT_5 (room DEFAULT). Inserted after hiteft11.
            "hiteft12", "hiteft13", "hiteft14", "hiteftpet", "hiteft3D",
        };
        // note 預覽動畫速度。hiteft2.an=40幀(10幀爆裂×4)：12fps 一輪3.3s(太慢)、60fps 0.67s(太快)；
        // 30fps → 一輪1.33s、單次爆裂0.33s，落在合理區間。要快/慢調這個值即可。
        private const float NoteEftFps = 20f;
        // note 特效預覽的黑底框(烘在 WaitingRoom.png Room72 crop 裡)實測內緣：Win2 局部左上(8,189)、大小 57×48。
        // 特效貼圖多為 53×48 / 54×54(甚至外掛皮可能更大)，比框高 → 底部溢出。用 RectMask2D 容器把貼圖硬裁進這塊。
        private const float NoteBoxX = 8f, NoteBoxY = 189f, NoteBoxW = 57f, NoteBoxH = 48f;
        private const float ChatBubbleLifetime = 10f;
        /// <summary>同一個人頭上最多同時掛幾顆已送出的泡（超過就從最舊的開始收）。
        /// 講得快的時候 4 顆會把還在畫面上的前幾句吃掉，看起來像「訊息不見了」→ 一整串講完都留得住。
        /// 代價是最壞情況 6 人 × 8 = 48 顆（每顆 1 個 GameObject + 3 Image + 1 TMP）—— 泡本來就 10 秒後自己消失，
        /// 六人同時洗頻才會逼近那個數字。</summary>
        private const int MaxBubblesPerOwner = 8;
        private const float ChatBubbleRiseSpeed = 12f;    // px/s；泡持續往上飄，不再卡在固定高度（點5）
        // 泡身垂直中心(畫布 y=56.5)對齊到「肩錨 + 位移」：換 sprite 不跳位、文字上下置中。位移=泡身中心相對肩錨的偏移。
        private const float ChatBubbleAnchorVisibleLeft = 80f;   // 泡身中心相對肩錨的水平位移(右+/左-)；調小/負=更靠名字
        private const float ChatBubbleAnchorVisibleTop = 10f;     // 泡身中心相對肩錨的垂直位移(下+/上-)；調大=更低(往胸)、負=更高(往頭)
        // Official FUN_00460ef0 is gated by a 0x32 ms timer and moves 1/3 of the remaining distance per accepted tick.
        private const float ChatBubbleFollowTicksPerSecond = 20f;
        private const float ChatBubbleFollowStep = 0.33333335f;
        private const float ChatBubbleDragScale = 1f;
        // 泡內字色分性別(女桃紅/男藍)。用 property 現算而非常數：性別可在遊玩中改(商城 ActivateGenderProfile)，
        // 打字泡是建一次重用的 → 每次進打字態要重刷色(見 BeginRoomBubbleTyping)；已送出的泡在 Spawn 當下取色。
        private Color ChatBubbleTextColor => RoomBubbleArt.TextColor(LocalIsMale);
        private bool LocalIsMale => Ctx != null && Ctx.Session != null && Ctx.Session.Gender == 1;

        /// <summary>某一顆泡該用的字色 —— 取**泡的主人**的性別，不是本機的。
        /// 房間裡別人的泡也是這裡生的(<see cref="SpawnSentRoomBubble"/> 帶 ownerUserId)，
        /// 用本機性別的話會變成「我是女生，所以全房的泡都是桃紅」。
        /// 查不到座位(剛離開/旁觀者)就退回女生桃紅＝官方原本唯一的那個顏色。</summary>
        private Color BubbleTextColor(int ownerUserId)
        {
            if (ownerUserId == 0) return ChatBubbleTextColor;
            var snap = Ctx != null && Ctx.Net != null ? Ctx.Net.Room : null;
            var seat = snap != null ? snap.SeatOf(ownerUserId) : null;
            return RoomBubbleArt.TextColor(seat != null && seat.Look != null && seat.Look.Male);
        }
        // 左下訊息欄配色：一般行名字/內容=白；系統行=金黃；密語=#1efefe；進出舞台廣播=#72c1fe；
        // 家族=綠（你沒有家族也用綠；你說＝白，沿用一般行色）。
        // 🔴 值本身搬到 <see cref="ChatPalette"/> 了 —— **大廳的訊息欄要用同一組**(使用者要求兩邊一致),
        //    同一個顏色寫兩份遲早走味。這裡保留這幾個名字,底下所有使用處不必動。
        private const string ChatSystemHex = ChatPalette.SystemHex;
        private const string WhisperHex = ChatPalette.WhisperHex;
        private const string StageHex = ChatPalette.StageHex;
        private const string GuildHex = ChatPalette.GuildHex;   // 家族頻道綠字「<家族>名字: 內容」＋「你沒有家族」
        // 訊息欄底是全透明（文字直接疊在 3D 房間上），小字會跟花背景糊在一起 → 每行描一圈黑邊拉開對比。
        // 動態 CJK 字型畫不出 TMP SDF 描邊，改用 OutlinedLabel 的位移複製法（和房間標題/頭上名字同一套）。
        private const float ChatEdgePx = 0.7f;   // 邊厚（design px）；13px 小字太厚會像粗體 → 0.7 細髮絲邊，字回正常字重又不糊
        private const int ChatEdgeDirs = 4;      // 正十字四向；歷史上限 200 行×每行複製份數，四向兼顧清晰與物件數
        private const float ChatLogW = 360f;     // 訊息欄視窗寬（見 BuildRoomChatLog 的 Place）
        private const int ChatLogPad = 3;        // VerticalLayoutGroup 的四邊內距（見 BuildRoomChatLog 的 AddVerticalScroll）
        /// <summary>一則訊息實際可用的排字寬 —— 折行就是折在這個寬度上（content 寬 − 左右內距）。</summary>
        private const float ChatLineWrapW = ChatLogW - ChatLogPad * 2;
        private const float ChatLineH = 16f;     // 一列的行距
        private const string WhisperLinkId = "w|";   // TMP link id 前綴：<link="w|名字">名字</link> → 點名字密語
        // 行內 emoji（表情 + 字）：emoji 疊在使用者打的位置——前字後留一段固定寬空檔，emoji 疊上去。自己調這幾個像素數就好。
        private const float BubbleEmojiGapPx = 24f;       // <space=…> 在字裡預留的水平空檔
        private const float BubbleEmojiSizePx = 22f;      // emoji 顯示邊長
        private const float BubbleEmojiInlinePadX = 1f;   // emoji 相對前字右緣的水平微調(右+/左-)
        private const float BubbleEmojiInlineOffY = 0f;   // emoji 垂直微調(上+/下-)

        private RawImage _backdrop;
        private RectTransform _bubbleLayer;   // 對話泡容器:夾在 3D 背景與 UI 面板之間 → 泡蓋過房間、被面板擋住
        private CanvasGroup _chatLogGroup;    // 收合時淡出左下訊息欄：win3 只下滑 119px,而訊息欄起點較高(y=445)會露出末幾行
        private readonly RawImage[] _slotHead = new RawImage[RoomLayout.SeatCount];
        private readonly Image[] _slotClose = new Image[RoomLayout.SeatCount];
        private readonly Image[] _slotMaster = new Image[RoomLayout.SeatCount];
        // 準備徽章(官方 charready0..5 / Room66.an)。與 HOST 徽章疊在同一條(y=102)——房主不會有 Ready
        // (NetSeat.Ready 對房主恆 false),所以兩者天生互斥。兩張都是四幀(白/橘/綠/藍)= 隊伍顏色。
        private readonly Image[] _slotReady = new Image[RoomLayout.SeatCount];
        // 名字底下那條名牌(官方 AvatarName0..5 的 background = Team.an)。四幀:第 0 幀是 1×1 的空白
        // (沒選隊 → 不畫),第 1/2/3 幀是 A/B/C 的橘/綠/青藍漸層條。選了隊,名字那一格就整條變成自己那隊的顏色。
        private readonly Image[] _slotPlate = new Image[RoomLayout.SeatCount];
        private Sprite[] _readyFrames, _masterFrames, _plateFrames, _noMapFrames, _playingFrames;
        private readonly TextMeshProUGUI[] _slotName = new TextMeshProUGUI[RoomLayout.SeatCount];
        // 狀態徽章(NO MAP / PLAYING,官方 c06..c09 / d06..d09)。跟 HOST / READY **同一條**(y=102)、
        // 四張共用那個位置一次只畫一張,優先序 PLAYING > NO MAP > HOST > READY —— 見 RenderSeatBadges。
        // 也是四色(黑=自由 / 橘=A / 綠=B / 藍=C),跟著那個人的隊伍換色,與 HOST/READY 同一套幀序。
        private readonly Image[] _slotMissing = new Image[RoomLayout.SeatCount];
        private readonly Image[] _slotPlaying = new Image[RoomLayout.SeatCount];
        // 傳檔跑條(執行期畫的兩個矩形,不烘圖)。
        private readonly Image[] _slotBarTrack = new Image[RoomLayout.SeatCount];
        private readonly Image[] _slotBarFill = new Image[RoomLayout.SeatCount];
        // 六格頭貼的透明命中盒 + 目前彈出的座位選單(一次只會有一個)。
        private readonly Image[] _slotHit = new Image[RoomLayout.SeatCount];
        private GameObject _slotPopup;
        private int _slotPopupFrame = -1;   // 彈出的那一幀不要被「點選單外面就關掉」自己關掉
        private OutlinedLabel _serverLabel, _channelLabel, _roomIdLabel;   // 白字 + 藍邊 (rgb 70,74,152)
        private TextMeshProUGUI _roomNameLabel;
        private OutlinedLabel _songLabel;   // 歌名(白邊)
        private OutlinedLabel _floatName;       // name marker that floats above the avatar in the room (官方頭上名字)；字 rgb(250,252,214) 描黑邊
        private RoomFamilyRow _localFamily;     // 自己頭上的家族列(徽章+家族名)；家族名留空則整條不顯示
        // ---- 頭上名字牌:一個人一層,層與層之間照他站的位置排(與泡同一套規則,見 SortNamePlateLayers)----
        // 名字牌全部畫在 UI 裡 ⇒ 誰蓋誰只由畫的順序決定。不分層的話「本機那面先建、遠端的後建」
        // → 站在最後面的遠端玩家的名字牌永遠蓋住站在最前面的自己(使用者回報的症狀)。
        private RectTransform _nameLayer;   // 名字牌/家族列的容器;夾在房間背景與泡層之間(泡仍畫在名字之上)
        private readonly Dictionary<int, RectTransform> _nameOwnerLayer = new Dictionary<int, RectTransform>();   // 0 = 本機
        private RectTransform _chatContent;
        private ScrollRect _chatScroll;
        private ChatLineClip _chatClip;
        private TMP_InputField _chatInput;
        private Image _chatCaret;   // 自畫閃爍游標(TMP 內建 caret 在執行期 CJK 字型+world-space canvas 下算不出可見寬高)
        private Button _chatModeBtn, _expressionBtn;
        private RectTransform _chatModeMenu, _expressionMenu;
        private TextMeshProUGUI _expressionTipText;
        private RectTransform _expressionTip;
        private ChatChannel _chatChannel = ChatChannel.Current;
        private int _chatScopeRoomId;   // 本房間的作用域房號：只顯示此房 + 密語(跨場)；隔離其他房/大廳訊息
        private int _expressionPage;
        private RectTransform _chatBubbleRoot;
        private Image _chatBubbleFrame, _chatBubbleAdd, _chatBubbleExpression;
        private Image _chatBubbleCaret;   // 泡內游標＝獨立疊圖(非 TMP 字元)，避免改字重算 mesh 造成的怪異閃爍
        private TextMeshProUGUI _chatBubbleText;
        private SpriteSeqAnim _chatBubbleFrameAnim, _chatBubbleAddAnim, _chatBubbleExpressionAnim;
        private bool _chatBubbleDragging, _chatBubbleTyping, _chatBubblePendingShow, _chatBubbleInputArmed;
        private bool _chatInputSticky;   // 左下輸入框「黏住 focus」：送出後不離開，點空曠/退出才放掉（比照 bubble 送完續打）
        private bool _chatDraftWasEmpty; // 上一幀 draft 已空：用它區分「刪最後一字」vs「空了再按 Backspace 退 focus」
        private bool _chatImeComposing;  // 上一幀還在 IME 組字：擋「選字 Enter」誤觸 onSubmit
        private bool _chatBubbleTypingArt; // 目前是固定打字小泡：有字後要換到隨長度變大的 style
        private int _chatBubbleStyle = 1;
        private bool _chatBubbleChainDragging;
        private SentRoomBubble _chatBubbleDraggedSent;
        private bool _chatBubbleDraggingTyping;
        private Vector2 _chatBubblePhysicsPos, _chatBubblePhysicsVel;
        private bool _chatBubbleHasPhysics;
        private Vector2 _chatBubbleDragStartPointer, _chatBubbleDragStartPos;
        private bool _chatBubbleDragPointerCaptured;
        // 已送出的泡：可同時多顆，各自壽命；打字泡仍用上面 _chatBubble*。
        private readonly List<SentRoomBubble> _sentBubbles = new List<SentRoomBubble>();

        // ---- 頭上泡:一個人一層,層與層之間照他站的位置排 ------------------------------------------------
        // 泡整個(代理 + 畫)都在 UI 裡,而 <see cref="_bubbleLayer"/> 夾在房間背景與 UI 面板之間
        // → 泡蓋過整張房間畫面(站在說話者前面的人、家具都擋不住它),但上排的六格頭貼框與其他面板
        // 蓋得住泡(使用者需求)。
        //
        // 「站在前面的人的泡蓋住站在後面的人的泡」則靠下面這層:每個人一個容器,容器之間每幀按
        // 各人沿相機視線的深度重排 sibling(見 SortBubbleOwnerLayers)。同一個人的泡在自己的容器裡
        // 照生成順序疊,與搬進來之前一樣。
        private readonly Dictionary<int, RectTransform> _bubbleOwnerLayer = new Dictionary<int, RectTransform>();
        private readonly Dictionary<int, float> _bubbleDepth = new Dictionary<int, float>();   // 給每幀重排前後用
        private Coroutine _chatInputFocusRoutine;
        private Button _songSelectBtn, _startBtn, _readyBtn, _cancelReadyBtn;
        private Button _spectateBtn, _enterBtn;   // 同一個位置的兩顆:座位上顯示「旁觀」、旁觀中顯示「進入」

        // ---- win2 右側面板控件（模式/場景/歌曲資訊/速度/note/組隊/掉落）----
        private OutlinedLabel _modeLabel;  // 自由模式/普通模式（白邊；線上是純文字，沒有 mode 圖）
        private Image _sceneThumb;         // 第二層場景圖（隨機 → RANDOM；具體 → Scene{id+1}）
        private Image _diffDisc;           // CD 光碟，依難度換色（Difficult.an 3 幀）
        private Sprite[] _diffDiscFrames;
        private Sprite _diffDiscGray;      // 隨機難度用的灰階碟（去色一次快取）：難度隨機 → 不顯示任何一色碟，用灰階當中性
        private OutlinedLabel _levelLabel, _bpmLabel;   // 難度/BPM 數字(白邊)
        private TextMeshProUGUI _speedLabel;
        private Image _noteDisplay;        // note 種類預覽框
        private SpriteSeqAnim _noteAnim;   // 預覽框的循環動畫驅動
        private int _speedIndex;
        private readonly Image[] _teamImg = new Image[4];      // 組隊 A/B/C/自由
        private readonly Sprite[] _teamNormal = new Sprite[4];
        private readonly Sprite[] _teamPushed = new Sprite[4];

        // ---- 自由模式的「難度設置」(官方 FMGameLevel)：跟「房主設置」鈕擺同一格，二選一 ----
        // 房主 → 房主設置(選歌)；自由模式的其他玩家 → 這一格，各自挑自己要打的難度。
        private Image _fmLevelBg;                 // FMdif.an 的框(烘死的簡體標題已抹掉)
        private OutlinedLabel _fmLevelTitle;      // 疊上去的「難度設置」(可翻譯)
        private TextMeshProUGUI _fmLevelValue;    // EASY / NORMAL / HARD
        private Button _fmLevelPrev, _fmLevelNext;

        private RoomScene3D _scene;
        private RoomHeadPortrait _localHead;
        private Camera _maskedCam; private int _savedMask;
        private bool _subscribed;

        // ---- 頭貼框取微調（男女各一組，獨立調整）----------------------------------------------------------------
        //  headAimUp   上下位置：變大 → 頭在框內往「上」    | zoom 遠近：變大 → 變「遠」變小、變小 → 拉近變大
        //  headFrameDist 遠近基準(距離=框高×此值×zoom；框高只由「臉」決定，換髮型不變 → 見 RoomHeadPortrait)
        //  只想微調的話：上下改 *HeadAimUp、遠近改 *Zoom 即可。改完 build 就生效。
        //  女生沿用男生這組（使用者：男生預設的頭大小/位置剛好，女生比照，且不隨髮型變）。
        private const float FemaleHeadAimUp = 0.25f, FemaleHeadZoom = 1f, FemaleHeadFrameDist = 1.9f, FemaleAvatarScale = 1.05f;
        private const float MaleHeadAimUp   = 0.25f, MaleHeadZoom   = 1f, MaleHeadFrameDist   = 1.9f, MaleAvatarScale   = 1.05f;

        // 依性別套用頭貼框取參數（上下位置 / 遠近）。必須在 RoomHeadPortrait.Init 之前呼叫，第一幕就正確。
        private static void ApplyHeadFraming(RoomHeadPortrait head, bool male)
        {
            head.headAimUp     = male ? MaleHeadAimUp     : FemaleHeadAimUp;
            head.zoom          = male ? MaleHeadZoom      : FemaleHeadZoom;
            head.headFrameDist = male ? MaleHeadFrameDist : FemaleHeadFrameDist;
            head.avatarScale   = male ? MaleAvatarScale   : FemaleAvatarScale;
        }

        /// <summary>
        /// 遠端那組頭貼也套**同一組**取景參數 —— 少了這一步,同一個角色的遠端頭貼會比他自己畫面上的
        /// 頭貼高 0.14×框高(≈14% 框高):遠端那邊原本寫死 RoomRemoteHeadSet/RoomHeadPortrait 的
        /// **欄位預設值** aimUp 0.11,而本機這條路是被上面覆寫成 0.25 的。
        ///
        /// 這裡不分性別:上面那兩組常數目前完全相同(「女生沿用男生這組」)。哪天真的要分,
        /// RoomRemoteHeadSet 就得改成 per-slot 參數(它一台相機輪拍男女混合的六個人)。
        /// </summary>
        private static void ApplyHeadFraming(RoomRemoteHeadSet heads)
        {
            if (heads == null) return;
            heads.aimUp = MaleHeadAimUp;
            heads.zoom = MaleHeadZoom;
            heads.frameDist = MaleHeadFrameDist;
            heads.fitHairTop = false;   // 與 RoomHeadPortrait.fitHairTop 的預設一致(兩邊都不理頭髮)
        }

        // ---- 徽章條與傳檔跑條的版位 ----
        // HOST / READY / NO MAP / PLAYING 四張都畫在頭貼下緣那一條(官方 master0..5 / charready0..5 的 y=102)。
        // 四張互斥(RenderSeatBadges 一次只開一張)→ 同一條不會疊,而且不再蓋住頭貼的臉(舊版狀態徽章在 y=62)。
        private const float BadgeY = 102f;
        // 四張共用**同一個顯示矩形** = 官方 HOST/READY 的 100×30。PLAYING 的圖(d06..d09)只有 100×27,
        // 這裡是**拉伸**(非等比)填滿到 30 高,不是照原尺寸畫 —— 使用者要四張的高寬完全一樣,
        // 寧可那張字被拉高 3px(約 11%),也不要一張比另三張矮一截。
        private const float BadgeW = 100f, BadgeH = 30f;
        // 跑條夾在頭貼下緣(132)與名牌(141)之間那條縫;寬度=名牌寬(跟名牌對齊成一組,見 RoomLayout.TransferBarX)。
        private const float BarY = 134f, BarH = 4f;
        private const float BarW = RoomLayout.NamePlateW;
        // 上傳/下載用顏色區分(使用者要求不要字):上傳偏藍、下載偏綠。
        // 藍色取自官方房主徽章第四幀 b09 的外框(0,53,165)那一系 —— 同一套素材的藍。
        private static readonly Color BarUpColor = new Color(0.42f, 0.66f, 1f, 1f);
        private static readonly Color BarDownColor = new Color(0.45f, 0.92f, 0.55f, 1f);

        // ---- win 容器（收合用）：win1/win2/win3 的所有元件各掛在自己的容器下，收合就整組滑出畫面（官方 uihide/uidisplay）。
        //      每個容器都是「錨定左上、原點、800×600」的全畫布 rect → 子元件座標仍用絕對(win.x+x) 不變，收合只動容器 anchoredPosition。
        private RectTransform _win1Root, _win2Root, _win3Root;
        private Button _uiHideBtn, _uiShowBtn;   // 左上收合(◄ BtnMaypopLeft) / 展開(► BtnMaypopRight) 切換鈕（同一位置 11,83）
        private bool _uiCollapsed;
        private float _collapseT;                // 0=完全展開 .. 1=完全收合（Update 內平滑補間）
        private SdoComboBox _dropCombo;          // 掉落方式下拉；收合時要主動關掉它的清單(否則清單跟著容器滑走)

        // 開始 → 全螢幕 1 秒漸暗再切舞台：最上層黑幕(平時停用/透明)，OnStart 觸發後淡入到全黑才交棒給 ScreenGameplay。
        private Image _startFade;
        private bool _starting;                  // 進入漸暗切場後鎖住，避免重複觸發
        private bool _returnedFromStage;         // true = 這次 OnShow 是從舞台遊戲回房(非從大廳進來) → 不重播進場廣播
        private const float StartFadeDuration = 1f;

        // 收合位移（anchoredPosition delta，逐字取自 DDRROOM.XML 各 Window 的 show→hide TransForm 目標）：
        // win1 頂部往上滑出(targety 1→-200)、win2 右側往右滑出(targetx 649→900)、win3 底部往下滑出(targety 481→600)。
        private static readonly Vector2 Win1Hidden = new Vector2(0f, 201f);    // 上（Unity y-up：+y = 往上）
        private static readonly Vector2 Win2Hidden = new Vector2(251f, 0f);    // 右
        private static readonly Vector2 Win3Hidden = new Vector2(0f, -119f);   // 下
        private const float CollapseSpeed = 3.2f;   // 收合/展開速度（1/speed ≈ 0.31s 完成一次滑動）

        // 掉落方式下拉清單（綠底）的文字色，取自線上 DDRROOM.XML chose_list color=0xff308769。
        private static readonly Color32 DropListColor = new Color32(0x30, 0x87, 0x69, 0xff);

        /// <summary>If the room renders upside-down on a given platform, flip the backdrop V (RT vertical convention).</summary>
        public bool flipBackdropV = false;
        // Head-slot placement: tune via the F2 panel; borders can show all six slots.
        // (the RT frames head+shoulder, so the face sits high in the slot).
        public Vector2 headSlotOffset = new Vector2(-10f, 6f);  // dialed in via the F2 panel: centres the head in the frame
        public Vector2 headSlotSize = new Vector2(99f, 76f);    // box (X-10/Y+6 from the AvatarView base, 99×76)

        /// <summary>空位上的「close」禁止圖標(🚫)。預設關閉(離線單人房乾淨呈現,只有本機 host);真連線要顯示關閉座位再開。</summary>
        public bool showEmptySeatCovers = false;
        private bool _debugOpen;            // F2: head-slot tuning panel (all 6 heads + borders + sliders)
        private static Texture2D _dbgPx;    // 1px texture for the debug borders

        private static string L(string k) => LocalizationManager.Get(k);

        /// <summary>
        /// 「這個操作為什麼沒成功」——**只寫 log,不彈 toast**。
        ///
        /// 這些原本都是畫面上方的浮動訊息,但它們幾乎全是「按了但條件不符」的例行拒絕
        /// (沒選歌、還有人沒準備、正在局裡不能旁觀…),而畫面本身已經表達了狀態:
        /// 沒選歌時歌名欄是空的、沒準備的人頭上沒有準備標記。一直跳訊息只是把畫面弄髒。
        /// 需要追原因時看 log(而且 log 印的是同一句本地化文字,不必再對照 key)。
        /// </summary>
        private static void Notice(string key)
            => Debug.Log("[room] " + L(key));

        protected override void BuildUI()
        {
            // 1) full-screen 3D-room backdrop (behind everything; texture wired in OnShow)
            var bgRt = UIKit.NewRect(Root, "RoomBackdrop");
            UIKit.Stretch(bgRt);
            _backdrop = bgRt.gameObject.AddComponent<RawImage>();
            _backdrop.color = Color.black;
            _backdrop.raycastTarget = false;
            if (flipBackdropV) _backdrop.uvRect = new Rect(0f, 1f, 1f, -1f);

            // 房間畫面的右鍵收訊面板 —— 右鍵**房裡的那個人**(3D 角色本體)也要出選單,不是只有上排那六格頭貼。
            // 🔴 建在這裡(backdrop 的下一個兄弟)= 整張 UI 最底層:名字牌、泡、win1/2/3 面板、選單本身
            //    全都是後面的兄弟,UGUI 的射線一律先打到最上面那個 → 這塊只收「什麼 UI 都沒有的地方」的點擊,
            //    不會搶走任何既有的互動。
            // 🔴 backdrop 自己不能兼任(它 raycastTarget=false 而且**整個畫面只有它鋪滿**):把它打開會讓
            //    3D 房間吃掉所有點擊,連帶影響 UI 之外的既有行為;分一塊透明面板出來只多一個 Graphic。
            var pick = UIKit.AddImage(Root, "RoomPickCatcher", new Color(0f, 0f, 0f, 0f), raycast: true);
            UIKit.Stretch(pick.rectTransform);
            pick.gameObject.AddComponent<PointerClickProxy>().Clicked = OnRoomPickClick;

            // 名字牌層。建在**泡層之前** → 泡永遠畫在名字之上(見下面 _bubbleLayer 的註解),
            // 建在**所有 UI 面板之前** → 名字牌被面板擋住。層內一個人一層,每幀按站位重排(SortNamePlateLayers)。
            // 🔴 不要對它呼叫 SetAsLastSibling —— 那就是在改上面兩條規則。
            _nameLayer = UIKit.NewRect(Root, "RoomNamePlateLayer");
            UIKit.Stretch(_nameLayer);

            // name marker that floats above the avatar's head in the room (positioned each frame in Update).
            // 跟遊戲內頭頂名字同款:共用色 TextStyles.FaceCream(rgb 250,252,214)+ 黑邊 + 粗體 + 8 向描邊。
            // trackEm = 字靠緊一點（真・字距，字不變形），跟遊戲內頭頂名字同一個值；實際收多少由 OutlinedLabel
            // 每次 SetText 依字串重算（固定收緊會把 SimSun 半形西文的 "TA" 黏成一塊 → 見 TextTracking）。
            // 本機的名字/家族列住 owner 0 那一層 —— 這樣它才跟遠端的名字牌一起參與「照站位排前後」。
            var localNameLayer = NamePlateOwnerLayer(0);
            _floatName = OutlinedLabel.Create(localNameLayer, "FloatName", 0, 0, 160, 20, 14, TextStyles.FaceCream, Color.black, HeadNameEdgePx, true,
                trackEm: TextStyles.HeadNameTrackEm);
            _floatName.gameObject.SetActive(false);

            // 家族列：家族名稱(白字描黑邊) + 名稱前的小徽章(EMBLEM/SMALL*)，畫在頭上名字的「上方」一行。
            // 內容與顯不顯示由這個角色的 profile.json 決定(沒設過才吃 config.ini 的預設，見 UpdateFamilyRow)，位置每幀跟著頭擺(PlaceFamilyRow)。
            // 版面與排版數學在 RoomFamilyRow —— **遠端玩家頭上那條走同一份**(見 SyncRemoteNamePlates)。
            _localFamily = RoomFamilyRow.Create(localNameLayer, "");

            // 對話泡層。位置(sibling index)**就是**泡的前後規則本身,所以這兩件事都靠它:
            //   • 建在**頭上名字/家族列那一層(_nameLayer)之後** → 泡永遠畫在名字之上(自己說話時泡不會被自己的名字擋住);
            //   • 建在**所有 UI 面板之前** → 上排的六格頭貼框與其他面板蓋得住泡(使用者需求)。
            // 泡因此蓋過整張房間畫面(站在說話者前面的人、家具都擋不住它),但不會浮到 UI 上面。
            // 🔴 不要把它搬到別處建、也不要對它呼叫 SetAsLastSibling —— 那就是在改上面兩條規則。
            // 打字泡與已送出的泡都掛這底下(一個人一層,見 BubbleOwnerLayer)。容器本身不擋點擊。
            _bubbleLayer = UIKit.NewRect(Root, "RoomChatBubbleLayer");
            UIKit.Stretch(_bubbleLayer);

            // window containers — everything in win1/win2/win3 hangs under one of these so the collapse button can slide
            // each panel off-screen as a single unit (官方 uihide/uidisplay). Each is a full-canvas rect anchored top-left
            // at the origin, so child coords stay absolute (win.x+x) and unchanged; only the container moves on collapse.
            _win1Root = MakeWinContainer("Win1Root");
            _win2Root = MakeWinContainer("Win2Root");
            _win3Root = MakeWinContainer("Win3Root");

            // 2) win1 — top head panel frame + 6 head slots + name plates + head-bar buttons + room/mode labels
            Art("WaitingRoomHead", Win1, 0, 0, "Win1Head");
            Art("Room65", Win1, 37, 47, "Win1HeadPanel");

            float[] sx = RoomLayout.HeadSlotX;
            // close-cover coords (DDRROOM close0..5) + name-plate coords (AvatarName0..5) + master badge (master0..5)
            float[] closeX = { 68, 188, 309, 431, 556, 678 };
            float[] nameX = RoomLayout.NamePlateX;
            float[] masterX = { 54, 176, 298, 421, 544, 666 };
            float[] readyX = { 53, 175, 298, 419, 542, 665 };   // DDRROOM charready0..5（與 master 差 1px，照官方）

            // HOST / READY 兩張徽章各四幀（白=自由 / 橘=A / 綠=B / 藍=C）——選了不同隊的人，頭貼上的字就是自己那隊的顏色。
            _masterFrames = RoomUiArt.AnFrames("master");   // b06..b09
            _readyFrames = RoomUiArt.AnFrames("Room66");    // a06..a09
            // NO MAP / PLAYING 是同一套素材的下兩組編號（c06..c09 / d06..d09），幀序與配色跟 a/b 完全一樣，
            // 只有第 0 幀是黑（a/b 是白）。官方沒把這兩組包成 .an（資料夾裡只有裸 PNG）→ 逐張讀，幀序由檔名保證。
            _noMapFrames = StateBadgeFrames("C");
            _playingFrames = StateBadgeFrames("D");
            // 名字底下那條名牌也是四幀，但第 0 幀是 1×1 空白 —— 官方用「畫一張看不見的圖」表示沒選隊。
            _plateFrames = RoomUiArt.AnFrames("Team");      // 空白 / 橘 / 綠 / 青藍
            for (int i = 0; i < RoomLayout.SeatCount; i++)
            {
                _slotHead[i] = AddRaw("Slot" + i, sx[i] + Win1.x, RoomLayout.HeadSlotY, RoomLayout.HeadSlotW, RoomLayout.HeadSlotH);
                _slotHead[i].enabled = false;   // shown only when occupied (head RT assigned)
                _slotClose[i] = Art("close", Win1, closeX[i], 59, "Close" + i);
                _slotMaster[i] = UIKit.AddSprite(_win1Root, "Master" + i, Frame(_masterFrames, 0),
                                                 Win1.x + masterX[i], Win1.y + BadgeY);
                _slotMaster[i].enabled = false;
                _slotReady[i] = UIKit.AddSprite(_win1Root, "Ready" + i, Frame(_readyFrames, 0),
                                                Win1.x + readyX[i], Win1.y + BadgeY);
                _slotReady[i].enabled = false;
                // 名牌先建、名字後建 → UGUI 的 sibling 順序讓白字畫在彩色名牌**上面**。
                _slotPlate[i] = UIKit.AddSprite(_win1Root, "NamePlate" + i, Frame(_plateFrames, 1),
                                                Win1.x + nameX[i], Win1.y + 141);
                _slotPlate[i].enabled = false;
                _slotName[i] = UIKit.AddText(_win1Root, "Name" + i, "", 13, Color.white, TextAlignmentOptions.Center);
                // 🔴 欄寬要用官方 AvatarName 的 108(不是頭貼格的 96),y 也要加上 Win1.y ——
                //    名字是**置中**排版,量錯寬度/少加一格就會偏:96 寬時字的中心在 x+48、名牌條中心在 x+53.5,
                //    在名牌沒畫出來的年代看不出來,現在選了隊、彩色條一畫上去,白字就明顯偏左又高 1px。
                Place(_slotName[i].rectTransform, nameX[i] + Win1.x, Win1.y + 141, RoomLayout.NamePlateW, 18);
                _slotName[i].gameObject.SetActive(false);

                // 狀態徽章:與 HOST / READY **同一條**、同一個 x(readyX)—— 四張互斥,由 RenderSeatBadges
                // 挑要畫哪一張,所以「房主在場中」不會兩張疊在一起,也不用再蓋住頭貼的臉。
                _slotMissing[i] = UIKit.AddSprite(_win1Root, "Missing" + i, Frame(_noMapFrames, 0),
                                                  Win1.x + readyX[i], Win1.y + BadgeY);
                _slotMissing[i].enabled = false;
                _slotPlaying[i] = UIKit.AddSprite(_win1Root, "Playing" + i, Frame(_playingFrames, 0),
                                                  Win1.x + readyX[i], Win1.y + BadgeY);
                _slotPlaying[i].enabled = false;
                // 四張一律撐成 BadgeW×BadgeH(AddSprite 依 sprite 原生尺寸給的 sizeDelta 在這裡被蓋掉)。
                StretchToBadgeRow(_slotMissing[i]);
                StretchToBadgeRow(_slotPlaying[i]);
                StretchToBadgeRow(_slotMaster[i]);
                StretchToBadgeRow(_slotReady[i]);

                // 上傳/下載的跑條:頭貼下緣與名牌之間那條縫(y=134..138)。
                // 刻意不烘圖也不寫百分比 —— 使用者要的就是一條會跑的條。
                // 🔴 x/寬要對**名牌**算(RoomLayout.TransferBarX),不是頭貼格:名牌比頭貼格寬 12px、
                //    左緣又往左 11px,拿 HeadSlotX/HeadSlotW 擺的話條的中心會比名牌右 5px → 看起來歪一邊。
                _slotBarTrack[i] = UIKit.AddImage(_win1Root, "Bar" + i, new Color(0f, 0f, 0f, 0.55f));
                Place(_slotBarTrack[i].rectTransform,
                      RoomLayout.TransferBarX(i, BarW) + Win1.x, BarY, BarW, BarH);
                _slotBarFill[i] = UIKit.AddImage(_slotBarTrack[i].rectTransform, "Fill", Color.white);
                var fr = _slotBarFill[i].rectTransform;
                fr.anchorMin = new Vector2(0f, 0f);
                fr.anchorMax = new Vector2(0f, 1f);      // 高度跟著 track,寬度由 sizeDelta.x 控制
                fr.pivot = new Vector2(0f, 0.5f);
                fr.anchoredPosition = Vector2.zero;
                fr.sizeDelta = new Vector2(0f, 0f);
                _slotBarTrack[i].enabled = false;
                _slotBarFill[i].enabled = false;

                // 透明命中盒:座位的右鍵選單與雙擊鎖格都靠它收滑鼠。
                // 為什麼不掛在 _slotHead 上:那張 RawImage 是 raycastTarget=false,而且**空位時 enabled=false**
                // → 收不到任何點擊,而「關閉一個空位」正是最需要點空位的操作。
                _slotHit[i] = UIKit.AddImage(_win1Root, "SlotHit" + i, new Color(0f, 0f, 0f, 0f), raycast: true);
                _slotHit[i].rectTransform.anchorMin = _slotHit[i].rectTransform.anchorMax = new Vector2(0f, 1f);
                _slotHit[i].rectTransform.pivot = new Vector2(0f, 1f);   // 同 AddRaw:左上為原點,y 往下為負
                var hitProxy = _slotHit[i].gameObject.AddComponent<PointerClickProxy>();
                int seatIndex = i;
                hitProxy.Clicked = ev => OnSlotPointerClick(seatIndex, ev);
            }

            // head-bar buttons (win1)
            // 修改(房間設定)按鈕：官方按了會跳一條半透明黑底橫幅(Toast) → 依需求拿掉，按了不做事。
            // 右上角 head-bar 圓形圖示鈕(天使/交易/邀請/設定/返回)是 34px CommonButtonNew 圓盤,盤緣是「寬軟 AA 邊」→
            // 走 circle:true(CircleMask 平滑圓邊 + 超取樣),否則 AnSoloAA 的 α<128→0 硬裁會把軟邊裁成 1-bit 圓 → 邊緣破碎。
            Btn("changeroomname", "Room45", "Room46", "Room47", Win1, 461, 7, null);                                 // 修改鈕:方框(WaitingRoom),非圓
            Btn("help", "BtnHeadHelp_1", "BtnHeadHelp_2", "BtnHeadHelp_3", Win1, 654, 7, null);                      // help crop 是空的(透明) → 不套圓
            Btn("roomangel", "roomangel_0", "roomangel_1", "roomangel_2", Win1, 616, 5, null, circle: true);
            Btn("roomexchange", "BtnHeadExchange_1", "BtnHeadExchange_2", "BtnHeadExchange_3", Win1, 652, 5, null, circle: true);   // 官方是交易鈕;重製沒有交易 → 按了不做事
            Btn("invite", "BtnHeadInvite_1", "BtnHeadInvite_2", "BtnHeadInvite_3", Win1, 688, 5, null, circle: true);
            Btn("setting", "BtnHeadOption_1", "BtnHeadOption_2", "BtnHeadOption_3", Win1, 724, 5, () => Nav.OpenSettings?.Invoke(), circle: true);
            Btn("leaveroom", "BtnHeadReturn_1", "BtnHeadReturn_2", "BtnHeadReturn_3", Win1, 760, 5, OnLeave, circle: true);

            // 左上角所在位置：自由練習場 / 頻道 / 房號 (DDRROOM servername/channelnum/roomid) — 白字 + 藍邊(70,74,152) 粗體。
            // 藍邊用 OutlinedLabel(位移複製)畫，不用 TMP SDF 材質描邊(那條在執行期動態 CJK 字型上畫不出來)。
            // 三欄都左對齊;初始 x 不重要，Render() 會量實際字寬後左到右排版(ServerX 起、欄間 HeaderGap)。
            // 欄寬給足(左對齊、透明容器):太窄會讓長字串(英文 Free Practice 1)自動換行成兩列 → 溢出紫框。寬一點只是留右側空白，不影響左緣定位。
            const float align_y = 11f, align_h = 18f;
            _serverLabel  = OutlinedLabel.Create(_win1Root, "ServerName", ServerX, align_y, 160, align_h, HeaderFontSz, Color.white, LeftEdge, HeaderEdgePx, true, TextAlignmentOptions.Left);
            _channelLabel = OutlinedLabel.Create(_win1Root, "ChannelNum", ServerX, align_y, 110, align_h, HeaderFontSz, Color.white, LeftEdge, HeaderEdgePx, true, TextAlignmentOptions.Left);
            _roomIdLabel  = OutlinedLabel.Create(_win1Root, "RoomId", ServerX, align_y, 60, align_h, HeaderFontSz, Color.white, LeftEdge, HeaderEdgePx, true, TextAlignmentOptions.Left);
            // 中央房名 (DDRROOM roomname) — 粗體白字(無描邊)，文字內容由 RoomLabels.DisplayName 決定。
            _roomNameLabel = UIKit.AddText(_win1Root, "RoomName", "", 12, Color.white, TextAlignmentOptions.Center);
            _roomNameLabel.fontStyle = FontStyles.Bold;
            Place(_roomNameLabel.rectTransform, 239 + Win1.x, 10, 188, 18);   // 舞蹈室房名往下 2px

            // 3) win2 — 右側「模式/場景/歌曲資訊/速度/note/組隊/掉落」面板。座標逐字取自線上 DDRROOM.XML，
            //    直接相對 Win2(649,177)。Room72 面板框(140×343)已把 SPEED/組隊/掉落方式 等字烘進去，程式只擺值/控件。
            Art("Room72", Win2, -3, -5, "Win2Panel");                       // 面板底框

            // 模式標題（自由模式/普通模式）：線上是純文字(無 mode 圖)，畫在頂端黃條；取代官方的問號佔位。白色描邊。
            _modeLabel = OutlinedLabel.Create(_win2Root, "ModeLabel", Win2.x + 8, Win2.y - 4, 120, 40, 14, ModeColor, Color.white, Win2EdgePx, true, glyphScaleX: 0.9f);

            // 場景縮圖（對應選歌選到的場景；預設 RANDOM）。實際圖在 RenderWin2 依 session 換。
            _sceneThumb = Art("randomscene", Win2, 7, 28, "SceneThumb");

            // 歌曲資訊 —— CD 光碟(依難度換色) + 難度字幕/數字 + BPM字幕/數字 + 歌名。
            _diffDiscFrames = RoomUiArt.AnFrames("Difficult");              // 3 幀：easy/normal/hard
            _diffDisc = Art("Difficult", Win2, 7, 109, "DiffDisc");
            MakeCaption("CapLevel", "難度", 32, 112);                       // 線上框沒烘難度/BPM字 → 自己畫
            MakeCaption("CapBpm", "BPM", 78, 112);
            _levelLabel = MakeInfoNum("SongLevel", 55, 112);
            _bpmLabel = MakeInfoNum("SongBpm", 101, 112);
            _songLabel = OutlinedLabel.Create(_win2Root, "SongName", Win2.x + 12, Win2.y + 128, 112, 20, 12, SongNameColor, Color.white, Win2EdgePx, true);

            // 速度 ◄ 值 ►（檔位清單與預設來自 config.ini，可改）
            _speedLabel = UIKit.AddText(_win2Root, "SpeedValue", "", 13, SpeedColor, TextAlignmentOptions.Center);
            _speedLabel.fontStyle = FontStyles.Bold;
            PlaceW2(_speedLabel.rectTransform, 86, 167, 19, 14);
            Btn("songpre", "BtnOraSmallLeftArrow_1", "BtnOraSmallLeftArrow_2", "BtnOraSmallLeftArrow_3", Win2, 66, 167, () => StepSpeed(-1), hoverSfx: null);
            Btn("songnext", "BtnOraSmallRightArrow_1", "BtnOraSmallRightArrow_2", "BtnOraSmallRightArrow_3", Win2, 109, 167, () => StepSpeed(1), hoverSfx: null);

            // note 種類（hit-effect）預覽框 + ◄ ►（預設 random）。hiteft.an 是多幀動畫(hiteft2=40幀) → 用 SpriteSeqAnim 循環撥放。
            // 貼圖(53×48/54×54/外掛皮可能更大)以 RectMask2D 容器硬裁進黑框 NoteBox，並在框內置中(焦點=貼圖中心) →
            // 保證預覽不溢出框底。ApplySprite 只改 sizeDelta(不動錨/pivot)，所以置中設定一次即永遠成立。
            var noteClip = NewClip("NoteClip", NoteBoxX, NoteBoxY, NoteBoxW, NoteBoxH);
            _noteDisplay = UIKit.AddImage(noteClip, "NoteDisplay", Color.white);
            var noteRt = _noteDisplay.rectTransform;
            noteRt.anchorMin = noteRt.anchorMax = noteRt.pivot = new Vector2(0.5f, 0.5f);
            noteRt.anchoredPosition = Vector2.zero;
            UIKit.ApplySprite(_noteDisplay, RoomUiArt.An("hiteft2"));   // 初始一幀；RenderWin2 隨即依 session 換
            _noteAnim = _noteDisplay.gameObject.AddComponent<SpriteSeqAnim>();
            _noteAnim.Fps = NoteEftFps;
            Btn("eftpre", "BtnOraLeftArrow_1", "BtnOraLeftArrow_2", "BtnOraLeftArrow_3", Win2, 8, 242, () => StepNote(-1), hoverSfx: null);
            Btn("eftnext", "BtnOraRightArrow_1", "BtnOraRightArrow_2", "BtnOraRightArrow_3", Win2, 36, 242, () => StepNote(1), hoverSfx: null);

            // 組隊 A / B / C / 自由（單選；預設自由）
            BuildTeamToggle(0, "Room33", "Room35", 69, 207);
            BuildTeamToggle(1, "Room36", "Room38", 96, 206);
            BuildTeamToggle(2, "Room39", "Room41", 69, 233);
            BuildTeamToggle(3, "Room42", "Room44", 96, 233);

            // 掉落方式 向上/向下/傾斜 —— 官方 win2 是「CurChose 值(55,266,黃) + chose ▼ 鈕(108,266,ShopDlg13/14/15) +
            // chose_list 綠色下拉清單(向下展開)」。「掉落方式」四字烘在 Room72 框上，這裡只放值+▼+清單。用 SdoComboBox（跟
            // 選歌面板旁觀人數下拉同一套），但清單改成向下展開(expandDown)、換上房間的綠底列圖(LabUnCheck/LabCheck)。
            // 座標同其他 win2 元件用「絕對畫布」= Win2 視窗原點 + 相對(線上 DDRROOM.XML: CurChose 55,266 / chose 108,266)。
            // 位置調整旋鈕（全部是「絕對畫布」= Win2 視窗原點 649,177 + 相對值）：
            //   值(向上/向下)：slotX = Win2.x+55、slotY = Win2.y+266、slotW = 70（值框寬，文字置中）
            //   ▼ 鈕左緣：arrowX = Win2.x+108
            //   綠色下拉清單：左緣 = listX（改這個 → 清單左右移動）、寬 = listWidth（改這個 → 清單變寬/窄）
            //     右緣 = listX + listWidth。目前 listX=Win2.x+55、listWidth=43 → 55..98。
            _dropCombo = SdoComboBox.Create(_win2Root, "DropDir", Win2.x + 50, Win2.y + 268, 75, 16, Win2.x + 105,
                // ▼ 鈕改走 premult：AnSolo 的 AlphaBleed(只補 α≤8)＋DeMatteWhite(純白 un-composite 還是白)都碰不到它外緣
                // 那圈 α≈5~30 的白 matte，放大後就是使用者看到的方形白邊。清單列(LabUnCheck/LabCheck)實測沒有白 matte，
                // 留在既有的 AnSolo 路徑；兩者是不同的 Image，材質各自獨立，不會互相影響。
                RoomUiArt.AnPremult("ShopDlg13"), RoomUiArt.AnSolo("LabUnCheck"), RoomUiArt.AnSolo("LabCheck"),
                // 清單由上而下＝向上 / 向下；傾斜沒實作所以不上架（NotePanelLayout.MenuValues 是那張列序↔值對照表，
                // 舊 config.ini 存著 2＝傾斜的話 MenuRow 會退回「向上」那一列）。
                // 選項字傳 key(不是解好的字串):這塊只建一次,中途換語言才跟得上(見 SdoComboBox.Relocalize)。
                null, null,
                NotePanelLayout.MenuRow(Ctx.Session.DropDirection), SpeedColor, DropListColor,
                row =>
                {
                    int v = (int)NotePanelLayout.FromMenuRow(row);
                    Ctx.Session.DropDirection = v; RoomConfig.defaultDropDirection = v; RoomConfig.Save();   // 持久化：掉落方式寫回 config.ini（進遊戲決定 note 面板上/下）
                },
                expandDown: true, listX: Win2.x + 70, listWidth: 38f,
                valueOffsetY: 2f,    // 只把「向上/向下」值往上 2px，▼ 鈕位置不動
                optionKeys: new[] { "room.drop_up", "room.drop_down" });
            // 掉落方式 ▼ 開關鈕按下 → SE_0001（清單列本來就有；此為開關鈕本身。中間設定塊仍不掛滑過音）。
            UiSfx.AttachClick(_dropCombo.GetComponent<Button>());

            // 房主設置（= 選歌入口）。線上原版 BtnRoomMaster_1/2/3。按下音效改 Buttonfloat（非預設 SE_0001）。
            _songSelectBtn = Btn("songselect", "BtnRoomMaster_1", "BtnRoomMaster_2", "BtnRoomMaster_3", Win2, 14, 296, () => GoTo(ScreenId.SongSelect), UiSfx.ButtonFloat);

            BuildFreeModeLevel();

            // 註：官方 WinMoveUpHelp(moveuphelp0.an) 其實是一張「黃底問號」的方向鍵提示圖，靜態擺在面板左上角就變成
            // 使用者看到的那顆問號 → 依需求移除（要做方向鍵提示應改成floating動畫貼在 3D 場景，不放面板裡）。

            // 4) win3 — bottom chat bar:官方 DDRROOM win3 一整排功能鈕(座標/圖名逐字取自 XML),目前都是裝飾(onClick=null)。
            Art("Room0", Win3, 8, 37, "Win3Panel");
            BuildRoomChatLog();
            _chatModeBtn = Btn("chatmode", "Room4", "Room5", "Room6", Win3, 17, 88, ToggleChatModeMenu);      // 聊天模式
            UpdateChatModeButton();
            var chatEdit = Art("EditBlank", Win3, 72, 92, "ChatEdit");   // 聊天輸入框(無 EditBlank 圖 → 透明佔位)
            if (chatEdit != null) chatEdit.color = new Color(1f, 1f, 1f, 0f);
            _chatInput = UIKit.AddInputField(_win3Root, "ChatEditInput", "", 12);
            Place(_chatInput.GetComponent<RectTransform>(), Win3.x + 72, Win3.y + 88, 193, 24);
            ConfigureRoomChatInput();
            // 直接點左下輸入框 → 取消頭上藍泡、改在輸入框打字（顯示光標+IME）。實體點擊才觸發，程式聚焦(bubble 模式)不觸發。
            _chatInput.gameObject.AddComponent<RoomChatInputClickHandle>().Owner = this;
            // 自畫閃爍游標：擺在輸入框文字區(textViewport)裡，跟著文字尾端移動。TMP 內建 caret 這裡畫不出來(見 _chatCaret 註)。
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
            if (_chatInput.targetGraphic is Image chatInputBg)
                chatInputBg.color = new Color(0f, 0f, 0f, 0f);
            // 下排功能鈕(泡泡/表情/喇叭/大聲公/寵物/翅膀/衣櫥/手環/信件)都是 31~33px 的紫色圓盤,盤緣是軟 AA 邊
            // (半徑剖面 α 237→138→10):跟右上 head-bar 同類 → circle:true(CircleMask 平滑圓邊 + 超取樣)。走預設的
            // AnSoloAA(α<128→0 硬裁)會把 α≈138 那圈軟邊 binarise 成 1-bit 圓 → 邊緣鋸齒/破碎。
            // 例外:聊天模式(Room4)與道具包(Room55)是膠囊/長條,不是圓 → 留在 clip 路徑(套圓遮罩會把兩端裁掉)。
            Btn("OpenRecord", "OpenRecord_a", "OpenRecord_b", "OpenRecord_c", Win3, 279, 82, null, circle: true);           // 錄製
            _expressionBtn = Btn("expression1", "BtnExpression_1", "BtnExpression_2", "BtnExpression_3", Win3, 311, 82, ToggleExpressionMenu, circle: true); // 表情
            Btn("ChatSendButton", "BtnSpeaker_1", "BtnSpeaker_2", "BtnSpeaker_3", Win3, 343, 82, SendRoomChat, circle: true);       // 喇叭/送出
            Btn("LoudSpeaker", "LoudSpeaker_1", "LoudSpeaker_2", "LoudSpeaker_3", Win3, 376, 82, null, circle: true);       // 大聲公
            Btn("RoomPet", "BtnPet_1", "BtnPet_2", "BtnPet_3", Win3, 411, 83, null, circle: true);                         // 寵物
            Btn("WingButton", "RoomWing", "RoomWing1", "RoomWing", Win3, 447, 82, null, circle: true);                     // 翅膀
            // 衣櫥 → 儲物櫃 (WardrobeScreen)。比照選歌鈕：按下用滑動音(ButtonFloat)，開櫃的 Frameround whoosh 由 WardrobeScreen.Open 播 → 服飾欄旋轉進場。
            Btn("ClosetButton", "RoomCloset001", "RoomCloset002", "RoomCloset003", Win3, 480, 81, () => Nav.OpenWardrobe?.Invoke(), UiSfx.ButtonFloat, circle: true);
            Btn("BangleButton", "Bangle0", "Bangle1", "Bangle0", Win3, 514, 82, null, circle: true);                       // 手環
            Btn("NotesButton", "Emai0", "Emai1", "Emai0", Win3, 548, 82, null, circle: true);                              // 信件
            Btn("tools", "Room55", "Room56", "Room57", Win3, 584, 85, null);                                // 道具包(膠囊,非圓)
            // 右邊這格是**同一個位置的兩顆球**(比照下面的準備/取消):
            //   • 在座位上 → 藍色「旁觀」(look, BtnLook):交出座位去看戲;
            //   • 旁觀中   → 官方綠色「進入」(play, Room92/93/94):回座位。
            // 兩顆都掛 OnSpectateToggle(它自己看目前是不是旁觀者決定送 spectate 還是 stopSpectate)。
            // 為什麼一定要換圖:同一顆「旁觀」鈕按下去之後還是寫著「旁觀」,玩家會以為沒生效而一直按 ——
            // 而那顆鈕在旁觀狀態下做的其實是相反的事。初始隱藏「進入」,等 Render 依快照決定。
            // 大顆圓鈕 → alphaHit：命中判定貼齊可見圓形,透明四角不再誤觸;disc：手繪圓盤的階梯描邊沿圓周低通抹平(見 Btn 註解)。
            _spectateBtn = Btn("look", "BtnLook_1", "BtnLook_2", "BtnLook_3", Win3, 651, 60, OnSpectateToggle, alphaHit: 0.5f, disc: true);
            _enterBtn = Btn("play", "Room92", "Room93", "Room94", Win3, 651, 60, OnSpectateToggle, alphaHit: 0.5f, disc: true);
            _enterBtn.gameObject.SetActive(false);

            // 開始：按下不走預設 SE_0001，改由 OnStart 播 Start 音效 + 全螢幕漸暗再切舞台。
            _startBtn = Btn("start", "Room15", "Room16", "Room17", Win3, 706, 43, OnStart, null, alphaHit: 0.5f, disc: true);
            // 準備 / 取消是**同一個位置的兩顆球**(官方 WaitingRoom.png 裡「取消」就烘在「準備」正下方一列):
            // 沒準備 → Room12「準備」;按了之後 server 回 roomState → 換成 c_ready0「取消」,再按一次取消準備。
            // 兩顆都掛 OnReadyToggle(它自己看目前狀態決定送 true 還是 false)。初始隱藏「取消」,等 Render 決定。
            _readyBtn = Btn("ready", "Room12", "Room13", "Room14", Win3, 706, 43, OnReadyToggle, alphaHit: 0.5f, disc: true);
            _cancelReadyBtn = Btn("cancel_ready", "c_ready0", "c_ready1", "c_ready2", Win3, 706, 43, OnReadyToggle, alphaHit: 0.5f, disc: true);
            _cancelReadyBtn.gameObject.SetActive(false);

            // 5) 左上「左拉」收合鈕（官方 uihide/uidisplay，同一位置 11,83）。按 ◄(BtnMaypopLeft) → 三個面板往四周滑出；
            //    收合後原地換成 ►(BtnMaypopRight) 展開鈕。掛在 Root（不隨面板收合），且最後建立 → 疊在最上層永遠可點。
            // 收合/展開鈕：滑過 Buttonfloat、按下 Interfaceout（官方 uihide/uidisplay 滑動音）。
            _uiHideBtn = UIKit.AddSpriteButton(Root, "uihide",
                RoomUiArt.AnSoloAA("BtnMaypopLeft_1"), RoomUiArt.AnSoloAA("BtnMaypopLeft_2"), RoomUiArt.AnSoloAA("BtnMaypopLeft_3"), 11, 83);
            UiHoverSfx.Attach(_uiHideBtn, UiSfx.ButtonFloat);
            UiSfx.AttachPress(_uiHideBtn, UiSfx.WindowSlide);
            _uiHideBtn.onClick.AddListener(() => SetCollapsed(true));
            _uiShowBtn = UIKit.AddSpriteButton(Root, "uidisplay",
                RoomUiArt.AnSoloAA("BtnMaypopRight_1"), RoomUiArt.AnSoloAA("BtnMaypopRight_2"), RoomUiArt.AnSoloAA("BtnMaypopRight_3"), 11, 83);
            UiHoverSfx.Attach(_uiShowBtn, UiSfx.ButtonFloat);
            UiSfx.AttachPress(_uiShowBtn, UiSfx.WindowSlide);
            _uiShowBtn.onClick.AddListener(() => SetCollapsed(false));
            _uiShowBtn.gameObject.SetActive(false);   // 初始展開 → 只顯示 ◄

            // 開始 → 1 秒漸暗再切舞台：最上層全螢幕黑幕(初始透明/停用)。最後建立 → 疊在所有面板/收合鈕之上。
            var fadeRt = UIKit.NewRect(Root, "StartFade");
            UIKit.Stretch(fadeRt);
            _startFade = fadeRt.gameObject.AddComponent<Image>();
            _startFade.color = new Color(0f, 0f, 0f, 0f);
            _startFade.raycastTarget = true;          // 漸暗期間吃掉所有點擊
            _startFade.gameObject.SetActive(false);
        }

        /// <summary>全畫布(800×600) win 容器：錨定左上、pivot 左上、原點 → 子元件座標仍用絕對(win.x+x)，收合只移動容器。</summary>
        private RectTransform MakeWinContainer(string name)
        {
            var rt = UIKit.NewRect(Root, name);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(800f, 600f);
            return rt;
        }

        /// <summary>切換 UI 收合狀態（官方 uihide/uidisplay）。實際滑動由 Update 內的補間處理。</summary>
        private void SetCollapsed(bool collapsed)
        {
            _uiCollapsed = collapsed;
            if (_uiHideBtn != null) _uiHideBtn.gameObject.SetActive(!collapsed);
            if (_uiShowBtn != null) _uiShowBtn.gameObject.SetActive(collapsed);
            if (_dropCombo != null) _dropCombo.CloseList();   // 收合前先關掉掉落方式清單(否則清單會跟著 win2 滑出畫面)
        }

        /// <summary>進房間時把 UI 收合狀態歸零到「完全展開」。RoomScreen 是常駐單例(切畫面只切 CanvasGroup、不重建)，
        /// 若不重置，收合後離開再回房間會停在收合狀態(整組面板滑出畫面)。每次 OnShow 都叫一次確保乾淨進場。</summary>
        private void ResetCollapse()
        {
            _uiCollapsed = false;
            _collapseT = 0f;
            ApplyCollapse();
            if (_uiHideBtn != null) _uiHideBtn.gameObject.SetActive(true);
            if (_uiShowBtn != null) _uiShowBtn.gameObject.SetActive(false);
            if (_dropCombo != null) _dropCombo.CloseList();
        }

        /// <summary>外部(選歌 overlay)改了房主設定後叫房間右側面板重畫。房間平常只在 <c>RoomUpdated</c> 時重畫，
        /// 而「在選歌畫面改場景」離線不會產生任何房間事件 → 沒有這條路的話外面那張場景縮圖要等關掉對話框才會對。</summary>
        public void RefreshPanel()
        {
            if (Ctx == null || Ctx.Session == null) return;
            RenderWin2();
        }

        /// <summary>進房間轉場的「四邊滑入」進場：把三個面板先擺到收合(畫面外)位置，再由 Update 補間滑回展開
        /// （win1 由上、win2 由右、win3 由下滑進來）。由 ScreenTransition 在漸亮開始時呼叫（Nav.PlayRoomEntrance），
        /// 這樣滑入動作正好隨黑幕散去而顯現。非轉場路徑(dev hooks)不呼叫 → OnShow 的 ResetCollapse 直接展開，不受影響。</summary>
        public void PlayEntrance()
        {
            _uiCollapsed = false;   // 目標＝完全展開
            _collapseT = 1f;        // 由完全收合(畫面外)起跳
            ApplyCollapse();        // 立即擺到畫面外，避免這一幀先閃到展開位置
            if (_uiHideBtn != null) _uiHideBtn.gameObject.SetActive(true);
            if (_uiShowBtn != null) _uiShowBtn.gameObject.SetActive(false);
            if (_dropCombo != null) _dropCombo.CloseList();
        }

        /// <summary>把三個面板容器依 _collapseT(0..1) 補到收合位移（SmoothStep 緩動）。
        /// 順帶把左下訊息欄隨收合淡出：win3 只下滑 119px，而訊息欄起點較高(y=445)不會被完全帶出畫面，
        /// 不淡出就會在收合後露出末幾行。展開時淡回。(對話泡層不動，維持原本一直顯示。)</summary>
        private void ApplyCollapse()
        {
            float e = Mathf.SmoothStep(0f, 1f, _collapseT);
            if (_win1Root != null) _win1Root.anchoredPosition = Win1Hidden * e;
            if (_win2Root != null) _win2Root.anchoredPosition = Win2Hidden * e;
            if (_win3Root != null) _win3Root.anchoredPosition = Win3Hidden * e;

            if (_chatLogGroup != null)
            {
                float chatVis = 1f - e;                          // 展開=1 顯示；收合=0 隱藏
                _chatLogGroup.alpha = chatVis;
                _chatLogGroup.blocksRaycasts = chatVis > 0.5f;   // 收合後訊息欄不再攔截捲動
            }
        }

        // ---- lifecycle: spawn / tear down the 3D room ----

        public override void OnShow()
        {
            // 自製輸入框：開啟 IME 組字並由 FeedImeCursorPos 指定選字視窗位置（Unity 官方作法）。離房時 OnHide 還原 Auto。
            Input.imeCompositionMode = IMECompositionMode.On;
            if (!_subscribed)
            {
                if (Ctx.Rooms != null) Ctx.Rooms.RoomUpdated += OnRoomUpdated;
                if (Ctx.Chat != null) Ctx.Chat.MessageReceived += OnRoomChatMessage;
                if (Ctx.Net != null)
                {
                    Ctx.Net.Kicked += OnKickedFromRoom;          // 被房主踢/位子被關 → 要離開房間畫面
                    Ctx.Net.MatchStarting += OnMatchStarting;    // server 說開場了 → 才進場(房主與非房主同一條路)
                    Ctx.Net.GameplayAborted += OnGameplayAborted;
                }
                LocalizationManager.LanguageChanged += Render;   // 切語言時，房號/房名/位置標示即時重譯
                _subscribed = true;
            }
            // joinResult and the first roomState can arrive before this screen subscribes; reconcile the snapshot now.
            SyncNetSongAvailability();
            // 同一個理由:第一份 roomState 可能早於 OnRoomUpdated 的訂閱 → 非房主要在這裡先把
            // 房間設定收進 session。漏掉的話「一進房房主就走人」那一瞬間升上來的新房主
            // 會拿自己 config.ini 的預設值把整間房的模式/場景蓋掉(見 AdoptIfNotHost)。
            NetRoomSettingsPublisher.AdoptIfNotHost(Ctx);


            bool localMale = Ctx != null && Ctx.Session != null && Ctx.Session.Gender == 1;
            // 從 id-based equippedItems 經 catalog 現算 (含合成 翅膀/表情/项链)，非讀可能過時的 equippedParts 快取 → 房間
            // 才會跟儲物櫃一致顯示飾品 (user: 儲物櫃有、room 沒有)。
            string[] localAvatarParts = ProfileManager.Active != null
                ? WardrobeStore.ResolveEquippedParts(ProfileManager.Active, localMale ? 1 : 0, id => AvatarItemCatalog.Instance.ById(id))
                : null;
            int localBody = ProfileManager.Active != null ? ProfileManager.Active.bodyShapeIndex : 0;   // 本機角色自己的體型 (胖瘦)

            // 🔴 連線模式:進房就把自己的外觀報一次給 server —— 別人是靠這份資料把你的角色建出來的。
            // 這裡是**唯一保證會跑到的地方**:本機 avatar 的穿搭就是在上面這三行解析出來的,
            // 而不管你是按「開房」、按「加入」、從遊戲打完回房、還是走 dev 的直達路徑,都會經過 OnShow。
            // (原本只掛在選男女畫面的 CommitIdentity 上 → dev 的 SDO_ROOM/SDO_JOINFIRST 兩條路徑
            //  完全繞過它,實測 server 一次 setLook 都沒收到,遠端角色全是預設的女角。)
            int localSeat = Ctx != null && Ctx.Rooms != null ? Mathf.Max(0, LocalSeatIndex(Ctx.Rooms.CurrentRoom)) : 0;
            if (Ctx != null && Ctx.Net != null)
            {
                Ctx.Net.PublishLook();        // 去重過;進房前 NetClient 已經送過一次,這裡是補網
                _localMoveSlot = int.MinValue;
                _moveThrottle.Reset();
            }

            if (_scene == null)
            {
                var sceneGo = new GameObject("RoomScene3D");
                _scene = sceneGo.AddComponent<RoomScene3D>();
                _scene.Build(localMale, localAvatarParts, localBody, localSeat);
                // 遠端玩家的頭貼:一台相機對準房間裡已經在跑的那幾隻角色(不再建 avatar)。
                var headsGo = new GameObject("RoomRemoteHeads");
                headsGo.transform.SetParent(_scene.transform, false);
                _remoteHeads = headsGo.AddComponent<RoomRemoteHeadSet>();
                ApplyHeadFraming(_remoteHeads);   // 與本機那顆同一組取景參數(否則遠端頭貼會偏高,見那邊的註解)
                _remoteHeads.Build(_scene);
                if (_backdrop != null && _scene.SceneTexture != null)
                {
                    _backdrop.texture = _scene.SceneTexture;
                    _backdrop.color = Color.white;
                    _backdrop.uvRect = flipBackdropV ? new Rect(0f, 1f, 1f, -1f) : new Rect(0f, 0f, 1f, 1f);
                }
            }

            if (_localHead == null)
            {
                var headGo = new GameObject("RoomLocalHead");
                _localHead = headGo.AddComponent<RoomHeadPortrait>();
                _localHead.layer = HeadLayer;
                ApplyHeadFraming(_localHead, localMale);   // 男女各自的上下/遠近
                _localHead.Init(localMale, localAvatarParts, localBody);
                _localHead.SetSpectating(LocalSpectating);   // 旁觀進房的人:頭貼一開始就不要演飛行動作
                _localHead.WalkingProvider = () => _scene != null && _scene.IsWalking;   // framed head mirrors the avatar's motion
                _localHead.FacingProvider = () => _scene != null ? _scene.AvatarFacing : 0f;   // …and its left/right facing
                _localHead.MirrorSourceProvider = () => _scene != null ? _scene.PlayerAvatar : null;   // …and its exact pose (no drift)
            }

            // mask the room's 3D layers off the front-end UI camera (it renders ~0, so it would otherwise draw them flat)
            var ui = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            if (ui != null)
            {
                _maskedCam = ui; _savedMask = ui.cullingMask;
                ui.cullingMask &= ~((1 << RoomScene3D.SceneLayer) | (1 << HeadLayer)
                                    | (1 << RoomScene3D.RemoteAvatarLayer));
            }

            // 儲物櫃換穿後 → 立即重建本機房間 avatar + 頭貼，讓新穿搭當場反映 (WardrobeScreen 已寫回 profile.json)。
            Nav.RefreshRoomAvatar = RefreshLocalAvatar;

            // 常駐單例：清掉上次「開始」漸暗殘留的黑幕，回房間才不會整片黑。
            _starting = false;
            if (_startFade != null) { _startFade.gameObject.SetActive(false); _startFade.color = new Color(0f, 0f, 0f, 0f); }

            ResetCollapse();             // 每次進場都從「完全展開」開始（常駐單例，避免上次收合狀態殘留）
            SeedDefaultSongIfNeeded();   // 進大廳預設選好 index 最大的歌(easy)，房間一進來就有歌
            Debug.Log("[dev] vars: ROOM=" + (ScreenGameplay.DevVar("SDO_ROOM") ?? "-")
                      + " JOINFIRST=" + (ScreenGameplay.DevVar("SDO_JOINFIRST") ?? "-")
                      + " AUTOREADY=" + (ScreenGameplay.DevVar("SDO_AUTOREADY") ?? "-")
                      + " AUTOSTART=" + (ScreenGameplay.DevVar("SDO_AUTOSTART") ?? "-")
                      + " SAY=" + (ScreenGameplay.DevVar("SDO_SAY") ?? "-")
                      + " PICKSONG=" + (ScreenGameplay.DevVar("SDO_PICKSONG") ?? "-"));
            // 聊天作用域切到本房間：之後的送話/廣播標記成此房，且只顯示此房 + 密語(跨場)。
            int prevChatScopeRoomId = _chatScopeRoomId;
            _chatScopeRoomId = Ctx.Rooms != null && Ctx.Rooms.CurrentRoom != null ? Ctx.Rooms.CurrentRoom.Id : 0;
            // 換場地才清訊息欄：**只有「大廳→房間」算換場地**。打完一首從舞台回到同一間房不清 ——
            // 使用者要求左下角的對話留著(進遊戲前講的話,回房後還看得到)。
            // 房號也要一樣才留:_returnedFromStage 只在回房那次 OnShow 被消耗,萬一遊戲後沒回房(中離走人)
            // 旗標會殘留到下一次進房 —— 那次是真的換場地,還是要清。
            bool sameRoomAsBefore = _chatScopeRoomId != 0 && _chatScopeRoomId == prevChatScopeRoomId;
            if (!_returnedFromStage || !sameRoomAsBefore) Ctx.Chat?.Clear();
            Ctx.Chat?.SetScope(ChatScope.Room, _chatScopeRoomId);
            RebuildRoomChat();
            Render();
            // 進場廣播「X 進入舞台遊戲」只在「從大廳進來」時送；從舞台遊戲回房(打完一首回房)不重播。
            if (_returnedFromStage) _returnedFromStage = false;
            else AnnounceStagePresence(true);   // 只同房、只在「當前」分類

            // DEV: SDO_SLOTMENU=<座位編號 0..5> → 進房間就把那一格的右鍵選單彈出來,用來截圖檢查選單外觀。
            // 選單用的是官方美術(EXPRESSIONINFO 的兩態列圖)並就地重造成 9-slice、不畫底板 ——
            // 圓角有沒有被拉扁、外面有沒有鑲白框、中文字塞不塞得下,只有實機截圖看得出來。
            string devSlot = ScreenGameplay.DevVar("SDO_SLOTMENU");
            if (!string.IsNullOrEmpty(devSlot))
            {
                int seat;
                if (!int.TryParse(devSlot, out seat)) seat = 0;
                StartCoroutine(DevShowSlotMenuCo(Mathf.Clamp(seat, 0, 5)));
            }
        }

        /// <summary>SDO_SLOTMENU 的實作:等座位畫好(Render 要先跑過)再彈選單。純除錯用。</summary>
        private System.Collections.IEnumerator DevShowSlotMenuCo(int seat)
        {
            yield return null;
            yield return null;
            var room = Ctx != null && Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
            // ShowSlotPopup 收的是**螢幕**座標(平常來自 PointerEventData.position),
            // 不是 800×600 的 design 座標 —— 挑一個畫面中段偏左的點,選單自己會夾進框內。
            ShowSlotPopup(seat, new Vector2(Screen.width * 0.25f, Screen.height * 0.55f),
                          room, CanManageSeats(room), seat == LocalSeatIndex(room));
        }

        // 儲物櫃換穿 → 重建本機房間 3D avatar + 頭貼 (讀最新 EquippedAvatarParts；WardrobeScreen 已寫回 profile)。
        private void RefreshLocalAvatar()
        {
            if (_scene == null) return;   // 房間不在場上 (OnHide 已拆掉) → 別重建出孤兒頭貼相機；回房 OnShow 會用最新穿搭重建
            bool male = Ctx != null && Ctx.Session != null && Ctx.Session.Gender == 1;
            string[] parts = ProfileManager.Active != null
                ? WardrobeStore.ResolveEquippedParts(ProfileManager.Active, male ? 1 : 0, id => AvatarItemCatalog.Instance.ById(id))
                : null;
            int body = ProfileManager.Active != null ? ProfileManager.Active.bodyShapeIndex : 0;   // 本機角色自己的體型 (胖瘦)
            if (_scene != null) _scene.RebuildLocalAvatar(male, parts, body);
            // 換裝後要重報一次外觀,否則別人畫面上的你還穿著舊衣服。
            // 走 PublishLook 而不是直接 SendLook:前者會更新「上次送出的外觀」快取。
            // 直接送的話快取會過期 → 之後某次真的變了反而被去重擋掉(而且很難查)。
            if (Ctx != null && Ctx.Net != null) Ctx.Net.PublishLook();
            // 頭貼要「整個重建」：RoomHeadPortrait.Init 每次都新建一隻頭 avatar/相機/RT 卻不清舊的 → 直接再 Init 只會疊一隻
            // 舊的、頭貼不更新。故銷毀整個 _localHead 再重建並重接 provider。
            // (Destroy 幀尾才生效 → 先 SetActive(false)，否則舊頭 avatar 這一幀還在同一個 parkSpot，新頭相機會同時拍到兩顆。)
            if (_localHead != null) { _localHead.gameObject.SetActive(false); Destroy(_localHead.gameObject); _localHead = null; }
            var headGo = new GameObject("RoomLocalHead");
            _localHead = headGo.AddComponent<RoomHeadPortrait>();
            _localHead.layer = HeadLayer;
            ApplyHeadFraming(_localHead, male);   // 男女各自的上下/遠近
            _localHead.Init(male, parts, body);
            _localHead.SetSpectating(LocalSpectating);   // 重建會回到 Init 的預設(穿翅膀=飛)→ 旁觀中要再關掉一次
            _localHead.WalkingProvider = () => _scene != null && _scene.IsWalking;
            _localHead.FacingProvider = () => _scene != null ? _scene.AvatarFacing : 0f;
            _localHead.MirrorSourceProvider = () => _scene != null ? _scene.PlayerAvatar : null;
        }

        // 進/出房間廣播（進入房間的人送出；同房才收得到，只在「當前」分類顯示）。
        private void AnnounceStagePresence(bool entered)
        {
            var room = Ctx != null && Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
            if (room == null || Ctx.Chat == null) return;
            string who = LocalName(room);
            if (string.IsNullOrEmpty(who)) return;
            if (entered) Ctx.Chat.AnnounceStageEnter(who);
            else Ctx.Chat.AnnounceStageLeave(who);
        }

        // 進房間時，若還沒選過歌就預設選「index(fileId) 最大的那首」easy。玩家之後自己選歌就蓋過去（HasSong 守門只做一次）。
        private void SeedDefaultSongIfNeeded()
        {
            var s = Ctx != null ? Ctx.Session : null;
            if (s == null) return;
            if (!s.HasSong)
            {
                var model = SongListModel.FromCatalog();          // 已按 gn 檔名(sdomNNNNk.gn)由大到小排序
                if (model.All.Count == 0) return;
                var e = model.All[0];                             // [0] = 檔名編號最大 = 清單最上面
                s.SongGn = e.gn;
                s.SongFileId = e.fileId;
                s.SongTitle = e.title ?? e.gn;
                s.SongArtist = e.artist;
                s.Difficulty = Difficulty.Easy;
            }
            // 不論剛預設或之前選的：確保「房間」也拿到這首歌。房間可能是重新建立的(SongTitle 還空著)——若只靠上面 HasSong
            // 守門,離開再進來就會 session 有歌、房間沒歌 → 開始鈕的 CanStart 檢查 room.SongTitle 誤判成「請先選擇歌曲」。
            var room = Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
            if (s.HasSong && room != null && string.IsNullOrEmpty(room.SongTitle))
                Ctx.Rooms.SetSong(s.SongTitle);               // 同步房間顯示（單機=房主）
            // 連線:房主要把歌**發給 server**,否則 server 眼中這間房沒有歌 → 沒人按得下準備、
            // 房主按開始只會收到「請先選擇歌曲」(見 NetSongPublisher 的註解)。
            NetSongPublisher.Publish(Ctx);
        }

        public override void OnHide()
        {
            // NOTE: 進選歌時房間「不會」走到這裡 —— 選歌是疊在房間上的 overlay，房間仍是 visible（見 FrontendApp.ShowOnly）。
            // OnHide 只在真正離開房間時觸發（回大廳 / 進遊戲），所以在這裡完整拆除 3D 場景是正確的。
            if (_subscribed)
            {
                if (Ctx.Rooms != null) Ctx.Rooms.RoomUpdated -= OnRoomUpdated;
                if (Ctx.Chat != null) Ctx.Chat.MessageReceived -= OnRoomChatMessage;
                if (Ctx.Net != null)
                {
                    Ctx.Net.Kicked -= OnKickedFromRoom;
                    Ctx.Net.MatchStarting -= OnMatchStarting;
                    Ctx.Net.GameplayAborted -= OnGameplayAborted;
                }
                LocalizationManager.LanguageChanged -= Render;
                _subscribed = false;
            }
            HideChatModeMenu();
            HideExpressionMenu();
            CloseSlotPopup();   // 常駐單例:選單開著時離房,回來不能還掛著一個指向舊座位的選單
            _awaitingMatchStart = false;   // 同理:離房時還在等 matchStarting → 回來不能卡住「開始」鈕
            HideRoomChatBubble();
            ClearSentRoomBubbles();
            ClearRemoteBubbleLayers();   // 離房的人不會再回來,他們的層留著只是空殼
            _chatInputSticky = false;   // 離開房間 → 放掉輸入框黏 focus，回來時不自動搶 focus
            Input.imeCompositionMode = IMECompositionMode.Auto;   // 還原，別影響遊戲/其他畫面的按鍵
            if (_maskedCam != null) { _maskedCam.cullingMask = _savedMask; _maskedCam = null; }
            if (_backdrop != null) { _backdrop.texture = null; _backdrop.color = Color.black; }
            for (int i = 0; i < _slotHead.Length; i++) if (_slotHead[i] != null) { _slotHead[i].texture = null; _slotHead[i].enabled = false; }
            if (_localHead != null) { Destroy(_localHead.gameObject); _localHead = null; }
            if (_scene != null) { Destroy(_scene.gameObject); _scene = null; }
            _remoteHeads = null;   // 它掛在 _scene 底下,跟著一起被拆掉(OnDestroy 會釋放 RT)
            // 遠端玩家的角色跟著 _scene 一起被拆掉,但名字牌是掛在 UI 上的 → 要自己收,
            // 否則回房間時會留下一排指向已消失角色的孤兒名字牌。
            ClearRemoteNamePlates();
            _remoteAvatarRev = -1;
            _localMoveSlot = int.MinValue;
            // A real leave clears the transfer's room-song latch. Without this, rejoining a room that
            // selected the same external pack can retain _handledPack from the previous visit and never
            // retry availability/download. Entering gameplay keeps Net.Room, so an in-flight host upload
            // is deliberately left alone.
            if (Ctx == null || Ctx.Net == null || !Ctx.Net.InRoom)
                NetSongTransfer.OnRoomSong(null);
            // 離房清掉「已廣播過誰」—— 不清的話回房時舊名單會被當成已知,
            // 那些人再進來就不會播進場廣播了。
            _announcedUsers.Clear();
            _announceSeeded = false;
        }

        /// <summary>
        /// Reconcile transfer ownership before reporting availability for the current song.
        /// Reporting first can see the previous external transfer as active and skip the new song forever;
        /// official songs do not start another transfer that could repair that skipped report.
        /// </summary>
        private void SyncNetSongAvailability()
        {
            var netSong = Ctx != null && Ctx.Net != null && Ctx.Net.Room != null
                ? Ctx.Net.Room.Song : null;
            // latch key 走 NetSongTransfer 的共用函式 —— NetSongTransfer.Tick 也在每幀補同一個 latch
            // (見那邊的 LatchRoomSong),兩邊算出不同的字串會讓它們每幀互相覆蓋。
            RunSongAvailabilitySync(
                NetSongTransfer.RoomPackKeyOf(netSong),
                NetSongTransfer.OnRoomSong,
                () => NetSongPublisher.ReportAvailability(Ctx));
        }

        /// <summary>
        /// One ordering point shared by first display and later room snapshots. The delegates keep this race
        /// contract testable without constructing a Unity room screen or a live network connection.
        /// </summary>
        private static void RunSongAvailabilitySync(
            string packId, System.Action<string> onRoomSong, System.Action reportAvailability)
        {
            onRoomSong(packId);
            reportAvailability();
        }

        private void OnRoomUpdated(int id)
        {
            EnsureChatScope();
            // 🔴 房主要把歌發給 server,而且必須在**每次房間快照**時檢查一次,不能只在進房那一刻送一次:
            //    進房時房間可能還沒建好(createRoom 要等 server 回 roomState),那時 InRoom 還是 false
            //    → 發布被靜默跳過、而且永遠不會再試 → server 眼中這間房永遠沒有歌 →
            //    沒人按得下準備、房主按開始也沒反應(實機兩開就是卡在這裡,而且三個 log 都看不出來)。
            //    這裡有 SongTitle 空的守門,所以送成功之後就不會再送(不會迴圈)。
            NetSongPublisher.PublishIfRoomHasNone(Ctx);
            // 房間設定(模式/隊形/旁觀人數/場景)同理:在此之前根本沒有任何一條路徑把它們送上去,
            // 所以線上這間房永遠停在 server 的預設值 —— 房主選了「普通模式」別人還是看到「自由模式」,
            // 而「只有普通模式才能組隊」就永遠不成立。守門是「跟 server 手上的不一樣才送」。
            //
            // 🔴 順序不能反:**先收再推**。非房主要先把房間設定收進自己的 session,
            //    否則房主一轉移,新房主就拿自己 config.ini 的預設值(通常是自由模式)把整間房蓋掉
            //    —— 使用者回報的「ShowTime 的房間把房主給別人之後變成自由模式」就是這條。
            NetRoomSettingsPublisher.AdoptIfNotHost(Ctx);
            NetRoomSettingsPublisher.SyncIfHost(Ctx);
            // 先取消/切換上一首歌的傳輸，再回報「我有沒有這首歌」。
            // 沒有可用性回報時，server 眼中每個人都是 Unknown，
            // 沒人按得下準備(R17)、也沒有人算參與者(R12)。見那邊的註解。
            SyncNetSongAvailability();
            Render();
        }

        /// <summary>
        /// 聊天的作用域房號要跟著房間資料走,不能只在 <c>OnShow</c> 設一次。
        ///
        /// 🔴 連線模式下「進房」與「拿到房間資料」是**兩件事**:加入成功的回應可能比第一份
        /// 房間快照先到,那時 <c>CurrentRoom</c> 還是 null → 作用域房號會停在 0,
        /// 而顯示過濾器要求 <c>m.RoomId == _chatScopeRoomId</c> → **整個房間的聊天一句都不會出現**
        /// (連自己說的也不會,因為線上版的自己那句也是從 server 繞回來的)。
        /// 實際踩過:兩台互打字都看不到對方,server 的 log 卻明明收到了 chatSay。
        /// </summary>
        private void EnsureChatScope()
        {
            int id = Ctx != null && Ctx.Rooms != null && Ctx.Rooms.CurrentRoom != null ? Ctx.Rooms.CurrentRoom.Id : 0;
            if (id == 0 || id == _chatScopeRoomId) return;
            _chatScopeRoomId = id;
            Ctx.Chat?.SetScope(ChatScope.Room, id);
        }

        private void BuildRoomChatLog()
        {
            // 訊息欄底改成全透明（原本是灰色半透明 a=0.18）；文字直接疊在 3D 房間上。
            _chatScroll = UIKit.AddVerticalScroll(_win3Root, "AllChatList", out _chatContent, 0f, ChatLogPad, new Color(0f, 0f, 0f, 0f));
            // 官方 DDRROOM 的 win4 是 x=14 y=445 w=360 h=104（底緣 549）。這裡**刻意往下 10px**：
            // 官方那個 TextList 的字是直接貼著視窗底緣排的，我們的 content 還有 3px 內距，最後一行的字底
            // 因此停在 546，離下面那條紫色輸入條（chatmode 鈕在 569）差了將近 20px —— 使用者回報「房間裡
            // 聊天的字跟下面的打字框間隔太遠」。往下挪之後最後一行貼在 556，剩下約半行的呼吸空間。
            // 高度不動（104 仍是 ChatLineClip 算整行裁切的依據），只是整塊下移。
            Place(_chatScroll.GetComponent<RectTransform>(), 14, ChatLogY, ChatLogW, 104);
            _chatScroll.scrollSensitivity = 18f;
            _chatLogGroup = _chatScroll.gameObject.AddComponent<CanvasGroup>();   // 收合時淡出(win3 下滑不足以完全移出訊息欄,見 ApplyCollapse)
            // 整行裁切：視窗 104px 不是行高的整數倍，捲到底時最上面那行只露下半截字且一直不走(見 ChatLineClip)。
            _chatClip = _chatScroll.gameObject.AddComponent<ChatLineClip>();

            // 打字泡：固定一顆。已送出的泡另外 Spawn，可並存一串。與自己已送出的泡同一層(owner 0)——
            // 不然它與別人的泡誰蓋誰就跟站位無關了(而且它是常駐單例,那層刻意不隨離房銷毀)。
            _chatBubbleRoot = UIKit.NewRect(BubbleOwnerLayer(0), "RoomChatTypingBubble");
            _chatBubbleRoot.anchorMin = _chatBubbleRoot.anchorMax = new Vector2(0f, 1f);
            _chatBubbleRoot.pivot = new Vector2(0f, 1f);
            _chatBubbleRoot.sizeDelta = new Vector2(171, 111);
            var drag = _chatBubbleRoot.gameObject.AddComponent<RoomBubbleDragHandle>();
            drag.Owner = this;
            drag.Sent = null;

            _chatBubbleFrame = UIKit.AddImage(_chatBubbleRoot, "Frame", Color.white, raycast: true);
            UIKit.Stretch(_chatBubbleFrame.rectTransform);
            UIKit.ApplySprite(_chatBubbleFrame, RoomBubbleArt.Base(1));
            Place(_chatBubbleFrame.rectTransform, 0, 0, 171, 111);
            _chatBubbleFrameAnim = _chatBubbleFrame.gameObject.AddComponent<SpriteSeqAnim>();
            _chatBubbleFrameAnim.Fps = 12f;

            _chatBubbleAdd = UIKit.AddImage(_chatBubbleRoot, "AddAni", Color.white);
            UIKit.Stretch(_chatBubbleAdd.rectTransform);
            _chatBubbleAddAnim = _chatBubbleAdd.gameObject.AddComponent<SpriteSeqAnim>();
            _chatBubbleAddAnim.Fps = 14f;
            _chatBubbleAddAnim.Frames = RoomBubbleArt.AddFrames();

            _chatBubbleText = UIKit.AddText(_chatBubbleRoot, "Text", "", 13, ChatBubbleTextColor, TextAlignmentOptions.MidlineLeft, true);
            Place(_chatBubbleText.rectTransform, 49, 43, 74, 28);
            _chatBubbleText.richText = true;
            _chatBubbleText.textWrappingMode = TextWrappingModes.Normal;
            _chatBubbleText.overflowMode = TextOverflowModes.Overflow;

            // 泡內游標：獨立 Image，掛在文字底下（子物件→畫在字上、跟著文字移動）。位置/閃爍由 UpdateBubbleCaretOverlay 每幀控。
            _chatBubbleCaret = UIKit.AddImage(_chatBubbleText.rectTransform, "TypingCaret", ChatBubbleTextColor, raycast: false);
            _chatBubbleCaret.rectTransform.anchorMin = _chatBubbleCaret.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            _chatBubbleCaret.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            _chatBubbleCaret.rectTransform.sizeDelta = new Vector2(2f, 15f);
            _chatBubbleCaret.gameObject.SetActive(false);

            _chatBubbleExpression = UIKit.AddImage(_chatBubbleRoot, "Expression", Color.white);
            _chatBubbleExpression.raycastTarget = false;
            _chatBubbleExpression.preserveAspect = true;
            Place(_chatBubbleExpression.rectTransform, 73, 43, 24, 24);
            _chatBubbleExpressionAnim = _chatBubbleExpression.gameObject.AddComponent<SpriteSeqAnim>();
            _chatBubbleExpressionAnim.Fps = 8f;

            _chatBubbleRoot.gameObject.SetActive(false);
        }

        private void ConfigureRoomChatInput()
        {
            if (_chatInput == null) return;
            _chatInput.characterLimit = 50;
            // onFocusSelectAll 預設 true：每次(重新)取得 focus 時 ActivateInputFieldInternal→OnFocus→SelectAll 會把
            // 選取錨點設回 0（stringSelectPositionInternal=0）＝整行反白、游標視覺跑到最前面。點聊天列人名會讓輸入框
            // 短暫失焦→重新啟用，而 ActivateInputField 的實際啟用延到下一個 LateUpdate 才跑，於是 SelectAll 永遠比
            // FocusRoomChatInput 的 MoveTextEnd 晚執行、蓋掉它 → 家族頻道點人名後游標跳到最前面。關掉即根治：
            // OnFocus 不再 SelectAll，MoveTextEnd 得以生效，直接點輸入框也改成把游標放到點擊處（見 line 2927 註解的預期）。
            _chatInput.onFocusSelectAll = false;
            _chatInput.customCaretColor = true;
            _chatInput.caretColor = Color.white;
            _chatInput.caretWidth = 2;
            _chatInput.caretBlinkRate = 0.85f;
            // richText 打開才會有 IME 組字底線：TMP_InputField.UpdateLabel 只在 m_RichText 為真時把 compositionString
            // 包成 <u>…</u>（新注音「選字階段」注音下面的那條底線）。UIKit.AddInputField 預設關掉 richText，這裡覆寫成開。
            // 送出/顯示都走 raw text（SendRoomChat 用 _chatInput.text、聊天列/泡走 EscapeTmp），richText 只影響輸入框自己的算圖。
            _chatInput.richText = true;
            if (_chatInput.textComponent != null) _chatInput.textComponent.richText = true;
            _chatInput.selectionColor = new Color(1f, 1f, 1f, 0.28f);
            _chatInput.onSubmit.AddListener(_ => SendRoomChat());
            _chatInput.onValueChanged.AddListener(OnRoomChatInputChanged);
            SetRoomChatInputEchoVisible(true);
            // IME 選字用 Enter 也會觸發 onSubmit；用 composition 狀態擋誤送。

            if (_chatInput.textViewport != null)
            {
                _chatInput.textViewport.offsetMin = new Vector2(5f, 4f);
                _chatInput.textViewport.offsetMax = new Vector2(-5f, -4f);
            }

            if (_chatInput.textComponent != null)
            {
                _chatInput.textComponent.color = Color.white;
                _chatInput.textComponent.fontSize = 12f;
                _chatInput.textComponent.alignment = TextAlignmentOptions.MidlineLeft;
                _chatInput.textComponent.margin = Vector4.zero;
            }

            if (_chatInput.placeholder is TextMeshProUGUI ph)
            {
                ph.fontSize = 12f;
                ph.alignment = TextAlignmentOptions.MidlineLeft;
                ph.margin = Vector4.zero;
            }
        }

        private void ToggleChatModeMenu()
        {
            if (_chatModeMenu == null) BuildChatModeMenu();
            bool show = !_chatModeMenu.gameObject.activeSelf;
            HideExpressionMenu();
            _chatModeMenu.gameObject.SetActive(show);
        }

        private void BuildChatModeMenu()
        {
            _chatModeMenu = UIKit.NewRect(_win3Root, "chatmodemenu");
            Place(_chatModeMenu, 15, 463, 41, 104);
            // 不畫 XML 的 background="Room_Pop16.an"：那張是 100% 灰階板(灰172/黑框)，直接 alpha-blend 會變成一塊
            // 灰白底露在按鈕四周。四顆按鈕自帶完整底圖，選單不需要背板。
            AddChatModeChoice("chatmode_family", ChatChannel.Family, 2, 2);
            AddChatModeChoice("chatmode_friend", ChatChannel.Friend, 2, 27);
            AddChatModeChoice("chatmode_cur", ChatChannel.Current, 2, 52);
            AddChatModeChoice("chatmode_talkback", ChatChannel.Reply, 2, 77);
            _chatModeMenu.gameObject.SetActive(false);
        }

        private void AddChatModeChoice(string name, ChatChannel channel, float x, float y)
        {
            ChatModeArt(channel, out var nrm, out var hov, out var psh);
            var b = UIKit.AddSpriteButton(_chatModeMenu, name, RoomUiArt.AnSolo(nrm), RoomUiArt.AnSolo(hov), RoomUiArt.AnSolo(psh), x, y);
            UiHoverSfx.Attach(b, UiSfx.ButtonFloat);
            UiSfx.AttachPress(b, UiSfx.Click);
            b.onClick.AddListener(() => SetChatChannel(channel));
        }

        private void SetChatChannel(ChatChannel channel)
        {
            var prev = _chatChannel;
            _chatChannel = channel;
            UpdateChatModeButton();
            UpdateChatListName();
            HideChatModeMenu();
            RebuildRoomChat();
            SyncChannelInputPrefix(prev, channel);
            if (_chatInput != null) _chatInput.ActivateInputField();
        }

        // 換頻道時同步輸入框的指令前綴：進家族 → 自動填「/家族 」並切輸入框回顯模式。
        // 離開家族「不」清掉「/家族 」草稿——「當前」＝綜合台，保留前綴讓使用者接著在當前打家族訊息（明打 /家族 一樣送家族綠字）。
        // 好友頻道的 [名字] 前綴由密語流程（InsertWhisperTarget / 送出後回填）維護，這裡不動它；
        // 進家族只在草稿為空時才填，避免蓋掉使用者打到一半的字。
        private void SyncChannelInputPrefix(ChatChannel from, ChatChannel to)
        {
            if (_chatInput == null || from == to) return;
            string draft = _chatInput.text ?? "";
            if (to == ChatChannel.Family)
            {
                if (!string.IsNullOrWhiteSpace(draft)) return;
                _chatInput.text = RoomChatCommand.GuildCommandPrefix;
                _chatDraftWasEmpty = false;
                _chatBubbleInputArmed = false;
                if (_chatBubbleTyping) HideRoomChatBubble();
                _chatInputSticky = true;
                SetRoomChatInputEchoVisible(true);
                FocusRoomChatInput();   // 游標移到「/家族 」結尾，接著打
            }
        }

        private void UpdateChatModeButton()
        {
            if (_chatModeBtn == null || !(_chatModeBtn.targetGraphic is Image img)) return;
            ChatModeArt(_chatChannel, out var nrm, out var hov, out var psh);
            UIKit.ApplySprite(img, RoomUiArt.AnSolo(nrm));   // 自貼圖去白邊（與 chatmode 鈕本身 Btn 預設一致）
            var st = _chatModeBtn.spriteState;
            st.highlightedSprite = RoomUiArt.AnSolo(hov);
            st.pressedSprite = RoomUiArt.AnSolo(psh);
            st.selectedSprite = RoomUiArt.AnSolo(nrm);
            _chatModeBtn.spriteState = st;
        }

        private static void ChatModeArt(ChatChannel channel, out string nrm, out string hov, out string psh)
        {
            switch (channel)
            {
                case ChatChannel.Family:
                    nrm = "Room203"; hov = "Room204"; psh = "Room205"; break;
                case ChatChannel.Friend:
                    nrm = "Room200"; hov = "Room201"; psh = "Room202"; break;
                case ChatChannel.Reply:
                    nrm = "Room206"; hov = "Room207"; psh = "Room208"; break;
                default:
                    nrm = "Room4"; hov = "Room5"; psh = "Room6"; break;
            }
        }

        private void ToggleExpressionMenu()
        {
            if (_expressionMenu == null) BuildExpressionMenu();
            bool show = !_expressionMenu.gameObject.activeSelf;
            HideChatModeMenu();
            _expressionMenu.gameObject.SetActive(show);
            if (show) RebuildExpressionMenu();
        }

        private void BuildExpressionMenu()
        {
            _expressionMenu = UIKit.NewRect(_win3Root, "expression");
            // ROOMPOPMENU expression = 165×152；對齊表情鈕(311,563)上方，底邊貼近 win3 紫條。
            Place(_expressionMenu, 248, 411, 165, 152);
            _expressionMenu.gameObject.SetActive(false);
            RebuildExpressionMenu();
        }

        private void RebuildExpressionMenu()
        {
            if (_expressionMenu == null) return;
            _expressionTip = null;
            _expressionTipText = null;
            UIKit.Clear(_expressionMenu);

            // ROOMPOPMENU: ExpBg at (0,20); NormalExp tab (5,3); arrows + page labels at bottom.
            UIKit.AddSprite(_expressionMenu, "ExpressionInfo", RoomUiArt.ExpressionInfoPage(_expressionPage), 0, 20);
            UIKit.AddSprite(_expressionMenu, "NormalExp", RoomUiArt.ExpressionNormalTab(selected: true), 5, 3);

            var leftFrames = RoomUiArt.ExpressionPageArrowFrames(left: true);
            var rightFrames = RoomUiArt.ExpressionPageArrowFrames(left: false);
            var prev = UIKit.AddSpriteButton(_expressionMenu, "preexp",
                leftFrames[0], leftFrames[1], leftFrames[2], 103, 131);
            UiSfx.AttachPress(prev, UiSfx.Click);
            prev.onClick.AddListener(() => StepExpressionPage(-1));
            var next = UIKit.AddSpriteButton(_expressionMenu, "nextexp",
                rightFrames[0], rightFrames[1], rightFrames[2], 146, 131);
            UiSfx.AttachPress(next, UiSfx.Click);
            next.onClick.AddListener(() => StepExpressionPage(1));

            int pages = Mathf.Max(1, RoomChatCommand.TotalExpressionPages);
            int pageNum = Mathf.Clamp(_expressionPage + 1, 1, pages);
            // CurrentPage / TotalPage — ROOMPOPMENU color 0xffbb2077
            var pageColor = new Color32(0xBB, 0x20, 0x77, 0xFF);
            var cur = UIKit.AddText(_expressionMenu, "CurrentPage", pageNum.ToString(), 12, pageColor, TextAlignmentOptions.Center);
            Place(cur.rectTransform, 118, 133, 12, 12);
            var sep = UIKit.AddText(_expressionMenu, "PageSlash", "/", 12, pageColor, TextAlignmentOptions.Center);
            Place(sep.rectTransform, 127, 133, 10, 12);
            var total = UIKit.AddText(_expressionMenu, "TotalPage", pages.ToString(), 12, pageColor, TextAlignmentOptions.Center);
            Place(total.rectTransform, 136, 133, 12, 12);

            for (int slot = 0; slot < RoomChatCommand.ExpressionsPerPage; slot++)
            {
                int expressionId = RoomChatCommand.ExpressionAtMenuSlot(_expressionPage, slot);
                if (expressionId <= 0) continue;
                float x = 4 + (slot % 6) * 26;
                float y = 24 + (slot / 6) * 26;
                AddExpressionChoice(slot, expressionId, x, y);
            }
        }

        private void AddExpressionChoice(int slot, int expressionId, float x, float y)
        {
            var hit = UIKit.AddImage(_expressionMenu, "BtExpSel_" + slot, new Color(1f, 1f, 1f, 0.001f), raycast: true);
            Place(hit.rectTransform, x, y, 24, 24);
            var btn = hit.gameObject.AddComponent<Button>();
            btn.targetGraphic = hit;
            btn.transition = Selectable.Transition.None;
            UiSfx.AttachPress(btn, UiSfx.Click);
            int id = expressionId;
            btn.onClick.AddListener(() =>
            {
                // 已經在左下輸入框打字 → 表情**塞進輸入框**（可以在它前後接著打字、也能配 [名字] 密語），
                // 不是直接送出去。使用者回報「明明 focus 在左下訊息欄，按 emoji 卻直接送出 bubble」。
                // 沒在輸入框打字（含頭上泡模式）才維持原本的「點一下就送」。
                if (TypingInChatInput()) InsertExpressionIntoChatInput(id);
                else Ctx?.Chat?.SendExpression(id, _chatChannel);
                HideExpressionMenu();
                if (_chatInput != null) _chatInput.ActivateInputField();
            });
            var tip = hit.gameObject.AddComponent<ExpressionTipHandle>();
            tip.Owner = this;
            tip.Command = RoomChatCommand.ExpressionDisplayText(expressionId);
            tip.LocalPos = new Vector2(x, y);
        }

        /// <summary>現在是不是在**左下輸入框**打字（而不是頭上泡模式、也不是完全沒在打字）。
        /// <c>_chatInputSticky</c> = 送出後仍黏著 focus 的續打狀態，跟 isFocused 一樣算「正在這裡打」。</summary>
        private bool TypingInChatInput()
            => _chatInput != null && !_chatBubbleTyping && !_chatBubbleInputArmed
               && (_chatInput.isFocused || _chatInputSticky);

        /// <summary>把表情指令（<c>/GO</c>）接到輸入框後面 —— 與遊戲畫面的 <c>GameplayChat.InsertExpression</c>
        /// 同一套規則：前面有字就補一個空白，結尾也留一個空白讓人接著打。</summary>
        private void InsertExpressionIntoChatInput(int expressionId)
        {
            string cmd = RoomChatCommand.ExpressionDisplayText(expressionId);
            if (string.IsNullOrEmpty(cmd) || _chatInput == null) return;
            _chatInput.text = Sdo.Game.ChatDraft.WithExpression(_chatInput.text, cmd, _chatInput.characterLimit);
            _chatDraftWasEmpty = false;
            FocusRoomChatInput();   // 內含 MoveTextEnd → 游標移到結尾接著打
        }

        private void StepExpressionPage(int delta)
        {
            int pages = Mathf.Max(1, RoomChatCommand.TotalExpressionPages);
            _expressionPage = (_expressionPage + delta) % pages;
            if (_expressionPage < 0) _expressionPage += pages;
            RebuildExpressionMenu();
        }

        private void HideChatModeMenu()
        {
            if (_chatModeMenu != null) _chatModeMenu.gameObject.SetActive(false);
        }

        private void HideExpressionMenu()
        {
            if (_expressionMenu != null) _expressionMenu.gameObject.SetActive(false);
            HideExpressionTip();
        }

        private void ShowExpressionTip(string command, Vector2 localPos)
        {
            if (string.IsNullOrEmpty(command) || _expressionMenu == null) return;
            if (_expressionTip == null) BuildExpressionTip();
            _expressionTipText.text = command;
            Vector2 pref = _expressionTipText.GetPreferredValues(command, 120f, 18f);
            float w = Mathf.Clamp(pref.x + 12f, 46f, 120f);
            float h = 19f;
            float x = Mathf.Clamp(localPos.x + 16f, 0f, 165f - w);
            float y = Mathf.Clamp(localPos.y - 18f, 0f, 133f);
            Place(_expressionTip, x, y, w, h);
            _expressionTip.gameObject.SetActive(true);
        }

        private void HideExpressionTip()
        {
            if (_expressionTip != null) _expressionTip.gameObject.SetActive(false);
        }

        private void BuildExpressionTip()
        {
            _expressionTip = UIKit.NewRect(_expressionMenu, "ExpressionCommandTip");
            var bg = _expressionTip.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.55f);
            bg.raycastTarget = false;
            _expressionTipText = UIKit.AddText(_expressionTip, "Text", "", 12, Color.white, TextAlignmentOptions.Center);
            UIKit.Stretch(_expressionTipText.rectTransform, 4, 0, 4, 0);
            _expressionTip.gameObject.SetActive(false);
        }

        private void RebuildRoomChat()
        {
            UpdateChatListName();
            UIKit.Clear(_chatContent);
            if (Ctx == null || Ctx.Chat == null) return;
            foreach (var m in Ctx.Chat.History) AddRoomChatLine(m);
            ScrollRoomChatToBottom();
        }

        private void UpdateChatListName()
        {
            if (_chatScroll == null) return;
            _chatScroll.gameObject.name = ChatListName(_chatChannel);
        }

        private static string ChatListName(ChatChannel channel)
        {
            switch (channel)
            {
                case ChatChannel.Family: return "FamilyChatList";
                case ChatChannel.Friend: return "FriendChatList";
                case ChatChannel.Reply: return "RecordChatList";
                default: return "AllChatList";
            }
        }

        private void OnRoomChatMessage(ChatMessage m)
        {
            // 只有原本就停在底部才自動跳到最新；若使用者往前捲看舊訊息，新訊息不搶捲，等他自己捲回底部才恢復。
            bool follow = ShouldShowChatMessage(m) && IsChatFollowingBottom();
            AddRoomChatLine(m);
            if (follow) ScrollRoomChatToBottom();
            // 誰的話該彈泡、泡是誰的,規則收在 RoomChatCommand.TryResolveBubbleOwner:文字提示行不彈、
            // 大廳假人的閒聊在房間裡不彈(它跟左下訊息欄一樣被作用域擋掉),別人的話要有 userId 才認得出主人。
            if (RoomChatCommand.TryResolveBubbleOwner(m, _chatScopeRoomId, out var owner)
                // owner 0 = 本機。遠端要先確認「他真的有一隻 3D 角色」—— 沒有角色就沒有肩膀可以掛泡。
                && (owner == 0 || (_scene != null && _scene.HasRemote(owner))))
            {
                // 旁觀的人只能用左下打字框(規則見 RoomBubblePolicy):頭上泡與關鍵字動作都不給,
                // 他的話純粹以文字進左下訊息欄。
                if (RoomBubblePolicy.CanEmoteInRoom(RoomBubblePolicy.SpeakerIsSpectator(m, LocalSpectating, RoomSnapshot)))
                {
                    ShowRoomChatBubble(m, owner);
                    PlayRoomChatAction(m, owner);
                }
            }
        }

        /// <param name="owner">0 = 本機;其餘 = 遠端玩家的 userId(動作播在他的角色上)。</param>
        private void PlayRoomChatAction(ChatMessage m, int owner = 0)
        {
            if (m == null || string.IsNullOrEmpty(m.RoomActionId)) return;
            if (!RoomChatCommand.TryGetRoomAction(m.RoomActionId, out var action) || action == null) return;
            // Gender picks BOTH the motion clip and the SE — same gender the id was parsed with (see MockChatService),
            // so female "再見"→action5→WREST0063+WOMAN_5 while male "88"→action6→MREST0076+MAN_6 stay self-consistent.
            // 🔴 性別要用**發言者**的,不是本機玩家的:動作 id 是收端用發言者性別解出來的
            // (見 ChatMessage.SenderMale 的註解),這裡若用本機性別就會「用女生的 id 播男生的動作」。
            // 離線模式 SenderMale 由 MockChatService 填,值與這裡原本的本機性別相同 → 行為不變。
            bool male = m.SenderMale;
            string motion = action.MotionFor(male);
            if (!string.IsNullOrEmpty(motion))
            {
                if (owner == 0)
                {
                    if (_scene != null) _scene.PlayChatAction(motion);
                    if (_localHead != null) _localHead.PlayChatAction(motion);   // 上面的頭貼跟著做同一個動作
                }
                else if (_scene != null) _scene.PlayRemoteChatAction(owner, motion);
            }
            UiSfx.Play(action.SoundFor(male));   // 語音房內所有人都聽得到(官方也是)
        }

        // 左下聊天：一整行帶黑邊的 rich 文字（VLG block，行高 16）。回傳 face TMP 供掛名字點擊。
        // 🔴 排版高度**不能寫死 16** —— 訊息長到折行時,一行的位置容不下兩行:第二行會壓在下一則訊息上,
        //    捲到底也只捲得到第一行(使用者回報的兩個症狀)。折了幾行由 TMP 量(見 ChatLineMetrics)。
        private TextMeshProUGUI ChatLine(string name, string rich)
        {
            // 長串英數(888888…／一長串英文)對 TMP 是「一個單字」，塞不下就整串跳到下一排、
            // 這一排卻空著 —— 先給它可折點(見 ChatSoftWrap)。量高度也要用同一份字串。
            rich = ChatSoftWrap.Apply(rich);
            var ol = OutlinedLabel.CreateRich(_chatContent, name, rich, 13, Color.black, ChatEdgePx, ChatEdgeDirs,
                true, TextAlignmentOptions.TopLeft);
            UIKit.Layout(ol.gameObject, ChatLineBlockHeight(ol.Face, rich));
            return ol.Face;
        }

        /// <summary>一則訊息在 <c>VerticalLayoutGroup</c> 裡要佔多高:不限寬量一次(＝保證不折的單行高)、
        /// 限 <see cref="ChatLineWrapW"/> 再量一次(＝折行後的實際總高),交給 <see cref="ChatLineMetrics"/> 換算。</summary>
        private static float ChatLineBlockHeight(TextMeshProUGUI face, string rich)
        {
            if (face == null) return ChatLineH;
            float one = face.GetPreferredValues(rich).y;
            float wrapped = face.GetPreferredValues(rich, ChatLineWrapW, 0f).y;
            return ChatLineMetrics.BlockHeight(wrapped, one, ChatLineH);
        }

        // 行內 emoji 行的一格帶黑邊 rich 文字（HLG cell）：holder 依實測字寬掛 LayoutElement。回傳 face TMP。
        private TextMeshProUGUI ChatCell(Transform row, string name, string rich, float flexibleWidth)
        {
            var ol = OutlinedLabel.CreateRich(row, name, rich, 13, Color.black, ChatEdgePx, ChatEdgeDirs,
                true, TextAlignmentOptions.MidlineLeft);
            var le = ol.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 18f;
            le.preferredWidth = ol.Face.GetPreferredValues(280f, 18f).x + 2f;   // 實測顯示字寬(含 escape 字面)貼齊 HLG
            le.flexibleWidth = flexibleWidth;
            return ol.Face;
        }

        private void AddRoomChatLine(ChatMessage m)
        {
            if (_chatContent == null || m == null) return;
            if (!ShouldShowChatMessage(m)) return;

            if (m.Notice != ChatNotice.None) { AddRoomChatNoticeLine(m); return; }
            if (m.Guild) { AddRoomChatGuildLine(m); return; }
            if (m.Stage != StageEventKind.None) { AddRoomChatStageLine(m); return; }
            if (m.Whisper != WhisperKind.None) { AddRoomChatWhisperLine(m); return; }

            if (!m.System && m.ExpressionId > 0)
            {
                AddRoomChatExpressionLine(m);
                return;
            }

            // 一般行：名字改成白色（原本是 #7FB6FF 藍）；名字可點 → 密語（見 WhisperNameLink / ChatWhisperLinkHandle）。
            string line = m.System
                ? "<color=#" + ChatSystemHex + ">" + EscapeTmp(m.Text) + "</color>"
                : WhisperNameLink(m) + ": " + EscapeTmp(ChatLineText(m));
            EnableWhisperNameClicks(ChatLine("line", line), m);
        }

        // 進出舞台廣播（顏色 #72c1fe）：「X 進入舞台遊戲」/「X 離開舞台」。
        private void AddRoomChatStageLine(ChatMessage m)
        {
            string key = m.Stage == StageEventKind.Enter ? "room.stage_enter" : "room.stage_leave";
            string text = LocalizationManager.Get(key, m.Sender ?? "");
            ChatLine("stageLine", "<color=#" + StageHex + ">" + EscapeTmp(text) + "</color>");
        }

        // 本機提示行：你說: xxx（好友頻道沒帶名字，白字）／你沒有家族（家族頻道無家族，綠字＝與家族訊息同色）。本機專屬、不彈泡。
        private void AddRoomChatNoticeLine(ChatMessage m)
        {
            string text, hex;
            if (m.Notice == ChatNotice.NoGuild)
            {
                text = LocalizationManager.Get("room.no_guild");
                hex = GuildHex;   // 「你沒有家族」用家族綠字
            }
            else   // SelfTalk：「你說: {內容}」
            {
                text = LocalizationManager.Get("room.selftalk", m.Text ?? "");
                hex = "FFFFFF";
            }
            ChatLine("noticeLine", "<color=#" + hex + ">" + EscapeTmp(text) + "</color>");
        }

        // 家族頻道綠字行：「<家族>名字: 內容」。此環境的 TMP 不會把 &lt;/&gt; 解碼回 <>（會印出字面），
        // 所以固定前綴 <家族> 改用 <noparse> 包住原字（不被當標籤、也不被解碼）；名字/內容仍走 EscapeTmp。名字可點密語（別人才可點）。
        private void AddRoomChatGuildLine(ChatMessage m)
        {
            // 家族頻道打的表情：畫 emoji 小動畫（綠字 + <家族> 前綴照舊），不要落成 "/翻" 那串字。
            if (m.ExpressionId > 0)
            {
                AddRoomChatExpressionLine(m, "<noparse>" + RoomChatCommand.GuildTag + "</noparse>", GuildHex);
                return;
            }
            string open = "<color=#" + GuildHex + ">";
            string tag = "<noparse>" + RoomChatCommand.GuildTag + "</noparse>";
            string line = open + tag + WhisperNameLink(m) + ": " + EscapeTmp(ChatLineText(m)) + "</color>";
            EnableWhisperNameClicks(ChatLine("guildLine", line), m);
        }

        // 密語行（顏色 #1efefe）：Outgoing 你對X說 / Incoming X對你說 / OffChannel 不在當前頻道 / NoId 無此id。
        private void AddRoomChatWhisperLine(ChatMessage m)
        {
            string party = m.WhisperParty ?? "";
            // 帶表情的密語（[X] /GO）：畫「前綴 + inline emoji」而非純文字。前綴＝把 loc 模板的內容欄位填空得到。
            if (m.ExpressionId > 0 && (m.Whisper == WhisperKind.Outgoing || m.Whisper == WhisperKind.Incoming))
            {
                string key = m.Whisper == WhisperKind.Outgoing ? "room.whisper_out" : "room.whisper_in";
                AddRoomChatWhisperExpressionLine(m, LocalizationManager.Get(key, party, ""));
                return;
            }

            ChatLine("whisperLine", "<color=#" + WhisperHex + ">" + EscapeTmp(ChatDisplay.WhisperText(m)) + "</color>");
        }

        // 帶 inline emoji 的密語行：前綴(你對X說: / X對你說:)+指令前字 + emoji 小動畫 + 指令後字，整行 #1efefe。
        private void AddRoomChatWhisperExpressionLine(ChatMessage m, string prefix)
        {
            var row = UIKit.NewRect(_chatContent, "whisperExprLine");
            UIKit.Layout(row.gameObject, 18);
            var hlg = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.spacing = 2f;
            hlg.padding = new RectOffset(0, 0, 0, 0);

            string open = "<color=#" + WhisperHex + ">";
            string lead = ExpressionLeadingText(m);
            string headPlain = prefix + lead;
            ChatCell(row, "head", open + EscapeTmp(headPlain) + "</color>", 0f);

            var frames = RoomExpressionArt.SmallFrames(m.ExpressionId);
            if (frames != null && frames.Length > 0)
            {
                var icon = UIKit.AddImage(row, "expr", Color.white);
                icon.preserveAspect = true;
                icon.raycastTarget = false;
                var iconLe = icon.gameObject.AddComponent<LayoutElement>();
                iconLe.preferredWidth = 16f;
                iconLe.preferredHeight = 16f;
                iconLe.flexibleWidth = 0f;
                icon.rectTransform.sizeDelta = new Vector2(16f, 16f);
                var anim = icon.gameObject.AddComponent<SpriteSeqAnim>();
                anim.Fps = 8f;
                anim.SetFrames(frames, restart: true);
            }
            else
            {
                ChatCell(row, "cmd",
                    open + EscapeTmp(RoomChatCommand.ExpressionDisplayText(m.ExpressionId)) + "</color>", 0f);
            }

            string trail = (m.Text ?? "").Trim();
            if (trail.Length > 0)
                ChatCell(row, "trail", open + " " + EscapeTmp(trail) + "</color>", 1f);
        }

        // 別人講的一般/表情行 → 名字包成可點的 TMP link（點了把 [名字] 塞進輸入框密語）。本機自己的名字不可點。
        private string WhisperNameLink(ChatMessage m)
        {
            string name = EscapeTmp(m.Sender);
            if (m == null || m.Local || string.IsNullOrEmpty(m.Sender)) return name;
            return "<link=\"" + WhisperLinkId + EscapeTmp(m.Sender) + "\">" + name + "</link>";
        }

        // 讓聊天列可接收點擊並解析 TMP link（名字）→ 觸發密語目標插入。
        private void EnableWhisperNameClicks(TextMeshProUGUI t, ChatMessage m)
        {
            if (t == null || m == null || m.Local || string.IsNullOrEmpty(m.Sender)) return;
            t.raycastTarget = true;
            var h = t.gameObject.AddComponent<ChatWhisperLinkHandle>();
            h.Owner = this;
            h.Text = t;
        }

        // 表情訊息：左下聊天列顯示「暱稱:」+ S_Expression 小動畫，不要落成 /無聊 文字。
        /// <param name="tagPrefix">名字前的固定標記（家族行的 <c>&lt;家族&gt;</c>，已自行包好 noparse）；null = 沒有。</param>
        /// <param name="hex">整行的顏色（家族行是綠字）；null = 沿用預設白字。</param>
        private void AddRoomChatExpressionLine(ChatMessage m, string tagPrefix = null, string hex = null)
        {
            string Tint(string s) => string.IsNullOrEmpty(hex) ? s : "<color=#" + hex + ">" + s + "</color>";
            var row = UIKit.NewRect(_chatContent, "exprLine");
            UIKit.Layout(row.gameObject, 18);
            var hlg = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.childAlignment = TextAnchor.MiddleLeft;
            // childControlWidth 必須 true：false 時 HLG 用子物件「實際 RectTransform 寬」(NewRect 預設 100px)排版，
            // 名字(我:)會佔滿 100px → emoji 被推到名字右邊很遠。true 才改用 LayoutElement.preferredWidth(實測字寬)貼齊。
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.spacing = 2f;
            hlg.padding = new RectOffset(0, 0, 0, 0);

            // 名字白色（原本 #7FB6FF 藍）+ 可點密語（別人才可點）。<link> 是零寬標記，量寬不受影響。
            string label = Tint((tagPrefix ?? "") + WhisperNameLink(m) + ":");
            EnableWhisperNameClicks(ChatCell(row, "name", label, 0f), m);

            // 指令前的字：排在名字後、emoji 前（保留輸入時 emoji 的位置：前字〔emoji〕後字）。
            string lead = ExpressionLeadingText(m);
            if (lead.Length > 0)
                ChatCell(row, "lead", Tint(EscapeTmp(lead)), 0f);

            var frames = RoomExpressionArt.SmallFrames(m.ExpressionId);
            bool hasFrames = frames != null && frames.Length > 0;
            if (hasFrames)
            {
                var icon = UIKit.AddImage(row, "expr", Color.white);
                icon.preserveAspect = true;
                icon.raycastTarget = false;
                var iconLe = icon.gameObject.AddComponent<LayoutElement>();
                iconLe.preferredWidth = 16f;
                iconLe.preferredHeight = 16f;
                iconLe.flexibleWidth = 0f;
                icon.rectTransform.sizeDelta = new Vector2(16f, 16f);
                var anim = icon.gameObject.AddComponent<SpriteSeqAnim>();
                anim.Fps = 8f;
                anim.SetFrames(frames, restart: true);
            }
            else
                ChatCell(row, "cmd", Tint(EscapeTmp(RoomChatCommand.ExpressionDisplayText(m.ExpressionId))), 0f);

            // 尾隨任意字（中文／英文／數字／標點），舊訊息 Text=/指令 不算尾隨。
            if (HasExpressionTrailingText(m))
                ChatCell(row, "trail", Tint(" " + EscapeTmp(m.Text.Trim())), 1f);
        }

        // 表情指令「前面」的字（顯示在 emoji 前）。空白／非表情訊息回 ""。
        private static string ExpressionLeadingText(ChatMessage m)
            => m != null && m.ExpressionId > 0 && !string.IsNullOrWhiteSpace(m.LeadingText) ? m.LeadingText.Trim() : "";

        private static bool HasExpressionTrailingText(ChatMessage m)
        {
            if (m == null || m.ExpressionId <= 0) return false;
            string t = (m.Text ?? "").Trim();
            if (t.Length == 0) return false;
            // 舊訊息把指令本身當 Text（如 "/無聊"）→ 不當尾隨顯示。
            if (RoomChatCommand.TryParseExpression(t, out var id, out var trail)
                && id == m.ExpressionId && string.IsNullOrEmpty(trail))
                return false;
            if (string.Equals(t, RoomChatCommand.ExpressionDisplayText(m.ExpressionId),
                    System.StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        private bool ShouldShowChatMessage(ChatMessage m)
        {
            if (m == null) return false;
            // 「當前」＝綜合台：家族綠字/好友你說/密語/進出舞台/一般聊天全部看得到；家族/好友分頁只看各自類別。
            bool all = _chatChannel == ChatChannel.Current;
            // 本機提示行/家族訊息跨作用域（不看房號），在對應分類＋當前綜合台顯示：
            //   你說（SelfTalk）→ 好友(＋當前)；你沒有家族（NoGuild）→ 家族(＋當前)；家族訊息（Guild）→ 家族(＋當前)。
            if (m.Notice == ChatNotice.SelfTalk) return all || _chatChannel == ChatChannel.Friend;
            if (m.Notice == ChatNotice.NoGuild) return all || _chatChannel == ChatChannel.Family;
            if (m.Guild) return all || _chatChannel == ChatChannel.Family;
            // 密語跨大廳/房間：不受作用域限制，出現在「當前」與「好友」頻道。
            if (m.Whisper != WhisperKind.None)
                return all || _chatChannel == ChatChannel.Friend;
            // 其餘（一般聊天/系統/進出廣播）只顯示本房間，隔離別房與大廳訊息。
            if (m.Scope != ChatScope.Room || m.RoomId != _chatScopeRoomId) return false;
            if (m.System) return true;
            // 進出舞台廣播：只在「當前」綜合台顯示，其他分類過濾掉。
            if (m.Stage != StageEventKind.None) return all;
            return all || m.Channel == _chatChannel;
        }

        private void ScrollRoomChatToBottom()
        {
            if (_chatScroll == null) return;
            Canvas.ForceUpdateCanvases();
            _chatScroll.verticalNormalizedPosition = 0f;
            if (_chatClip != null) _chatClip.Refresh();   // 立刻按新位置整行裁切，別等下一幀（會閃一格半截字）
        }

        // 是否停在（貼近）底部：內容還不足以捲動時一律視為在底部。0 = 底部（見 ScrollRoomChatToBottom）。
        // 於「加入新訊息之前」呼叫 → verticalNormalizedPosition 反映使用者當下的位置。
        private bool IsChatFollowingBottom()
        {
            if (_chatScroll == null) return true;
            var content = _chatScroll.content;
            var viewport = _chatScroll.viewport;
            if (content == null || viewport == null) return true;
            if (content.rect.height <= viewport.rect.height + 1f) return true;   // 不能捲 → 跟隨
            return _chatScroll.verticalNormalizedPosition <= 0.02f;
        }

        // ---- DEV: SDO_SAY=<文字> → 進房後定期自動說一次那句話 ------------------------------------------------
        // 為什麼需要這個 hook:頭上泡的東西(尤其「泡與泡之間誰蓋誰」)只能實機截圖驗,而「點空曠處 → 打字 → Enter」
        // 用注入按鍵驅動太脆 —— 實測進得去打字模式、游標也在閃,但一個字都沒進去(看起來像輸入框壞了)。
        // 這裡刻意走 SendRoomChat(),與使用者真的按 Enter 完全同一條路(頻道解析、泡生成、上網),
        // 所以截到的畫面是真的,不是為了測試另外搭的假路徑。只有設了環境變數才會動。
        private float _devSayAt = -1f;
        private string _devSayText;
        private bool _devSayResolved;

        private void TickDevAutoSay()
        {
            if (!_devSayResolved)
            {
                _devSayResolved = true;
                _devSayText = ScreenGameplay.DevVar("SDO_SAY");
            }
            if (string.IsNullOrEmpty(_devSayText) || _chatInput == null || Ctx == null || Ctx.Chat == null) return;
            if (Ctx.Flow != null && Ctx.Flow.Current != ScreenId.Room) return;
            float now = Time.unscaledTime;
            if (_devSayAt < 0f) { _devSayAt = now + 4f; return; }   // 進房後先等一下,等連線/座位就位
            if (now < _devSayAt) return;
            _devSayAt = now + DevSayEverySec;
            _chatInput.text = _devSayText;
            SendRoomChat();
        }

        private const float DevSayEverySec = 6f;   // 泡的壽命比這個長 → 畫面上一直有泡可看

        // DEV: SDO_AUTOREADY=1 → 非房主一進房就自動按「準備」。
        // 同 SDO_SAY 的理由:同步進場只能兩開實機驗,而「用注入的滑鼠點右下那顆圓鈕」需要精確的
        // 設計座標→螢幕座標換算(目前那條換算有一個還沒查清的水平偏移)。走 OnReadyToggle
        // 就是玩家真的按下去的同一條路。只有設了環境變數才會動。
        private bool _devAutoReadyDone;
        private float _devAutoReadyAt = -1f;

        // DEV: SDO_AUTOSTART=1 → 房主自動按「開始」,每 2 秒重試一次直到這一場真的開始。
        // 重試同時也把「第一次只提示、1.5 秒內再按才強制開始」那條路走完 —— 所以不需要另外傳 force。
        private float _devAutoStartAt = -1f;

        private void TickDevAutoStart()
        {
            if (string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_AUTOSTART"))) return;
            if (_starting || _awaitingMatchStart) return;
            // 線上要是房主才按得動;**離線單人房也要能用** —— 效能量測(SDO_DANCERS)是離線跑的,
            // 而它需要有人把遊戲開起來。原本這裡直接 `if (!Online) return;`,結果離線那幾組
            // 一行都沒量到(client 一直停在房間)。
            if (Online && !Ctx.Net.IsHost) return;
            var room = Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
            if (room == null || string.IsNullOrEmpty(room.SongTitle)) return;
            // 🔴 線上要等第二個人真的坐下來才開始。不等的話房主會在自己還在開機的那幾秒就 solo 開場,
            //    等別人加入時房間已經是 playing → 他的 join 被 R18 以 inGame 拒絕(而且畫面上看不出來)。
            //    離線沒有別人可以等,所以這條只在線上成立。
            if (Online && SeatedPlayerCount(room) < 2)
            {
                if (Time.frameCount % 300 == 0) Debug.Log("[dev] SDO_AUTOSTART 還在等第二個人坐下");
                return;
            }
            if (_devAutoStartAt < 0f) { _devAutoStartAt = Time.unscaledTime + 6f; return; }   // 等他按完準備
            if (Time.unscaledTime < _devAutoStartAt) return;
            _devAutoStartAt = Time.unscaledTime + 2f;
            OnStart();
        }

        // DEV: SDO_CLOSESEATS=1 → 房主把**自己以外的座位全部關掉**,做出一個「座位滿了」的房間。
        // 為什麼需要:要驗「座位滿了會自動改用旁觀身分進去」得先有一間滿的房,而湊六台 client
        // 不現實。關閉的座位在 FirstOpenSeat 眼中與被坐走完全一樣(都不是 Open),
        // 所以這條 hook 造出來的「滿」與六個人坐滿是同一個狀態。
        private bool _devCloseSeatsDone;

        private void TickDevCloseSeats()
        {
            if (_devCloseSeatsDone) return;
            if (string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_CLOSESEATS"))) { _devCloseSeatsDone = true; return; }
            if (!Online || !Ctx.Net.IsHost) return;
            var snap = Ctx.Net.Room;
            if (snap == null) return;

            int mySeat = snap.SeatIndexOf(Ctx.Net.UserId);
            if (mySeat < 0) return;                       // 還沒坐下(快照還沒到)
            _devCloseSeatsDone = true;
            for (int i = 0; i < snap.Seats.Length; i++)
            {
                if (i == mySeat) continue;                // 自己的位子關不了(server 會回 badSeat)
                Ctx.Net.SetSeatClosed(i, true);
            }
            Debug.Log("[dev] SDO_CLOSESEATS:把座位 " + mySeat + " 以外的位子都關了(做出滿房)");
        }

        // DEV: SDO_TEAM=<0..3> → 一進房就把自己分到那一隊(0=A 1=B 2=C 3=自由)。
        // 為什麼需要:頭貼上的 READY / HOST 徽章要**依隊伍換色**,而要驗那件事得先讓兩台
        // client 各自選到不同的隊 —— 用滑鼠自動化去點那四格得先做設計→螢幕座標換算
        // (那條換算有已知偏移,不可靠)。這條走與玩家按下去**完全同一條路徑**(PickOwnTeam →
        // server 的 setOwnTeam),所以驗出來的畫面就是玩家會看到的畫面。
        private bool _devTeamDone;

        private void TickDevTeam()
        {
            if (_devTeamDone) return;
            var want = ScreenGameplay.DevVar("SDO_TEAM");
            if (string.IsNullOrEmpty(want)) { _devTeamDone = true; return; }
            int team;
            if (!int.TryParse(want, out team) || team < 0 || team > (int)TeamTag.Free)
            {
                _devTeamDone = true;
                Debug.LogWarning("[dev] SDO_TEAM 要是 0..3(0=A 1=B 2=C 3=自由),收到:" + want);
                return;
            }
            // 線上要等自己真的坐上位子(server 才收得下 setOwnTeam);離線直接設就好。
            if (Online && (Ctx.Net == null || Ctx.Net.Room == null || Ctx.Net.Room.SeatIndexOf(Ctx.Net.UserId) < 0)) return;
            _devTeamDone = true;
            PickOwnTeam(team);
            Debug.Log("[dev] SDO_TEAM:自己分到隊伍 " + team);
        }

        // DEV: SDO_PICKSONG=<歌名片段> → 房主自動選「第一首名字含這段字的外部歌」。
        // 為什麼要這個 hook:缺歌傳檔(M5)的實機驗證需要房主選一首**外部歌**,而用滑鼠自動化走
        // 選歌畫面要精確的設計→螢幕座標換算(那條換算有已知偏移),不可靠。這條走與玩家一樣的
        // 「填 session → SetSong → Publish」路徑,但**只填傳檔需要的欄位** —— 它不是完整的選歌
        // (要真的進遊戲玩那首歌還是得走 SongSelectScreen.OnConfirm)。
        private bool _devPickSongDone;

        private void TickDevPickSong()
        {
            if (_devPickSongDone) return;
            var want = ScreenGameplay.DevVar("SDO_PICKSONG");
            if (string.IsNullOrEmpty(want)) { _devPickSongDone = true; return; }
            if (!Online || !Ctx.Net.IsHost) return;
            var room = Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
            if (room == null) return;                                   // 還沒進房
            // 🔴 這裡刻意**不**檢查「房間已經有歌」。session 會記著上次選的歌,而
            //    NetSongPublisher.PublishIfRoomHasNone 在進房那一刻就把它發出去了 →
            //    加了那個檢查的話這個 hook 永遠不會動(實測就是這樣:房間變成上次那首官方歌,
            //    而 log 裡一行都沒有,看起來像 hook 壞了)。SDO_PICKSONG 是明確的指令,要蓋過去。
            if (Sdo.Game.ExternalSongLibrary.Scanning) return;          // 等歌庫掃完

            var all = Sdo.Game.SongCatalog.All;
            Sdo.Game.SongCatalog.Entry hit = null;
            if (all != null)
                for (int i = 0; i < all.Count; i++)
                {
                    var e = all[i];
                    if (e == null || !e.external || string.IsNullOrEmpty(e.title)) continue;
                    if (e.title.IndexOf(want, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    hit = e; break;
                }
            if (hit == null)
            {
                if (Time.frameCount % 300 == 0) Debug.Log("[dev] SDO_PICKSONG 找不到外部歌:" + want);
                return;
            }

            _devPickSongDone = true;
            var s = Ctx.Session;
            s.SongGn = hit.gn;
            s.SongFileId = hit.fileId;
            s.SongTitle = hit.title;
            s.SongArtist = hit.artist ?? "";
            s.SongIsRandom = false;
            s.IsExternalSong = true;
            s.ExternalFolderPath = hit.folderPath ?? "";
            s.ExternalSongKey = hit.songKey ?? "";
            s.ExternalPackId = hit.packId ?? "";             // 生成舞蹈的 seed（見 Sdo.Game.ExternalDps）
            s.ExternalChartFormat = hit.chartFormat;
            s.ExternalAudioPath = hit.audioPath ?? "";
            // 譜面路徑一定要填:協定要求外部歌帶 ChartRelPath(空的話 server 直接回
            // badState「bad song ref」,而畫面上只看到「選了歌但房間沒歌」)。取難度槽 0。
            s.Difficulty = Difficulty.Easy;
            s.ExternalChartPath = hit.ChartPath(0);
            s.ExternalChartIndex = hit.ChartIndex(0);
            s.ExternalChartSeed = hit.chartSeed;
            s.ExternalDpsPath = hit.dpsPath ?? "";
            s.ExternalLevel = hit.DisplayLevel(0);
            s.ExternalSongBpm = hit.bpm;                     // 生成編舞的節拍網格：整首歌一個 BPM，換難度不會換舞
            // 生成編舞要量「這首歌所有難度」的頭尾（不是只有選到這張）—— 三個格子照原順序帶過去，空的留 ""
            s.ExternalSongChartPaths = new[] { hit.ChartPath(0), hit.ChartPath(1), hit.ChartPath(2) };
            s.ExternalSongChartIndices = new[] { hit.ChartIndex(0), hit.ChartIndex(1), hit.ChartIndex(2) };
            Debug.Log("[dev] SDO_PICKSONG 選了外部歌:" + hit.title + "(packId=" + (hit.packId ?? "(空)")
                      + " 譜=" + s.ExternalChartPath + ")");
            Ctx.Rooms.SetSong(s.SongTitle);
            NetSongPublisher.Publish(Ctx);
        }

        private void TickDevAutoReady()
        {
            if (_devAutoReadyDone) return;
            if (string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_AUTOREADY"))) { _devAutoReadyDone = true; return; }
            if (!Online || Ctx.Net.IsHost) return;                              // 房主沒有「準備」這個狀態
            var room = Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
            if (room == null || string.IsNullOrEmpty(room.SongTitle))
            {
                if (Time.frameCount % 180 == 0) Debug.Log("[dev] SDO_AUTOREADY 還在等:房間沒有歌");
                return;   // 還沒選歌 → 按了會被 R17 擋掉
            }
            if (_devAutoReadyAt < 0f) { _devAutoReadyAt = Time.unscaledTime + 3f; return; }   // 等座位/歌同步好
            if (Time.unscaledTime < _devAutoReadyAt) return;
            _devAutoReadyDone = true;
            Debug.Log("[dev] SDO_AUTOREADY:按下準備");
            if (!LocalReady(room)) OnReadyToggle();
        }

        /// <summary>家族頻道的內容 —— 是表情指令（<c>/翻</c>）就帶著 expressionId 送，否則當純文字。
        /// 不這樣分，家族頻道打的 emoji 會以字面 "/翻" 送出去，收端也就只印得出那串字。
        /// 遊戲畫面的聊天框走 <c>FrontendApp.SendGuildText</c>，同一套規則。</summary>
        private void SendGuildText(string body)
        {
            if (RoomChatCommand.TryParseExpression(body, out var eid, out var lead, out var trail))
                Ctx.Chat.SendGuildExpression(eid, lead, trail);
            else Ctx.Chat.SendGuild(body);
        }

        private void SendRoomChat()
        {
            if (_chatInput == null || Ctx == null || Ctx.Chat == null) return;
            // 中文 IME 還沒選完 / 剛用 Enter 選字：onSubmit 會誤觸，不要送、也不要清 draft。
            if (IsRoomChatImeComposing() || _chatImeComposing) return;
            string txt = _chatInput.text;
            if (string.IsNullOrWhiteSpace(txt))
            {
                // 空字串按 Enter（或送出鈕）→ 退出打字：bubble 與輸入框模式皆然（取代舊的「空了再按 Backspace 退出」）。
                if (_chatBubbleTyping || _chatBubbleInputArmed || _chatInput.isFocused)
                    CancelRoomChatTyping();
                return;
            }
            // 依頻道決定「要送什麼」+「送出後留在輸入框的前綴(postDraft)」。sendAction==null → 不送、續打。
            //   家族：剝掉「/家族」→ SendGuild（有家族=綠字、沒家族=你沒有家族）；前綴「/家族 」留下。
            //   好友：帶 [名字]→密語（[名字] 前綴留下）；沒帶名字→SendSelfTalk（白字「你說: xxx」本機專屬）。
            //   當前/回覆：原本行為（密語 > 表情 > 一般）。
            System.Action sendAction = null;
            string postDraft = "";
            // 頭上泡打字（_chatBubbleTyping／送出後 armed 續打）＝一律「一般說話」：不論左下頻道選在家族/好友，氣泡打字
            // 都走當前頻道、彈頭上藍泡，不被劫走成家族綠字或密語。家族/好友專屬訊息只在「輸入框回顯」模式打
            // （頻道選單自動填「/家族 」前綴、或直接點左下輸入框）。見 RoomChatCommand.ResolveSendChannel。
            bool bubbleMode = _chatBubbleTyping || _chatBubbleInputArmed;
            ChatChannel route = RoomChatCommand.ResolveSendChannel(bubbleMode, _chatChannel);
            switch (route)
            {
                case ChatChannel.Family:
                {
                    string body = RoomChatCommand.StripGuildCommand(txt);
                    if (string.IsNullOrWhiteSpace(body)) return;   // 只有「/家族 」還沒打內容 → 續打
                    sendAction = () => SendGuildText(body);
                    postDraft = RoomChatCommand.GuildCommandPrefix;
                    break;
                }
                case ChatChannel.Friend:
                {
                    if (RoomChatCommand.TryParseWhisper(txt, out var target, out var body))
                    {
                        if (string.IsNullOrWhiteSpace(body)) return;   // 只選了對象還沒打內容 → 續打
                        sendAction = () => Ctx.Chat.SendWhisper(target, body, ChatChannel.Friend);
                        postDraft = "[" + target + "] ";   // [名字] 前綴留下，繼續密語同一人
                    }
                    else
                    {
                        sendAction = () => Ctx.Chat.SendSelfTalk(txt);   // 沒帶 [名字] → 你說: xxx（不送任何人、不彈泡）
                    }
                    break;
                }
                default:
                {
                    // 「當前」綜合台：明打「/家族 …」前綴 → 送家族綠字（本頁也看得到），前綴留著接著打；
                    // 沒前綴才照密語 > 表情 > 一般說話（一般說話彈頭上藍泡）。
                    if (RoomChatCommand.TryStripGuildCommand(txt, out var guildBody))
                    {
                        if (string.IsNullOrWhiteSpace(guildBody)) return;   // 只有「/家族 」還沒打內容 → 續打
                        sendAction = () => SendGuildText(guildBody);
                        postDraft = RoomChatCommand.GuildCommandPrefix;
                        break;
                    }
                    bool isWhisper = RoomChatCommand.TryParseWhisper(txt, out var target, out var body);
                    if (isWhisper && string.IsNullOrWhiteSpace(body)) return;   // 只選了對象還沒打內容 → 續打
                    if (isWhisper) sendAction = () => Ctx.Chat.SendWhisper(target, body, route);
                    else if (RoomChatCommand.TryParseExpression(txt, out var eid, out var lead, out var trail))
                        sendAction = () => Ctx.Chat.SendExpression(eid, route, lead, trail);
                    else sendAction = () => Ctx.Chat.Send(txt, route);
                    break;
                }
            }
            if (sendAction == null) return;

            // 確定要送 → 收掉打字泡；記住是否 bubble 模式。
            bool keepBubbleInput = _chatBubbleTyping || _chatBubbleInputArmed;
            if (_chatBubbleTyping) HideRoomChatBubble();
            else _chatBubbleTyping = false;

            sendAction();
            HideChatModeMenu();
            HideExpressionMenu();
            _chatInput.text = postDraft;
            if (!string.IsNullOrEmpty(postDraft))
            {
                // 家族 / 密語：前綴留下，強制輸入框回顯模式、游標移到結尾接著打（不進頭上泡）。
                _chatInputSticky = true;
                SetRoomChatInputEchoVisible(true);
                FocusRoomChatInput();
            }
            else if (keepBubbleInput) ArmRoomBubbleInput();
            else { _chatInputSticky = true; FocusRoomChatInput(); }   // 輸入框模式送完保持 focus 續打，不退出
        }

        private void ShowRoomChatBubble(ChatMessage m, int ownerUserId = 0)
        {
            if (Root == null || m == null) return;
            // 舊泡保留：新泡另開一顆，串起來各自計時。
            // 🔴 只有**自己**送出時才收掉打字泡 —— 那是「我送出了 → 打字泡變成已送出泡」的語意。
            //    不加 owner 守門的話,別人講一句話就會把我正在打的字泡吃掉。
            if (ownerUserId == 0 && _chatBubbleTyping) HideRoomChatBubble();

            var bubble = SpawnSentRoomBubble(ownerUserId);
            bubble.PendingShow = true;
            bubble.ShownAt = Time.unscaledTime;   // 每顆泡以此為「年齡」起點，各自從肩錨往上飄
            bubble.HideAt = bubble.ShownAt + ChatBubbleLifetime;

            string lead = ExpressionLeadingText(m);
            string trail = HasExpressionTrailingText(m) ? (m.Text ?? "").Trim() : "";
            bool exprInline = m.ExpressionId > 0 && (lead.Length > 0 || trail.Length > 0);   // 表情 + 前/後字
            bool pureEmoji = m.ExpressionId > 0 && !exprInline;                              // 只有表情
            // 泡大小：純表情用固定小泡；表情+字用「前字 + emoji 寬 + 後字」估寬；一般訊息照原本量文字。
            // 🔴 量寬度用的字串要和**真正貼上去**的字串一致 —— 底下每一處 bubble.Text.text 都套了
            //    ChatSoftWrap(長串英數給可折點),這裡沒套的話會量到一個折不開的超寬單字 → 挑到最大的泡、字還是滿出去。
            string sizeText = pureEmoji
                ? ""
                : ChatSoftWrap.Apply(exprInline ? lead + "　　" + trail : ChatLineText(m));
            int style = pureEmoji ? 1 : RoomBubbleStyleForText(sizeText, bubble.Text);
            ApplySentBubbleStyle(bubble, style, entering: true);
            var enterFrames = RoomBubbleArt.EnterFrames(style);
            bubble.TalkAt = bubble.ShownAt + Mathf.Clamp((enterFrames != null ? enterFrames.Length : 0) / 12f, 0.5f, 1.2f);
            // pop 音節流:server 的聊天限制是每人 5 則/3 秒,六人房最壞 10 則/秒全部疊在同一個
            // AudioSource 上 → 音量疊加爆音。0.12s 以內只放一次。
            if (string.IsNullOrEmpty(m.RoomActionId) && Time.unscaledTime - _lastBubblePopAt >= 0.12f)
            {
                _lastBubblePopAt = Time.unscaledTime;
                UiSfx.Play(UiSfx.Bubble);
            }
            if (bubble.Add != null) bubble.Add.gameObject.SetActive(false);
            if (bubble.AddAnim != null) bubble.AddAnim.Frames = null;
            bubble.EmojiInlineLeadLen = -1;   // 預設不做行內 emoji 疊圖

            if (m.ExpressionId > 0)
            {
                var frames = RoomExpressionArt.SmallFrames(m.ExpressionId);
                bool hasFrames = frames != null && frames.Length > 0;

                if (hasFrames && !exprInline)
                {
                    // 純表情：只播小動畫（emoji 由 ApplySentBubbleStyle 置中）。
                    bubble.Text.gameObject.SetActive(false);
                    bubble.Expression.gameObject.SetActive(true);
                    bubble.ExpressionAnim.Frames = frames;
                    bubble.Expression.sprite = frames[0];
                }
                else if (hasFrames && lead.Length > 0)
                {
                    // 前面有字（「字 /GO」「字 /GO 字」）：emoji 疊在前字之後——前字 + 固定寬空檔（emoji 疊上）+ 後字。
                    // 用前字最後一格的 xAdvance 定位（characterInfo 一定有這格，與 <space> 是否成字無關）。
                    bubble.Expression.gameObject.SetActive(true);
                    bubble.ExpressionAnim.Frames = frames;
                    bubble.Expression.sprite = frames[0];
                    bubble.Text.gameObject.SetActive(true);
                    bubble.Text.alignment = TextAlignmentOptions.MidlineLeft;
                    // 🔴 只給 trail 可折點,lead 一個字都不能動 —— 下面的 EmojiInlineLeadLen 是用 lead 的**字數**
                    //    去 characterInfo 找 emoji 該疊在哪一格,塞了零寬空格就會整個位移。
                    bubble.Text.text = EscapeTmp(lead)
                        + "<space=" + ((int)BubbleEmojiGapPx) + ">"
                        + ChatSoftWrap.Apply(EscapeTmp(trail));
                    // emoji 掛到 Text 底下，用 characterInfo 座標定位（跟泡內游標同套機制）；泡活化後才有 mesh，故延後擺。
                    bubble.Expression.rectTransform.SetParent(bubble.Text.rectTransform, false);
                    bubble.EmojiInlineLeadLen = lead.Length;
                }
                else if (hasFrames)
                {
                    // 只有後字（「/GO 字」）：沿用原本穩定排版——emoji 靠左、字接在右邊。
                    bubble.Expression.gameObject.SetActive(true);
                    bubble.ExpressionAnim.Frames = frames;
                    bubble.Expression.sprite = frames[0];
                    bubble.Text.gameObject.SetActive(true);
                    bubble.Text.text = ChatSoftWrap.Apply(EscapeTmp(trail));
                    var tr = RoomBubbleArt.TextRect(bubble.Style);
                    Place(bubble.Expression.rectTransform, tr.x, tr.y + (tr.height - 24f) * 0.5f, 24, 24);
                    Place(bubble.Text.rectTransform, tr.x + 26f, tr.y, Mathf.Max(8f, tr.width - 26f), tr.height);
                    bubble.Text.alignment = TextAlignmentOptions.MidlineLeft;
                }
                else
                {
                    // 沒有小圖：退回文字指令，前後字照位置串起來。
                    bubble.Expression.gameObject.SetActive(false);
                    if (bubble.ExpressionAnim != null) bubble.ExpressionAnim.Frames = null;
                    bubble.Text.gameObject.SetActive(true);
                    string fb = RoomChatCommand.ExpressionDisplayText(m.ExpressionId);
                    if (lead.Length > 0) fb = lead + " " + fb;
                    if (trail.Length > 0) fb = fb + " " + trail;
                    bubble.Text.text = ChatSoftWrap.Apply(EscapeTmp(fb));
                }
            }
            else
            {
                bubble.Expression.gameObject.SetActive(false);
                if (bubble.ExpressionAnim != null) bubble.ExpressionAnim.Frames = null;
                bubble.Text.gameObject.SetActive(true);
                bubble.Text.text = ChatSoftWrap.Apply(EscapeTmp(ChatLineText(m)));
            }

            _sentBubbles.Add(bubble);
            // 防洗版:**per-owner** 計數。全域計數的話一個人洗頻會把別人的泡全踢光。
            int mine = 0;
            for (int i = _sentBubbles.Count - 1; i >= 0; i--)
                if (_sentBubbles[i] != null && _sentBubbles[i].OwnerUserId == bubble.OwnerUserId) mine++;
            while (mine > MaxBubblesPerOwner)
            {
                for (int i = 0; i < _sentBubbles.Count; i++)
                    if (_sentBubbles[i] != null && _sentBubbles[i].OwnerUserId == bubble.OwnerUserId)
                    { DestroySentRoomBubble(_sentBubbles[i]); break; }
                mine--;
            }
        }

        /// <summary>
        /// 某個人的泡住的那一層(lazily 建)。整層與 <see cref="_bubbleLayer"/> 同框、不吃滑鼠,
        /// 所以泡的座標仍是 800×600 的**絕對設計座標** —— 鏈物理/拖曳/命中一行都不用改。
        ///
        /// 一個人一層的唯一理由是**排序**:層與層之間每幀按各人的深度重排(見 SortBubbleOwnerLayers),
        /// 而同一個人的泡在自己層裡照生成順序疊。混在同一層的話「照站位排前後」就不可能做到 ——
        /// 那是使用者要的行為。
        /// </summary>
        private RectTransform BubbleOwnerLayer(int owner)
        {
            if (_bubbleLayer == null) return null;
            RectTransform rt;
            if (_bubbleOwnerLayer.TryGetValue(owner, out rt) && rt != null) return rt;
            rt = UIKit.NewRect(_bubbleLayer, "RoomBubbleOwner" + owner);
            UIKit.Stretch(rt);
            _bubbleOwnerLayer[owner] = rt;
            return rt;
        }

        /// <summary>
        /// 離開房間 → 收掉**遠端**那些人的層(他們的泡是動態生成的,跟著層一起走;
        /// 名字牌不在這裡面 —— 它掛在 Root 底下,由 ClearRemoteNamePlates 收)。
        /// 🔴 owner 0 那層留著:打字泡是 BuildUI 建一次的**常駐單例**,住在裡面(拆了就永遠沒有打字泡)。
        /// </summary>
        private void ClearRemoteBubbleLayers()
        {
            _bubbleScratchOwners.Clear();
            foreach (var kv in _bubbleOwnerLayer) if (kv.Key != 0) _bubbleScratchOwners.Add(kv.Key);
            for (int i = 0; i < _bubbleScratchOwners.Count; i++)
            {
                int owner = _bubbleScratchOwners[i];
                var rt = _bubbleOwnerLayer[owner];
                if (rt != null) Destroy(rt.gameObject);
                _bubbleOwnerLayer.Remove(owner);
            }
            _bubbleDepth.Clear();
        }

        private readonly List<int> _bubbleScratchOwners = new List<int>();

        private SentRoomBubble SpawnSentRoomBubble(int ownerUserId = 0)
        {
            // 代理:說話者自己那一層。鏈物理、拖曳、命中測試、壽命全部認它,而層是與 _bubbleLayer 同框的
            // 全畫布 rect → 座標仍是絕對設計座標(那些程式碼一行都不用改)。
            var root = UIKit.NewRect(BubbleOwnerLayer(ownerUserId) ?? _bubbleLayer, "RoomChatBubble");
            root.anchorMin = root.anchorMax = new Vector2(0f, 1f);
            root.pivot = new Vector2(0f, 1f);
            root.sizeDelta = new Vector2(RoomBubbleArt.CanvasW, RoomBubbleArt.CanvasH);

            var bubble = new SentRoomBubble { Root = root, OwnerUserId = ownerUserId };

            // 畫:貼著代理(位移永遠 0)。分成兩個物件是為了滑鼠命中 —— 見下面那張透明 Hit 圖的註解。
            var visual = UIKit.NewRect(root, "RoomChatBubbleArt");
            visual.anchorMin = visual.anchorMax = new Vector2(0f, 1f);
            visual.pivot = new Vector2(0f, 1f);
            visual.sizeDelta = new Vector2(RoomBubbleArt.CanvasW, RoomBubbleArt.CanvasH);
            bubble.Visual = visual;

            // 🔴 只有自己的泡能拖。拖曳狀態是**全域單一**的(_chatBubbleChainDragging /
            //    _chatBubbleDraggedSent),而拖住時的補償會延長**那條鏈上所有泡**的壽命 ——
            //    跨人就變成「你按住不放,別人的泡也不會消失」。官方也沒有拖別人的泡這回事。
            if (ownerUserId == 0)
            {
                var drag = root.gameObject.AddComponent<RoomBubbleDragHandle>();
                drag.Owner = this;
                drag.Sent = bubble;
                // 代理要有一個「看不見但收得到滑鼠」的圖 —— 拖曳事件靠它冒泡到上面那個 handle。
                // 不讓泡框自己收(Frame raycast:false):泡的畫會換 sprite/換大小,命中範圍就跟著抖。
                // 這張固定是整張 171×111,可命中的範圍與泡框的畫布完全相同。
                var hit = UIKit.AddImage(root, "Hit", new Color(0f, 0f, 0f, 0f), raycast: true);
                UIKit.Stretch(hit.rectTransform);
            }

            bubble.Frame = UIKit.AddImage(visual, "Frame", Color.white, raycast: false);
            UIKit.Stretch(bubble.Frame.rectTransform);
            UIKit.ApplySprite(bubble.Frame, RoomBubbleArt.Base(1));
            Place(bubble.Frame.rectTransform, 0, 0, RoomBubbleArt.CanvasW, RoomBubbleArt.CanvasH);
            bubble.FrameAnim = bubble.Frame.gameObject.AddComponent<SpriteSeqAnim>();
            bubble.FrameAnim.Fps = 12f;

            bubble.Add = UIKit.AddImage(visual, "AddAni", Color.white);
            UIKit.Stretch(bubble.Add.rectTransform);
            bubble.AddAnim = bubble.Add.gameObject.AddComponent<SpriteSeqAnim>();
            bubble.AddAnim.Fps = 14f;

            bubble.Text = UIKit.AddText(visual, "Text", "", 13, BubbleTextColor(ownerUserId), TextAlignmentOptions.MidlineLeft, true);
            Place(bubble.Text.rectTransform, 49, 43, 74, 28);
            bubble.Text.richText = true;
            bubble.Text.textWrappingMode = TextWrappingModes.Normal;
            bubble.Text.overflowMode = TextOverflowModes.Overflow;

            bubble.Expression = UIKit.AddImage(visual, "Expression", Color.white);
            bubble.Expression.raycastTarget = false;
            bubble.Expression.preserveAspect = true;
            Place(bubble.Expression.rectTransform, 73, 43, 24, 24);
            bubble.ExpressionAnim = bubble.Expression.gameObject.AddComponent<SpriteSeqAnim>();
            bubble.ExpressionAnim.Fps = 8f;

            SetBubbleActive(bubble, false);
            return bubble;
        }

        /// <summary>代理與畫要一起開關 —— 只關畫的話代理還在吃滑鼠(看不到的泡照樣被拖);只關代理的話泡還看得見。</summary>
        private static void SetBubbleActive(SentRoomBubble b, bool on)
        {
            if (b == null) return;
            if (b.Root != null && b.Root.gameObject.activeSelf != on) b.Root.gameObject.SetActive(on);
            if (b.Visual != null && b.Visual.gameObject.activeSelf != on) b.Visual.gameObject.SetActive(on);
        }

        private void ApplySentBubbleStyle(SentRoomBubble bubble, int style, bool entering)
        {
            if (bubble == null || bubble.Root == null) return;
            style = Mathf.Clamp(style, 1, 11);
            bubble.Style = style;
            var frames = entering ? RoomBubbleArt.EnterFrames(style) : null;
            var sprite = frames != null && frames.Length > 0 ? frames[0] : RoomBubbleArt.Base(style);

            bubble.Root.sizeDelta = new Vector2(RoomBubbleArt.CanvasW, RoomBubbleArt.CanvasH);
            if (bubble.Visual != null) bubble.Visual.sizeDelta = new Vector2(RoomBubbleArt.CanvasW, RoomBubbleArt.CanvasH);
            if (bubble.FrameAnim != null) bubble.FrameAnim.SetFrames(frames, restart: true, loop: !entering);
            if (bubble.Frame != null)
            {
                UIKit.ApplySprite(bubble.Frame, sprite);
                Place(bubble.Frame.rectTransform, 0, 0, RoomBubbleArt.CanvasW, RoomBubbleArt.CanvasH);
            }

            var textRect = RoomBubbleArt.TextRect(style);
            if (bubble.Text != null)
            {
                bubble.Text.fontSize = 13f;
                Place(bubble.Text.rectTransform, textRect.x, textRect.y, textRect.width, textRect.height);
            }
            if (bubble.Expression != null)
            {
                var tr = RoomBubbleArt.TextRect(style);
                float ex = tr.x + (tr.width - 24f) * 0.5f;
                float ey = tr.y + (tr.height - 24f) * 0.5f;
                Place(bubble.Expression.rectTransform, ex, ey, 24, 24);
            }
        }

        // 表情 + 字（前面有字）：泡活化後才有 mesh，這裡把 Expression 疊到「前字最後一格」之後。
        // 用該格的 xAdvance 當 emoji 左緣、ascender/descender 取垂直中線（跟泡內游標 UpdateBubbleCaretOverlay 同一套 characterInfo 定位）。
        private void LayoutSentBubbleInlineEmoji(SentRoomBubble b)
        {
            if (b == null || b.EmojiInlineLeadLen <= 0 || b.Text == null || b.Expression == null) return;
            b.Text.ForceMeshUpdate();
            var ti = b.Text.textInfo;
            if (ti == null || ti.characterCount <= 0) return;

            int idx = Mathf.Clamp(b.EmojiInlineLeadLen - 1, 0, ti.characterCount - 1);   // 前字最後一格
            var ci = ti.characterInfo[idx];
            float leftX = ci.xAdvance;                              // 前字右緣 = emoji 左緣
            float cy = (ci.ascender + ci.descender) * 0.5f;

            float size = BubbleEmojiSizePx;
            var rt = b.Expression.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(size, size);
            rt.localPosition = new Vector3(leftX + size * 0.5f + BubbleEmojiInlinePadX, cy + BubbleEmojiInlineOffY, 0f);
            b.EmojiInlineLeadLen = -1;   // 一次性擺好
        }

        private void DestroySentRoomBubble(SentRoomBubble bubble)
        {
            if (bubble == null) return;
            if (ReferenceEquals(_chatBubbleDraggedSent, bubble))
            {
                _chatBubbleDraggedSent = null;
                _chatBubbleChainDragging = false;
                _chatBubbleDragging = false;
            }
            _sentBubbles.Remove(bubble);
            if (bubble.FrameAnim != null) bubble.FrameAnim.Frames = null;
            if (bubble.AddAnim != null) bubble.AddAnim.Frames = null;
            if (bubble.ExpressionAnim != null) bubble.ExpressionAnim.Frames = null;
            if (bubble.Root != null) Destroy(bubble.Root.gameObject);   // 畫是它的子物件,一起走
        }

        private void ClearSentRoomBubbles()
        {
            for (int i = _sentBubbles.Count - 1; i >= 0; i--)
                DestroySentRoomBubble(_sentBubbles[i]);
            _sentBubbles.Clear();
            _chatBubbleChainDragging = false;
            _chatBubbleDragging = false;
            _chatBubbleDraggedSent = null;
            _chatBubbleDraggingTyping = false;
            _chatBubbleDragPointerCaptured = false;
            _chatBubbleHasPhysics = false;
            _chatBubblePhysicsVel = Vector2.zero;
        }

        private void HideRoomChatBubble()
        {
            _chatBubbleTyping = false;
            _chatBubblePendingShow = false;
            if (!_chatBubbleInputArmed)
                SetRoomChatInputEchoVisible(true);
            if (_chatBubbleCaret != null) _chatBubbleCaret.gameObject.SetActive(false);
            _bubbleBodyFor = null;   // 下次開始打字時強制重設文字＋重量尺寸
            _bubbleSizedFor = null;
            if (_chatBubbleRoot != null) _chatBubbleRoot.gameObject.SetActive(false);
            if (_chatBubbleAdd != null) _chatBubbleAdd.gameObject.SetActive(false);
            if (_chatBubbleFrameAnim != null) _chatBubbleFrameAnim.Frames = null;
            if (_chatBubbleAddAnim != null) _chatBubbleAddAnim.Frames = null;
            if (_chatBubbleExpressionAnim != null) _chatBubbleExpressionAnim.Frames = null;
            _chatBubbleHasPhysics = false;
            _chatBubblePhysicsVel = Vector2.zero;
            if (_chatBubbleDraggingTyping)
            {
                _chatBubbleDragging = false;
                _chatBubbleChainDragging = false;
                _chatBubbleDraggingTyping = false;
                _chatBubbleDragPointerCaptured = false;
            }
        }

        private void CancelRoomChatTyping()
        {
            if (_chatInput != null) _chatInput.text = "";
            _chatDraftWasEmpty = true;
            if (_chatBubbleTyping) HideRoomChatBubble();
            EndRoomChatInputFocus();
        }

        // 「點空曠處」= bubble 打字模式：頭上彈藍色打字泡顯示草稿(含 | 假光標)，左下輸入框回顯隱藏(當隱形捕捉欄)。
        // 「直接點左下輸入框」走另一條(OnRoomChatInputPointerDown)：取消藍泡、改在輸入框顯示字+閃爍光標+IME。
        private void BeginRoomBubbleTyping(bool preserveDraft = false)
        {
            if (_chatInput == null || _chatBubbleRoot == null) return;
            // 旁觀的人不能用氣泡,只能用左下打字框(RoomBubblePolicy)。所有入口都會經過這裡
            // ——點空曠處、點別人的泡、送出後續打——所以擋在這一點就夠,不必每個呼叫端各判一次。
            if (!RoomBubblePolicy.CanTypeInBubble(LocalSpectating))
            {
                BeginRoomChatInputTyping(preserveDraft);
                return;
            }
            if (_chatBubbleTyping)
            {
                FocusRoomChatInput();
                return;
            }

            HideChatModeMenu();
            HideExpressionMenu();
            _chatBubbleInputArmed = false;
            _chatBubbleTyping = true;
            _chatInputSticky = false;   // 切到 bubble 模式 → 放掉輸入框黏 focus
            _bubbleBodyFor = null;   // 進打字態強制重畫文字＋重量尺寸（preserveDraft 也要重新算一次）
            _bubbleSizedFor = null;
            _chatBubbleRoot.gameObject.SetActive(true);
            _chatBubbleDragging = false;
            _chatBubbleChainDragging = false;
            _chatBubbleDraggedSent = null;
            _chatBubbleDraggingTyping = false;
            _chatBubbleDragPointerCaptured = false;
            _chatBubblePendingShow = false;
            _chatBubblePhysicsVel = Vector2.zero;
            _chatBubbleHasPhysics = false;
            if (!preserveDraft) _chatInput.text = "";
            _chatDraftWasEmpty = string.IsNullOrEmpty(_chatInput.text);
            SetRoomChatInputEchoVisible(false);
            FocusRoomChatInput();

            var textColor = ChatBubbleTextColor;   // 泡是重用的：性別可能在建完之後才換(商城換性別)→ 每次進打字態重取
            if (_chatBubbleText != null) _chatBubbleText.color = textColor;
            if (_chatBubbleCaret != null) _chatBubbleCaret.color = textColor;

            ApplyRoomBubbleTypingStyle();
            if (_chatBubbleAdd != null) _chatBubbleAdd.gameObject.SetActive(false);
            if (_chatBubbleAddAnim != null) _chatBubbleAddAnim.Frames = null;
            if (_chatBubbleExpression != null) _chatBubbleExpression.gameObject.SetActive(false);
            if (_chatBubbleExpressionAnim != null) _chatBubbleExpressionAnim.Frames = null;
            UpdateRoomBubbleDraft();
            SnapRoomBubbleTypingToAnchor();
        }

        private void UpdateRoomBubbleDraft()
        {
            if (!_chatBubbleTyping || _chatInput == null || _chatBubbleRoot == null) return;
            if (!_chatBubbleRoot.gameObject.activeSelf) _chatBubbleRoot.gameObject.SetActive(true);
            // 已上屏字 + IME 組字中（拼音／候選還沒寫進 text）都要顯示在 bubble。
            string committed = _chatInput.text ?? "";
            string composition = Input.compositionString ?? "";
            if (_chatBubbleText == null) return;
            if (_chatBubbleExpression != null) _chatBubbleExpression.gameObject.SetActive(false);
            _chatBubbleText.gameObject.SetActive(true);

            // 尺寸只在草稿內容變動時重量（每幀跑 GetPreferredValues 會拖累且是舊版怪閃的來源之一）。
            string sizeKey = committed + "" + composition;
            if (sizeKey != _bubbleSizedFor) { _bubbleSizedFor = sizeKey; SyncRoomBubbleTypingSize(committed + composition); }

            // 游標／選取跟著實際輸入位置（方向鍵往回移、中間刪、Shift 選取都要在泡裡看得到）。
            int caret = Mathf.Clamp(_chatInput.stringPosition, 0, committed.Length);                    // 游標(移動端)
            int anchor = Mathf.Clamp(_chatInput.selectionStringAnchorPosition, 0, committed.Length);    // 選取固定端
            string body = BubbleDraftBody(committed, caret, anchor, composition);
            // 游標字元索引 = 游標前的已上屏字數 + 組字內部游標（原生 IMM32；往回選會往回移，拿不到則落組字串尾端）。
            int caretChar = caret + ImeCompositionCursor(composition);
            // 長串英數要給可折點,否則泡挑不到裝得下的尺寸、字直接滿出去（見 ChatSoftWrap）。
            // 零寬空格自己也佔一格 characterInfo → 游標索引要一起換算,不然打愈長偏愈多。
            body = ChatSoftWrap.Apply(body, ChatSoftWrap.DefaultMinRun, caretChar, out caretChar);
            // 注意：空字時就讓 text=""（cc==0），走 UpdateBubbleCaretOverlay 的 rect 置中 fallback。
            // 不要塞空白當佔位——空白字元的 ascender/descender 近乎 0，會把游標釘在框頂(pivot=左上)。
            bool bodyChanged = body != _bubbleBodyFor;
            if (bodyChanged) { _chatBubbleText.text = body; _bubbleBodyFor = body; }
            UpdateBubbleCaretOverlay(caretChar, bodyChanged);
        }

        // 空 draft → ADDANI 打字小泡；有字 → TALK_N（下面有棍）依長度變寬，不要用無棍的 Base/ENTER。
        private void SyncRoomBubbleTypingSize(string draft)
        {
            if (string.IsNullOrEmpty(draft))
            {
                // 打了字再刪成空：不要跳回 ADDANI 動態圖（換 sprite＋重播動畫＝那個「抖一下」）。維持目前 talk 泡的圖不變。
                // 但把「文字框」對回初次空字用的 TypingTextRect＋字級11，讓空字游標一律落在同一點——
                // 這樣你調的 CaretEmptyX/Y 不論初次 focus 或刪成空都對得上（初次 focus 的文字框也是這個 rect）。
                if (_chatBubbleText != null)
                {
                    _chatBubbleText.fontSize = 11f;
                    var tr = RoomBubbleArt.TypingTextRect();
                    Place(_chatBubbleText.rectTransform, tr.x, tr.y, tr.width, tr.height);
                    _bubbleTextRectDirty = true;   // 文字框已移到 TypingTextRect → 下次打字必須重跑 SizedStyle 還原 TextRect/字級13
                }
                return;
            }

            // 量寬度的字串要和貼上去的一致（貼上去的走了 ChatSoftWrap，見 SyncRoomBubbleTyping）。
            int style = RoomBubbleStyleForText(ChatSoftWrap.Apply(draft));
            // _bubbleTextRectDirty：即使 style 沒變，只要剛才空字把文字框移走了，也要重跑 SizedStyle 把文字框搬回 TextRect(style)。
            if (_chatBubbleTypingArt || _bubbleTextRectDirty || style != _chatBubbleStyle)
                ApplyRoomBubbleTypingSizedStyle(style);
            _bubbleTextRectDirty = false;
        }

        private static bool IsRoomChatImeComposing()
            => !string.IsNullOrEmpty(Input.compositionString);

        private int RoomBubbleStyleForText(string text, TextMeshProUGUI measureText = null)
        {
            var measure = measureText != null ? measureText : _chatBubbleText;
            string sample = string.IsNullOrEmpty(text) ? " " : text;
            for (int style = 1; style <= 11; style++)
            {
                var r = RoomBubbleArt.TextRect(style);
                if (measure == null)
                {
                    if (sample.Length * 12f <= r.width) return style;
                    continue;
                }

                Vector2 pref = measure.GetPreferredValues(sample, r.width, 200f);
                if (pref.x <= r.width + 1f && pref.y <= r.height + 1f)
                    return style;
            }
            return 11;
        }

        private void ApplyRoomBubbleStyle(int style, bool entering)
        {
            if (_chatBubbleRoot == null) return;
            style = Mathf.Clamp(style, 1, 11);
            _chatBubbleStyle = style;
            _chatBubbleTypingArt = false;
            var frames = entering ? RoomBubbleArt.EnterFrames(style) : null;
            var sprite = frames != null && frames.Length > 0 ? frames[0] : RoomBubbleArt.Base(style);

            _chatBubbleRoot.sizeDelta = new Vector2(RoomBubbleArt.CanvasW, RoomBubbleArt.CanvasH);
            if (_chatBubbleFrameAnim != null) _chatBubbleFrameAnim.SetFrames(frames, restart: true, loop: !entering);
            if (_chatBubbleFrame != null)
            {
                UIKit.ApplySprite(_chatBubbleFrame, sprite);
                Place(_chatBubbleFrame.rectTransform, 0, 0, RoomBubbleArt.CanvasW, RoomBubbleArt.CanvasH);
            }

            var textRect = RoomBubbleArt.TextRect(style);
            if (_chatBubbleText != null)
            {
                _chatBubbleText.fontSize = 13f;
                Place(_chatBubbleText.rectTransform, textRect.x, textRect.y, textRect.width, textRect.height);
            }
            if (_chatBubbleExpression != null)
                Place(_chatBubbleExpression.rectTransform, 73, 43, 24, 24);
        }

        private void ApplyRoomBubbleTypingStyle()
        {
            if (_chatBubbleRoot == null) return;
            _chatBubbleStyle = 1;
            _chatBubbleTypingArt = true;
            _chatBubbleRoot.sizeDelta = new Vector2(RoomBubbleArt.CanvasW, RoomBubbleArt.CanvasH);
            // ADDANI = 官方打字泡動態；空字時循環播，不要只貼靜態第 11 幀。
            var addFrames = RoomBubbleArt.AddFrames();
            if (_chatBubbleFrameAnim != null)
                _chatBubbleFrameAnim.SetFrames(addFrames != null && addFrames.Length > 0 ? addFrames : null, restart: true, loop: false);
            if (_chatBubbleFrame != null)
            {
                if (addFrames == null || addFrames.Length == 0)
                    UIKit.ApplySprite(_chatBubbleFrame, RoomBubbleArt.Typing());
                Place(_chatBubbleFrame.rectTransform, 0, 0, RoomBubbleArt.CanvasW, RoomBubbleArt.CanvasH);
            }
            if (_chatBubbleAdd != null) _chatBubbleAdd.gameObject.SetActive(false);
            if (_chatBubbleAddAnim != null) _chatBubbleAddAnim.Frames = null;

            if (_chatBubbleText != null)
            {
                _chatBubbleText.fontSize = 11f;
                var textRect = RoomBubbleArt.TypingTextRect();
                Place(_chatBubbleText.rectTransform, textRect.x, textRect.y, textRect.width, textRect.height);
            }
            if (_chatBubbleExpression != null)
                Place(_chatBubbleExpression.rectTransform, 73, 43, 24, 24);
        }

        // 打字有字時變寬：用帶下方棍子的 TALK_N 靜態框（不要用無棍的 Base/ENTER）。
        private void ApplyRoomBubbleTypingSizedStyle(int style)
        {
            if (_chatBubbleRoot == null) return;
            style = Mathf.Clamp(style, 1, 11);
            _chatBubbleStyle = style;
            _chatBubbleTypingArt = false;
            _chatBubbleRoot.sizeDelta = new Vector2(RoomBubbleArt.CanvasW, RoomBubbleArt.CanvasH);
            if (_chatBubbleFrameAnim != null) _chatBubbleFrameAnim.Frames = null;
            if (_chatBubbleFrame != null)
            {
                UIKit.ApplySprite(_chatBubbleFrame, RoomBubbleArt.Talk(style));
                Place(_chatBubbleFrame.rectTransform, 0, 0, RoomBubbleArt.CanvasW, RoomBubbleArt.CanvasH);
            }

            var textRect = RoomBubbleArt.TextRect(style);
            if (_chatBubbleText != null)
            {
                _chatBubbleText.fontSize = 13f;
                Place(_chatBubbleText.rectTransform, textRect.x, textRect.y, textRect.width, textRect.height);
            }
            if (_chatBubbleExpression != null)
            {
                float ex = textRect.x + (textRect.width - 24f) * 0.5f;
                float ey = textRect.y + (textRect.height - 24f) * 0.5f;
                Place(_chatBubbleExpression.rectTransform, ex, ey, 24, 24);
            }
        }

        // 打字草稿的「本體」：純文字＋選取反白，NOT 含游標。游標改用獨立 Image 疊圖（UpdateBubbleCaretOverlay）：
        // 早期把 | 塞進 TMP 字串靠改字閃爍 → 每次改字重算 mesh，配上每幀 SyncSize 的 GetPreferredValues 造成怪異閃爍。
        // 現在字串只在草稿真的變動時才改，游標是圖層靠 alpha 閃 → 乾淨、位置也不頂開字。
        private static string BubbleDraftBody(string committed, int caret, int anchor, string composition)
        {
            committed = committed ?? "";
            caret = Mathf.Clamp(caret, 0, committed.Length);
            anchor = Mathf.Clamp(anchor, 0, committed.Length);

            // IME 組字中：組字串插在游標處（不畫組字反白，組字回饋交給 IME 自己；只有下面的手動選取才反白）。
            if (!string.IsNullOrEmpty(composition))
                return EscapeTmp(committed.Substring(0, caret)) + EscapeTmp(composition) + EscapeTmp(committed.Substring(caret));

            int selStart = Mathf.Min(caret, anchor);
            int selEnd = Mathf.Max(caret, anchor);
            if (selStart != selEnd)
            {
                // 有選取（Shift+方向鍵）：反白選取區（<mark> 不佔寬、不影響字元索引）。
                string before = EscapeTmp(committed.Substring(0, selStart));
                string sel = "<mark=#5B8DEF66>" + EscapeTmp(committed.Substring(selStart, selEnd - selStart)) + "</mark>";
                string after = EscapeTmp(committed.Substring(selEnd));
                return before + sel + after;
            }

            return EscapeTmp(committed);
        }

        // 游標閃爍：對稱 50/50、~0.53s 半週期（比照 Windows 文字游標）。bubble 泡與左下輸入框共用同一相位，避免看起來怪。
        private const float CaretBlinkHalfSec = 0.53f;
        private static bool CaretBlinkOn()
            => Mathf.Repeat(Time.unscaledTime, CaretBlinkHalfSec * 2f) < CaretBlinkHalfSec;

        private string _bubbleBodyFor = null;   // 目前 _chatBubbleText.text 對應的 body：只有變動才重設（避免每幀改字重算 mesh）
        private string _bubbleSizedFor = null;  // 目前泡尺寸對應的草稿：只有變動才重量 GetPreferredValues
        private bool _bubbleTextRectDirty = false; // 空字時文字框被移到 TypingTextRect → 下次打字要強制重跑 SizedStyle 還原

        // ====== 泡內游標微調（自己改這幾個數字就好；改完直接重跑，不用動邏輯）======
        private const float CaretWidthPx    = 2f;    // 游標寬（豎線粗細）
        private const float CaretHeightScale = 1f;   // 游標高倍率（1=同字高；想短一點填 0.8、長一點 1.2）
        private const float CaretOffsetX    = 0f;    // 水平微調：正=右移、負=左移（像素）。空字與有字都套用
        private const float CaretOffsetY    = 0f;    // 垂直微調：正=上移、負=下移（像素）。空字與有字都套用
        // 只影響「空字（初始）」時的起點：
        private const float CaretEmptyX     = 8f;    // 空字時額外水平微調（接在左內緣之後）
        private const float CaretEmptyY     = -1f;    // 空字時額外垂直微調（接在垂直中線之後）
        // =========================================================================

        // 泡內游標＝獨立 Image（_chatBubbleCaret，_chatBubbleText 的子物件）。用 textInfo 求第 caretCharIndex 個字元的位置，
        // 擺到該處、依 CaretBlinkOn 閃 alpha。文字沒變就不 ForceMeshUpdate（方向鍵移游標只挪圖層，不重算文字）。
        private void UpdateBubbleCaretOverlay(int caretCharIndex, bool textChanged)
        {
            if (_chatBubbleCaret == null || _chatBubbleText == null) return;
            if (textChanged) _chatBubbleText.ForceMeshUpdate();
            var ti = _chatBubbleText.textInfo;
            int cc = ti != null ? ti.characterCount : 0;

            float x, top, bot;
            if (cc <= 0)
            {
                // 空字：無字元可參照。文字框 pivot=(0,1)（左上），故 y=0 是「上緣」不是中線——要用 rect 中心算垂直中線，
                // 否則初始游標會跑到框頂。x 取左內緣、y 取（含上下 margin 的）垂直中央，高度用字級。
                var rect = _chatBubbleText.rectTransform.rect;
                var mg = _chatBubbleText.margin;   // x=左, y=上, z=右, w=下
                x = rect.xMin + mg.x + CaretEmptyX;
                float cy = (rect.yMin + rect.yMax) * 0.5f + (mg.w - mg.y) * 0.5f + CaretEmptyY;
                float h = _chatBubbleText.fontSize;
                top = cy + h * 0.5f;
                bot = cy - h * 0.5f;
            }
            else
            {
                int idx = Mathf.Clamp(caretCharIndex, 0, cc);
                var ci = idx < cc ? ti.characterInfo[idx] : ti.characterInfo[cc - 1];
                x = idx < cc ? ci.origin : ci.xAdvance;   // 字前緣 / 末字後緣
                top = ci.ascender;
                bot = ci.descender;
            }

            // characterInfo 座標與子物件 localPosition 同為「相對父 pivot」空間 → 直接設 localPosition，跟著泡移動不延遲。
            float cx = x + CaretOffsetX;
            float cyMid = (top + bot) * 0.5f + CaretOffsetY;
            _chatBubbleCaret.rectTransform.localPosition = new Vector3(cx, cyMid, 0f);
            _chatBubbleCaret.rectTransform.sizeDelta = new Vector2(CaretWidthPx, Mathf.Max(8f, (top - bot) * CaretHeightScale));
            var col = _chatBubbleCaret.color; col.a = CaretBlinkOn() ? 1f : 0f; _chatBubbleCaret.color = col;
            if (!_chatBubbleCaret.gameObject.activeSelf) _chatBubbleCaret.gameObject.SetActive(true);
            FeedImeCursorPos(_chatBubbleCaret.rectTransform);   // 泡打字時選字視窗跟著泡內游標
        }

        private void OnRoomChatInputChanged(string text)
        {
            if (_chatBubbleInputArmed && !_chatBubbleTyping && !string.IsNullOrEmpty(text))
                BeginRoomBubbleTyping(preserveDraft: true);
        }

        /// <summary>
        /// 「用左下打字框打字」——<see cref="BeginRoomBubbleTyping"/> 的替身,給**不能用氣泡的人**(旁觀者)。
        /// 做的事跟玩家實體點左下輸入框(<see cref="OnRoomChatInputPointerDown"/>)一樣:收掉頭上泡、
        /// 顯示輸入框回顯(字+閃爍光標+IME)、黏住 focus;差別只在這條是程式呼叫的,要自己聚焦。
        /// </summary>
        private void BeginRoomChatInputTyping(bool preserveDraft)
        {
            if (_chatInput == null) return;
            HideChatModeMenu();
            HideExpressionMenu();
            _chatBubbleInputArmed = false;
            _chatInputSticky = true;
            if (_chatBubbleTyping) HideRoomChatBubble();   // armed 已清掉 → 裡面會把回顯打開
            else SetRoomChatInputEchoVisible(true);
            if (!preserveDraft) _chatInput.text = "";
            _chatDraftWasEmpty = string.IsNullOrEmpty(_chatInput.text);
            FocusRoomChatInput();
        }

        /// <summary>
        /// 打字打到一半身分變成旁觀(自己按「旁觀」、或座位被房主關掉)→ 把泡收掉、草稿原封搬回左下打字框。
        /// 沒有這一步的話,切換那一刻已經開著的泡會一路留到送出。
        /// </summary>
        private void EnsureSpectatorTypesInChatInput()
        {
            if (RoomBubblePolicy.CanTypeInBubble(LocalSpectating)) return;
            if (!_chatBubbleTyping && !_chatBubbleInputArmed) return;
            BeginRoomChatInputTyping(preserveDraft: true);
        }

        private void ArmRoomBubbleInput()
        {
            if (_chatInput == null) return;
            // 旁觀者送出後也不能續打氣泡 → 停在輸入框模式繼續打(BeginRoomBubbleTyping 那道門的另一半)。
            if (!RoomBubblePolicy.CanTypeInBubble(LocalSpectating))
            {
                BeginRoomChatInputTyping(preserveDraft: true);
                return;
            }
            _chatBubbleInputArmed = true;
            _chatInputSticky = false;   // bubble armed 態不是輸入框黏 focus
            _chatBubbleTyping = false;
            _chatDraftWasEmpty = string.IsNullOrEmpty(_chatInput.text);
            SetRoomChatInputEchoVisible(false);
            FocusRoomChatInput();
        }

        // 兩種打字模式共用：bubble 模式(點空曠處)→隱藏輸入框回顯(草稿改顯示在頭上藍泡+假光標)；輸入框模式(直接點左下
        // 輸入框)→顯示回顯=白字+閃爍白光標+IME 組字底線(richText→TMP 畫 <u>)。visible=false 把字與光標一起設成透明。
        private void SetRoomChatInputEchoVisible(bool visible)
        {
            if (_chatInput == null) return;
            Color textColor = visible ? Color.white : new Color(1f, 1f, 1f, 0f);
            if (_chatInput.textComponent != null)
                _chatInput.textComponent.color = textColor;
            if (_chatInput.placeholder is TextMeshProUGUI ph)
                ph.color = visible ? new Color(1f, 1f, 1f, 0.5f) : new Color(1f, 1f, 1f, 0f);
            // TMP 內建 caret 一律透明，改用自畫 _chatCaret（UpdateChatCaret 控制顯示/位置/閃爍）。
            _chatInput.customCaretColor = true;
            _chatInput.caretColor = new Color(1f, 1f, 1f, 0f);
        }

        // IME 組字內部游標(字元索引)：由原生 SdoImeHook 讀 IMM32 GCS_CURSORPOS；拿不到 → 退化成組字串尾端。
        // 新注音「往回選」時，游標就靠這個往回移。
        private static int ImeCompositionCursor(string comp)
        {
            if (string.IsNullOrEmpty(comp)) return 0;
            int c = SdoImeHook.CursorPos();
            return (c >= 0 && c <= comp.Length) ? c : comp.Length;
        }

        // 自製輸入框要自己告訴系統「文字游標在螢幕哪裡」，選字視窗才會跟著游標出現（Unity 官方作法）。
        // 每幀把目前 caret 的螢幕座標餵給 Input.compositionCursorPos（World-Space canvas → 傳 worldCamera）。
        private readonly Vector3[] _imeCorners = new Vector3[4];
        private void FeedImeCursorPos(RectTransform caretRt)
        {
            if (caretRt == null) return;
            var canvas = caretRt.GetComponentInParent<Canvas>();
            Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;
            caretRt.GetWorldCorners(_imeCorners);
            Input.compositionCursorPos = RectTransformUtility.WorldToScreenPoint(cam, _imeCorners[0]); // 左下角
        }

        // 輸入框模式(直接點左下輸入框、非 bubble 模式)才顯示自畫游標；擺在目前文字(含 IME 組字)尾端，閃爍同 bubble。
        private void UpdateChatCaret()
        {
            if (_chatCaret == null || _chatInput == null) return;
            // armed（bubble 送出後續待下一則草稿）仍 focus 但屬 bubble 模式：此時左下不該有游標，草稿會顯示在頭上藍泡。
            bool inputMode = _chatInput.isFocused && !_chatBubbleTyping && !_chatBubbleInputArmed;
            if (!inputMode)
            {
                if (_chatCaret.gameObject.activeSelf) _chatCaret.gameObject.SetActive(false);
                return;
            }
            // 非組字：游標擺在「實際輸入位置」（量到 stringPosition 的字寬）→ 往回移/中間刪都跟著動。
            // 組字中：游標擺在「已上屏字 + 完整組字串」尾端（正常打字回饋）；內部選字回饋交給系統候選視窗
            //（位置由 FeedImeCursorPos 餵給 compositionCursorPos，視窗會跟著游標）。
            string committed = _chatInput.text ?? "";
            int caretPos = Mathf.Clamp(_chatInput.stringPosition, 0, committed.Length);
            string comp = Input.compositionString ?? "";
            int imeCur = ImeCompositionCursor(comp);   // 組字內部游標（往回選時會往回移）
            string upTo = committed.Substring(0, caretPos) + comp.Substring(0, imeCur);
            float w = (_chatInput.textComponent != null && upTo.Length > 0)
                ? _chatInput.textComponent.GetPreferredValues(upTo).x : 0f;
            // 字比框寬時 TMP 會把整段文字往左推(讓游標留在框內),自畫的游標要跟著那個位移走,
            // 否則它會一路往右跑出框外、和實際看得到的字對不上(見 InputCaretMetrics)。
            float shift = _chatInput.textComponent != null
                ? _chatInput.textComponent.rectTransform.anchoredPosition.x : 0f;
            float viewW = _chatInput.textViewport != null ? _chatInput.textViewport.rect.width : 0f;
            _chatCaret.rectTransform.anchoredPosition =
                new Vector2(InputCaretMetrics.CaretX(2f, w, shift, viewW, _chatCaret.rectTransform.sizeDelta.x), 0f);
            if (!_chatCaret.gameObject.activeSelf) _chatCaret.gameObject.SetActive(true);
            bool on = CaretBlinkOn();
            var c = _chatCaret.color; c.a = on ? 1f : 0f; _chatCaret.color = c;
            FeedImeCursorPos(_chatCaret.rectTransform);   // 選字視窗跟著游標
        }

        // 送出（Enter）會讓 TMP_InputField 反 activate；黏 focus 態下每幀把 focus 搶回來，做到「送完續打、點別處才離開」。
        // 直接 ActivateInputField（非走會每幀重啟 coroutine 的 FocusRoomChatInput）；已 focus/IME 組字/離房/bubble 態就不動。
        private void MaintainRoomChatInputFocus()
        {
            if (_chatInput == null) return;
            // 三種要保住 focus 的態：輸入框黏 focus(sticky)、bubble 送完待打(armed)、bubble 打字中(typing)。
            // 少了 typing 這條，點/拖泡讓 EventSystem 把輸入框反選後就回不來 → 打不了字也送不出去；
            // 少了 armed 這條則「送完續打泡不出來」。
            if (!_chatInputSticky && !_chatBubbleInputArmed && !_chatBubbleTyping) return;
            bool roomTop = Ctx == null || Ctx.Flow == null || Ctx.Flow.Current == ScreenId.Room;
            if (!roomTop)
            {
                // 切到別畫面(含選歌 overlay)：不只放掉黏 focus，還要完整取消打字態（清藍泡/輸入框殘草稿＋放 focus）。
                // 否則頭上打字泡/輸入框的殘草稿會活到選歌畫面，選歌搜尋框一按 Enter 就把殘草稿當聊天送出（泡把字送出去）。
                // 三態全清後下一幀由上面 early-return 擋住，不會每幀重呼；回房也不自動搶 focus。
                CancelRoomChatTyping();
                return;
            }
            // modal(商城/儲物櫃/設定)疊在房間上時不搶 focus：設定的鍵盤頁要收按鍵，focus 被搶回去的話那些字母
            // 會打進聊天欄(還會把 IME 組字叫回來)。modal 關掉後 sticky 還在 → 焦點自動回到聊天欄。
            if (FrontendApp.Instance != null && FrontendApp.Instance.AnyModalOpen) return;
            if (_chatBubbleDragging) return;                      // 拖曳已送出泡進行中→不搶 focus，放開後下一幀再補
            if (_chatInput.isFocused || IsRoomChatImeComposing()) return;
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(_chatInput.gameObject);
            _chatInput.ActivateInputField();
        }

        private void FocusRoomChatInput()
        {
            if (_chatInput == null) return;
            if (_chatInputFocusRoutine != null)
            {
                StopCoroutine(_chatInputFocusRoutine);
                _chatInputFocusRoutine = null;
            }
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(_chatInput.gameObject);
            _chatInput.Select();
            _chatInput.ActivateInputField();
            _chatInput.MoveTextEnd(false);
            _chatInputFocusRoutine = StartCoroutine(FocusRoomChatInputNextFrame());
        }

        private IEnumerator FocusRoomChatInputNextFrame()
        {
            yield return null;
            if (_chatInput == null)
            {
                _chatInputFocusRoutine = null;
                yield break;
            }
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(_chatInput.gameObject);
            _chatInput.Select();
            _chatInput.ActivateInputField();
            _chatInput.MoveTextEnd(false);
            _chatInputFocusRoutine = null;
        }

        private void EndRoomChatInputFocus()
        {
            _chatBubbleInputArmed = false;
            _chatInputSticky = false;   // 明確退出（Esc／空字 Enter／方向鍵走路）→ 放掉黏住的 focus
            if (_chatInputFocusRoutine != null)
            {
                StopCoroutine(_chatInputFocusRoutine);
                _chatInputFocusRoutine = null;
            }
            SetRoomChatInputEchoVisible(true);
            if (_chatInput == null) return;
            _chatInput.DeactivateInputField();
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == _chatInput.gameObject)
                EventSystem.current.SetSelectedGameObject(null);
        }

        private static string ChatLineText(ChatMessage m)
        {
            if (m == null) return "";
            return m.ExpressionId > 0 ? RoomChatCommand.ExpressionDisplayText(m.ExpressionId) : (m.Text ?? "");
        }

        private static string EscapeTmp(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        // ---- render the seat occupancy / labels from the room state ----

        private void Render()
        {
            var room = Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
            bool isHost = Ctx.Rooms != null && Ctx.Rooms.IsHost;

            if (room != null)
            {
                int srv = Ctx.Session != null ? Ctx.Session.ServerNumber : 1;
                int ch = Ctx.Session != null ? Ctx.Session.Channel : 1;
                _serverLabel.SetText(RoomLabels.ServerName(srv));      // 自由練習場1
                _channelLabel.SetText(RoomLabels.Channel(ch));         // 頻道1
                // 左上這排照官方原本的樣子:練習場 + 頻道 + **房間序號**(一個小數字)。
                // ⚠️ 這裡放的是 Seq 不是 Id —— 5 位數房號官方畫面上根本不出現(要進房就在大廳點那張卡),
                //    所以中央房名後面不再接括弧房號,只留這個給人看的門牌序號。
                // 門牌從 000 起算 → 判斷是 >= 0(0 是一間真的房,-1 才是「不知道門牌」)。
                _roomIdLabel.SetText(room.Seq >= 0 ? room.Seq.ToString("000") : "");
                // 量實際字寬，左到右自動排版(固定 HeaderGap 間距):不論字長/語言都不會疊、間距一致。
                float lx = ServerX;
                _serverLabel.SetX(lx);  lx += _serverLabel.PreferredWidth + HeaderGap;
                _channelLabel.SetX(lx); lx += _channelLabel.PreferredWidth + HeaderGap;
                _roomIdLabel.SetX(lx);

                // 中央只放房名:「飄漂o的舞蹈室」。官方那塊牌子上沒有房號。
                // (feat/online 那邊是「線上才把房號接在房名後面」,這裡走得更遠:房號一律不進房名,
                //  改成左上那排的門牌 Seq —— 5 位數 code 官方畫面上本來就不出現。)
                _roomNameLabel.text = RoomLabels.DisplayName(room.Name, room.HostName);
            }

            // 歌名/模式/場景/CD/難度/BPM/速度/note/組隊/掉落。
            // 房主與離線:依 session;**線上非房主依房間快照** —— 房主一換歌/換場景,server 立刻推一份
            // roomState,這裡就跟著重畫(Render 是 OnRoomUpdated 直接叫的,所以是同一刻)。
            // 速度/note 皮/掉落方向仍是個人偏好,不跟房間(官方也是各自設定)。
            RenderWin2();

            RenderSlots(room);
            // a NAME marker floats above the avatar in the room (官方: 人頭上的名字 + ▼), NOT the head portrait.
            // 名字後面接等級「Lv:N」(等級留空則不接)；家族列(徽章+名稱)另外畫在名字上方(UpdateFamilyRow)。
            // 等級走 ProfileFields —— 外層 profile.json 是共用預設,這個角色自己設過就以它自己的為準(同 UpdateFamilyRow)。
            if (_floatName != null)
            {
                string nm = LocalName(room);
                string lvl = ProfileFields.LevelLabel(ProfileManager.Active);
                _floatName.SetText(lvl.Length > 0 ? nm + "  " + lvl : nm);
                _floatName.gameObject.SetActive(true);
            }
            UpdateFamilyRow();

            // host sees Start; guest sees Ready/Cancel (single-player host → Start visible)
            bool localReady = LocalReady(room);
            if (_startBtn != null) _startBtn.gameObject.SetActive(isHost);
            if (_readyBtn != null) _readyBtn.gameObject.SetActive(!isHost && !localReady);
            if (_cancelReadyBtn != null) _cancelReadyBtn.gameObject.SetActive(!isHost && localReady);
            if (_songSelectBtn != null) _songSelectBtn.gameObject.SetActive(isHost);
            // 旁觀 ↔ 進入:一律以 server 快照為準(見 LocalSpectating)—— 不做樂觀更新,
            // 按下去到 server 認可之間,鈕還是留在原本那一顆(同 OnSpectateToggle 的原則)。
            bool spectating = LocalSpectating;
            if (_spectateBtn != null) _spectateBtn.gameObject.SetActive(!spectating);
            if (_enterBtn != null) _enterBtn.gameObject.SetActive(spectating);
        }

        // ---- win2 右側面板：依 GameSession 重畫模式/場景/CD/難度/BPM/速度/note/組隊/掉落 ----
        private void RenderWin2()
        {
            var s = Ctx != null ? Ctx.Session : null;
            if (s == null) return;

            // 🔴 這整個面板顯示的是**房間的設定**,而房間的設定是房主定的。
            // 離線時本機就是房主,所以讀 session 是對的;線上的非房主讀 session 會顯示自己上次的設定,
            // 而房間實際上是別人選的 —— 歌名、場景縮圖、難度碟、模式全都會是錯的。
            // 這些值 server 都在 roomState 裡帶著走(NetRoomSettings + song),所以一律以快照為準。
            var netRoom = NetRoomForPanel();
            var netSet = netRoom != null ? netRoom.Settings : null;

            // 模式標題（自由模式/普通模式/ShowTime模式）—— 純文字 + 白邊。
            // 線上一律讀 server 的房間設定(房主也是,見 RoomGameMode)—— 房主的選擇會 push 上去,
            // 兩邊本來就該一致;推送還沒成功時顯示 server 手上的那個才是誠實的。
            int gameMode = RoomGameMode();
            if (_modeLabel != null)
                _modeLabel.SetText(L(RoomLabels.ModeKey(gameMode)));

            // 場景縮圖：隨機 → RANDOM；具體 → Scene{id+1}（官方縮圖編號是 1-based）
            bool sceneRandom = netSet != null ? netSet.SceneRandom : s.StageRandom;
            int sceneId = netSet != null ? netSet.SceneId : s.StageId;
            if (_sceneThumb != null)
            {
                Sprite sc = sceneRandom
                    ? RoomUiArt.An("randomscene")
                    : (RoomUiArt.An("scene" + (sceneId + 1)) ?? RoomUiArt.An("scene1"));
                UIKit.ApplySprite(_sceneThumb, sc);
            }

            // 歌名 + 難度 + BPM（從歌曲目錄查；沒選歌就空白）。
            //
            // 🔴 離線時 session 就是房主選的歌,所以以 session 為準是對的。但**線上的非房主不是** ——
            // 它的 session 記著自己上次選的歌,面板會顯示那一首,而房間實際上是房主選的另一首
            // (實機兩開就是這樣:房主選了外部歌,客人的面板還寫著自己上次玩的官方歌)。
            // 線上非房主一律看房間快照;缺歌的人也看得到歌名/等級/BPM(那些值 server 帶著走)。
            var netSong = netRoom != null ? netRoom.Song : null;
            if (netSong != null && !netSong.HasSong) netSong = null;

            bool hasSong = netSong != null || s.HasSong;
            bool isRandom = netSong != null ? netSong.RandomTitle : s.SongIsRandom;
            string title = netSong != null ? netSong.Title : s.SongTitle;

            // 本機的目錄:官方歌用 gn(全球穩定),外部歌用 packId(跨電腦的內容指紋)。查不到 = 我沒這首歌。
            // 隨機難度選擇：房間顯示「隨機難度 X」標籤、不揭曉抽到的歌 → 等級/BPM 也一併隱藏(否則會露出那首歌的等級/BPM)。
            SongCatalog.Entry entry = isRandom ? null : LocalEntryFor(netSong);

            // 🎚 自由模式:非房主那格「房主設置」換成「難度設置 ◄ EASY ►」,每個人挑自己要打的難度
            // (netRoom != null ⟺ 線上且不是房主 —— 與上面 netSet 同一個判斷,兩者不會不同步)。
            // 挑了之後**上面的難度數字與 CD 光碟就跟著自己選的走**,因為那才是自己等一下要打的譜。
            bool picksOwnDiff = FreeModeDifficulty.PlayerPicksOwn(gameMode, isHost: netRoom == null);
            SetFreeModeLevelVisible(picksOwnDiff);
            int diffSlot = netSong != null ? netSong.Difficulty : (int)s.Difficulty;
            if (picksOwnDiff)
            {
                // 房主換了歌之後,上次選的難度在新的那首可能沒有譜(外部歌常常只有一兩張)→ 貼到最近的可打難度。
                s.FreeDifficulty = FreeModeDifficulty.Snap(s.FreeDifficulty, PlayableSlots(entry));
                diffSlot = s.FreeDifficulty;
                if (_fmLevelValue != null) _fmLevelValue.text = FreeModeDifficulty.Name(s.FreeDifficulty);
            }

            // CD 光碟依難度換色（Difficult0/1/2）。隨機難度選擇：難度也是隨機的 → 用「灰階碟」當中性顯示
            // （不鎖任何一色；實際難度進遊戲才抽）。灰階碟去色失敗(材質不可讀)時退回原本的難度碟。
            if (_diffDisc != null && _diffDiscFrames != null && _diffDiscFrames.Length > 0)
            {
                Sprite disc = isRandom
                    ? (DiffDiscGray() ?? _diffDiscFrames[_diffDiscFrames.Length - 1])
                    : _diffDiscFrames[Mathf.Clamp(diffSlot, 0, _diffDiscFrames.Length - 1)];
                UIKit.ApplySprite(_diffDisc, disc);
            }

            if (_songLabel != null)
                // 已選歌曲：跟選歌清單／遊戲中同一個上限（NoWrap+Overflow → 長歌名會往兩邊溢出面板美術）
                _songLabel.SetText(hasSong ? SongTextLimits.ClampTitle(title) : L("room.no_song"));
            if (_levelLabel != null)
            {
                // 目錄查得到就用本機的值,查不到(缺歌)退回 server 帶來的那份。
                int lvl = entry != null ? entry.DisplayLevel(diffSlot)
                        : (netSong != null && !isRandom && netSong.Level > 0 ? netSong.Level : -1);
                _levelLabel.SetText(lvl >= 0 ? lvl.ToString() : "");
            }
            if (_bpmLabel != null)
            {
                float bpm = entry != null && entry.bpm > 0f ? entry.bpm
                          : (netSong != null && !isRandom ? (float)netSong.Bpm : 0f);
                _bpmLabel.SetText(bpm > 0f ? Mathf.RoundToInt(bpm).ToString() : "");
            }

            // 速度（對齊到 config 檔位）
            var steps = SpeedSteps();
            _speedIndex = IndexOfNearest(steps, s.Speed);
            if (_speedLabel != null) _speedLabel.text = steps[Mathf.Clamp(_speedIndex, 0, steps.Length - 1)].ToString("0.0");

            // note 種類預覽：-1=隨機 → 靜態 FREE.PNG（官方「隨機」圖示，與 EFT_2 區隔，否則隨機格會借用 hiteft2 的圖
            // 而跟真正選 EFT_2 撞圖）；>=0 → 對應 hiteft .an 多幀，給 SpriteSeqAnim 循環撥放。
            if (_noteDisplay != null)
            {
                if (s.NoteType < 0)
                {
                    if (_noteAnim != null) _noteAnim.Frames = null;   // 停掉循環 → 靜態圖不被覆寫
                    var free = RoomUiArt.Image("FREE.PNG");
                    if (free != null) UIKit.ApplySprite(_noteDisplay, free);
                }
                else
                {
                    int ni = Mathf.Min(s.NoteType, NoteEftArt.Length - 1);
                    var frames = RoomUiArt.AnFrames(NoteEftArt[ni]);
                    if (_noteAnim != null) _noteAnim.Frames = frames;
                    if (frames != null && frames.Length > 0) UIKit.ApplySprite(_noteDisplay, frames[0]);
                }
            }

            // 組隊單選：選到的顯示 pushed 圖，其餘顯示 normal。
            // 🔴 亮哪一格要看 **server 認定的隊伍**,不是 session.Team —— 線上換隊刻意不做樂觀更新
            // (PickOwnTeam 只送 setOwnTeam,顯示等 roomState 回來),而 session.Team 在線上根本沒人寫,
            // 所以拿它來畫的話:頭貼上的 READY 已經是 B 隊的綠色了,格子卻還亮在「自由」。
            int myTeam = LocalTeam();
            for (int i = 0; i < _teamImg.Length; i++)
                if (_teamImg[i] != null) UIKit.ApplySprite(_teamImg[i], myTeam == i ? _teamPushed[i] : _teamNormal[i]);

            // 掉落方式的值由 SdoComboBox 自己維護（onPick → session.DropDirection）；此處不需重畫。
        }

        /// <summary>
        /// 右側面板要讀的房間快照 —— **線上而且不是房主**才有。
        /// 房主與離線看自己的 session(它就是房間的設定);非房主看快照(房間是別人選的)。
        /// 「netRoom == null」因此也正好等於「這台是房主」。
        /// </summary>
        private NetRoomSnapshot NetRoomForPanel()
            => Online && Ctx != null && Ctx.Net != null && !Ctx.Net.IsHost ? Ctx.Net.Room : null;

        /// <summary>
        /// 本機這一格現在在**哪一隊**(0=A 1=B 2=C 3=自由)。
        ///
        /// 線上以 server 的座位為準:換隊不做樂觀更新(見 <see cref="PickOwnTeam"/>),而且線上沒有人寫
        /// <c>GameSession.Team</c> —— 讀它會讓「組隊」四格永遠亮在自己上次離線時選的那一格。
        /// 離線(本機就是房主、沒有 server)才用 session 的值。
        /// </summary>
        /// <summary>
        /// 這間房**現在**是什麼模式(0=自由 1=普通 2=ShowTime)。
        ///
        /// 線上一律以 server 的房間設定為準(房主自己也是 —— 它按下去的模式會 push 上去,
        /// 兩邊本來就該一致;拿 session 當來源的話,推送失敗時房主會看到一個只有它自己相信的模式)。
        /// 離線沒有 server,session 就是房間設定。
        /// </summary>
        private int RoomGameMode()
        {
            if (Online && Ctx != null && Ctx.Net != null && Ctx.Net.Room != null && Ctx.Net.Room.Settings != null)
                return Ctx.Net.Room.Settings.GameMode;
            return Ctx != null && Ctx.Session != null ? Ctx.Session.GameMode : 0;
        }

        private int LocalTeam()
        {
            if (Online && Ctx != null && Ctx.Net != null && Ctx.Net.Room != null)
            {
                var seat = Ctx.Net.Room.SeatOf(Ctx.Net.UserId);
                if (seat != null) return seat.Team;
            }
            return Ctx != null && Ctx.Session != null ? Ctx.Session.Team : (int)TeamTag.Free;
        }

        /// <summary>
        /// 面板現在顯示的那首歌在**本機**歌曲目錄裡的那一筆。
        /// <paramref name="netSong"/> 有值(線上非房主)就用房間那份的識別去查,否則查 session 自己選的。
        /// 查不到 = 我沒這首歌(缺歌)→ null,呼叫端退回 server 帶來的顯示值。
        /// </summary>
        private SongCatalog.Entry LocalEntryFor(NetSongRef netSong)
        {
            if (netSong != null)
                return netSong.Official
                    ? SongCatalog.Get(netSong.Gn)
                    : Sdo.Game.ExternalSongLibrary.FindByPack(netSong.PackId, netSong.SongKey);
            var s = Ctx != null ? Ctx.Session : null;
            return s != null && s.HasSong ? SongCatalog.Get(s.SongGn) : null;
        }

        /// <summary>這首歌三個難度槽各有沒有譜。查不到目錄(缺歌/隨機難度)→ null = 三個都當可選。</summary>
        private static bool[] PlayableSlots(SongCatalog.Entry entry)
        {
            if (entry == null) return null;
            var ok = new bool[FreeModeDifficulty.SlotCount];
            for (int i = 0; i < ok.Length; i++) ok[i] = entry.HasChart(i);
            return ok;
        }

        /// <summary>自由模式難度選擇器現在該用哪一份「哪些難度有譜」(◄ ► 要跳過沒譜的難度)。</summary>
        private bool[] CurrentPlayableSlots()
        {
            var netSong = NetRoomForPanel()?.Song;
            if (netSong != null && !netSong.HasSong) netSong = null;
            bool isRandom = netSong != null ? netSong.RandomTitle : (Ctx != null && Ctx.Session != null && Ctx.Session.SongIsRandom);
            return PlayableSlots(isRandom ? null : LocalEntryFor(netSong));
        }

        /// <summary>隨機難度用的灰階 CD 碟：把任一難度碟去色一次並快取（碟形相同、只差色相，去色後即中性灰）。
        /// 來源材質不可讀時回 null，呼叫端退回原本的難度碟。</summary>
        private Sprite DiffDiscGray()
        {
            if (_diffDiscGray != null) return _diffDiscGray;
            if (_diffDiscFrames == null || _diffDiscFrames.Length == 0) return null;
            _diffDiscGray = ToGrayscale(_diffDiscFrames[_diffDiscFrames.Length - 1]);
            return _diffDiscGray;
        }

        /// <summary>Desaturate a sprite (luminance, alpha preserved) into a fresh sprite of the same on-screen size.
        /// Reads the sprite's crop out of its (readable) atlas texture; returns the source unchanged if it can't.</summary>
        private static Sprite ToGrayscale(Sprite src)
        {
            if (src == null || src.texture == null) return src;
            var r = src.textureRect;
            int x = Mathf.RoundToInt(r.x), y = Mathf.RoundToInt(r.y);
            int w = Mathf.RoundToInt(r.width), h = Mathf.RoundToInt(r.height);
            if (w <= 0 || h <= 0) return src;
            Color[] px;
            try { px = src.texture.GetPixels(x, y, w, h); }
            catch { return src; }   // texture not CPU-readable -> caller falls back to the colour disc
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                float g = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;   // Rec.601 luma
                px[i] = new Color(g, g, g, c.a);
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            tex.SetPixels(px);
            tex.Apply(false);
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), src.pixelsPerUnit, 0, SpriteMeshType.FullRect);
        }

        /// <summary>速度檔位清單（config.ini → RoomConfig.speedSteps；壞掉就回退內建）。</summary>
        private static float[] SpeedSteps()
            => (RoomConfig.speedSteps != null && RoomConfig.speedSteps.Length > 0)
                ? RoomConfig.speedSteps
                : new[] { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 4.0f, 5.0f, 6.0f, 8.0f };

        private static int IndexOfNearest(float[] steps, float want)
        {
            if (steps == null || steps.Length == 0) return 0;
            int best = 0; float bd = Mathf.Abs(steps[0] - want);
            for (int i = 1; i < steps.Length; i++) { float d = Mathf.Abs(steps[i] - want); if (d < bd) { bd = d; best = i; } }
            return best;
        }

        private void StepSpeed(int d)
        {
            var steps = SpeedSteps();
            _speedIndex = ((IndexOfNearest(steps, Ctx.Session.Speed) + d) % steps.Length + steps.Length) % steps.Length;
            Ctx.Session.Speed = steps[_speedIndex];
            RoomConfig.defaultSpeed = Ctx.Session.Speed;   // 持久化：玩家選的速度寫回 config.ini（下次開房沿用；刪檔 → 回預設 2.5）
            RoomConfig.Save();
            RenderWin2();
        }

        private void StepNote(int d)
        {
            int n = NoteEftArt.Length + 1;                 // +1 = 隨機
            int cur = ((Ctx.Session.NoteType + 1 + d) % n + n) % n;   // 內部索引：0=隨機, 1..n=指定+1
            Ctx.Session.NoteType = cur - 1;
            RoomConfig.defaultNoteType = Ctx.Session.NoteType;   // 持久化：玩家選的 note 寫回 config.ini（刪檔 → 回隨機）
            RoomConfig.Save();
            RenderWin2();
        }

        // Make the local head portrait FOLLOW the avatar: each frame project the avatar's head through the scene camera
        // and place the floating head (+ name) there (EXE Player_ComputeHeadRect: the looker's head portrait tracks the
        // projected Bip01_Head). Runs only while the room is mounted (_scene != null, cleared on OnHide).
        private void Update()
        {
            // F2：直接開始遊戲（等同按「開始」鈕；OnStart 內含選歌/等待/重複按守門）。
            // 只在房間為當前畫面、且非聊天輸入中才收，避免打字或選歌疊層時誤觸。
            if (Input.GetKeyDown(KeyCode.F2))
            {
                bool roomIsTop = Ctx == null || Ctx.Flow == null || Ctx.Flow.Current == ScreenId.Room;
                bool typingChat = _chatBubbleTyping || _chatBubbleInputArmed || (_chatInput != null && _chatInput.isFocused);
                if (roomIsTop && !typingChat) { UiSfx.Play(UiSfx.Click); OnStart(); }   // 按 F2 發出 SE_0001（UiSfx.Click）
            }

            // F3（除錯）：切換本機「有家族 / 沒有家族」，用來測試家族頻道兩種行為（綠字 <家族>… / 你沒有家族）。
            // 允許打字中也可按（F 鍵不會產生輸入字元）。只在編輯器裡有效，打包成 build 一律關閉（見 SdoDebugFeatures）。
            if (SdoDebugFeatures.Enabled && Input.GetKeyDown(KeyCode.F3))
            {
                bool roomIsTop = Ctx == null || Ctx.Flow == null || Ctx.Flow.Current == ScreenId.Room;
                if (roomIsTop && Ctx != null && Ctx.Session != null)
                {
                    var s = Ctx.Session;
                    // 🔴 切的是 profile 的家族覆寫,不能只切 Session.GuildName —— 送上線的身分與線上家族頻道的
                    //    門檻現在都吃 ProfileFields 的真值(見 AppContext),只動 session 的話這顆鍵在**線上**
                    //    完全沒有效果。不呼叫 ProfileManager.Save():除錯開關不該寫進存檔,重開就恢復。
                    var prof = ProfileManager.Active;
                    bool had = ProfileFields.FamilyName(prof).Length > 0;
                    ProfileFields.SetOverrides(prof, had ? "" : GameSession.DemoGuildName,
                                               had ? "" : DebugGuildEmblem, ProfileFields.PlayerLevel(prof));
                    s.GuildName = had ? "" : GameSession.DemoGuildName;   // 單機那份 MockChatService 吃這個
                    Ctx.Net?.PublishIdentity();                            // 讓房裡的人立刻看到家族列變了
                    string state = had ? LocalizationManager.Get("room.debug_guild_none") : GameSession.DemoGuildName;
                    Ctx.Chat?.SendSystem(LocalizationManager.Get("room.debug_guild", state));
                }
            }

            // ESC → 退回房間的上一層（線上=大廳、離線=選男女，見 ExitScreen）。只在房間為當前畫面、非聊天輸入中、且無 modal(商城/儲物櫃/設定)疊層、
            // 非轉場中時收——避免打字、選歌疊層、或 modal 開著時誤觸（打字中的 ESC 由 HandleRoomChatTypingKeys 取消打字）。
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                bool roomIsTop = Ctx == null || Ctx.Flow == null || Ctx.Flow.Current == ScreenId.Room;
                bool typingChat = _chatBubbleTyping || _chatBubbleInputArmed || (_chatInput != null && _chatInput.isFocused);
                bool modalOpen = FrontendApp.Instance != null && FrontendApp.Instance.AnyModalOpen;
                // 走 OnLeave（同「返回」鈕）：務必先 LeaveRoom 清掉房間，否則 CurrentRoom 殘留、換身分再進房時
                // 本機不再擁有房主座位 → IsHost=false → 房主標記消失（女→ESC→男 進房 host 字樣不見的 bug）。
                if (roomIsTop && !typingChat && !modalOpen && !ScreenTransition.Busy)
                    OnLeave();
            }

            // UI 收合/展開補間（官方 uihide/uidisplay 面板滑動）。與 3D 掛載無關，永遠推進到目標狀態。
            float ct = _uiCollapsed ? 1f : 0f;
            if (!Mathf.Approximately(_collapseT, ct))
            {
                _collapseT = Mathf.MoveTowards(_collapseT, ct, Time.unscaledDeltaTime * CollapseSpeed);
                ApplyCollapse();
            }

            HandleContextMenuDismiss();   // 座位/分隊選單開著時,點到外面就關掉
            // 「點空曠處 → 頭上打字泡」不在這裡輪詢:它是 RoomPickCatcher 的**左鍵事件**(見 OnRoomPickClick)。
            HandleRoomChatTypingKeys();
            EnsureSpectatorTypesInChatInput();   // 打字中被切成旁觀 → 泡收掉,草稿搬去左下打字框
            // 組字中持續舉旗；選字那幀 EventSystem 可能先觸發 onSubmit，旗標要撐到 LateUpdate 才清。
            if (IsRoomChatImeComposing()) _chatImeComposing = true;
            MaintainRoomChatInputFocus();
            // armed(bubble 送完待打下一則)：一開始打字就把打字泡叫回來。中文 IME 組字時 onValueChanged 不會觸發
            // （text 要等選字上屏才變），只靠 OnRoomChatInputChanged 會「續打泡不出來」；這裡每幀也看 compositionString。
            if (_chatBubbleInputArmed && !_chatBubbleTyping && _chatInput != null
                && (!string.IsNullOrEmpty(_chatInput.text) || IsRoomChatImeComposing()))
                BeginRoomBubbleTyping(preserveDraft: true);
            UpdateRoomBubbleDraft();
            UpdateChatCaret();

            if (_scene == null) return;

            // 房間仍在選歌畫面底下即時 render，但選歌疊在上面時要凍結走動（否則方向鍵會把底下的角色走來走去）。
            // 只有房間是最上層(當前畫面)時才收方向鍵。打字/focus 中鎖方向鍵；離開 focus 後立刻可走。
            bool roomTop = Ctx == null || Ctx.Flow == null || Ctx.Flow.Current == ScreenId.Room;
            bool chatCapturingKeys = _chatBubbleTyping
                || _chatBubbleInputArmed
                || (_chatInput != null && _chatInput.isFocused);
            _scene.InputEnabled = roomTop && !chatCapturingKeys;
            // 六格頭貼每幀重畫一次(見 RenderSlots 的註解:F2 面板要能即時拉位置/尺寸,
            // 而且連線模式的座位狀態是 server 推來的,不能只在 Render() 那一刻套一次)。
            RenderSlots(Ctx != null && Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null);
            EnsureChatScope();          // 房間資料可能比進場晚到(見那邊的註解);設好之後就是純比較,不花成本
            // 🔴 這五行的順序是定案的,不要重排:
            //   SyncRemoteRoomAvatars 有 rev-gate(只在房間快照變時跑) → 生/拆/換裝遠端角色
            //   ApplyRemoteMoves / SendLocalMove **不能**放進去 —— 位置每幀都要套,rev 不會每幀變
            //   名字牌與頭上泡要在位置套完之後才擺,否則會慢一幀(走動時看得出來)
            SyncRemoteRoomAvatars();
            ApplyRemoteMoves();
            SendLocalMove();
            PlaceRemoteNamePlates();
            PlaceRemoteChatBubbles();
            if (_remoteHeads != null) _remoteHeads.Tick();   // 每幀輪轉拍一格遠端頭貼

            UpdateRoomChatBubble();
            UpdateSentRoomBubbles();

            // 本機的名字牌 + 家族列:UI 的絕對設計座標,與泡一樣蓋過房間畫面、被面板擋住。
            // 刻意**不**放進泡那一層(泡要畫在名字之上),但與遠端的名字牌住同一層 —— 名字牌之間
            // 照站位排前後(SortNamePlateLayers),否則站在後面的人的名字牌會蓋住站在前面的自己。
            if (_scene.TryHeadViewport(out var vp))
            {
                if (_floatName != null && _floatName.gameObject.activeSelf)
                    PlaceFollow(_floatName.Rect, vp, -8f);   // 名字在頭的正上方
                PlaceFamilyRow(vp);                          // 家族列再往上疊一行
            }
            // 名字牌都擺完了 → 按各人站的位置重排「名字牌與名字牌」的前後(本機那面也算在內)。
            SortNamePlateLayers();

            bool needBubbleAnchor = HasBubbleOf(0)
                || (_chatBubbleRoot != null && (_chatBubbleRoot.gameObject.activeSelf || _chatBubblePendingShow));
            if (needBubbleAnchor)
            {
                if (_scene.TryChatBubbleViewport(out var bubbleVp))
                    PlaceRoomChatBubbles(bubbleVp, 0, true);
                else if (_scene.TryHeadViewport(out var fallbackVp))
                    PlaceRoomChatBubbles(fallbackVp, 0, true);
            }

            // 所有人的泡都擺完了 → 按各人站的位置重排「泡與泡」的前後。
            SortBubbleOwnerLayers();

            TickRoomPerf();              // DEV only:設了 SDO_ROOMAVATARS 才會動(量 16 隻角色的成本)
            TickAwaitingMatchStart();   // requestStart 沒回應 → 放開「開始」鈕
            TickDevCloseSeats();         // DEV only:設了 SDO_CLOSESEATS 才會動(做出滿房,驗自動轉旁觀)
            TickDevPickSong();           // DEV only:設了 SDO_PICKSONG 才會動(缺歌傳檔的實機驗證用)
            TickDevTeam();               // DEV only:設了 SDO_TEAM 才會動(驗徽章依隊伍換色)
            TickDevAutoReady();          // DEV only:設了 SDO_AUTOREADY 才會動
            TickDevAutoStart();          // DEV only:設了 SDO_AUTOSTART 才會動
            TickDevAutoSay();   // DEV only:設了 SDO_SAY 才會動(見那邊的註解)
        }

        private bool HasBubbleOf(int owner)
        {
            for (int i = 0; i < _sentBubbles.Count; i++)
                if (_sentBubbles[i] != null && _sentBubbles[i].OwnerUserId == owner) return true;
            return false;
        }

        /// <summary>
        /// 遠端玩家的泡:每人一條鏈,掛在**他自己**角色的肩膀上。
        /// 角色在鏡頭後面(看不到)時把泡藏起來 —— 不藏的話泡會黏在畫面邊角。
        /// </summary>
        private void PlaceRemoteChatBubbles()
        {
            if (_scene == null || _sentBubbles.Count == 0) return;
            _bubbleOwners.Clear();
            for (int i = 0; i < _sentBubbles.Count; i++)
            {
                var b = _sentBubbles[i];
                if (b != null && b.OwnerUserId != 0) _bubbleOwners.Add(b.OwnerUserId);
            }
            if (_bubbleOwners.Count == 0) return;

            foreach (int owner in _bubbleOwners)
            {
                Vector2 vp;
                if (_scene.TryRemoteBubbleViewport(owner, out vp)) { PlaceRoomChatBubbles(vp, owner, false); continue; }
                for (int i = 0; i < _sentBubbles.Count; i++)
                {
                    var b = _sentBubbles[i];
                    if (b != null && b.OwnerUserId == owner && b.Root != null && b.Root.gameObject.activeSelf)
                        SetBubbleActive(b, false);
                }
            }
        }

        private readonly HashSet<int> _bubbleOwners = new HashSet<int>();
        private float _lastBubblePopAt = -1f;

        /// <summary>某個人離房 → 清掉他所有的泡(角色已經被拆了,泡留著會孤兒地掛到壽命結束)。</summary>
        private void DestroySentBubblesOf(int owner)
        {
            for (int i = _sentBubbles.Count - 1; i >= 0; i--)
                if (_sentBubbles[i] != null && _sentBubbles[i].OwnerUserId == owner)
                    DestroySentRoomBubble(_sentBubbles[i]);
        }

        private void LateUpdate()
        {
            // UI / IME 事件跑完後再同步，選字 Enter 那幀 onSubmit 仍看得到「剛在組字」。
            _chatImeComposing = IsRoomChatImeComposing();
        }

        private void UpdateRoomChatBubble()
        {
            // 打字泡不壽命到期；已送出泡走 UpdateSentRoomBubbles。
            if (_chatBubbleRoot == null || !_chatBubbleRoot.gameObject.activeSelf) return;
            if (!_chatBubbleTyping) return;
            // drag 回彈整串共用，交 UpdateSentRoomBubbles / 下面 chain damp。
        }

        private void UpdateSentRoomBubbles()
        {
            float now = Time.unscaledTime;
            float heldTime = _chatBubbleChainDragging ? Time.unscaledDeltaTime : 0f;
            for (int i = _sentBubbles.Count - 1; i >= 0; i--)
            {
                var b = _sentBubbles[i];
                if (b == null || b.Root == null)
                {
                    _sentBubbles.RemoveAt(i);
                    continue;
                }
                if (heldTime > 0f)
                {
                    b.HideAt += heldTime;
                    if (!float.IsInfinity(b.TalkAt))
                        b.TalkAt += heldTime;
                }
                if (now >= b.HideAt)
                {
                    DestroySentRoomBubble(b);
                    continue;
                }
                if (now >= b.TalkAt)
                {
                    if (b.FrameAnim != null) b.FrameAnim.Frames = null;
                    if (b.Frame != null) UIKit.ApplySprite(b.Frame, RoomBubbleArt.Base(b.Style));
                    if (b.Add != null) b.Add.gameObject.SetActive(false);
                    if (b.AddAnim != null) b.AddAnim.Frames = null;
                    b.TalkAt = float.PositiveInfinity;
                }
            }
        }

        /// <param name="owner">只排這個人的泡(0 = 本機鏈)。每個人的泡各自成一條鏈,掛在自己的肩膀上。</param>
        /// <param name="includeTyping">要不要把打字泡算進這條鏈(只有本機鏈才有打字泡)。</param>
        private void PlaceRoomChatBubbles(Vector2 vp, int owner = 0, bool includeTyping = true)
        {
            float anchorTop = (1f - vp.y) * 600f;
            float visibleLeft = vp.x * 800f + ChatBubbleAnchorVisibleLeft;
            float visibleTop = anchorTop + ChatBubbleAnchorVisibleTop;

            bool typingVisible = includeTyping && _chatBubbleRoot != null
                && (_chatBubbleTyping || _chatBubbleRoot.gameObject.activeSelf || _chatBubblePendingShow);

            bool hasTypingNode = false;
            RoomBubbleLayoutNode typingNode = default;
            if (typingVisible)
            {
                typingNode = new RoomBubbleLayoutNode
                {
                    Root = _chatBubbleRoot,
                    Typing = true,
                    Position = _chatBubblePhysicsPos,
                    Velocity = _chatBubblePhysicsVel,
                    HasPhysics = _chatBubbleHasPhysics,
                    Bounds = _chatBubbleTypingArt ? RoomBubbleArt.TypingBounds() : RoomBubbleArt.BubbleBounds(_chatBubbleStyle),
                    Style = _chatBubbleStyle,
                    Dragging = _chatBubbleChainDragging && _chatBubbleDraggingTyping
                };
                hasTypingNode = true;
            }

            // 可重用的清單:這個方法現在會被呼叫「1 + 遠端人數」次/幀,每次 new 一個 List 就是 6 倍垃圾。
            _bubbleNodes.Clear();
            var nodes = _bubbleNodes;
            for (int i = _sentBubbles.Count - 1; i >= 0; i--)
            {
                var b = _sentBubbles[i];
                if (b == null || b.Root == null) continue;
                if (b.OwnerUserId != owner) continue;
                nodes.Add(new RoomBubbleLayoutNode
                {
                    Sent = b,
                    Root = b.Root,
                    Position = b.PhysicsPos,
                    Velocity = b.PhysicsVel,
                    HasPhysics = b.HasPhysics,
                    Bounds = RoomBubbleArt.BubbleBounds(b.Style),
                    Style = b.Style,
                    Dragging = _chatBubbleChainDragging && ReferenceEquals(_chatBubbleDraggedSent, b)
                });
            }

            if (!hasTypingNode && nodes.Count == 0) return;

            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0.001f, 0.05f);
            if (hasTypingNode)
            {
                Vector2 typingTarget = BubbleRootFromVisible(visibleLeft, visibleTop, typingNode.Bounds);
                if (!typingNode.HasPhysics)
                {
                    typingNode.Position = typingTarget;
                    typingNode.Velocity = Vector2.zero;
                }
                else if (!typingNode.Dragging)
                {
                    if (StepBubbleNode(ref typingNode, typingTarget, dt))
                        typingNode.HasPhysics = false;
                }
                else
                {
                    KeepDraggedBubbleNode(ref typingNode);
                }

                if (typingNode.Root != null)
                    typingNode.Root.anchoredPosition = typingNode.Position;
                _chatBubblePhysicsPos = typingNode.Position;
                _chatBubblePhysicsVel = typingNode.Velocity;
                _chatBubbleHasPhysics = typingNode.HasPhysics;
                if (_chatBubblePendingShow && _chatBubbleRoot != null)
                {
                    _chatBubbleRoot.gameObject.SetActive(true);
                    _chatBubblePendingShow = false;
                }
                if (_chatBubbleRoot != null) _chatBubbleRoot.SetAsLastSibling();
            }

            if (nodes.Count == 0) return;

            TrackBubbleOwnerDepth(owner);   // 這個人站多前面 → 決定他那層蓋不蓋得住別人的(SortBubbleOwnerLayers)

            int draggedIndex = -1;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!nodes[i].Dragging) continue;
                draggedIndex = i;
                break;
            }

            bool draggingSentBubble = _chatBubbleChainDragging && draggedIndex >= 0;
            // 整串是「緊密堆疊」一起往上飄：基準 = 最新(最後送出)那顆的年齡。一送出新泡 → nodes[0] 換成它(年齡0)→ 基準歸零
            // → 舊的(已飄高的)被拉回錨點重新疊好(compact)，之後整串繼續往上飄「不停頓」。nodes[0]=最新=串底(在錨點+baseRise)。
            float baseRise = nodes[0].Sent != null ? Mathf.Max(0f, Time.unscaledTime - nodes[0].Sent.ShownAt) * ChatBubbleRiseSpeed : 0f;
            Vector2 anchorRoot = BubbleRootFromVisible(visibleLeft, visibleTop, nodes[0].Bounds);
            var homeTargets = new Vector2[nodes.Count];
            // 打字泡也在錨點：整串往上讓「一個間距」給它，之後照 baseRise 繼續飄。不用 Max clamp——clamp 會把串釘在
            // 固定高度、等 baseRise 追上才動 = 停頓；改成固定 +間距的位移，串一被頂上去就繼續往上飄不卡。
            float stackY = anchorRoot.y + baseRise + (hasTypingNode ? OfficialBubbleFollowSpacing(nodes[0]) : 0f);
            for (int i = 0; i < nodes.Count; i++)
            {
                if (i > 0) stackY += OfficialBubbleFollowSpacing(nodes[i]);
                homeTargets[i] = new Vector2(anchorRoot.x, stackY);
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                // 目標：拖曳中「被拖那顆上面的泡」跟著實際位置(火車效果)；其餘一律用自己的 home 目標(年齡上升 + 最小間距)。
                Vector2 target = (draggingSentBubble && i > draggedIndex)
                    ? nodes[i - 1].Position + new Vector2(0f, OfficialBubbleFollowSpacing(node))
                    : homeTargets[i];

                if (node.Dragging)
                {
                    KeepDraggedBubbleNode(ref node);
                }
                else if (node.Sent != null && node.Sent.PendingShow)
                {
                    // 剛送出第一次：直接落在錨點(不從角落彈性飛入)。
                    node.Position = target;
                    node.Velocity = Vector2.zero;
                    node.HasPhysics = false;
                }
                else
                {
                    // 之後一律彈性跟隨(StepBubbleNode，跟拖曳的跟隨同款緩動)：新泡把舊泡往上頂、舊泡歸位時都帶一點彈性。
                    node.HasPhysics = !StepBubbleNode(ref node, target, dt);
                }

                nodes[i] = node;
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node.Root != null)
                    node.Root.anchoredPosition = node.Position;   // 畫是它的子物件(位移 0)→ 跟著走

                if (node.Sent != null)
                {
                    node.Sent.PhysicsPos = node.Position;
                    node.Sent.PhysicsVel = node.Velocity;
                    node.Sent.HasPhysics = node.HasPhysics;
                    if (node.Sent.PendingShow)
                    {
                        SetBubbleActive(node.Sent, true);
                        node.Sent.PendingShow = false;
                        LayoutSentBubbleInlineEmoji(node.Sent);   // 活化後才有 mesh，這時把行內 emoji 疊到打的位置
                    }
                    // 註:同一條鏈裡誰畫在上面**刻意不動** —— 這一層裡的兄弟順序就是生成順序
                    //     (最新的泡最後生成 → 畫在最上面)。這裡若補 SetAsLastSibling 反而會照 nodes 的
                    //     逆序每幀重排,把順序倒過來。
                }
            }
        }

        /// <summary>
        /// 記下這個人**沿相機視線的深度** = 他站多前面。這是泡與泡之間排前後的唯一依據
        /// (見 <see cref="SortBubbleOwnerLayers"/>)。回 false = 這一幀量不到(角色沒了/還沒生出來)。
        ///
        /// 🔴 量的是**肩膀錨點**(泡掛的那根骨頭),不是角色的 bounds 之類的東西:bounds 會被翅膀
        /// 之類的部件撐大一圈,戴翅膀的人的泡就會永遠排在最前面 —— 而症狀只有兩顆泡重疊時才看得出來。
        /// </summary>
        private bool TrackBubbleOwnerDepth(int owner)
        {
            var cam = _scene != null ? _scene.SceneCamera : null;
            if (cam == null) return false;
            Vector3 anchorWorld;
            bool haveAnchor = owner == 0
                ? _scene.TryChatBubbleAnchorWorld(out anchorWorld)
                : _scene.TryRemoteChatBubbleAnchorWorld(owner, out anchorWorld);
            if (!haveAnchor) return false;
            _bubbleDepth[owner] = Vector3.Dot(anchorWorld - cam.transform.position, cam.transform.forward);
            return true;
        }

        /// <summary>
        /// 泡與泡之間的前後關係(使用者需求):**站在前面的人的泡蓋住站在後面的人的泡**,
        /// 而且那個人往前走幾步,他的泡就跟著浮上來。
        ///
        /// 泡整個在 UI 裡 ⇒ 誰蓋誰只由**畫的順序**決定。一個人一層,每幀按各人的深度把層重排
        /// (遠的先畫、近的後畫 = 蓋住遠的)。名次的算法在 <see cref="RoomBubbleDrawOrder.FarToNear"/>(有測試)。
        ///
        /// 重排本身在 <see cref="RoomBubbleDrawOrder.ApplyFarToNear"/>(與名字牌共用同一段;
        /// 那裡有「為什麼是 SetAsLastSibling 而不是 SetSiblingIndex」的 🔴 註解)。
        /// </summary>
        private void SortBubbleOwnerLayers()
        {
            if (_bubbleOwnerLayer.Count <= 1) return;
            _bubbleSortLayers.Clear();
            _bubbleSortDepths.Clear();
            foreach (var kv in _bubbleOwnerLayer)
            {
                if (kv.Value == null) continue;
                float d;
                // 深度不知道(這一幀還沒量到:人剛進來/在鏡頭後面)→ 當成無限遠,排在所有人後面。
                if (!_bubbleDepth.TryGetValue(kv.Key, out d)) d = float.MaxValue;
                _bubbleSortLayers.Add(kv.Value);
                _bubbleSortDepths.Add(d);
            }
            RoomBubbleDrawOrder.ApplyFarToNear(_bubbleSortLayers, _bubbleSortDepths, _bubbleSortOrders);
        }

        private readonly List<RectTransform> _bubbleSortLayers = new List<RectTransform>();
        private readonly List<float> _bubbleSortDepths = new List<float>();
        private readonly List<int> _bubbleSortOrders = new List<int>();


        private static bool StepBubbleNode(ref RoomBubbleLayoutNode node, Vector2 target, float dt)
        {
            Vector2 before = node.Position;
            float t = 1f - Mathf.Pow(1f - ChatBubbleFollowStep, Mathf.Max(0.001f, dt) * ChatBubbleFollowTicksPerSecond);
            node.Position = Vector2.LerpUnclamped(node.Position, target, Mathf.Clamp01(t));
            node.Velocity = (node.Position - before) / Mathf.Max(0.001f, dt);
            if ((target - node.Position).sqrMagnitude < 0.25f)
            {
                node.Position = target;
                node.Velocity = Vector2.zero;
                return true;
            }
            return false;
        }


        private static void KeepDraggedBubbleNode(ref RoomBubbleLayoutNode node)
        {
            node.HasPhysics = true;
            node.Velocity = Vector2.zero;
        }

        // 用「固定畫布錨點」對齊(bounds 忽略)：所有 bubble sprite 都畫在同一張 171×111 畫布，打字圖(AddAni)、文字圖
        // (Talk_N)、各 style 的 body 垂直中心都在畫布 y=56.5、x=85.5。把這畫布點固定到螢幕 (refX, refY) → 換 sprite
        // 時泡身不跳位(點1)，且泡身垂直中心=文字中心一直落在 refY(文字上下置中，點2)。
        private static Vector2 BubbleRootFromVisible(float refX, float refY, Rect bounds)
            => new Vector2(refX - RoomBubbleArt.AnchorCanvasX, -(refY - RoomBubbleArt.AnchorCanvasY));

        private void SnapRoomBubbleTypingToAnchor()
        {
            if (_chatBubbleRoot == null || _scene == null) return;
            Vector2 vp;
            if (!_scene.TryChatBubbleViewport(out vp) && !_scene.TryHeadViewport(out vp))
                return;

            float anchorTop = (1f - vp.y) * 600f;
            float visibleLeft = vp.x * 800f + ChatBubbleAnchorVisibleLeft;
            float visibleTop = anchorTop + ChatBubbleAnchorVisibleTop;
            Rect bounds = _chatBubbleTypingArt ? RoomBubbleArt.TypingBounds() : RoomBubbleArt.BubbleBounds(_chatBubbleStyle);
            Vector2 pos = BubbleRootFromVisible(visibleLeft, visibleTop, bounds);
            _chatBubbleRoot.anchoredPosition = pos;
            _chatBubblePhysicsPos = pos;
            _chatBubblePhysicsVel = Vector2.zero;
            _chatBubbleHasPhysics = false;
        }

        private static float OfficialBubbleFollowSpacing(RoomBubbleLayoutNode node)
        {
            float spacing = RoomBubbleArt.CanvasH * 0.35f;
            int style = Mathf.Clamp(node.Style, 1, 11);
            if (style >= 8)
                spacing += (style * 5 - 0x23) * 2f;
            return spacing;
        }

        private void BeginRoomChatBubbleDrag(PointerEventData eventData, SentRoomBubble sent = null)
        {
            if (TryResolveRoomBubbleAtPointer(eventData, out var resolvedSent, out var resolvedTyping))
            {
                sent = resolvedTyping ? null : resolvedSent;
            }

            // 打字中的泡固定不動：不給拖（點它仍可 focus，見 OnPointerClick→ClickRoomChatBubble）。只有已送出的泡能拖。
            if (sent == null) return;

            _chatBubbleDragging = true;
            _chatBubbleChainDragging = true;
            _chatBubbleDraggedSent = sent;
            _chatBubbleDraggingTyping = sent == null;
            CaptureRoomChatBubbleChainPhysics();
            ExtendRoomBubbleLifetimeForDrag();
            Vector2 startPos = Vector2.zero;
            if (sent != null)
            {
                sent.HasPhysics = true;
                sent.PhysicsPos = sent.Root != null ? sent.Root.anchoredPosition : sent.PhysicsPos;
                sent.PhysicsVel = Vector2.zero;
                startPos = sent.PhysicsPos;
            }
            else if (_chatBubbleRoot != null)
            {
                _chatBubbleHasPhysics = true;
                _chatBubblePhysicsPos = _chatBubbleRoot.anchoredPosition;
                _chatBubblePhysicsVel = Vector2.zero;
                startPos = _chatBubblePhysicsPos;
            }

            if (TryRoomChatPointerLocal(eventData, out var pointerLocal))
            {
                _chatBubbleDragStartPointer = pointerLocal;
                _chatBubbleDragStartPos = startPos;
                _chatBubbleDragPointerCaptured = true;
            }
            else
            {
                _chatBubbleDragStartPointer = Vector2.zero;
                _chatBubbleDragStartPos = startPos;
                _chatBubbleDragPointerCaptured = false;
            }
        }

        private void ExtendRoomBubbleLifetimeForDrag()
        {
            float minHideAt = Time.unscaledTime + ChatBubbleLifetime;
            for (int i = 0; i < _sentBubbles.Count; i++)
            {
                var b = _sentBubbles[i];
                if (b != null) b.HideAt = Mathf.Max(b.HideAt, minHideAt);
            }
        }

        private void CaptureRoomChatBubbleChainPhysics()
        {
            if (_chatBubbleRoot != null && _chatBubbleRoot.gameObject.activeSelf)
            {
                _chatBubbleHasPhysics = true;
                _chatBubblePhysicsPos = _chatBubbleRoot.anchoredPosition;
                _chatBubblePhysicsVel = Vector2.zero;
            }

            for (int i = 0; i < _sentBubbles.Count; i++)
            {
                var b = _sentBubbles[i];
                if (b == null || b.Root == null) continue;
                b.HasPhysics = true;
                b.PhysicsPos = b.Root.anchoredPosition;
                b.PhysicsVel = Vector2.zero;
            }
        }

        private void DragRoomChatBubble(PointerEventData eventData, SentRoomBubble sent = null)
        {
            var draggedSent = _chatBubbleChainDragging ? _chatBubbleDraggedSent : sent;
            if (draggedSent == null) return;   // 打字中的泡不給拖（Unity 仍會發 OnDrag，這裡擋掉移動）

            float dt = Mathf.Max(0.001f, Time.unscaledDeltaTime);
            Vector2 current = draggedSent.PhysicsPos;
            Vector2 next = RoomChatDragPosition(eventData, current);
            draggedSent.HasPhysics = true;
            draggedSent.PhysicsPos = next;
            draggedSent.PhysicsVel = (next - current) / dt;
        }

        private Vector2 RoomChatDragPosition(PointerEventData eventData, Vector2 current)
        {
            if (_chatBubbleDragPointerCaptured && TryRoomChatPointerLocal(eventData, out var pointerLocal))
                return _chatBubbleDragStartPos + (pointerLocal - _chatBubbleDragStartPointer) * ChatBubbleDragScale;

            return current + RoomChatDragDelta(eventData);
        }

        private bool TryRoomChatPointerLocal(PointerEventData eventData, out Vector2 local)
        {
            local = Vector2.zero;
            if (eventData == null || Root == null) return false;
            var cam = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            return RectTransformUtility.ScreenPointToLocalPointInRectangle(Root, eventData.position, cam, out local);
        }

        private Vector2 RoomChatDragDelta(PointerEventData eventData)
        {
            if (eventData != null && Root != null)
            {
                var cam = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(Root, eventData.position, cam, out var now) &&
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(Root, eventData.position - eventData.delta, cam, out var prev))
                    return (now - prev) * ChatBubbleDragScale;

                return eventData.delta * ChatBubbleDragScale;
            }

            return Vector2.zero;
        }

        private bool TryResolveRoomBubbleAtPointer(PointerEventData eventData, out SentRoomBubble sent, out bool typing)
        {
            sent = null;
            typing = false;
            if (eventData == null) return false;

            float best = float.PositiveInfinity;
            if (_chatBubbleRoot != null && _chatBubbleRoot.gameObject.activeSelf)
            {
                Rect bounds = _chatBubbleTypingArt ? RoomBubbleArt.TypingBounds() : RoomBubbleArt.BubbleBounds(_chatBubbleStyle);
                if (TryBubbleBoundsHit(_chatBubbleRoot, bounds, eventData.position, out best))
                    typing = true;
            }

            for (int i = _sentBubbles.Count - 1; i >= 0; i--)
            {
                var b = _sentBubbles[i];
                if (b == null || b.Root == null || !b.Root.gameObject.activeSelf) continue;
                if (!TryBubbleBoundsHit(b.Root, RoomBubbleArt.BubbleBounds(b.Style), eventData.position, out var distance))
                    continue;
                if (distance >= best) continue;
                best = distance;
                sent = b;
                typing = false;
            }

            return typing || sent != null;
        }

        private static bool TryBubbleBoundsHit(RectTransform root, Rect bounds, Vector2 screenPos, out float distance)
        {
            distance = float.PositiveInfinity;
            if (root == null) return false;
            var cam = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(root, screenPos, cam, out var local))
                return false;

            float x = local.x;
            float y = -local.y;
            if (x < bounds.xMin || x > bounds.xMax || y < bounds.yMin || y > bounds.yMax)
                return false;

            var center = bounds.center;
            distance = (new Vector2(x, y) - center).sqrMagnitude;
            return true;
        }

        private void EndRoomChatBubbleDrag(SentRoomBubble sent = null)
        {
            ExtendRoomBubbleLifetimeForDrag();
            _chatBubbleDragging = false;
            _chatBubbleChainDragging = false;
            _chatBubbleDraggedSent = null;
            _chatBubbleDraggingTyping = false;
            _chatBubbleDragPointerCaptured = false;
        }

        private void ClickRoomChatBubble()
        {
            BeginRoomBubbleTyping();
        }

        private struct RoomBubbleLayoutNode
        {
            public SentRoomBubble Sent;
            public RectTransform Root;
            public bool Typing;
            public bool Dragging;
            public bool HasPhysics;
            public Vector2 Position;
            public Vector2 Velocity;
            public Rect Bounds;
            public int Style;
        }

        private sealed class SentRoomBubble
        {
            /// <summary>
            /// **命中代理**,住在說話者那一層(BubbleOwnerLayer)裡:透明、沒有畫面,只負責兩件事 ——
            /// ① 滑鼠命中(拖曳/點擊) ② 承載「絕對設計座標」。鏈物理、拖曳、命中測試、壽命補償
            /// 全部讀寫它的 anchoredPosition。
            /// </summary>
            public RectTransform Root;

            /// <summary>**畫**,貼著代理(位移永遠 0)的子物件 —— 換 sprite/換大小都不會動到命中範圍。</summary>
            public RectTransform Visual;

            public Image Frame, Add, Expression;
            public TextMeshProUGUI Text;
            public SpriteSeqAnim FrameAnim, AddAnim, ExpressionAnim;
            public int Style = 1;
            public float ShownAt, HideAt, TalkAt;
            public bool PendingShow;
            public bool HasPhysics;
            public Vector2 PhysicsPos, PhysicsVel;
            public int EmojiInlineLeadLen = -1;   // >=0：泡活化後把 Expression 疊到 Text 第 leadLen 個字之後；-1=不做

            /// <summary>
            /// 這顆泡是誰的?**0 = 本機**(它與打字泡共用同一條鏈),其餘 = 遠端玩家的 userId。
            /// 每個人的泡各自成一條鏈,掛在各自角色的肩膀上。
            /// </summary>
            public int OwnerUserId;
        }

        private sealed class RoomBubbleDragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
        {
            public RoomScreen Owner;
            public SentRoomBubble Sent;

            public void OnBeginDrag(PointerEventData eventData)
            {
                if (Owner != null) Owner.BeginRoomChatBubbleDrag(eventData, Sent);
            }

            public void OnDrag(PointerEventData eventData)
            {
                if (Owner != null) Owner.DragRoomChatBubble(eventData, Sent);
            }

            public void OnEndDrag(PointerEventData eventData)
            {
                if (Owner != null) Owner.EndRoomChatBubbleDrag(Sent);
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData != null && eventData.dragging) return;
                if (Owner != null) Owner.ClickRoomChatBubble();
            }
        }

        private void HandleRoomChatTypingKeys()
        {
            if (_chatInput == null) return;
            if (Ctx != null && Ctx.Flow != null && Ctx.Flow.Current != ScreenId.Room) return;

            bool capturing = _chatBubbleTyping || _chatBubbleInputArmed || _chatInput.isFocused;
            if (!capturing) return;

            bool composing = IsRoomChatImeComposing();
            string draft = _chatInput.text ?? "";
            bool empty = draft.Length == 0 && !composing;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (composing) return; // 先讓 IME 自己吃掉 Esc 取消選字
                CancelRoomChatTyping();
                return;
            }

            // 空了再按 Backspace 不再退出打字（改由「空字串按 Enter」退出，見 SendRoomChat）。

            // 已空才按方向鍵：退 focus，讓本幀起可走路。
            if (empty && _chatDraftWasEmpty && RoomArrowKeyDown())
            {
                CancelRoomChatTyping();
                return;
            }

            _chatDraftWasEmpty = empty;
        }

        private static bool RoomArrowKeyDown()
        {
            return Input.GetKeyDown(KeyCode.UpArrow)
                || Input.GetKeyDown(KeyCode.DownArrow)
                || Input.GetKeyDown(KeyCode.LeftArrow)
                || Input.GetKeyDown(KeyCode.RightArrow);
        }

        /// <summary>
        /// 左鍵點房間的空曠處 → 開頭上打字泡(官方在房間裡就是這樣開始說話的)。
        ///
        /// 🔴 只能由 <c>RoomPickCatcher</c> 的點擊事件進來(<see cref="OnRoomPickClick"/>),**不可以**改回
        ///    在 Update 裡用 <c>Input.GetMouseButtonDown</c> + <c>IsPointerOverGameObject()</c> 判「有沒有點到 UI」——
        ///    pick catcher 本身就是一張鋪滿整個畫面、raycastTarget=true 的透明 Graphic(右鍵 3D 角色開選單要靠它),
        ///    那個查詢從此在房間任何一點都回 true,整條路就永遠 return 掉了(這正是 922ea5c 打壞這功能的原因)。
        ///    走事件語意也才對:UGUI 的射線一律先給最上層的 Graphic,能一路走到 pick catcher 才是真的沒點到任何 UI。
        /// </summary>
        private void HandleRoomBlankChatClick()
        {
            if (_scene == null || _chatInput == null) return;
            BeginRoomBubbleTyping();
        }

        // 使用者「實體點擊」左下輸入框 → 切成輸入框打字：取消頭上藍泡、顯示回顯(字+閃爍光標+IME)，保留已打的草稿。
        // 只由 RoomChatInputClickHandle(IPointerDownHandler) 呼叫；bubble 模式的程式聚焦走 Select()，不會誤觸這裡。
        private void OnRoomChatInputPointerDown()
        {
            _chatBubbleInputArmed = false;
            _chatInputSticky = true;                       // 進入輸入框模式 → 黏住 focus，直到點空曠/退出
            if (_chatBubbleTyping) HideRoomChatBubble();   // 取消藍泡（HideRoomChatBubble 內 !armed 會 SetRoomChatInputEchoVisible(true)）
            else SetRoomChatInputEchoVisible(true);
            // TMP_InputField 自己的 OnPointerDown 會聚焦並把光標放到點擊處（標準路徑，光標穩定顯示）。
        }

        private sealed class RoomChatInputClickHandle : MonoBehaviour, IPointerDownHandler
        {
            public RoomScreen Owner;

            public void OnPointerDown(PointerEventData eventData)
            {
                if (Owner != null) Owner.OnRoomChatInputPointerDown();
            }
        }

        /// <summary>
        /// 「對這個人開始密語」—— 玩家**主動選**私聊的那條路:座位右鍵選單的「私聊」、
        /// 玩家資訊視窗(PlayerInfoModal)的私聊鈕(<c>FrontendApp</c> 建 <see cref="Nav.OpenPlayerInfo"/>
        /// 時把它包成 callback 傳進去)。
        ///
        /// 🔴 與 <see cref="InsertWhisperTarget"/> 的差別是**頻道**。那個函式在家族/回覆頻道刻意什麼都不做,
        ///    因為它服務的是「點聊天列上的人名」—— 手滑機率高,而且會把草稿搞成 <c>[名字] /家族</c>。
        ///    但從選單按下「私聊」是明確意圖,套同一條規則就變成整個動作靜默失敗(玩家只會覺得選單壞了)。
        ///    所以這裡先把頻道切回「當前」(綜合台,密語與回覆都看得到),並剝掉草稿開頭的「/家族 」——
        ///    換頻道本身刻意不清那個前綴(見 <see cref="SyncChannelInputPrefix"/>),不剝就會被塞成
        ///    <c>[名字] /家族 …</c> 送出去。打到一半的內容會保留,只是改用密語送。
        /// </summary>
        public void BeginWhisperTo(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (_chatChannel != ChatChannel.Current && _chatChannel != ChatChannel.Friend)
            {
                SetChatChannel(ChatChannel.Current);
                if (_chatInput != null) _chatInput.text = RoomChatCommand.StripGuildCommand(_chatInput.text);
            }
            InsertWhisperTarget(name);
        }

        // 點聊天列的人名 → 把 `[名字] ` 塞進輸入框，切成輸入框打字模式，保留已打的內容。
        private void InsertWhisperTarget(string name)
        {
            if (_chatInput == null || string.IsNullOrWhiteSpace(name)) return;
            if (Ctx != null && Ctx.Flow != null && Ctx.Flow.Current != ScreenId.Room) return;
            // 只有「當前」與「好友」頻道點人名才插入密語對象；家族/回覆頻道點人名不插入
            // （否則會污染家族的「/家族 」前綴，變成 [名字] /家族）。點擊會讓輸入框短暫失焦→重新聚焦，
            // 游標跳最前面的根因是 onFocusSelectAll 的 SelectAll（已在 ConfigureRoomChatInput 關掉）；這裡再
            // FocusRoomChatInput 一次把 focus/游標穩在結尾（等同「沒反應」），只是不改文字。
            if (_chatChannel != ChatChannel.Current && _chatChannel != ChatChannel.Friend)
            {
                FocusRoomChatInput();
                return;
            }

            // 切成輸入框打字模式（比照實體點輸入框）：取消頭上藍泡、顯示回顯、黏住 focus。
            _chatBubbleInputArmed = false;
            _chatInputSticky = true;
            if (_chatBubbleTyping) HideRoomChatBubble();
            else SetRoomChatInputEchoVisible(true);
            HideChatModeMenu();
            HideExpressionMenu();

            // 保留使用者已打的本文（若已有 [舊名字] 前綴就換掉，只留內容）。
            string draft = _chatInput.text ?? "";
            string body = RoomChatCommand.TryParseWhisper(draft, out _, out var existingBody) ? existingBody : draft.Trim();
            string prefix = "[" + name.Trim() + "] ";
            _chatInput.text = string.IsNullOrEmpty(body) ? prefix : prefix + body;
            _chatDraftWasEmpty = false;
            FocusRoomChatInput();   // 內含 MoveTextEnd → 游標移到結尾接著打
        }

        // 掛在聊天列 TMP 上：點到名字 <link="w|名字"> 就把 [名字] 塞進輸入框密語。
        private void OnChatWhisperLinkClick(TextMeshProUGUI text, PointerEventData eventData)
        {
            if (text == null || eventData == null) return;
            var canvas = text.canvas;
            Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;
            int idx = TMP_TextUtilities.FindIntersectingLink(text, eventData.position, cam);
            if (idx < 0 || idx >= text.textInfo.linkCount) return;
            string id = text.textInfo.linkInfo[idx].GetLinkID();
            if (string.IsNullOrEmpty(id) || !id.StartsWith(WhisperLinkId, System.StringComparison.Ordinal)) return;
            InsertWhisperTarget(id.Substring(WhisperLinkId.Length));
        }

        private sealed class ChatWhisperLinkHandle : MonoBehaviour, IPointerClickHandler
        {
            public RoomScreen Owner;
            public TextMeshProUGUI Text;

            public void OnPointerClick(PointerEventData eventData)
            {
                if (Owner != null) Owner.OnChatWhisperLinkClick(Text, eventData);
            }
        }

        private sealed class ExpressionTipHandle : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            public RoomScreen Owner;
            public string Command;
            public Vector2 LocalPos;

            public void OnPointerEnter(PointerEventData eventData)
            {
                if (Owner != null) Owner.ShowExpressionTip(Command, LocalPos);
            }

            public void OnPointerExit(PointerEventData eventData)
            {
                if (Owner != null) Owner.HideExpressionTip();
            }
        }
        // (projected from the 800×600 canvas through the UI camera so they land exactly on the slots).
        private void OnGUI()
        {
            if (!_debugOpen) return;
            GUILayout.BeginArea(new Rect(10, 10, 320, 170), GUI.skin.box);
            GUILayout.Label("Head-slot tuning (F2). All 6 heads shown.");
            GUILayout.Label($"left/right  X: {headSlotOffset.x:F0}");
            headSlotOffset.x = GUILayout.HorizontalSlider(headSlotOffset.x, -100f, 100f);
            GUILayout.Label($"up/down  Y: {headSlotOffset.y:F0}");
            headSlotOffset.y = GUILayout.HorizontalSlider(headSlotOffset.y, -60f, 100f);
            GUILayout.Label($"width  W: {headSlotSize.x:F0}");
            headSlotSize.x = GUILayout.HorizontalSlider(headSlotSize.x, 40f, 200f);
            GUILayout.Label($"height  H: {headSlotSize.y:F0}");
            headSlotSize.y = GUILayout.HorizontalSlider(headSlotSize.y, 40f, 200f);
            GUILayout.Label($"=> offset=({headSlotOffset.x:F0},{headSlotOffset.y:F0})  size=({headSlotSize.x:F0},{headSlotSize.y:F0})");
            GUILayout.EndArea();

            var cam = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            if (cam == null) return;
            if (_dbgPx == null) { _dbgPx = new Texture2D(1, 1); _dbgPx.SetPixel(0, 0, Color.green); _dbgPx.Apply(); }
            for (int i = 0; i < RoomLayout.SeatCount; i++)
                DrawCanvasBorder(cam, RoomLayout.HeadSlotX[i] + headSlotOffset.x, RoomLayout.HeadSlotY + headSlotOffset.y, headSlotSize.x, headSlotSize.y);
        }

        // draw a 2px green rectangle outline at a canvas rect (top-left x,y; size w,h), projected to screen via the UI cam
        private static void DrawCanvasBorder(Camera cam, float x, float y, float w, float h)
        {
            Vector3 tl = CanvasToGui(cam, x, y), br = CanvasToGui(cam, x + w, y + h);
            float x0 = Mathf.Min(tl.x, br.x), x1 = Mathf.Max(tl.x, br.x);
            float y0 = Mathf.Min(tl.y, br.y), y1 = Mathf.Max(tl.y, br.y);
            const float t = 2f;
            GUI.DrawTexture(new Rect(x0, y0, x1 - x0, t), _dbgPx);
            GUI.DrawTexture(new Rect(x0, y1 - t, x1 - x0, t), _dbgPx);
            GUI.DrawTexture(new Rect(x0, y0, t, y1 - y0), _dbgPx);
            GUI.DrawTexture(new Rect(x1 - t, y0, t, y1 - y0), _dbgPx);
        }

        // canvas pixel (x from left, y from top, in the 800×600 world canvas centred at origin) → GUI screen pixel
        private static Vector3 CanvasToGui(Camera cam, float x, float y)
        {
            Vector3 sp = cam.WorldToScreenPoint(new Vector3(x - 400f, 300f - y, 0f));
            return new Vector3(sp.x, Screen.height - sp.y, 0f);
        }

        // viewport (0..1, y-up) → 800×600 canvas, centred on x, rect TOP at the point + topOffset (negative = above).
        private static void PlaceFollow(RectTransform rt, Vector2 vp, float topOffset)
        {
            float topFromTop = (1f - vp.y) * 600f + topOffset;
            rt.anchoredPosition = new Vector2(vp.x * 800f - rt.sizeDelta.x * 0.5f, -topFromTop);
        }

        // 設定頭上「家族列」(徽章＋家族名稱)的內容與顯示與否；實際位置每幀由 PlaceFamilyRow 跟著頭擺放。
        //   familyName 留空 → 整條家族列(名稱+徽章)不顯示。
        //   familyEmblem 留空或載入失敗 → 只顯示家族名稱、不放徽章。
        //
        // 值走 ProfileFields 而不是直接讀外層的 profile.json:那份只是**所有角色共用的預設**,
        // 這個角色自己設過就以它自己的為準。直接讀預設的話,切到有自訂家族的角色時名牌不會跟著換。
        private void UpdateFamilyRow()
        {
            if (_localFamily == null) return;
            var prof = ProfileManager.Active;
            _localFamily.Set(ProfileFields.FamilyName(prof), ProfileFields.FamilyEmblem(prof));
        }

        // 把頭上「家族列」整組水平置中於頭部，疊在名字上方一行(排版數學見 RoomFamilyRow.Place)。
        private void PlaceFamilyRow(Vector2 vp)
        {
            if (_localFamily == null) return;
            // 名字列頂端 = (1-vp.y)*600 - 8（見 Update 內 PlaceFollow 給 _floatName 的 topOffset=-8）。
            _localFamily.Place(vp.x * 800f, NameTopOf(vp));
        }

        /// <summary>名字列 holder 的頂端 y(設計座標,自上緣往下為正)。家族列疊在它上方一行。</summary>
        private static float NameTopOf(Vector2 vp) => (1f - vp.y) * 600f - 8f;

        /// <summary>
        /// 六格頭貼的**唯一**繪製點:頭貼、名字、房主徽章、關閉座位的 🚫 覆蓋圖。
        ///
        /// 🔴 這件事以前在 <c>Render()</c> 與 <c>Update()</c> 各做了一次,而 <c>Update()</c> 那份每幀重套
        /// 位置/尺寸/顯示 —— 所以任何「只寫在 Render() 的座位狀態」都會被下一幀蓋回去。
        /// 合成一個之後就只有一條路徑,新的狀態(缺歌/遊戲中/下載中徽章)加在這裡就不會被覆蓋。
        ///
        /// 位置與尺寸為什麼要每幀套:F2 除錯面板能即時拉 <see cref="headSlotOffset"/>/<see cref="headSlotSize"/>
        /// 對位,所以不能只在建立時套一次。
        /// </summary>
        // ==================== 座位操作(房主專屬)====================
        // 每一項 server 都獨立驗過(R7 host-only / R8 不准關自己的位子 / TransferHost 要求目標在座位上)。
        // 這邊的守門純粹是 UX:不要把按不動的東西畫出來。規則本體在 RoomSlotMenu(純函式 + 測試)。

        /// <summary>我現在是這間房的房主、而且在連線模式嗎?(離線房只有自己一個人,這些操作沒有意義)</summary>
        private bool CanManageSeats(RoomInfo room)
            => room != null && Ctx != null && Ctx.Net != null
               && Ctx.Net.IsConnected && Ctx.Net.InRoom && Ctx.Net.IsHost;

        private void OnSlotPointerClick(int seat, PointerEventData ev)
        {
            if (ev == null || seat < 0 || seat >= RoomLayout.SeatCount) return;
            if (Ctx != null && Ctx.Flow != null && Ctx.Flow.Current != ScreenId.Room) return;
            if (FrontendApp.Instance != null && FrontendApp.Instance.AnyModalOpen) return;

            var room = Ctx != null && Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
            bool host = CanManageSeats(room);
            bool isSelf = seat == LocalSeatIndex(room);

            if (ev.button == PointerEventData.InputButton.Right)
            {
                ShowSlotPopup(seat, ev.position, room, host, isSelf);
                return;
            }
            // 雙擊 = 鎖格/解鎖。有人坐的位子 server 會先把他踢掉再關(R8)—— 那就是「鎖住這一格」的語意。
            if (ev.button == PointerEventData.InputButton.Left && ev.clickCount >= 2
                && RoomSlotMenu.DoubleClickAllowed(host, true, isSelf))
            {
                var s = SeatAt(room, seat);
                Ctx.Net.SetSeatClosed(seat, RoomSlotMenu.DoubleClickClosesSeat(s != null && s.IsClosed));
                UiSfx.Play(UiSfx.Click);
                CloseSlotPopup();
            }
        }

        private static SeatInfo SeatAt(RoomInfo room, int seat)
            => room != null && seat >= 0 && seat < room.Seats.Count ? room.Seats[seat] : null;

        /// <summary>
        /// 房間畫面「什麼 UI 都沒有的地方」的點擊總入口(<c>RoomPickCatcher</c>,建在整張 UI 最底層):
        ///   • **左鍵** → 開頭上打字泡(<see cref="HandleRoomBlankChatClick"/>)。
        ///   • **右鍵** → 看看指到的是不是**房裡的某個 3D 角色**,是的話開那個人的座位選單。
        ///
        /// 🔴 左鍵這條**一定要掛在這裡**。它以前是 Update 裡自己輪詢滑鼠 + <c>IsPointerOverGameObject()</c>,
        ///    而 pick catcher 一鋪滿畫面就讓那個查詢永遠是 true → 點空曠處再也開不出泡。理由見那邊的 doc。
        ///
        /// 官方在房間裡右鍵人物本體就會跳選單(使用者回報「不只點上面大頭貼」),而我們原本只有上排那六格
        /// 頭貼接得到右鍵。這裡刻意**重用 <see cref="ShowSlotPopup"/>** —— 兩個入口點同一個人就該給同一份選單
        /// (含房主的踢人/轉房主),分兩套遲早會長歪。
        ///
        /// 挑到的人**不一定坐在座位上** —— 旁觀席上的十個人(以及站上旁觀席的自己)也挑得到,
        /// 那時走 <see cref="ShowSpectatorPopup"/> 給社交選單(玩家信息 / 私聊 / 加為好友)。
        /// 需要座位的只有房主那組管理項(踢人要座位號、開關位置要座位號),社交項只需要「一個人」;
        /// 以前整條路徑先換算座位、查不到就 return,於是右鍵旁觀者完全沒有反應(使用者回報)。
        /// </summary>
        private void OnRoomPickClick(PointerEventData ev)
        {
            if (ev == null) return;
            if (Ctx != null && Ctx.Flow != null && Ctx.Flow.Current != ScreenId.Room) return;
            if (FrontendApp.Instance != null && FrontendApp.Instance.AnyModalOpen) return;

            if (ev.button == PointerEventData.InputButton.Left)
            {
                HandleRoomBlankChatClick();
                return;
            }
            if (ev.button != PointerEventData.InputButton.Right) return;
            if (_scene == null || Root == null) return;

            // 設計座標 → 場景相機的 viewport。相機鋪滿整張 backdrop、backdrop 又鋪滿 Root,所以就是單純的正規化;
            // y 要翻(設計座標由上往下、viewport 由下往上),而 flipBackdropV 開著時畫面本身已經上下顛倒 → 不再翻。
            Vector2 d = PointerToDesign(ev.position);
            var r = Root.rect;
            float fw = r.width > 0f ? r.width : 800f;
            float fh = r.height > 0f ? r.height : 600f;
            var vp = new Vector2(d.x / fw, flipBackdropV ? d.y / fh : 1f - d.y / fh);

            if (!_scene.TryPickAvatar(vp, out int userId)) return;   // 點到地板/家具 → 什麼都不做

            var room = Ctx != null && Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
            var target = RoomPickTarget.Resolve(room, userId, LocalUserId, LocalSeatIndex(room), LocalSpectating);
            switch (target.Kind)
            {
                case RoomPickKind.Seat:
                    if (target.Index >= RoomLayout.SeatCount) return;
                    ShowSlotPopup(target.Index, ev.position, room, CanManageSeats(room), target.IsSelf);
                    break;
                case RoomPickKind.Spectator:
                    ShowSpectatorPopup(target.Index, userId, ev.position, room, target.IsSelf);
                    break;
            }
        }

        /// <summary>
        /// **旁觀席上那個人**的右鍵選單 —— 只有社交項(玩家信息 / 私聊 / 加為好友)。
        ///
        /// 刻意重用 <see cref="RoomSlotMenu.For"/> 而不是大廳那份 <see cref="PlayerContextMenu"/>:
        /// 這是房間裡的同一顆選單,只是這個人沒有座位 → 房主組的守門直接餵 <c>isHost: false</c>
        /// (旁觀者不佔座位,踢人/轉房主/開關位置一項都不適用),社交組的規則則一字不改。
        /// 兩份規則各寫一次的下場見 <see cref="RoomSlotMenu"/> 的類別註解。
        ///
        /// <paramref name="index"/> 是旁觀名單的索引;-1 = 本機自己但快照還沒把他放進名單
        /// (按下「旁觀」到 server 回快照之間)—— 那時只有「玩家信息」,而它開的是本機那份,不需要名單。
        /// </summary>
        private void ShowSpectatorPopup(int index, int userId, Vector2 screenPos, RoomInfo room, bool isSelf)
        {
            CloseSlotPopup();
            var sp = room != null && room.Spectators != null && index >= 0 && index < room.Spectators.Count
                ? room.Spectators[index] : null;
            string who = sp != null ? (sp.DisplayName ?? "").Trim() : "";
            // 名字是社交項的鍵(好友清單以名字為鍵、私聊也要名字)—— 查不到名字的人只剩「玩家信息」。
            bool taken = isSelf || who.Length > 0;
            bool friend = who.Length > 0 && FriendList.IsFriend(ProfileManager.Active, who);
            var actions = RoomSlotMenu.For(false, Online, isSelf, taken, false, friend);
            if (actions.Length == 0) return;

            _slotPopup = BuildSlotMenu("SpectatorPopup", screenPos, actions.Length,
                idx => SlotActionLabel(actions[idx]),
                idx =>
                {
                    switch (actions[idx])
                    {
                        case RoomSlotAction.PlayerInfo: OpenSpectatorPlayerInfo(userId, isSelf); break;
                        case RoomSlotAction.Whisper: BeginWhisperTo(who); break;
                        // 好友清單的鍵是**名字**,備查編號線上一律用 server 的 userId
                        // (與 NetRoomMapping.ToSeatInfo 給座位的 PlayerProfile.Id 同一個約定)。
                        case RoomSlotAction.AddFriend: ToggleFriend(who, UserIdText(userId), true); break;
                        case RoomSlotAction.RemoveFriend: ToggleFriend(who, UserIdText(userId), false); break;
                    }
                    CloseSlotPopup();
                });
        }

        private static string UserIdText(int userId)
            => userId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>座位右鍵選單。項目由 <see cref="RoomSlotMenu"/> 決定(空 → 不彈)。</summary>
        private void ShowSlotPopup(int seat, Vector2 screenPos, RoomInfo room, bool host, bool isSelf)
        {
            CloseSlotPopup();
            var s = SeatAt(room, seat);
            bool taken = s != null && !s.IsEmpty;
            bool closed = s != null && s.IsClosed;
            string who = taken && s.Player != null ? (s.Player.DisplayName ?? "") : "";
            // 「是不是好友」在這裡查完再餵給純規則 —— 好友清單住在 profile.json,
            // RoomSlotMenu 碰它就變成要有檔案系統才測得動(見那邊的 doc)。
            bool friend = taken && FriendList.IsFriend(ProfileManager.Active, who);
            var actions = RoomSlotMenu.For(host, Online, isSelf, taken, closed, friend);
            if (actions.Length == 0) return;

            int targetUser = taken && s != null ? s.UserId : 0;
            _slotPopup = BuildSlotMenu("SlotPopup", screenPos, actions.Length,
                idx => SlotActionLabel(actions[idx]),
                idx =>
                {
                    switch (actions[idx])
                    {
                        // 社交三項誰都能用 → **不能**再套房主二次驗證(那道守門只屬於管理項,見 HostSlotAction)。
                        case RoomSlotAction.PlayerInfo: OpenSeatPlayerInfo(seat, isSelf); break;
                        // 走 BeginWhisperTo 不是 InsertWhisperTarget:選單是明確意圖,在家族頻道也要能開始密語。
                        case RoomSlotAction.Whisper: BeginWhisperTo(who); break;
                        case RoomSlotAction.AddFriend: ToggleSeatFriend(s, true); break;
                        case RoomSlotAction.RemoveFriend: ToggleSeatFriend(s, false); break;
                        default: HostSlotAction(actions[idx], seat, targetUser); break;
                    }
                    CloseSlotPopup();
                });
        }

        /// <summary>
        /// 房主專屬的那四項。
        ///
        /// 🔴 這裡再檢查一次房主身分:選單彈出到按下之間房主可能已經被轉走
        ///    (自己交出房主、或 server 因為別人離開重新指派)。原本這段寫在 callback 開頭,
        ///    社交項加進來之後不能再那樣寫 —— 非房主連「玩家信息」都會被那個 return 吃掉。
        /// </summary>
        private void HostSlotAction(RoomSlotAction a, int seat, int targetUser)
        {
            var now = Ctx != null && Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
            if (!CanManageSeats(now)) return;
            switch (a)
            {
                case RoomSlotAction.Kick: if (targetUser != 0) Ctx.Net.KickUser(targetUser); break;
                case RoomSlotAction.TransferHost: if (targetUser != 0) Ctx.Net.TransferHost(targetUser); break;
                case RoomSlotAction.CloseSeat: Ctx.Net.SetSeatClosed(seat, true); break;
                case RoomSlotAction.OpenSeat: Ctx.Net.SetSeatClosed(seat, false); break;
            }
        }

        /// <summary>
        /// 「玩家信息」→ 玩家資訊視窗。視窗本體是 modal,由 FrontendApp 接到
        /// <see cref="Nav.OpenPlayerInfo"/> / <see cref="Nav.OpenSelfInfo"/>;沒接的時候什麼也不做(不是 NRE)。
        ///
        /// 座位快照是每幀重來的,所以按下的當下重新查一次 —— 彈選單到按下之間那個人可能已經離開,
        /// 用彈出時抓的 SeatInfo 會開出一個「已經不在房裡的人」的視窗。
        /// </summary>
        private void OpenSeatPlayerInfo(int seat, bool isSelf)
        {
            if (isSelf) { Nav.OpenSelfInfo?.Invoke(); return; }
            var room = Ctx != null && Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
            var s = SeatAt(room, seat);
            if (s == null || s.IsEmpty) return;
            Nav.OpenPlayerInfo?.Invoke(s.Player, GenderOfUser(s.UserId), s.UserId);
        }

        /// <summary>
        /// 「玩家信息」—— **旁觀席**上那個人的版本。
        ///
        /// 旁觀者沒有 <see cref="SeatInfo"/>,所以現組一份 <see cref="PlayerProfile"/> 餵給視窗
        /// (名字/等級/家族都在旁觀名單裡,見 <see cref="NetRoomMapping"/>);命中率那些數字與真正的穿搭
        /// 一樣是視窗自己拿 userId 去跟 server 要名片 —— 那條查詢認的是**連線**不是座位,旁觀者照樣查得到。
        ///
        /// 名單在按下的當下**重查一次**(同 <see cref="OpenSeatPlayerInfo"/>):快照每幀重來,
        /// 彈選單到按下之間那個人可能已經走了,用彈出時抓的那筆會開出一個已經不在房裡的人。
        /// </summary>
        private void OpenSpectatorPlayerInfo(int userId, bool isSelf)
        {
            if (isSelf) { Nav.OpenSelfInfo?.Invoke(); return; }
            var room = Ctx != null && Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
            int i = RoomPickTarget.SpectatorIndexOf(room, userId);
            if (i < 0) return;
            var sp = room.Spectators[i];
            var who = new PlayerProfile(UserIdText(sp.UserId), sp.DisplayName ?? "", sp.Level, sp.Guild ?? "");
            Nav.OpenPlayerInfo?.Invoke(who, GenderOfUser(userId), userId);
        }

        /// <summary>那個人的性別(0=女 1=男)。<see cref="SeatInfo"/> 沒帶性別,所以從連線快照的
        /// <c>NetAvatarLook</c> 查 —— 那正是房間 3D 拿來建他角色的同一份資料,不會出現「視窗畫男的、房裡站女的」。
        /// 座位上查不到就再查**旁觀名單**(旁觀者的 Look 一樣在快照裡);兩邊都查不到
        /// (離線 / 剛離開)才退回本機性別,至少不是一個非法值。</summary>
        private int GenderOfUser(int userId)
        {
            var snap = Ctx != null && Ctx.Net != null ? Ctx.Net.Room : null;
            if (snap != null && userId != 0)
            {
                var ns = snap.SeatOf(userId);
                if (ns != null && ns.Look != null) return ns.Look.Gender;
                int si = snap.SpectatorIndexOf(userId);
                if (si >= 0 && snap.Spectators != null && si < snap.Spectators.Length)
                {
                    var spec = snap.Spectators[si];
                    if (spec != null && spec.Look != null) return spec.Look.Gender;
                }
            }
            return Ctx != null && Ctx.Session != null ? Ctx.Session.Gender : 0;
        }

        /// <summary>座位版的加 / 刪好友(名字與存檔編號都在 <see cref="SeatInfo.Player"/> 裡)。</summary>
        private void ToggleSeatFriend(SeatInfo s, bool add)
        {
            if (s == null || s.Player == null) return;
            ToggleFriend(s.Player.DisplayName, s.Player.Id, add);
        }

        /// <summary>
        /// 加 / 刪好友。
        ///
        /// 清單存在**自己的** profile.json(為什麼不是 server 見 <see cref="FriendList"/> 的 doc),
        /// 而那一層刻意不自己存檔 → 改完一定要 <c>ProfileManager.Save()</c>,否則關掉遊戲就沒了。
        /// 結果一定要 Toast 講出來:好友清單不在這個畫面上,沒有回饋玩家會以為按了沒反應
        /// (尤其「已經是好友」「清單滿了」這兩種失敗,畫面上完全看不出差別)。
        /// </summary>
        private void ToggleFriend(string displayName, string playerId, bool add)
        {
            string name = (displayName ?? "").Trim();
            if (name.Length == 0) return;
            var me = ProfileManager.Active;
            bool ok = add ? FriendList.Add(me, name, playerId ?? "", System.DateTime.UtcNow.ToString("o"))
                          : FriendList.Remove(me, name);
            if (ok) ProfileManager.Save();
            string key = add ? (ok ? "room.friend_added" : "room.friend_add_failed")
                             : (ok ? "room.friend_removed" : "room.friend_remove_failed");
            Toast.Show(LocalizationManager.Get(key, name));
        }

        private string SlotActionLabel(RoomSlotAction a)
        {
            switch (a)
            {
                case RoomSlotAction.PlayerInfo: return L("room.slot_player_info");
                case RoomSlotAction.Whisper: return L("room.slot_whisper");
                case RoomSlotAction.AddFriend: return L("room.slot_add_friend");
                case RoomSlotAction.RemoveFriend: return L("room.slot_remove_friend");
                case RoomSlotAction.Kick: return L("room.slot_kick");
                case RoomSlotAction.TransferHost: return L("room.slot_transfer_host");
                case RoomSlotAction.CloseSeat: return L("room.slot_close");
                default: return L("room.slot_open");
            }
        }

        /// <summary>滑鼠螢幕座標 → 800×600 設計座標(左上原點、y 往下)。同 SongSelectScreen.ScreenToDesign 的算法。</summary>
        private Vector2 PointerToDesign(Vector2 screenPos)
        {
            var cam = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            if (Root != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(Root, screenPos, cam, out var lp))
            {
                var r = Root.rect;
                return new Vector2(lp.x - r.xMin, r.yMax - lp.y);
            }
            return Vector2.zero;
        }

        private void CloseSlotPopup()
        {
            if (_slotPopup != null) { Destroy(_slotPopup); _slotPopup = null; }
        }

        /// <summary>
        /// 座位右鍵選單 —— 官方 SP_PopMenu 的複刻。繪製本體搬到 <see cref="SdoPopupMenu"/>:
        /// 官方**同一個選單樣式**還出現在大廳玩家名單與「房間信息」的參與者列表,複製第二份就要再踩一次
        /// 那邊註解裡的坑(白框、9-slice 的 ppu、圖集滲白)。這裡只剩「彈出的那一幀」這個畫面自己的狀態。
        ///
        /// 為什麼跟 <see cref="BuildContextMenu"/> 分家:官方那張列圖是 92px 的固定膠囊,而分隊選單的
        /// 「2對2對2」在 92px 內會被夾壞。共用一個繪製函式就一定有一邊要犧牲 —— 座位選單有官方美術可對,
        /// 分隊選單沒有(那顆鈕是我們加的),所以各走各的。
        /// </summary>
        private GameObject BuildSlotMenu(string name, Vector2 screenPos, int count,
                                         System.Func<int, string> labelOf, System.Action<int> onPick)
        {
            var cam = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            var go = SdoPopupMenu.Build(Root, name, screenPos, cam, count, labelOf, onPick);
            _slotPopupFrame = Time.frameCount;
            return go;
        }

        /// <summary>
        /// 純色的小彈出選單 —— 現在**只剩房主的自動分隊選單**在用。
        ///
        /// 座位選單已經換成官方美術(<see cref="BuildSlotMenu"/>,對照 UI/ROOM/POPMENU.XML 的 SP_PopMenu)。
        /// 分隊選單留在這個純色版本,是因為官方**沒有**這顆鈕(右鍵組隊格是我們加的,見 <see cref="BuildTeamToggle"/>),
        /// 沒有可以對照的美術;而官方那張列圖是 92px 的固定膠囊,「2對2對2」塞進去會被夾壞。
        /// 深色底 + 白字,寬度依最長那一項算。位置是滑鼠的設計座標,並夾進 800×600 框內。
        /// </summary>
        private GameObject BuildContextMenu(string name, Vector2 screenPos, int count,
                                            System.Func<int, string, string> labelOf, System.Action<int> onPick)
        {
            const float rowH = 22f, padX = 10f, fontSize = 13f;
            var labels = new string[count];
            float w = 60f;
            for (int i = 0; i < count; i++)
            {
                labels[i] = labelOf(i, null) ?? "";
                w = Mathf.Max(w, labels[i].Length * fontSize * 0.62f + padX * 2f);   // 粗估:CJK 一字約 0.62em
            }
            float h = rowH * count;
            Vector2 tl = PointerToDesign(screenPos);
            float x = Mathf.Clamp(tl.x, 0f, 800f - w);
            float y = Mathf.Clamp(tl.y, 0f, 600f - h);

            var panel = UIKit.AddImage(Root, name, new Color32(0x2A, 0x1B, 0x45, 0xF0), raycast: true);
            Place(panel.rectTransform, x, y, w, h);
            panel.transform.SetAsLastSibling();
            for (int i = 0; i < count; i++)
            {
                int idx = i;
                var row = UIKit.AddImage(panel.rectTransform, "Row" + i, new Color(1f, 1f, 1f, 0f), raycast: true);
                Place(row.rectTransform, 0, rowH * i, w, rowH);
                var btn = row.gameObject.AddComponent<Button>();
                btn.targetGraphic = row;
                btn.transition = Selectable.Transition.ColorTint;
                btn.colors = new ColorBlock
                {
                    normalColor = new Color(1f, 1f, 1f, 0f),
                    highlightedColor = new Color(1f, 1f, 1f, 0.18f),
                    pressedColor = new Color(1f, 1f, 1f, 0.30f),
                    selectedColor = new Color(1f, 1f, 1f, 0.18f),
                    disabledColor = new Color(1f, 1f, 1f, 0f),
                    colorMultiplier = 1f, fadeDuration = 0.05f,
                };
                UiSfx.AttachClick(btn);
                UiHoverSfx.Attach(btn);
                btn.onClick.AddListener(() => onPick(idx));
                var t = UIKit.AddText(row.rectTransform, "Label", labels[i], fontSize, Color.white,
                                      TextAlignmentOptions.Left);
                Place(t.rectTransform, padX, 2, w - padX * 2f, rowH - 2f);
            }
            _slotPopupFrame = Time.frameCount;
            return panel.gameObject;
        }

        /// <summary>選單開著時點到選單外面 → 關掉(彈出那一幀不算,否則會被觸發它的那次點擊自己關掉)。</summary>
        private void HandleContextMenuDismiss()
        {
            if (_slotPopup == null) return;
            if (Time.frameCount == _slotPopupFrame) return;
            if (!Input.GetMouseButtonDown(0) && !Input.GetMouseButtonDown(1)) return;
            var rt = _slotPopup.transform as RectTransform;
            var cam = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            if (rt != null && RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, cam)) return;
            CloseSlotPopup();
        }

        private void RenderSlots(RoomInfo room)
        {
            Texture localHeadTex = _localHead != null ? _localHead.Texture : null;
            int localSeat = LocalSeatIndex(room);

            for (int i = 0; i < RoomLayout.SeatCount; i++)
            {
                var seat = room != null && i < room.Seats.Count ? room.Seats[i] : null;
                bool taken = seat != null && !seat.IsEmpty;
                bool closed = seat != null && seat.IsClosed;
                bool isLocal = i == localSeat;

                // 命中盒跟著頭貼格走(F2 對位面板會即時改 headSlotOffset/headSlotSize)。
                // 它一直是 active 的 —— 空位/關閉的位子也要能被右鍵(那正是「開啟/關閉位子」要點的地方)。
                if (_slotHit[i] != null)
                {
                    var hr = _slotHit[i].rectTransform;
                    // 與下面 _slotHead 的算式**逐字相同**(同一個 _win1Root 座標系)—— 兩邊會一起被 F2 面板改,
                    // 寫成不同式子的話某天調版位就會只動一邊,命中盒與頭貼錯開。
                    hr.anchoredPosition = new Vector2(RoomLayout.HeadSlotX[i] + headSlotOffset.x,
                                                      -(RoomLayout.HeadSlotY + headSlotOffset.y));
                    hr.sizeDelta = headSlotSize;
                }

                if (_slotHead[i] != null)
                {
                    var rt = _slotHead[i].rectTransform;
                    rt.anchoredPosition = new Vector2(RoomLayout.HeadSlotX[i] + headSlotOffset.x,
                                                      -(RoomLayout.HeadSlotY + headSlotOffset.y));
                    rt.sizeDelta = headSlotSize;
                    // 本機那格用 RoomHeadPortrait(它自己有一隻 idle avatar,會鏡射走路/動作);
                    // 遠端那幾格由 RoomRemoteHeadSet 拍房間裡**已經在跑**的那隻角色 —— 不另外建 avatar。
                    // _debugOpen(F2 對位面板)時六格都畫本機的頭貼,方便對版位。
                    Texture tex = null;
                    if (isLocal || _debugOpen) tex = localHeadTex;
                    else if (taken && _remoteHeads != null) tex = _remoteHeads.Texture(seat.UserId);
                    bool showHead = tex != null;
                    _slotHead[i].texture = tex;
                    _slotHead[i].enabled = showHead;
                }

                // 🚫 覆蓋圖:被房主「關閉」的位子一定畫。空但開放的位子只有 showEmptySeatCovers 開著才畫
                // (那個欄位的原意就是這樣;離線單人房預設關掉,畫面比較乾淨)。有人坐的位子絕不畫。
                if (_slotClose[i] != null) _slotClose[i].enabled = !taken && (closed || showEmptySeatCovers);

                // 🔴 房主徽章跟 HostUserId 走,不是「座位 0」—— 轉移房主時 server 只換那個值、不搬座位。
                // 離線模式沒有 userId(恆 0),那時退回 SeatInfo.IsHost。
                bool seatIsHost = taken && (seat.UserId != 0 && room != null ? room.IsHostUser(seat.UserId) : seat.IsHost);
                int badgeFrame = taken ? RoomBadgeFrames.ForTeam(seat.Team) : 0;
                RenderSeatBadges(i, seat, taken, seatIsHost, badgeFrame, isLocal);

                // 名字底下那條名牌:選了隊才畫,畫的是那一隊的色條(官方 Team.an 第 1/2/3 幀)。
                // 沒選隊(自由)= 官方的第 0 幀是 1×1 空白 → 這裡直接不畫,名字就落在頭貼面板原本的紫底上。
                if (_slotPlate[i] != null)
                {
                    bool showPlate = taken && TeamColors.IsTeam(seat.Team);
                    _slotPlate[i].enabled = showPlate;
                    if (showPlate) UIKit.ApplySprite(_slotPlate[i], Frame(_plateFrames, badgeFrame));
                }

                if (_slotName[i] != null)
                {
                    _slotName[i].gameObject.SetActive(taken);
                    // 本機那格用 LocalName():離線模式的座位名可能還沒同步到改過的名字。
                    if (taken) _slotName[i].text = isLocal ? LocalName(room)
                                                          : (seat.Player != null ? seat.Player.DisplayName : "");
                }

                RenderSlotBar(i, seat, taken);
            }
        }

        /// <summary>
        /// 頭貼下緣那一條徽章:PLAYING / NO MAP / HOST / READY **四張共用同一個位置**,一次只畫一張。
        ///
        /// 「哪一張」是純邏輯,住在 <see cref="RoomBadgeChoice"/>(優先序 PLAYING &gt; NO MAP &gt; HOST &gt; READY
        /// 與理由都寫在那裡,並由 RoomBadgeChoiceTests 釘住)。這裡只負責把選中的那一張開起來、
        /// 換成**那個人自己的隊伍色**那一幀,其餘三張關掉。
        ///
        /// 🔴 <see cref="_starting"/> 期間整條凍結。server 在開場那一刻就把所有參與者轉成 waitingForLoad
        /// (頭貼會立刻翻成 PLAYING),但那時本機的黑幕才剛開始淡 —— 不擋的話自己會眼睜睜看著
        /// 自己那格在亮著的畫面上翻牌,而要的是「畫面先全黑、進 loading,之後才換」。
        /// 順序是穩的:server 先送 matchStarting(這裡才會 _starting=true)才廣播含新狀態的房間快照,
        /// 同一條連線不會倒過來。留在房間的人(沒被納入這場、缺歌的旁觀者)收不到 matchStarting,
        /// _starting 恆 false → 照樣即時看到別人翻成 PLAYING,那正是他們需要的資訊。
        ///
        /// 🔴 <paramref name="isLocal"/> 管的是**回來**那半段:自己那格永遠不畫 PLAYING(理由見
        /// <see cref="RoomBadgeChoice.For"/> 的參數說明)。兩道守門缺一不可 —— _starting 擋出去、
        /// isLocal 擋回來,少了後者的症狀是「中離回房後自己那格掛著 PLAYING 直到全場打完」。
        /// </summary>
        private void RenderSeatBadges(int i, SeatInfo seat, bool taken, bool seatIsHost, int badgeFrame, bool isLocal)
        {
            if (_starting) return;

            var badge = taken
                ? RoomBadgeChoice.For(true, seatIsHost, seat.IsReady, seat.PlayState, seat.Avail, isLocal)
                : RoomSeatBadge.None;

            Badge(_slotPlaying[i], badge == RoomSeatBadge.Playing, _playingFrames, badgeFrame);
            Badge(_slotMissing[i], badge == RoomSeatBadge.NoMap, _noMapFrames, badgeFrame);
            Badge(_slotMaster[i], badge == RoomSeatBadge.Host, _masterFrames, badgeFrame);
            Badge(_slotReady[i], badge == RoomSeatBadge.Ready, _readyFrames, badgeFrame);
        }

        /// <summary>徽章條上的一張:要畫就換成那個隊伍色的幀,不畫就關掉(關掉時不換圖,省一次 mesh 重建)。</summary>
        private static void Badge(Image img, bool show, Sprite[] frames, int frame)
        {
            if (img == null) return;
            img.enabled = show;
            if (!show) return;
            UIKit.ApplySprite(img, Frame(frames, frame));
            StretchToBadgeRow(img);   // ApplySprite 會把 sizeDelta 設回 sprite 原生尺寸 → 蓋回統一矩形
        }

        /// <summary>
        /// 把徽章撐成徽章條的統一矩形 BadgeW×BadgeH。
        ///
        /// UGUI 的 Image 預設就是「把 sprite 拉滿 rect」(simple + 不保持長寬比),所以只要蓋掉
        /// <see cref="UIKit.ApplySprite"/> 依原生尺寸算出來的 sizeDelta,矮 3px 的 PLAYING 就會被
        /// 垂直拉伸填滿,而不是照原比例畫成一張比較矮的圖。
        /// </summary>
        private static void StretchToBadgeRow(Image img)
        {
            if (img == null) return;
            img.preserveAspect = false;   // 明講:不保持長寬比(預設就是 false,但這一行是這個方法的重點)
            img.rectTransform.sizeDelta = new Vector2(BadgeW, BadgeH);
        }

        /// <summary>
        /// 一格頭貼的傳檔跑條(夾在頭貼下緣與名牌之間那條縫)。
        ///
        /// 與徽章條是分開的兩件事,可以同時出現:一邊下載、一邊還是缺歌 → NO MAP 徽章 + 綠色跑條在跑。
        /// </summary>
        private void RenderSlotBar(int i, SeatInfo seat, bool taken)
        {
            // 跑條:自己的進度看本機的傳檔器(每幀最新),別人的看 server 轉播的 blobProgress;
            // 另外 server 的座位快照本身也帶著下載進度(availProgress)—— 兩個來源取有值的那個。
            float frac = 0f;
            bool uploading = false;
            bool show = false;
            if (taken)
            {
                // 歌先看:歌缺著就按不了準備,那條進度比「他的模型還沒到」重要得多
                // (而且兩者不會同時跑 —— NetModelTransfer 讓歌先走)。
                if (NetSongTransfer.TryProgressOf(Ctx, seat.UserId, out frac, out uploading)) show = true;
                // MMD 模型:條掛在**模型的主人**那一格(下載中的是我,但要變樣子的是他)。
                else if (NetModelTransfer.TryProgressOf(Ctx, seat.UserId, out frac, out uploading)) show = true;
                else if (seat.Avail == Availability.Downloading) { frac = seat.AvailProgress; show = true; }
            }

            if (_slotBarTrack[i] != null) _slotBarTrack[i].enabled = show;
            if (_slotBarFill[i] == null) return;
            _slotBarFill[i].enabled = show;
            if (!show) return;
            _slotBarFill[i].color = uploading ? BarUpColor : BarDownColor;
            _slotBarFill[i].rectTransform.sizeDelta =
                new Vector2(BarW * Mathf.Clamp01(frac), 0f);
        }

        // 房間 3D 裡「其他玩家」的角色。只在 server 的 rev 變動時重建 —— 生一隻 avatar 要讀十幾個部件檔,
        // 每幀重來會卡死;而座位表只在有人進出/換裝時才變,rev 正好是那個變動的訊號。
        private int _remoteAvatarRev = -1;
        private readonly List<RoomScene3D.RemotePlayer> _remoteBuf = new List<RoomScene3D.RemotePlayer>();
        private RoomRemoteHeadSet _remoteHeads;
        private readonly List<int> _remoteHeadIds = new List<int>();

        // 遠端玩家頭上的名字牌(userId → label)。跟著 3D 角色的頭每幀擺位。
        private readonly Dictionary<int, OutlinedLabel> _remoteNames = new Dictionary<int, OutlinedLabel>();

        // 遠端玩家頭上的家族列(userId → 徽章+家族名)。與名字牌同生共死、住同一層(見 RemoteFamilyRow)。
        private readonly Dictionary<int, RoomFamilyRow> _remoteFamilies = new Dictionary<int, RoomFamilyRow>();

        private void SyncRemoteRoomAvatars()
        {
            if (_scene == null || Ctx == null) return;
            // 單機沒有別人 —— 但**量測模式例外**:SDO_ROOMAVATARS 要能在離線下把房間補到 16 隻,
            // 否則量「6 座位 + 10 旁觀」的成本就得先湊出 16 個真人,那不現實。
            if (Ctx.Net == null)
            {
                if (string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_ROOMAVATARS"))) return;
                if (_remoteAvatarRev == -2) return;   // 離線只補一次(沒有 rev 可以比)
                _remoteAvatarRev = -2;
                _remoteBuf.Clear();
                PadDevRoomAvatars();
                _scene.SyncRemotePlayers(_remoteBuf);
                return;
            }
            var snap = Ctx.Net.Room;
            if (snap == null)
            {
                if (_remoteAvatarRev != -1) { _scene.SyncRemotePlayers(null); ClearRemoteNamePlates(); _remoteAvatarRev = -1; }
                _localMoveSlot = int.MinValue;
                return;
            }
            if (snap.Rev == _remoteAvatarRev) return;
            _remoteAvatarRev = snap.Rev;

            int me = Ctx.Net.UserId;
            // 座位確定了 → 補正本機出生點(OnShow 那一刻常常還查不到座位,見 SetLocalSeat 的註解)。
            // 自己在旁觀時要給**旁觀 slot**(6..15):不給的話 SeatIndexOf 回 -1,
            // 本機角色就會留在剛才那個座位上,別人畫面上的自己卻已經站到旁觀席 —— 兩邊對不上。
            int mySlot = snap.SeatIndexOf(me);
            if (mySlot < 0) mySlot = LocalSpectatorSlot(snap, me);
            if (mySlot >= 0)
            {
                if (_localMoveSlot != int.MinValue && _localMoveSlot != mySlot)
                    _moveThrottle.Reset();
                _localMoveSlot = mySlot;
                _scene.SetLocalSeat(mySlot);
                // 頭貼鏡射的是同一隻角色的動作 → 旁觀席那條「不飛」也要一起套,
                // 不然穿翅膀旁觀時會變成「頭在飛、身體在看戲」(見 RoomHeadPortrait.SetSpectating)。
                if (_localHead != null) _localHead.SetSpectating(RoomScene3D.IsSpectatorSlot(mySlot));
            }
            _remoteBuf.Clear();
            for (int i = 0; i < snap.Seats.Length; i++)
            {
                var s = snap.Seats[i];
                if (!s.IsTaken || s.UserId == me) continue;   // 自己那隻是可走動的本機 avatar,不重複生
                _remoteBuf.Add(new RoomScene3D.RemotePlayer
                {
                    UserId = s.UserId,
                    Seat = i,
                    Male = s.Look != null && s.Look.Male,
                    Parts = s.Look != null ? s.Look.Parts : null,
                    BodyIndex = s.Look != null ? s.Look.BodyIndex : 0,
                    // 🔴 一定要填。忘了填的話它恆為 null,而「外觀變了嗎」是比對這個鍵 ——
                    // null == null → 永遠判定沒變 → **別人換衣服在我畫面上永遠不會反映**
                    // (使用者回報:「用儲物櫃換衣服 遠端沒有跟著換」)。
                    LookKey = s.Look != null ? s.Look.Key() : "",
                    MmdRef = s.Look != null ? s.Look.MmdRef() : "",
                });
            }

            // 旁觀者也要站在房間裡(官方就有,而且座標表早就解出來了:RoomLayout.SpectatorAnchors,
            // EXE slots 6..15 —— 十個圍在舞者周圍的固定站位)。
            // 🔴 slot 給 SeatCount + 名單序號,SpawnSpot 才會走「官方 looker 位置」那條;
            //    給 0..5 會被當成舞者去搶隨機走位點,兩個人可能疊在一起。
            var specs = snap.Spectators;
            if (specs != null)
                for (int i = 0; i < specs.Length && i < NetLimits.MaxSpectators; i++)
                {
                    var sp = specs[i];
                    if (sp == null || sp.UserId == 0 || sp.UserId == me) continue;   // 自己旁觀時走本機 avatar
                    _remoteBuf.Add(new RoomScene3D.RemotePlayer
                    {
                        UserId = sp.UserId,
                        Seat = RoomLayout.SeatCount + i,
                        Male = sp.Look != null && sp.Look.Male,
                        Parts = sp.Look != null ? sp.Look.Parts : null,
                        BodyIndex = sp.Look != null ? sp.Look.BodyIndex : 0,
                        LookKey = sp.Look != null ? sp.Look.Key() : "",
                        MmdRef = sp.Look != null ? sp.Look.MmdRef() : "",
                    });
                }

            PadDevRoomAvatars();   // DEV only:SDO_ROOMAVATARS 才會動(量 6 座位 + 10 旁觀 = 16 隻的成本)
            _scene.SyncRemotePlayers(_remoteBuf);
            SyncRemoteNamePlates(snap, me);
            AnnounceRemoteComings(snap, me);
            if (_remoteHeads != null)
            {
                _remoteHeadIds.Clear();
                for (int i = 0; i < _remoteBuf.Count; i++) _remoteHeadIds.Add(_remoteBuf[i].UserId);
                _remoteHeads.SetRoster(_remoteHeadIds);
            }
        }

        /// <summary>
        /// 自己是旁觀者時佔的 slot(<c>SeatCount + 名單序號</c>);不在旁觀名單裡 → -1。
        ///
        /// 🔴 序號一定要用**陣列 index**,不能用「跳過自己之後的計數」—— 生遠端角色那個迴圈用的就是
        /// 陣列 index,兩邊算法不一致的話「我以為我站第 3 格、別人看我在第 2 格」。
        /// </summary>
        private static int LocalSpectatorSlot(Sdo.Net.NetRoomSnapshot snap, int me)
        {
            var specs = snap != null ? snap.Spectators : null;
            if (specs == null || me == 0) return -1;
            for (int i = 0; i < specs.Length && i < NetLimits.MaxSpectators; i++)
                if (specs[i] != null && specs[i].UserId == me) return RoomLayout.SeatCount + i;
            return -1;
        }

        // 「誰已經被廣播過了」。第一份快照只用來建底(不廣播),否則一進房就會看到房裡每個人
        // 都跳一行「進入舞台遊戲」—— 官方只在**之後**有人進出時才播。
        private readonly Dictionary<int, string> _announcedUsers = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _presenceNow = new Dictionary<int, string>();
        private readonly List<string> _presenceEntered = new List<string>();
        private readonly List<string> _presenceLeft = new List<string>();
        private bool _announceSeeded;

        /// <summary>
        /// 遠端玩家進出的藍字廣播(「X 進入舞台遊戲」/「X 離開舞台遊戲」)。
        ///
        /// **不需要新訊息**:每台 client 自己比對前後兩份名單就知道誰來誰走 ——
        /// 房間快照本來就會在有人進出時推新版。加一條專門的廣播訊息反而要處理
        /// 「它與快照的先後順序」(先收到廣播卻還沒有那個人的座位資料),徒增一種不一致。
        ///
        /// 名單怎麼算、差異怎麼比都在 <see cref="RoomPresence"/>(純函式、有單元測試)——
        /// 這裡只負責「把差異播出去」。🔴 名單含**旁觀席**:切旁觀不是離開房間,見那邊的註解。
        /// </summary>
        private void AnnounceRemoteComings(Sdo.Net.NetRoomSnapshot snap, int me)
        {
            RoomPresence.Collect(snap, me, _presenceNow);
            RoomPresence.Diff(_announcedUsers, _presenceNow, _presenceEntered, _presenceLeft);

            if (_announceSeeded)
            {
                for (int i = 0; i < _presenceEntered.Count; i++) Ctx.Chat?.AnnounceStageEnter(_presenceEntered[i]);
                for (int i = 0; i < _presenceLeft.Count; i++) Ctx.Chat?.AnnounceStageLeave(_presenceLeft[i]);
            }

            _announcedUsers.Clear();
            foreach (var kv in _presenceNow) _announcedUsers[kv.Key] = kv.Value;
            _announceSeeded = true;
        }

        // 頭上的名字牌:跟本機那顆同款(FaceCream + 黑邊 + 粗體),沒有它的話房間裡的人是誰全靠猜。
        //
        // 🔴 名字牌**只寫名字**。「他在打歌」是頭貼那條徽章的事(見 RenderSeatBadges) ——
        // 名字被狀態字蓋掉的話,留在房間的人反而認不出誰是誰。
        private void SyncRemoteNamePlates(Sdo.Net.NetRoomSnapshot snap, int me)
        {
            _remoteScratchIds.Clear();
            for (int i = 0; i < snap.Seats.Length; i++)
            {
                var s = snap.Seats[i];
                if (!s.IsTaken || s.UserId == me) continue;
                _remoteScratchIds.Add(s.UserId);

                OutlinedLabel lbl;
                if (!_remoteNames.TryGetValue(s.UserId, out lbl) || lbl == null)
                {
                    lbl = OutlinedLabel.Create(NamePlateOwnerLayer(s.UserId) ?? Root, "RemoteName" + s.UserId, 0, 0, 160, 20, 14,
                                               TextStyles.FaceCream, Color.black, HeadNameEdgePx, true,
                                               trackEm: TextStyles.HeadNameTrackEm);
                    _remoteNames[s.UserId] = lbl;
                }
                string lvl = s.Level > 0 ? "  LV:" + s.Level : "";
                lbl.SetText((s.Name ?? "") + lvl);
                RemoteFamilyRow(s.UserId).Set(s.Guild, s.GuildEmblem);
            }

            // Spectators still own a live 3D avatar in a looker slot. Keep the same userId-keyed label while
            // switching between a seat and spectator list so its head/world-canvas binding follows that avatar.
            var specs = snap.Spectators;
            if (specs != null)
                for (int i = 0; i < specs.Length; i++)
                {
                    var sp = specs[i];
                    if (sp == null || sp.UserId == 0 || sp.UserId == me) continue;
                    _remoteScratchIds.Add(sp.UserId);

                    OutlinedLabel lbl;
                    if (!_remoteNames.TryGetValue(sp.UserId, out lbl) || lbl == null)
                    {
                        // Spectators have no team, so use the same free/default cream name colour as local players.
                        lbl = OutlinedLabel.Create(NamePlateOwnerLayer(sp.UserId) ?? Root, "RemoteName" + sp.UserId, 0, 0, 160, 20, 14,
                                                   TextStyles.FaceCream, Color.black, HeadNameEdgePx, true,
                                                   trackEm: TextStyles.HeadNameTrackEm);
                        _remoteNames[sp.UserId] = lbl;
                    }
                    string lvl = sp.Level > 0 ? "  LV:" + sp.Level : "";
                    lbl.SetText((sp.Name ?? "") + lvl);
                    RemoteFamilyRow(sp.UserId).Set(sp.Guild, sp.GuildEmblem);
                }

            _remoteGoneIds.Clear();
            foreach (var kv in _remoteNames) if (!_remoteScratchIds.Contains(kv.Key)) _remoteGoneIds.Add(kv.Key);
            foreach (var id in _remoteGoneIds)
            {
                if (_remoteNames[id] != null) Destroy(_remoteNames[id].gameObject);
                _remoteNames.Remove(id);
                RoomFamilyRow fam;
                if (_remoteFamilies.TryGetValue(id, out fam))
                {
                    if (fam != null) fam.Destroy();
                    _remoteFamilies.Remove(id);
                }
                RectTransform layer;
                // 名字牌住的那一層跟著走:留著的話房間開久了會累積一堆空層(每幀都要走一遍排序)。
                if (_nameOwnerLayer.TryGetValue(id, out layer))
                {
                    if (layer != null) Destroy(layer.gameObject);
                    _nameOwnerLayer.Remove(id);
                }
                DestroySentBubblesOf(id);   // 角色已被拆掉,泡不清會孤兒地掛到壽命結束
            }
        }

        private readonly HashSet<int> _remoteScratchIds = new HashSet<int>();
        private readonly List<int> _remoteGoneIds = new List<int>();

        private void ClearRemoteNamePlates()
        {
            foreach (var kv in _remoteNames) if (kv.Value != null) Destroy(kv.Value.gameObject);
            _remoteNames.Clear();
            foreach (var kv in _remoteFamilies) if (kv.Value != null) kv.Value.Destroy();
            _remoteFamilies.Clear();
            ClearRemoteNamePlateLayers();
        }

        /// <summary>
        /// 那個人頭上的家族列(沒有就建一條,住他自己的名字牌層 → 與他的名字一起參與前後排序)。
        ///
        /// 為什麼遠端也要有:家族(名字+徽章)以前只有**自己**看得到自己的 —— 別人頭上永遠只有名字,
        /// 於是「同族的人在房間裡認不出彼此」(使用者回報)。資料是座位快照帶來的
        /// (<c>NetSeat.Guild</c> / <c>GuildEmblem</c>,對方在 setIdentity 報的)。
        /// </summary>
        private RoomFamilyRow RemoteFamilyRow(int userId)
        {
            RoomFamilyRow row;
            if (_remoteFamilies.TryGetValue(userId, out row) && row != null) return row;
            row = RoomFamilyRow.Create(NamePlateOwnerLayer(userId) ?? Root, userId.ToString());
            _remoteFamilies[userId] = row;
            return row;
        }

        /// <summary>
        /// 某個人的名字牌住的那一層(lazily 建)。整層與 <see cref="_nameLayer"/> 同框、不吃滑鼠,
        /// 所以名字牌的座標仍是 800×600 的**絕對設計座標**(PlaceFollow/PlaceFamilyRow 一行都不用改)。
        ///
        /// 一個人一層的唯一理由是**排序**:層與層之間每幀按各人的深度重排(見 <see cref="SortNamePlateLayers"/>)。
        /// owner 0 = 本機自己(名字 + 家族列 + 徽章都在裡面,它們彼此不重疊,層內順序無所謂)。
        /// </summary>
        private RectTransform NamePlateOwnerLayer(int owner)
        {
            if (_nameLayer == null) return null;
            RectTransform rt;
            if (_nameOwnerLayer.TryGetValue(owner, out rt) && rt != null) return rt;
            rt = UIKit.NewRect(_nameLayer, "RoomNamePlateOwner" + owner);
            UIKit.Stretch(rt);
            _nameOwnerLayer[owner] = rt;
            return rt;
        }

        /// <summary>
        /// 離開房間 → 收掉**遠端**那些人的層。🔴 owner 0 那層留著:本機的名字/家族列/徽章是 BuildUI
        /// 建一次的常駐物件,住在裡面(拆了就永遠沒有自己的名字牌)。
        /// </summary>
        private void ClearRemoteNamePlateLayers()
        {
            _nameScratchOwners.Clear();
            foreach (var kv in _nameOwnerLayer) if (kv.Key != 0) _nameScratchOwners.Add(kv.Key);
            for (int i = 0; i < _nameScratchOwners.Count; i++)
            {
                int owner = _nameScratchOwners[i];
                var rt = _nameOwnerLayer[owner];
                if (rt != null) Destroy(rt.gameObject);
                _nameOwnerLayer.Remove(owner);
            }
        }

        /// <summary>
        /// 名字牌與名字牌之間的前後(使用者需求):**站在前面的人的名字牌蓋住站在後面的人的**。
        /// 沒有它的話順序就是「誰先建誰在下面」—— 本機的名字牌是 BuildUI 建的,所以站在最後面的
        /// 遠端玩家的名字牌會永遠蓋住站在最前面的自己。
        ///
        /// 深度量的是**泡的那個錨點(肩膀骨)**,與 <see cref="TrackBubbleOwnerDepth"/> 同一個點:
        /// 同一個人的泡與名字牌用同一個深度,兩者的前後才不會互相矛盾。量不到(人剛進來/在鏡頭後面)
        /// → 當成無限遠,排在所有人後面。
        /// </summary>
        private void SortNamePlateLayers()
        {
            if (_nameOwnerLayer.Count <= 1) return;
            var cam = _scene != null ? _scene.SceneCamera : null;
            if (cam == null) return;

            _nameSortLayers.Clear();
            _nameSortDepths.Clear();
            foreach (var kv in _nameOwnerLayer)
            {
                if (kv.Value == null) continue;
                Vector3 anchorWorld;
                bool have = kv.Key == 0
                    ? _scene.TryChatBubbleAnchorWorld(out anchorWorld)
                    : _scene.TryRemoteChatBubbleAnchorWorld(kv.Key, out anchorWorld);
                _nameSortLayers.Add(kv.Value);
                _nameSortDepths.Add(have
                    ? Vector3.Dot(anchorWorld - cam.transform.position, cam.transform.forward)
                    : float.MaxValue);
            }
            RoomBubbleDrawOrder.ApplyFarToNear(_nameSortLayers, _nameSortDepths, _nameSortOrders);
        }

        private readonly List<int> _nameScratchOwners = new List<int>();
        private readonly List<RectTransform> _nameSortLayers = new List<RectTransform>();
        private readonly List<float> _nameSortDepths = new List<float>();
        private readonly List<int> _nameSortOrders = new List<int>();

        // 名字牌每幀跟著頭走(角色是 3D 的,鏡頭會動)。看不到的人(在鏡頭後面)就藏起來。
        // 與本機那面一樣:UI 的絕對設計座標,不進泡那一層(見 Update 裡那段註解)。
        private void PlaceRemoteNamePlates()
        {
            if (_scene == null || _remoteNames.Count == 0) return;
            foreach (var kv in _remoteNames)
            {
                var lbl = kv.Value;
                if (lbl == null) continue;
                Vector2 vp;
                bool visible = _scene.TryRemoteHeadViewport(kv.Key, out vp);
                if (lbl.gameObject.activeSelf != visible) lbl.gameObject.SetActive(visible);

                // 家族列跟著同一顆頭:看不到的人整條一起藏(有家族的人才畫得出來,見 RoomFamilyRow.Set)。
                RoomFamilyRow fam;
                if (_remoteFamilies.TryGetValue(kv.Key, out fam) && fam != null)
                {
                    fam.SetVisible(visible);
                    if (visible) fam.Place(vp.x * 800f, NameTopOf(vp));
                }

                if (!visible) continue;
                PlaceFollow(lbl.Rect, vp, -8f);
            }
        }


        // ---- 房間裡的走動同步 ----

        private readonly Sdo.Net.MoveThrottle _moveThrottle = new Sdo.Net.MoveThrottle();
        private int _localMoveSlot = int.MinValue;

        // 泡鏈排版用的可重用暫存(見 PlaceRoomChatBubbles 的註解:一幀會被呼叫多次)。
        private readonly List<RoomBubbleLayoutNode> _bubbleNodes = new List<RoomBubbleLayoutNode>();

        /// <summary>
        /// 把 server 推來的位置套到遠端角色上。**每幀都要跑** —— 位置流不受
        /// <see cref="SyncRemoteRoomAvatars"/> 的 rev-gate 管(rev 不會每幀變,但位置會)。
        ///
        /// 讀的是 <c>NetClient.Moves</c> 這張「每個人的最新位置」字典而不是事件:
        /// 站著不動的人永不回報,所以晚建起來的 3D 房間只能靠這張表把他們放到對的位置。
        /// </summary>
        private void ApplyRemoteMoves()
        {
            if (_scene == null || Ctx == null || Ctx.Net == null) return;
            var moves = Ctx.Net.Moves;
            if (moves.Count == 0) return;
            foreach (var kv in moves)
            {
                var r = kv.Value;
                _scene.ApplyRemoteMove(r.UserId, r.X, r.Z, r.Facing, r.Walking);
            }
        }

        /// <summary>
        /// 把本機的位置送上網。真正要不要送由 <see cref="Sdo.Net.MoveThrottle"/> 決定
        /// (站著不動就完全不送;開始走/停下/轉向立刻送;走動中 10 Hz)。
        ///
        /// 放在 RoomScreen 而不是 RoomScene3D:後者在 <c>Sdo.Game</c>,不認識連線層。
        /// 這裡是唯一的黏合層,而且離線(<c>Ctx.Net == null</c>)時整段跳過、零成本。
        /// </summary>
        private void SendLocalMove()
        {
            if (_scene == null || Ctx == null || Ctx.Net == null || !Ctx.Net.InRoom) return;
            long now = (long)(Time.realtimeSinceStartupAsDouble * 1000.0);
            float x = _scene.LocalWalkX, z = _scene.LocalWalkZ, f = _scene.AvatarFacing;
            bool walking = _scene.IsWalking;
            if (_moveThrottle.ShouldSend(x, z, f, walking, now)) Ctx.Net.SendMove(x, z, f, walking);
        }

        /// <summary>本機玩家坐在第幾格?找不到回 -1(旁觀或還沒進座位)。</summary>
        private int LocalSeatIndex(RoomInfo room)
            => RoomLocalSeat.IndexOf(room, LocalUserId, LocalProfileId);

        /// <summary>server 配的 userId(離線 0)。認人一律用它 —— 見 <see cref="RoomLocalSeat"/>。</summary>
        private int LocalUserId => Ctx != null && Ctx.Net != null ? Ctx.Net.UserId : 0;

        private string LocalProfileId => Ctx != null && Ctx.Session != null ? Ctx.Session.LocalPlayerId : null;

        private string LocalName(RoomInfo room)
        {
            if (room == null) return "";
            foreach (var s in room.Seats)
                if (!s.IsEmpty && s.Player.Id == Ctx.Session.LocalPlayerId) return s.Player.DisplayName;
            return Ctx.Session != null ? Ctx.Session.LocalPlayerName : "";
        }

        /// <summary>
        /// 本機按過準備了嗎。**認人走 userId,不是 profile id** —— 線上座位的 Player.Id 是 server 的
        /// userId,拿本機存檔 id 去比永遠比不中(細節見 <see cref="RoomLocalSeat"/>)。這個值同時決定
        /// 右下角那顆球畫「準備」還是「取消」,以及按下去要送 setReady(true) 還是 (false)。
        /// </summary>
        private bool LocalReady(RoomInfo room)
            => RoomLocalSeat.IsReady(room, LocalUserId, LocalProfileId);

        /// <summary>準備 ⇄ 取消(同一顆球換圖)。狀態的權威在 server,這裡只送切換、等 roomState 回來才翻圖。</summary>
        private void OnReadyToggle()
        {
            var room = Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
            if (room == null) return;
            Ctx.Rooms.SetReady(!LocalReady(room));
        }

        /// <summary>
        /// 組隊模式下這一局湊得出合法站位嗎 —— 與 server 的 R10c 走**同一條純規則**
        /// (<see cref="TeamLayoutRules"/>),client 只是提早把失敗原因說出來,不然玩家按下開始
        /// 只會收到一個看不懂的 badTeams。
        ///
        /// 參與者集合的定義照 server:「(是房主 或 已準備) 且 有這首歌」的座位。
        /// 6 人房只有 4 人準備且 A/B 各 2 人 → 2v2 合法,可以開始。
        /// </summary>
        private bool TeamsCanStart(RoomInfo room, out bool teamMode)
        {
            teamMode = false;
            if (room == null) return true;
            // 非普通模式一律不是組隊局 —— 就算座位上還留著剛切模式那一瞬間的舊隊伍值(server 會清,
            // 但快照可能還沒回來),也不能讓它把開始鈕卡住。
            if (!TeamLayoutRules.TeamsAllowedIn(RoomGameMode())) return true;
            int a = 0, b = 0, c = 0;
            for (int i = 0; i < room.Seats.Count; i++)
            {
                var s = room.Seats[i];
                if (s == null || s.IsEmpty) continue;
                if (s.Team != (int)TeamTag.Free) teamMode = true;   // 有人不是「自由」→ 這房在組隊
                bool isHost = s.UserId != 0 ? room.IsHostUser(s.UserId) : s.IsHost;
                if (!(isHost || s.IsReady) || s.Avail != Availability.Have) continue;   // 不是參與者
                if (s.Team == (int)TeamTag.A) a++;
                else if (s.Team == (int)TeamTag.B) b++;
                else if (s.Team == (int)TeamTag.C) c++;
            }
            if (!teamMode) return true;
            return TeamLayoutRules.TryLayoutFor(a, b, c, out _);
        }

        // ==================== 同步進場(M4)====================
        // 離線:按開始 → 直接漸暗進場(與加連線之前一模一樣)。
        // 連線:按開始 → requestStart → **等 server 的 matchStarting** 才漸暗。
        //       為什麼不本機先進場:場景/難度的隨機值由 server echo,而參與者集合是 server 在
        //       「open → waitingForLoad」那一刻凍結的 —— 本機先跑就會用自己猜的值,兩台看到不同的東西。
        //       非房主也是收到 matchStarting 才進場,所以兩邊走的是同一條路。
        private bool _awaitingMatchStart;
        private float _awaitingSince;
        private float _lastStartPressAt = -99f;
        private const float RequestStartTimeoutSec = 8f;    // 沒回應就放開按鈕(不要讓玩家以為卡住)
        private const float ForceStartDoubleTapSec = 1.5f;  // 這段時間內再按一次 = 強制開始

        private bool Online => Ctx != null && Ctx.Net != null && Ctx.Net.IsConnected && Ctx.Net.InRoom;

        /// <summary>本機現在是不是旁觀者 —— **以 server 快照為準**(查房間的旁觀名單裡有沒有自己),
        /// 不是本機猜的。按下鈕到 server 認可之間答案不變,所以畫面不會先報成功再被打臉。離線恆 false。</summary>
        private bool LocalSpectating => Ctx != null && Ctx.Net != null && Ctx.Net.IsSpectating;

        /// <summary>server 的房間快照(離線 / 還沒進房 → null)。判「誰是旁觀者」一律讀它。</summary>
        private Sdo.Net.NetRoomSnapshot RoomSnapshot => Ctx != null && Ctx.Net != null ? Ctx.Net.Room : null;

        /// <summary>
        /// 「旁觀」鈕:交出座位變旁觀者,再按一次搶回座位(需求 10 / D13)。
        ///
        /// 三道門(D13)在 server 那邊是權威(R21),這裡先擋一次**只是為了把原因講出來** ——
        /// 靜默失敗的話玩家只會覺得「這顆鈕壞了」:
        ///   • 已經在這一局裡的參與者 → 不能中途離場;
        ///   • 已按準備的一般玩家 → 先取消準備;
        ///   • 房主 → server 會自動把房主交給剩下座位索引最小的人;沒人能接手就擋下來。
        /// </summary>
        private void OnSpectateToggle()
        {
            var net = Ctx != null ? Ctx.Net : null;
            if (net == null || !net.IsConnected || !net.InRoom)
            {
                Notice("room.spectate_offline");   // 離線單機沒有旁觀(沒有別人可看)
                return;
            }

            // 送出就好,**不要先報成功**。server 可能拒絕(房間開打了、座位全滿/全關),
            // 那時「已回到座位」是騙人的 —— 而人還在旁觀席。狀態變了會有新的 roomState 快照,
            // 畫面自己就會更新(整個連線層的原則:不做樂觀更新)。
            if (net.IsSpectating) { net.StopSpectate(); return; }

            var snap = net.Room;
            var me = snap != null ? snap.SeatOf(net.UserId) : null;
            if (me == null) return;   // 不在座位上也不是旁觀者 → 狀態還沒同步,等下一份快照

            bool completed = me.PlayState == PlayState.Finished || me.PlayState == PlayState.Results;
            if (me.PlayState != PlayState.Idle && !completed) { Notice("room.spectate_in_match"); return; }
            // 房主的 Ready 恆 false(D12)—— 所以這條天然不會擋到房主,不用另外排除它。
            if (me.Ready && !completed) { Notice("room.spectate_ready"); return; }
            net.Spectate();   // 同上:等 server 的快照,不先報成功
        }

        private Sdo.Game.FrameStats _roomPerf;

        /// <summary>
        /// 房間的幀時間量測。房間的最壞情況(6 座位 + 10 旁觀 = 16 隻)**比打歌畫面更重**,
        /// 所以兩邊都要量,而且用同一份統計程式(<see cref="Sdo.Game.FrameStats"/>)。
        /// </summary>
        private void TickRoomPerf()
        {
            if (string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_ROOMAVATARS"))) return;
            if (_roomPerf == null) _roomPerf = new Sdo.Game.FrameStats("room");
            _roomPerf.Tick(_remoteBuf.Count + 1);   // +1 = 本機那隻可走動的
        }

        /// <summary>
        /// DEV:<c>SDO_ROOMAVATARS=&lt;n&gt;</c> → 把房間的角色數補到 n 隻(用真的 avatar,不是假物件)。
        ///
        /// 為什麼需要:房間的**最壞情況比打歌畫面更重** —— 6 個座位 + 10 個旁觀 = 16 隻角色同時在場
        /// (官方的 looker 站位表就是 10 格,RoomLayout.SpectatorAnchors)。而要湊出 16 隻真人來量測不現實,
        /// 所以補一批假 userId 走**同一條生成路徑**(SpawnRemote → SdoRoomAvatar.Build),
        /// 量到的成本就是真的成本。座位序號從 SeatCount 起算 → 站官方的旁觀位置。
        /// </summary>
        private void PadDevRoomAvatars()
        {
            var v = ScreenGameplay.DevVar("SDO_ROOMAVATARS");
            int want;
            if (string.IsNullOrEmpty(v) || !int.TryParse(v, out want)) return;
            want = Mathf.Clamp(want, 0, RoomLayout.SlotCount);

            // 已經有的(座位 + 旁觀)不重複補;本機那隻是可走動的 avatar,不算在 _remoteBuf 裡但要算進總數。
            int have = _remoteBuf.Count + 1;
            for (int i = have; i < want; i++)
            {
                int slot = Mathf.Clamp(RoomLayout.SeatCount + (i - 1), RoomLayout.SeatCount, RoomLayout.SlotCount - 1);
                _remoteBuf.Add(new RoomScene3D.RemotePlayer
                {
                    UserId = 900000 + i,          // 不可能與真 userId 撞(server 從 1 開始發)
                    Seat = slot,
                    Male = (i & 1) == 0,          // 男女交錯 → 兩套部件都會被載到(成本才是真的)
                    Parts = null,                 // null = 預設整套
                    BodyIndex = 0,
                    LookKey = "dev" + i,
                });
            }
            if (want > have) Debug.Log("[perf] SDO_ROOMAVATARS:房間補到 " + want + " 隻角色(真實 " + have + " 隻)");
        }

        private void OnMatchStarting(NetMatchStart m)
        {
            _awaitingMatchStart = false;
            if (m == null || _starting) return;
            // 🔴 只信 server echo 的 resolved:隨機場景/難度都在那裡面。用自己算的 → 每台的場景不一樣。
            ApplyResolvedRound(m);
            _starting = true;
            _returnedFromStage = true;
            // 🔴 進遊戲**不清**訊息欄歷史:清掉的話回房那次 RebuildRoomChat 就沒東西可重建了(見 OnShow)。
            UiSfx.Play(UiSfx.GameStart);
            StartCoroutine(FadeToStage());
        }

        private void OnGameplayAborted(long matchId, string reason)
        {
            _awaitingMatchStart = false;
            Debug.Log("[room] gameplay aborted: " + (reason ?? ""));
            Notice("room.match_aborted");
        }

        /// <summary>
        /// 房主把這一局的隨機值抽好交給 server(它會驗範圍再 echo 給所有人)。
        ///
        /// 🔴 隨機值一定要在**這一刻**抽好、由 server echo:讓每台自己抽的話場景就會不一樣,
        /// 而那是「大家在同一個房間玩」最基本的東西。非房主送的 resolved server 不看(host-only)。
        /// </summary>
        private NetResolvedRound BuildResolvedRound()
        {
            var s = Ctx != null ? Ctx.Session : null;
            var r = new NetResolvedRound();
            if (s == null) return r;
            // 隨機場景 → 現在抽(結果只進這一局,房間設定不動,見 RoundStageChoice)
            r.SceneId = RoundStageChoice.Pick(s.StageRandom, s.StageId, NetLimits.MaxSceneId, Random.Range);
            // 隊形:GameSession.Formation 的 3 = 隨機 → 抽 0..2(官方只有三張個人隊形表),同樣不寫回設定
            r.FormationType = RoundFormationChoice.Pick(s.Formation, Random.Range);
            // 組隊版型:湊得出來才填(TeamsCanStart 已經在 OnStart 擋過湊不出來的情形)
            var room = Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
            if (room != null && TeamsCanStart(room, out bool teamMode) && teamMode)
            {
                int a = 0, b = 0, c = 0;
                for (int i = 0; i < room.Seats.Count; i++)
                {
                    var seat = room.Seats[i];
                    if (seat == null || seat.IsEmpty) continue;
                    bool isHost = seat.UserId != 0 ? room.IsHostUser(seat.UserId) : seat.IsHost;
                    if (!(isHost || seat.IsReady) || seat.Avail != Availability.Have) continue;
                    if (seat.Team == (int)TeamTag.A) a++;
                    else if (seat.Team == (int)TeamTag.B) b++;
                    else if (seat.Team == (int)TeamTag.C) c++;
                }
                if (TeamLayoutRules.TryLayoutFor(a, b, c, out TeamLayout layout)) r.TeamLayout = layout;
            }
            r.RandomSong = BuildRandomSongPick(s);
            return r;
        }

        /// <summary>
        /// 隨機難度局:這一局要玩哪一首,**現在**抽(離線是在 FrontendApp.StartGameplay 抽)。
        /// 不是隨機難度 → null,server 就用房間當下那首歌(<c>NetRoom.RequestStart</c>:
        /// <c>Song = resolved.RandomSong ?? _state.Song</c>)。
        ///
        /// 🔴 為什麼線上一定要在這裡抽:每台自己抽會各玩一首歌。以前這個欄位**沒有任何地方會填**,
        /// 所以線上的隨機難度其實是「選歌那一刻抽一首,之後每一局都是同一首」——「隨機」只發生過一次。
        ///
        /// 🔴 為什麼只抽官方歌:抽出來的歌不會再經過缺歌檢查(房裡每個人的 availability 是對**房間那首**
        /// 回報的,而這一首是開場當下才決定的)。官方歌隨遊戲資料出貨、大家一定都有;抽到房主自己
        /// Songs\ 裡的外部歌,別人載不到譜就卡在載入畫面等逾時。
        ///
        /// Title 保持「隨機難度 X」那個標籤 + RandomTitle=true:收端拿它去覆寫房間顯示的話就等於提前揭曉
        /// (真正抽到哪一首在 Gn 裡,開場載譜用的是那個)。
        /// </summary>
        private NetSongRef BuildRandomSongPick(GameSession s)
        {
            if (s == null || !s.SongIsRandom) return null;
            var pool = SongListModel.RandomCandidates(SongListModel.FromCatalog().All, s.SongRandomRange,
                                                      officialOnly: true);
            if (pool.Count == 0)
            {
                // 抽不出東西(這個難度區間本機一首官方歌都沒有)→ 不填,照房間那首開場,不要讓整局開不成。
                Debug.LogWarning("[room] 隨機難度抽不到官方歌(range=" + s.SongRandomRange + ")→ 這一局用房間現在那首");
                return null;
            }
            var cand = pool[Random.Range(0, pool.Count)];
            int slot = Mathf.Clamp(cand.Difficulty, 0, 2);
            var song = new NetSongRef
            {
                Official = true,
                Gn = cand.Song.gn ?? "",
                FileId = cand.Song.fileId,
                ChartIndex = slot,
                Difficulty = slot,
                Title = s.SongTitle ?? "",   // 標籤,不是歌名
                Artist = "",
                RandomTitle = true,
            };
            Debug.Log("[room] 隨機難度抽出這一局的歌:" + cand.Song.gn + " slot=" + slot);
            return song;
        }

        /// <summary>
        /// 離線這一局的場景 + 隊形:房間設定是隨機 → 現在抽;指定 → 就是那個。都寫進 Round*,設定不動。
        ///
        /// 線上這件事是房主抽好、server echo 回來的(<see cref="BuildResolvedRound"/> / <see cref="ApplyResolvedRound"/>),
        /// 離線沒有那一段 —— 以前離線隨機場景實際上跑的是「按 ◄ ► 選到隨機那一刻」抽的佔位值,
        /// 也就是不重選就每一局都同一個場景;隊形更是**完全沒接**(離線 game.formationType 永遠是 0,
        /// 在選歌對話框選了扇形/環線也沒有用)。兩邊現在都是每一局重抽,而且都不動房間設定。
        /// </summary>
        private void ResolveLocalRound()
        {
            var s = Ctx != null ? Ctx.Session : null;
            if (s == null) return;
            var st = StageCatalog.Get(RoundStageChoice.Pick(s.StageRandom, s.StageId, NetLimits.MaxSceneId, Random.Range));
            s.RoundStageId = st.Id;
            s.RoundStageFolder = st.Folder;
            s.RoundFormationType = RoundFormationChoice.Pick(s.Formation, Random.Range);
        }

        /// <summary>requestStart 送出去之後沒有回應 → 放開按鈕。不放的話玩家會以為遊戲卡死。</summary>
        private void TickAwaitingMatchStart()
        {
            if (!_awaitingMatchStart) return;
            if (Time.unscaledTime - _awaitingSince < RequestStartTimeoutSec) return;
            _awaitingMatchStart = false;
            Notice("room.start_no_response");
        }

        /// <summary>把 server echo 的這一場設定套進 session —— 場景/難度/歌曲都要與所有人一致。</summary>
        private void ApplyResolvedRound(NetMatchStart m)
        {
            var s = Ctx != null ? Ctx.Session : null;
            if (s == null) return;
            var r = m.Resolved;
            if (r != null)
            {
                // 🔴 落點是 Round* 而不是房間設定的 Stage*:寫回設定的話,房間 win2 那張場景縮圖會在進遊戲
                // 那一瞬間從 RANDOM 變成抽到的場景,回房之後房間也不再是「隨機場景」了(房主那台還會把
                // sceneRandom=false 推給 server → 全房一起變)。設定歸設定,這一局的結果歸這一局。
                //
                // 🔴 Folder 才是 gameplay 真正用的(scenePath = "SCENE/" + RoundStageFolder)——
                // 只設 id 的話場景還是舊的那個,而且兩台會不一樣(症狀:「場景不同步」)。
                var st = StageCatalog.Get(r.SceneId);
                s.RoundStageId = st.Id;
                s.RoundStageFolder = st.Folder;
                // 隊形同理:落在 Round*,房間設定的「隨機隊形」要留著(否則打完一局就定死成抽到的那一種)。
                s.RoundFormationType = Mathf.Clamp(r.FormationType, 0, NetResolvedRound.FormationTypeCount - 1);
                // 🔴 這裡以前還有一行 `if (r.IsRandomSong) s.SongIsRandom = false;` —— 已刪掉。
                // 兩個理由:①「隨機難度」跟隨機場景/隊形一樣是**房間設定**,不能被某一局的結果清掉
                //   (清掉之後房主推給 server 的 song.randomTitle 也變 false → 房間面板從「隨機難度 X」
                //    變成抽到那首歌的歌名+等級+BPM,等於把下一局要抽的池子提前揭曉了);
                // ② 它其實從來沒被執行過 —— resolved.randomSong 沒有任何地方會填(host 端不抽),
                //   所以線上的隨機難度目前是「選歌那一刻抽一首,之後每局都是同一首」。那是功能缺口,
                //   不是這裡能修的:要補的話是 BuildResolvedRound 抽一首**官方**歌填進 r.RandomSong
                //   (server 已經支援:NetRoom.RequestStart 用 resolved.RandomSong ?? 房間的歌)。
            }
            // 歌本身:server 的那份才是這一場真正要玩的(隨機難度時是抽出來的那首)。
            // 難度**不是** —— 自由模式下每個人打自己在「難度設置」挑的那個(見 LocalPlaySlot)。
            if (m.Song == null) return;
            if (m.Song.Official)
            {
                if (string.IsNullOrEmpty(m.Song.Gn)) return;
                // 🔴 一定要走 SetOfficialSong,不能只寫 SongGn:非房主/旁觀者的 session 裡是**他自己**
                // 上次選的歌,那首可能是外部歌 → IsExternalSong 還留著 true,進場就會照舊放那首外部歌
                // (FrontendApp.StartGameplay 只看這個旗標,見 GameSession.SetOfficialSong 的註解)。
                //
                // 顯示欄位優先用本機目錄(官方歌每台都一樣);查不到才用 server 帶來的那份。
                // 隨機難度局的 Title 是「隨機難度 X」標籤而不是歌名 → 那種情況寧可留 gn,不要把標籤當歌名。
                //
                // 🔴 隨機難度局(RandomTitle)則是**兩個都不動**(傳 null):session 的 SongTitle 是
                // 「隨機難度 X」那個標籤,被抽到的歌名蓋掉的話 ①房間面板直接寫出這一局抽到什麼(隨機難度
                // 刻意不揭曉)、②房主回房後 NetSongPublisher 比對會認為「房間換歌了」就重送,把全房的
                // ready/avail 打回去(見 NetSongPublisher.Publish 的隨機難度守門)。
                var meta = Sdo.Game.SongCatalog.Get(m.Song.Gn);
                string title = m.Song.RandomTitle ? null
                             : (meta != null ? (meta.title ?? m.Song.Gn)
                             : (!string.IsNullOrEmpty(m.Song.Title) ? m.Song.Title : m.Song.Gn));
                string artist = m.Song.RandomTitle ? null
                              : (meta != null ? (meta.artist ?? "") : (m.Song.Artist ?? ""));
                s.SetOfficialSong(m.Song.Gn, m.Song.FileId, title, artist);
                s.Difficulty = (Difficulty)LocalPlaySlot(m.Song, Mathf.Clamp(m.Song.ChartIndex, 0, 2));
                return;
            }
            ApplyResolvedExternalSong(s, m.Song, LocalPlaySlot(m.Song, Mathf.Clamp(m.Song.Difficulty, 0, 2)));
        }

        /// <summary>
        /// 這一場**我**要打哪一個難度槽。
        ///
        /// 一般/ShowTime 模式:房主選的那個(<paramref name="hostSlot"/>,server 帶來的)——全場同一張譜。
        /// 自由模式的非房主:自己在「難度設置」挑的那個 —— 這就是「同一首歌每個人可以打不同難度」的落點。
        /// 挑的那個在這首歌沒有譜就貼到最近的可打難度(外部歌常只有一兩張)。
        ///
        /// 「隨機難度」局例外:歌與難度都是房主當場抽出來的一組,覆寫難度等於把隨機的意義拿掉 → 照房主那份。
        /// </summary>
        private int LocalPlaySlot(NetSongRef song, int hostSlot)
        {
            var s = Ctx != null ? Ctx.Session : null;
            if (s == null || song == null || song.RandomTitle) return hostSlot;
            var netRoom = NetRoomForPanel();
            int gameMode = netRoom != null && netRoom.Settings != null ? netRoom.Settings.GameMode : s.GameMode;
            if (!FreeModeDifficulty.PlayerPicksOwn(gameMode, isHost: netRoom == null)) return hostSlot;
            return FreeModeDifficulty.Snap(s.FreeDifficulty, PlayableSlots(LocalEntryFor(song)));
        }

        /// <summary>
        /// 外部歌:把「房主選的那一份」對映到**本機自己的路徑**(M5)。
        ///
        /// 🔴 為什麼不能直接用 server 帶來的路徑:那是房主電腦上的絕對路徑,而且外部歌的 gn 是
        /// 「絕對路徑的 hash」—— 換台電腦完全不同。所以身分走 packId + songKey(內容指紋),
        /// 查到本機的那筆 catalog entry 之後,譜/音檔/資料夾全部用**自己這邊**的值。
        /// 少了這一步,非房主進場時 ExternalChartPath 還是房主的路徑 → 載不到譜(黑畫面),
        /// 而症狀完全指不到「身分對映」這一層。
        ///
        /// 找不到(還在下載 / 下載失敗)就什麼都不動:那台本來就不會被納入這一場(R12 要求 avail==have)。
        /// </summary>
        private static void ApplyResolvedExternalSong(GameSession s, NetSongRef song, int slot)
        {
            if (string.IsNullOrEmpty(song.PackId)) return;
            var hit = Sdo.Game.ExternalSongLibrary.FindByPack(song.PackId, song.SongKey);
            if (hit == null)
            {
                Debug.LogWarning("[room] 本機找不到這一場的外部歌(packId=" + song.PackId + ")—— 進場會載不到譜");
                return;
            }

            slot = Mathf.Clamp(slot, 0, 2);    // 自由模式時這是**自己**挑的難度(見 LocalPlaySlot),不一定是房主那個
            s.SongGn = hit.gn;                 // 本機的 gn(每台不同,只在本機有意義)
            s.SongFileId = hit.fileId;
            s.SongTitle = hit.title;
            s.SongArtist = hit.artist ?? "";
            s.SongIsRandom = false;
            s.IsExternalSong = true;
            s.Difficulty = (Difficulty)slot;
            s.ExternalChartFormat = hit.chartFormat;
            s.ExternalChartPath = hit.ChartPath(slot);
            s.ExternalChartIndex = hit.ChartIndex(slot);
            s.ExternalChartSeed = hit.chartSeed;
            s.ExternalDpsPath = hit.dpsPath;
            s.ExternalAudioPath = hit.audioPath;
            s.ExternalLevel = hit.DisplayLevel(slot);
            s.ExternalFolderPath = hit.folderPath;
            s.ExternalSongKey = hit.songKey ?? "";
            // 舞蹈的 seed:**內容指紋**,不是資料夾名 —— 傳檔來的那份放在 connect/<歌名 - 作者 [tag]>/,
            // 資料夾名與持有原檔的人不同,吃資料夾名的話同一場的兩個人會跳完全不同的舞(見 Sdo.Game.ExternalDps)。
            s.ExternalPackId = hit.packId ?? "";
            // 生成編舞的輸入一樣要走**本機**這筆 catalog:舞是一首歌一支,不能因為房主選了 hard
            // 就跟自己單機玩 easy 時生出的舞不同(見 Sdo.Osu.DanceInputs)。少了這三行,線上開外部歌
            // 會退回「選到那張譜自己的 span/bpm」—— 正是這次要修掉的那個 bug,只是躲在連線這條路徑上。
            s.ExternalSongBpm = hit.bpm;
            s.ExternalSongChartPaths = new[] { hit.ChartPath(0), hit.ChartPath(1), hit.ChartPath(2) };
            s.ExternalSongChartIndices = new[] { hit.ChartIndex(0), hit.ChartIndex(1), hit.ChartIndex(2) };
            Debug.Log("[room] 外部歌已對映到本機:" + hit.title + " → " + s.ExternalChartPath);
        }

        private void OnStart()
        {
            Debug.Log("[room] OnStart: starting=" + _starting + " online=" + Online
                      + " awaiting=" + _awaitingMatchStart
                      + " canStart=" + (Ctx != null && Ctx.Rooms != null && Ctx.Rooms.CanStart())
                      + " songTitle='" + (Ctx != null && Ctx.Rooms != null && Ctx.Rooms.CurrentRoom != null
                                          ? Ctx.Rooms.CurrentRoom.SongTitle : "<no room>") + "'");
            if (_starting) return;   // 已在漸暗切場中，忽略重複按
            // 組隊模式湊不出官方的三張站位表(2v2 / 3v3 / 2v2v2)→ 擋住並說明原因。
            // 🔴 擋住而不是退回個人隊形:退回會讓玩家以為分隊生效了卻看到單人站位,那是靜默的錯誤行為。
            //    server 也會獨立擋一次(含 force),這裡只是提早講清楚。
            var teamRoom = Ctx != null && Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
            if (!TeamsCanStart(teamRoom, out _)) { Notice("room.teams_need_layout"); return; }

            if (Online)
            {
                if (_awaitingMatchStart) return;   // 已經送出請求,在等 server 回 matchStarting
                bool canStart = Ctx.Rooms != null && Ctx.Rooms.CanStart();
                bool force = false;
                if (!canStart)
                {
                    var r0 = Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
                    // 沒歌是硬條件,強制也開不了 → 直接說。
                    if (r0 == null || string.IsNullOrEmpty(r0.SongTitle)) { Notice("room.need_song"); return; }
                    // 有人沒準備 → 第一次按只提示,1.5 秒內再按一次才強制開始(需求:房主連按兩下強制開始)。
                    if (Time.unscaledTime - _lastStartPressAt > ForceStartDoubleTapSec)
                    {
                        _lastStartPressAt = Time.unscaledTime;
                        Debug.Log("[room] 開始被本機擋下:有人沒準備 → 提示再按一次強制開始");
                        Toast.Show(L("room.force_start_hint"));
                        return;
                    }
                    force = true;
                }
                _lastStartPressAt = -99f;
                _awaitingMatchStart = true;
                _awaitingSince = Time.unscaledTime;
                Debug.Log("[room] 送出 requestStart(force=" + force + ")");
                Ctx.Net.RequestStart(force, BuildResolvedRound());
                return;   // 🔴 這裡**不**進場 —— 等 matchStarting(見 OnMatchStarting)
            }

            if (Ctx.Rooms == null || !Ctx.Rooms.CanStart())
            {
                var room = Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
                Notice(room != null && string.IsNullOrEmpty(room.SongTitle) ? "room.need_song" : "room.waiting_players");
                return;
            }
            ResolveLocalRound();               // 離線沒有 server echo → 這一局的場景/隊形自己抽(房間設定不動)
            _starting = true;
            _returnedFromStage = true;         // 記住:待會回房的那次 OnShow 不再廣播「進入舞台遊戲」、訊息欄也不清
            UiSfx.Play(UiSfx.GameStart);       // 開始音效
            StartCoroutine(FadeToStage());     // 全螢幕 1 秒漸暗 → 才 StartGame 切舞台
        }

        // 全螢幕黑幕淡入(0→1) StartFadeDuration 秒，全黑後才交棒給 ScreenGameplay（避免場景切換的閃爍露餡）。
        private IEnumerator FadeToStage()
        {
            if (_startFade != null) _startFade.gameObject.SetActive(true);
            float t = 0f;
            while (t < StartFadeDuration)
            {
                t += Time.unscaledDeltaTime;
                if (_startFade != null) _startFade.color = new Color(0f, 0f, 0f, Mathf.Clamp01(t / StartFadeDuration));
                yield return null;
            }
            if (_startFade != null) _startFade.color = Color.black;
            Nav.StartGame?.Invoke();
        }

        /// <summary>
        /// 離開房間之後要落在哪一個畫面 —— **房間的「上一層」依模式而不同**:
        /// <list type="bullet">
        /// <item>線上:大廳(官方流程就是這條:大廳建房/加入 → 房間 → 返回 → 大廳)。</item>
        /// <item>離線(單機):選男女畫面。單機玩家**根本沒經過大廳** —— 按登入連不上就直接進自己的房間
        ///       (見 <c>GenderSelectScreen.EnterOwnRoomOffline</c>),所以他的上一層本來就是選男女。</item>
        /// </list>
        ///
        /// 🔴 判斷一律用 <see cref="AppContext.IsOnline"/>(定義是 <c>Net != null</c>),**不可以**改用本檔的
        ///    <see cref="Online"/>:那個是 <c>Net != null &amp;&amp; IsConnected &amp;&amp; InRoom</c>,而離房的兩個出口
        ///    都會在判斷前後把 <c>InRoom</c> 弄成 false —— 被踢時 NetClient 先清 <c>Room</c> 才 raise 事件、
        ///    OnLeave 的 swap callback 第一行就是 <c>LeaveRoom()</c>。用它會把線上玩家誤判成離線、丟回選男女。
        ///    也不要用 <c>IsConnected</c>:連線抖一下時 UserId 會先歸 0,但 Rooms/Chat 還是線上那份,狀態不一致。
        /// </summary>
        private ScreenId ExitScreen => Ctx != null && Ctx.IsOnline ? ScreenId.Lobby : ScreenId.GenderSel;

        /// <summary>
        /// 被房主踢出 / 位子被關掉(server 的 R8 會先發 kicked 再標 Closed)。
        ///
        /// 沒有這條的話症狀很怪:server 已經把你移出房間了,但畫面還停在房間 —— 六格全空、
        /// 房主的角色不見了,只剩你自己站在一個空房間裡,而且什麼提示都沒有(實機兩開驗到的)。
        ///
        /// 這裡刻意**不**廣播「離開舞台」:我們已經不在房裡了,那則訊息送不出去也不該送。
        /// 轉場順序與 <see cref="OnLeave"/> 相同(全黑時才清房間) —— 見那邊的註解。
        /// 目的地走共用的 <see cref="ExitScreen"/>:被踢**只可能發生在線上**(kicked 事件只在 Ctx.Net != null 時訂閱),
        /// 所以這裡實際上永遠是大廳;仍共用同一個答案,是為了讓三個離房出口看起來就是同一件事。
        /// </summary>
        private void OnKickedFromRoom(string reason)
        {
            if (Ctx == null || Ctx.Flow == null || Ctx.Flow.Current != ScreenId.Room) return;   // 不在房間畫面就不搶轉場
            Debug.Log("[room] kicked: " + (reason ?? ""));
            Notice("room.kicked");
            var exit = ExitScreen;   // 先取值,理由同 OnLeave
            ScreenTransition.Run(() => { Ctx.Rooms?.LeaveRoom(); GoTo(exit); });
        }

        private void OnLeave()
        {
            AnnounceStagePresence(false);   // 廣播「X 離開舞台」（趁還在房間、名字還查得到）
            // 目的地:線上回大廳、離線回選男女 —— 為什麼分流見 ExitScreen 的註解。
            // 🔴 在 ScreenTransition.Run **之前**先取值。IsOnline 只看 Net != null,其實不受 LeaveRoom() 影響,
            //    但 swap callback 裡第一行就在清房間,先取值能讓「為什麼安全」變成一眼看得出來、不必推理的事。
            var exit = ExitScreen;
            // 漸黑 → loading → 漸亮（同其它畫面進出效果）。切畫面(GoTo)在全黑時執行；
            // 大廳/選男女都無四邊滑入 UI → 不傳 onReveal。
            // LeaveRoom() 一定要在轉場「全黑」時才呼叫,不能在轉場前:它會觸發 RoomUpdated → Render(),此時 IsHost 已變 false
            // → 「開始」鈕被藏、橘色「準備」鈕現身,玩家會在黑幕蓋上前瞥見這一翻。放進 swap callback(全黑執行)即可藏住,
            // 且仍在 GoTo 之前 → 維持「先清房再換身分」的既有順序(F9 換性別 host 標記 bug 需要此順序)。
            ScreenTransition.Run(() => { Ctx.Rooms?.LeaveRoom(); GoTo(exit); });
        }

        /// <summary>Blue text edge on the location labels — rgb(70,74,152), per the official 白字藍邊 look.</summary>
        private static readonly Color32 LeftEdge = new Color32(70, 74, 152, 255);

        /// <summary>How thick (canvas px) the blue edge on 自由練習場/頻道/房號 is. Bump it for a heavier stroke.</summary>
        private const float HeaderEdgePx = 1.2f;

        // 左上位置標示(自由練習場 / 頻道 / 房號):左對齊,Render() 量實際字寬後左到右自動排版。
        private const float ServerX = 19f;       // 起始左緣(紫框左邊)
        private const float HeaderGap = 16f;     // 欄與欄的固定間距(px);調大=更開、調小=更擠。壓小一點讓多語系(英文較長)都塞進紫框
        private const float HeaderFontSz = 14f;  // 字級(這串比官方「新手一区」長,比 14 小一點才連間距一起塞進紫框)

        /// <summary>頭上漂浮名字的黑邊厚度(canvas px)。字色/粗體跟遊戲內頭頂名字共用 <see cref="Sdo.Game.TextStyles.FaceCream"/>。</summary>
        private const float HeadNameEdgePx = 1.4f;

        // 頭上名字牌的「家族列」版面(徽章大小/間距/行距/描邊)搬去 <see cref="RoomFamilyRow"/> ——
        // 本機與每個遠端玩家共用同一份,常數留在這裡的話兩邊會各調各的。

        /// <summary>F3 除錯用的示範徽章(切出「有家族」狀態時配 <see cref="GameSession.DemoGuildName"/>)。</summary>
        private const string DebugGuildEmblem = "SMALL43";

        /// <summary>win(Win1/Win2/Win3) → 對應的收合容器；其他一律回 Root。</summary>
        private RectTransform WinRoot(Vector2 win)
            => win == Win1 ? _win1Root : win == Win2 ? _win2Root : win == Win3 ? _win3Root : Root;

        private Image Art(string an, Vector2 win, float x, float y, string name)
            => UIKit.AddSprite(WinRoot(win), name, RoomUiArt.An(an), win.x + x, win.y + y);

        /// <summary>多幀 .an 的第 n 幀；載不到就回 null(呼叫端的 Image 只是不顯示，不會爆)。</summary>
        private static Sprite Frame(Sprite[] frames, int n)
            => frames != null && frames.Length > 0 ? frames[Mathf.Clamp(n, 0, frames.Length - 1)] : null;

        /// <summary>
        /// NO MAP / PLAYING 的四色幀:官方把它們放成「一個顏色一個裸 PNG」(<c>C06..C09</c> / <c>D06..D09</c>),
        /// 沒有 .an 包 —— 幀序由檔名的編號保證,與 Room66.an / master.an 的 a06..a09 / b06..b09 一致
        /// (黑/白=自由、橘=A、綠=B、藍=C)。少一張就只是那一幀畫不出來,不會位移其他幀。
        /// </summary>
        private static Sprite[] StateBadgeFrames(string prefix)
        {
            var frames = new Sprite[RoomBadgeFrames.FrameCount];
            for (int i = 0; i < frames.Length; i++)
                frames[i] = RoomUiArt.Image(prefix + "0" + (6 + i) + ".png");
            return frames;
        }

        // 裁切容器：左上錨在 Win2 局部(x,y)、大小 w×h，掛 RectMask2D → 子物件超出即被硬裁(同 AddSprite 的左上像素座標系)。
        private RectTransform NewClip(string name, float x, float y, float w, float h)
        {
            var rt = UIKit.NewRect(_win2Root, name);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(Win2.x + x, -(Win2.y + y));
            rt.sizeDelta = new Vector2(w, h);
            rt.gameObject.AddComponent<RectMask2D>();
            return rt;
        }

        // win2 文字定位：把線上 DDRROOM.XML 子座標 (x,y) 換成絕對畫布座標（相對 Win2 視窗原點）
        private static void PlaceW2(RectTransform rt, float x, float y, float w, float h)
            => Place(rt, Win2.x + x, Win2.y + y, w, h);

        // win2 難度/BPM 數字（淡紫粗體置中 + 白邊；座標 = Win2 + (x,y)）
        private OutlinedLabel MakeInfoNum(string name, float x, float y)
            => OutlinedLabel.Create(_win2Root, name, Win2.x + x, Win2.y + y, 21, 14, 12, InfoValueColor, Color.white, Win2EdgePx, true);

        // win2 難度/BPM 字幕（線上框沒烘這兩個字 → 自己畫；白邊；粗體；座標 = Win2 + (x,y)）
        private void MakeCaption(string name, string text, float x, float y)
            => OutlinedLabel.Create(_win2Root, name, Win2.x + x, Win2.y + y, 21, 14, 12, SongNameColor, Color.white, Win2EdgePx, true).SetText(text);

        /// <summary>
        /// 自由模式的「難度設置 ◄ EASY ►」(官方 DDRROOM.XML 的 <c>FMGameLevel</c> 視窗)。
        ///
        /// 版位逐字取自官方:視窗 <c>(5,292) 140×40 背景 FMdif.an</c>,子件 <c>FMLvlSelL(1,17)</c> /
        /// <c>FMLvlSelR(95,17)</c> / <c>FMLvlChoose(30,16) 66×19</c> —— 全部再加上 Win2 原點。
        /// 它與房主的 <c>songselect</c>(14,296)**疊在同一格**,所以永遠只會有一個是開著的(見 Render)。
        /// </summary>
        private void BuildFreeModeLevel()
        {
            _fmLevelBg = UIKit.AddSprite(_win2Root, "FmLevelBg", EraseFmDifTitle(RoomUiArt.An("FMdif")),
                                         Win2.x + 5, Win2.y + 292);
            // 標題自己畫:官方那張圖把「难度设置」烘死在裡面(簡體),抹掉之後疊上翻譯過的字。
            // 顏色/描邊沿用原圖量到的值(紫 = ModeColor、乳白描邊)，看起來和沒動過一樣。
            _fmLevelTitle = OutlinedLabel.Create(_win2Root, "FmLevelTitle", Win2.x + 5, Win2.y + 293, 124, 16, 13,
                                                 ModeColor, Color.white, Win2EdgePx, true);
            _fmLevelTitle.SetText(L("room.difficulty_setting"));

            _fmLevelValue = UIKit.AddText(_win2Root, "FmLevelValue", "", 13, FmLevelColor, TextAlignmentOptions.Center);
            _fmLevelValue.fontStyle = FontStyles.Bold;
            PlaceW2(_fmLevelValue.rectTransform, 35, 308, 66, 19);

            // ◄ / ► 用官方的橘色箭頭(FMLvlDown/FMLvlUp 三態)。跟速度那對小箭頭一樣不掛滑過音。
            _fmLevelPrev = Btn("fmlvlprev", "FMLvlDown1", "FMLvlDown2", "FMLvlDown3", Win2, 6, 309,
                               () => StepFreeDifficulty(-1), hoverSfx: null);
            _fmLevelNext = Btn("fmlvlnext", "FMLvlup1", "FMLvlup2", "FMLvlup3", Win2, 100, 309,
                               () => StepFreeDifficulty(1), hoverSfx: null);

            SetFreeModeLevelVisible(false);
        }

        private void SetFreeModeLevelVisible(bool on)
        {
            if (_fmLevelBg != null) _fmLevelBg.gameObject.SetActive(on);
            if (_fmLevelTitle != null) _fmLevelTitle.gameObject.SetActive(on);
            if (_fmLevelValue != null) _fmLevelValue.gameObject.SetActive(on);
            if (_fmLevelPrev != null) _fmLevelPrev.gameObject.SetActive(on);
            if (_fmLevelNext != null) _fmLevelNext.gameObject.SetActive(on);
        }

        /// <summary>
        /// FMdif.an 的標題「难度设置」是**烘在圖上的簡體字** —— 把它抹掉,標題改由程式疊(可翻譯)。
        ///
        /// 抹法之所以是像素完全正確的:那塊底是**純垂直漸層**(同一列從左到右每個像素一模一樣,已逐列驗過),
        /// 所以把字那塊的每個像素換成**同一列**左邊乾淨處(x=20)的顏色,得到的就是原本被字蓋住的底。
        /// 只動 y=2..14 這幾列,下面 y≥15 的深青色值框一個像素都不碰。
        /// 材質不可讀時原樣回傳(頂多標題疊字,不會沒圖)。
        /// </summary>
        private static Sprite EraseFmDifTitle(Sprite src)
        {
            if (src == null || src.texture == null) return src;
            var r = src.textureRect;
            int tx = Mathf.RoundToInt(r.x), ty = Mathf.RoundToInt(r.y);
            int w = Mathf.RoundToInt(r.width), h = Mathf.RoundToInt(r.height);
            if (w < FmTitleClearX1 || h < FmTitleClearY1) return src;
            Color[] px;
            try { px = src.texture.GetPixels(tx, ty, w, h); }
            catch { return src; }
            for (int yTop = FmTitleClearY0; yTop < FmTitleClearY1; yTop++)
            {
                int row = (h - 1 - yTop) * w;                 // GetPixels 是由下往上,.an 座標是由上往下
                Color clean = px[row + FmTitleCleanX];        // 同一列、字左邊那塊乾淨的底色
                for (int x = FmTitleClearX0; x < FmTitleClearX1; x++) px[row + x] = clean;
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            tex.SetPixels(px);
            tex.Apply(false);
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), src.pixelsPerUnit, 0, SpriteMeshType.FullRect);
        }

        /// <summary>自由模式:按 ◄ / ► 換自己的難度(跳過這首歌沒有的譜),存進 session 並重畫面板。</summary>
        private void StepFreeDifficulty(int dir)
        {
            var s = Ctx != null ? Ctx.Session : null;
            if (s == null) return;
            int next = FreeModeDifficulty.Step(s.FreeDifficulty, dir, CurrentPlayableSlots());
            if (next == s.FreeDifficulty) return;
            s.FreeDifficulty = next;
            RenderWin2();
        }

        // 組隊單選格：normal/pushed 兩態，點了把 GameSession.Team 設成 idx 並重畫（座標 = Win2 + (x,y)）
        private void BuildTeamToggle(int idx, string normalAn, string pushedAn, float x, float y)
        {
            _teamNormal[idx] = RoomUiArt.AnSoloAA(normalAn);   // 自貼圖 + 邊緣抗鋸齒（同其他房間按鈕）
            _teamPushed[idx] = RoomUiArt.AnSoloAA(pushedAn);
            var img = UIKit.AddSprite(_win2Root, "Team" + idx, _teamNormal[idx], Win2.x + x, Win2.y + y, raycast: true);
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.None;
            UiSfx.AttachPress(btn, UiSfx.Click);   // 按下 SE_0001；win2 中間設定塊滑過不出聲 → 不掛 hover
            int i = idx;
            btn.onClick.AddListener(() => PickOwnTeam(i));
            // 房主右鍵組隊鈕 → 自動分隊選單(2對2 / 3對3 / 2對2對2)。官方沒有這顆鈕,而 win2 也沒有空位再擺一顆,
            // 所以掛在同一塊上;非房主右鍵不會有反應(選單規則與座位選單同一套守門)。
            var proxy = img.gameObject.AddComponent<PointerClickProxy>();
            proxy.Clicked = ev =>
            {
                if (ev != null && ev.button == PointerEventData.InputButton.Right) ShowAssignTeamsPopup(ev.position);
            };
            _teamImg[idx] = img;
        }

        /// <summary>
        /// 自己換隊。連線模式送 <c>setOwnTeam</c> 讓 server 決定(它會擋「已準備就不能換」= R10a),
        /// **不做樂觀更新** —— 顯示一律等 server 的 roomState 回來(與整個連線層同一個原則)。
        /// 離線模式維持原本的純本機行為。
        /// </summary>
        private void PickOwnTeam(int team)
        {
            // 只有普通模式能組隊(server 也擋,這裡只是提早把原因講出來 —— 官方那四格在自由模式下
            // 圖也是照畫的,所以不能靠「看起來能不能按」讓玩家知道)。改回「自由」永遠放行。
            if (team != (int)TeamTag.Free && !TeamLayoutRules.TeamsAllowedIn(RoomGameMode()))
            {
                Notice("room.teams_normal_mode_only");
                return;
            }
            if (Ctx != null && Ctx.Net != null && Ctx.Net.IsConnected && Ctx.Net.InRoom)
            {
                Ctx.Net.SetOwnTeam(team);
                return;
            }
            Ctx.Session.Team = team;
            RenderWin2();
        }

        /// <summary>房主的「自動分隊」選單。人數湊不出那個版型時 server 會回 <c>error{badTeams}</c>(R10b),
        /// 所以這裡先用同一條純規則把湊不出來的項目擋掉,按下去才不會只收到一個看不懂的錯誤。</summary>
        private void ShowAssignTeamsPopup(Vector2 screenPos)
        {
            var room = Ctx != null && Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
            if (!CanManageSeats(room)) return;
            // 自動分隊也只在普通模式(server 的 assignTeams 同樣會擋)。
            if (!TeamLayoutRules.TeamsAllowedIn(RoomGameMode())) { Notice("room.teams_normal_mode_only"); return; }
            int seated = SeatedPlayerCount(room);
            var layouts = new List<TeamLayout>(3);
            if (seated == 4) layouts.Add(TeamLayout.V2v2);
            if (seated == 6) { layouts.Add(TeamLayout.V3v3); layouts.Add(TeamLayout.V2v2v2); }
            if (layouts.Count == 0) { Notice("room.teams_need_layout"); return; }

            CloseSlotPopup();
            _slotPopup = BuildContextMenu("TeamsPopup", screenPos, layouts.Count,
                (idx, _) => L(layouts[idx] == TeamLayout.V2v2 ? "room.teams_2v2"
                            : layouts[idx] == TeamLayout.V3v3 ? "room.teams_3v3" : "room.teams_2v2v2"),
                idx =>
                {
                    var now = Ctx != null && Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
                    if (CanManageSeats(now)) { Ctx.Net.AssignTeams(layouts[idx]); Notice("room.teams_assigned"); }
                    CloseSlotPopup();
                });
        }

        /// <summary>座位上有幾個人(不含旁觀者、不含空位/關閉的位子)。</summary>
        private static int SeatedPlayerCount(RoomInfo room)
        {
            if (room == null) return 0;
            int n = 0;
            for (int i = 0; i < room.Seats.Count; i++)
                if (room.Seats[i] != null && !room.Seats[i].IsEmpty) n++;
            return n;
        }

        // 所有房間按鈕統一：滑過 Buttonfloat(圖1/圖2)、按下 SE_0001(圖3 預設)。少數例外用參數覆寫：
        //   pressSfx：房主設置→Buttonfloat；開始→null(由 OnStart 播 Start 音 + 漸暗)。
        //   hoverSfx：win2 中間設定塊(速度/note/組隊/掉落)→null(滑過不出聲)，其餘保留 Buttonfloat。
        private Button Btn(string objName, string nrm, string hov, string psh, Vector2 win, float x, float y,
            System.Action onClick, string pressSfx = UiSfx.Click, string hoverSfx = UiSfx.ButtonFloat, bool solo = true,
            float alphaHit = 0f, bool circle = false, bool disc = false)
        {
            // solo=true(預設) → 三態都用 AnSoloAA(自貼圖 + 3× 超取樣)載入：消掉 atlas 鄰居白邊，並把官方近 1-bit 圓鈕以
            // 3× 解析度存、用邏輯尺寸顯示 → GPU 面積降取樣出乾淨的 ~1px 抗鋸齒邊(開始/旁觀/房主設置…),不鋸齒也不糊;
            // 載不到 solo crop 時自動回退共用大圖，安全。
            // circle=true → 圓形圖示鈕(右上 head-bar 的設定/邀請/返回/交易/天使,以及下排工具列的泡泡/表情/喇叭/大聲公/
            // 寵物/翅膀/衣櫥/手環/信件):它們是 31~34px 帶「軟 AA 邊」的圓盤,AnSoloAA 的 α<128→0 硬裁會把軟邊裁成
            // 1-bit 圓 → 邊緣鋸齒/破碎;改走 AnSoloCircleAA(CircleMask 平滑圓邊 + 超取樣)。只給真正是圓的鈕:膠囊/長條
            // (聊天模式 Room4、道具包 Room55)留在預設路徑。
            // disc=true → 大顆實心圓球(開始/準備/取消/旁觀):55~73px 手繪圓盤,深色描邊本身就是「階梯」(這邊 1 texel、那邊
            // 2 texel),放大就是一圈黑色缺口。alpha 遮罩救不了(只會減、補不回缺口,而且階梯也在 RGB 描邊上)→ 走
            // AnSoloDiscAA:沿圓周方向低通把階梯抹平 + alpha 用解析圓重建。
            System.Func<string, Sprite> res;
            if (disc) res = RoomUiArt.AnSoloDiscAA;
            else if (circle) res = RoomUiArt.AnSoloCircleAA;
            else if (solo) res = RoomUiArt.AnSoloAA;
            else res = RoomUiArt.An;
            var b = UIKit.AddSpriteButton(WinRoot(win), objName, res(nrm), res(hov), res(psh), win.x + x, win.y + y);
            if (hoverSfx != null) UiHoverSfx.Attach(b, hoverSfx);
            UiSfx.AttachPress(b, pressSfx);
            if (onClick != null) b.onClick.AddListener(() => onClick());
            // alphaHit>0：大顆圓鈕(開始/準備/旁觀)命中判定跟著可見像素走,透明四角不再誤觸。小箭頭鈕刻意不開(整塊 rect 較好按)。
            UIKit.SetAlphaHit(b.targetGraphic, alphaHit);
            return b;
        }

        private RawImage AddRaw(string name, float x, float y, float w, float h)
        {
            var rt = UIKit.NewRect(_win1Root, name);   // head slots live in the top head panel (win1)
            var ri = rt.gameObject.AddComponent<RawImage>();
            ri.raycastTarget = false;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return ri;   // head-portrait RTs use natural orientation (only the scene backdrop honours flipBackdropV)
        }

        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }
    }
}
