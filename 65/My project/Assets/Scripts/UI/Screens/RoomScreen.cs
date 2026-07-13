using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.Localization;
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
        private const int HeadLayer = 11;

        // win2 文字色（取自線上 DDRROOM.XML）：歌名/難度·BPM字幕 0xff835ce1、難度·BPM數字 0xffc969e3、
        // 速度值 0xfffff5a4、模式名 0xff9d6ac9。
        private static readonly Color32 SongNameColor = new Color32(0x83, 0x5c, 0xe1, 0xff);
        private static readonly Color32 InfoValueColor = new Color32(0xc9, 0x69, 0xe3, 0xff);
        private static readonly Color32 SpeedColor = new Color32(0xff, 0xf5, 0xa4, 0xff);
        private static readonly Color32 ModeColor = new Color32(0x9d, 0x6a, 0xc9, 0xff);
        // 自由模式/歌名/難度/BPM 的白色描邊(位移複製,不靠 SDF) 厚度(canvas px)。要更粗就調大。
        private const float Win2EdgePx = 1.1f;

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
        private const float ChatBubbleLifetime = 10f;
        private const float ChatBubbleRiseSpeed = 12f;    // px/s；泡持續往上飄，不再卡在固定高度（點5）
        // 泡身垂直中心(畫布 y=56.5)對齊到「肩錨 + 位移」：換 sprite 不跳位、文字上下置中。位移=泡身中心相對肩錨的偏移。
        private const float ChatBubbleAnchorVisibleLeft = 80f;   // 泡身中心相對肩錨的水平位移(右+/左-)；調小/負=更靠名字
        private const float ChatBubbleAnchorVisibleTop = 10f;     // 泡身中心相對肩錨的垂直位移(下+/上-)；調大=更低(往胸)、負=更高(往頭)
        // Official FUN_00460ef0 is gated by a 0x32 ms timer and moves 1/3 of the remaining distance per accepted tick.
        private const float ChatBubbleFollowTicksPerSecond = 20f;
        private const float ChatBubbleFollowStep = 0.33333335f;
        private const float ChatBubbleDragScale = 1f;
        private static readonly Color ChatBubbleTextColor = new Color32(0x7C, 0x01, 0x38, 0xFF);
        // 左下訊息欄配色：一般行名字/內容=白；系統行=金黃；密語=#1efefe；進出舞台廣播=#72c1fe。
        private const string ChatSystemHex = "F0C24A";
        private const string WhisperHex = "1EFEFE";
        private const string StageHex = "72C1FE";
        private const string WhisperLinkId = "w|";   // TMP link id 前綴：<link="w|名字">名字</link> → 點名字密語
        // 行內 emoji（表情 + 字）：emoji 疊在使用者打的位置——前字後留一段固定寬空檔，emoji 疊上去。自己調這幾個像素數就好。
        private const float BubbleEmojiGapPx = 24f;       // <space=…> 在字裡預留的水平空檔
        private const float BubbleEmojiSizePx = 22f;      // emoji 顯示邊長
        private const float BubbleEmojiInlinePadX = 1f;   // emoji 相對前字右緣的水平微調(右+/左-)
        private const float BubbleEmojiInlineOffY = 0f;   // emoji 垂直微調(上+/下-)

        private RawImage _backdrop;
        private RectTransform _bubbleLayer;   // 對話泡容器:夾在 3D 背景與 UI 之間 → 泡畫在 UI 底下
        private CanvasGroup _chatLogGroup;    // 收合時淡出左下訊息欄：win3 只下滑 119px,而訊息欄起點較高(y=445)會露出末幾行
        private readonly RawImage[] _slotHead = new RawImage[RoomLayout.SeatCount];
        private readonly Image[] _slotClose = new Image[RoomLayout.SeatCount];
        private readonly Image[] _slotMaster = new Image[RoomLayout.SeatCount];
        private readonly TextMeshProUGUI[] _slotName = new TextMeshProUGUI[RoomLayout.SeatCount];
        private OutlinedLabel _serverLabel, _channelLabel, _roomIdLabel;   // 白字 + 藍邊 (rgb 70,74,152)
        private TextMeshProUGUI _roomNameLabel;
        private OutlinedLabel _songLabel;   // 歌名(白邊)
        private OutlinedLabel _floatName;       // name marker that floats above the avatar in the room (官方頭上名字)；字 rgb(250,252,214) 描黑邊
        private RectTransform _chatContent;
        private ScrollRect _chatScroll;
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
        private Coroutine _chatInputFocusRoutine;
        private Button _songSelectBtn, _startBtn, _readyBtn, _cancelReadyBtn;

        // ---- win2 右側面板控件（模式/場景/歌曲資訊/速度/note/組隊/掉落）----
        private OutlinedLabel _modeLabel;  // 自由模式/普通模式（白邊；線上是純文字，沒有 mode 圖）
        private Image _sceneThumb;         // 第二層場景圖（隨機 → RANDOM；具體 → Scene{id+1}）
        private Image _diffDisc;           // CD 光碟，依難度換色（Difficult.an 3 幀）
        private Sprite[] _diffDiscFrames;
        private OutlinedLabel _levelLabel, _bpmLabel;   // 難度/BPM 數字(白邊)
        private TextMeshProUGUI _speedLabel;
        private Image _noteDisplay;        // note 種類預覽框
        private SpriteSeqAnim _noteAnim;   // 預覽框的循環動畫驅動
        private int _speedIndex;
        private readonly Image[] _teamImg = new Image[4];      // 組隊 A/B/C/自由
        private readonly Sprite[] _teamNormal = new Sprite[4];
        private readonly Sprite[] _teamPushed = new Sprite[4];

        private RoomScene3D _scene;
        private RoomHeadPortrait _localHead;
        private Camera _maskedCam; private int _savedMask;
        private bool _subscribed;

        // ---- 頭貼框取微調（男女各一組，獨立調整）----------------------------------------------------------------
        //  headAimUp   上下位置：變大 → 頭在框內往「上」    | zoom 遠近：變大 → 變「遠」變小、變小 → 拉近變大
        //  headFrameDist 遠近基準(距離=頭高×此值×zoom)      | avatarScale 整體大小
        //  只想微調的話：上下改 *HeadAimUp、遠近改 *Zoom 即可。改完 build 就生效。
        private const float FemaleHeadAimUp = 0.11f, FemaleHeadZoom = 0.9f, FemaleHeadFrameDist = 1.9f, FemaleAvatarScale = 1.05f;
        private const float MaleHeadAimUp   = 0.25f, MaleHeadZoom   = 1f, MaleHeadFrameDist   = 1.9f, MaleAvatarScale   = 1.05f;

        // 依性別套用頭貼框取參數（上下位置 / 遠近）。必須在 RoomHeadPortrait.Init 之前呼叫，第一幕就正確。
        private static void ApplyHeadFraming(RoomHeadPortrait head, bool male)
        {
            head.headAimUp     = male ? MaleHeadAimUp     : FemaleHeadAimUp;
            head.zoom          = male ? MaleHeadZoom      : FemaleHeadZoom;
            head.headFrameDist = male ? MaleHeadFrameDist : FemaleHeadFrameDist;
            head.avatarScale   = male ? MaleAvatarScale   : FemaleAvatarScale;
        }

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

        protected override void BuildUI()
        {
            // 1) full-screen 3D-room backdrop (behind everything; texture wired in OnShow)
            var bgRt = UIKit.NewRect(Root, "RoomBackdrop");
            UIKit.Stretch(bgRt);
            _backdrop = bgRt.gameObject.AddComponent<RawImage>();
            _backdrop.color = Color.black;
            _backdrop.raycastTarget = false;
            if (flipBackdropV) _backdrop.uvRect = new Rect(0f, 1f, 1f, -1f);

            // 對話泡層:緊接在背景之後建立(sibling index 低)，之後才建的 UI 都疊在它上面 → 泡在 UI 底下、3D 背景之上。
            // 打字泡與已送出的泡都掛這底下（見 BuildRoomChatLog / SpawnSentRoomBubble）。容器本身不擋點擊。
            _bubbleLayer = UIKit.NewRect(Root, "RoomChatBubbleLayer");
            UIKit.Stretch(_bubbleLayer);

            // name marker that floats above the avatar's head in the room (positioned each frame in Update).
            // 跟遊戲內頭頂名字同款:共用色 TextStyles.FaceCream(rgb 250,252,214)+ 黑邊 + 粗體 + 8 向描邊。
            _floatName = OutlinedLabel.Create(Root, "FloatName", 0, 0, 160, 20, 14, TextStyles.FaceCream, Color.black, HeadNameEdgePx, true);
            _floatName.gameObject.SetActive(false);

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
            float[] nameX = { 52, 172, 293, 418, 539, 662 };
            float[] masterX = { 54, 176, 298, 421, 544, 666 };
            for (int i = 0; i < RoomLayout.SeatCount; i++)
            {
                _slotHead[i] = AddRaw("Slot" + i, sx[i] + Win1.x, RoomLayout.HeadSlotY, RoomLayout.HeadSlotW, RoomLayout.HeadSlotH);
                _slotHead[i].enabled = false;   // shown only when occupied (head RT assigned)
                _slotClose[i] = Art("close", Win1, closeX[i], 59, "Close" + i);
                _slotMaster[i] = Art("master", Win1, masterX[i], 102, "Master" + i);
                _slotMaster[i].enabled = false;
                var plate = Art("Team", Win1, nameX[i], 141, "NamePlate" + i);
                plate.enabled = false;
                _slotName[i] = UIKit.AddText(_win1Root, "Name" + i, "", 13, Color.white, TextAlignmentOptions.Center);
                Place(_slotName[i].rectTransform, nameX[i] + Win1.x, 141, RoomLayout.HeadSlotW, 18);
                _slotName[i].gameObject.SetActive(false);
            }

            // head-bar buttons (win1)
            // 修改(房間設定)按鈕：官方按了會跳一條半透明黑底橫幅(Toast) → 依需求拿掉，按了不做事。
            Btn("changeroomname", "Room45", "Room46", "Room47", Win1, 461, 7, null);
            Btn("help", "BtnHeadHelp_1", "BtnHeadHelp_2", "BtnHeadHelp_3", Win1, 654, 7, null);
            Btn("roomangel", "roomangel_0", "roomangel_1", "roomangel_2", Win1, 616, 5, null);
            Btn("roomexchange", "BtnHeadExchange_1", "BtnHeadExchange_2", "BtnHeadExchange_3", Win1, 652, 5, () => Nav.OpenShop?.Invoke());   // → 商城 (avatar shop)
            Btn("invite", "BtnHeadInvite_1", "BtnHeadInvite_2", "BtnHeadInvite_3", Win1, 688, 5, null);
            Btn("setting", "BtnHeadOption_1", "BtnHeadOption_2", "BtnHeadOption_3", Win1, 724, 5, () => Nav.OpenSettings?.Invoke());
            Btn("leaveroom", "BtnHeadReturn_1", "BtnHeadReturn_2", "BtnHeadReturn_3", Win1, 760, 5, OnLeave);

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
            _noteDisplay = Art("hiteft2", Win2, 11, 191, "NoteDisplay");
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
                RoomUiArt.An("ShopDlg13"), RoomUiArt.An("LabUnCheck"), RoomUiArt.An("LabCheck"),
                new[] { L("room.drop_up"), L("room.drop_down"), L("room.drop_tilt") }, null,
                Mathf.Clamp(Ctx.Session.DropDirection, 0, 2), SpeedColor, DropListColor,
                i => { Ctx.Session.DropDirection = i; RoomConfig.defaultDropDirection = i; RoomConfig.Save(); },   // 持久化：掉落方式寫回 config.ini（進遊戲決定 note 面板上/下）
                expandDown: true, listX: Win2.x + 70, listWidth: 38f,
                valueOffsetY: 2f);   // 只把「向上/向下」值往上 2px，▼ 鈕位置不動
            // 掉落方式 ▼ 開關鈕按下 → SE_0001（清單列本來就有；此為開關鈕本身。中間設定塊仍不掛滑過音）。
            UiSfx.AttachClick(_dropCombo.GetComponent<Button>());

            // 房主設置（= 選歌入口）。線上原版 BtnRoomMaster_1/2/3。按下音效改 Buttonfloat（非預設 SE_0001）。
            _songSelectBtn = Btn("songselect", "BtnRoomMaster_1", "BtnRoomMaster_2", "BtnRoomMaster_3", Win2, 14, 296, () => GoTo(ScreenId.SongSelect), UiSfx.ButtonFloat);

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
            Btn("OpenRecord", "OpenRecord_a", "OpenRecord_b", "OpenRecord_c", Win3, 279, 82, null);           // 錄製
            _expressionBtn = Btn("expression1", "BtnExpression_1", "BtnExpression_2", "BtnExpression_3", Win3, 311, 82, ToggleExpressionMenu); // 表情
            Btn("ChatSendButton", "BtnSpeaker_1", "BtnSpeaker_2", "BtnSpeaker_3", Win3, 343, 82, SendRoomChat);       // 喇叭/送出
            Btn("LoudSpeaker", "LoudSpeaker_1", "LoudSpeaker_2", "LoudSpeaker_3", Win3, 376, 82, null);       // 大聲公
            Btn("RoomPet", "BtnPet_1", "BtnPet_2", "BtnPet_3", Win3, 411, 83, null);                         // 寵物
            Btn("WingButton", "RoomWing", "RoomWing1", "RoomWing", Win3, 447, 82, null);                     // 翅膀
            // 衣櫥 → 儲物櫃 (WardrobeScreen)。比照選歌鈕：按下用滑動音(ButtonFloat)，開櫃的 Frameround whoosh 由 WardrobeScreen.Open 播 → 服飾欄旋轉進場。
            Btn("ClosetButton", "RoomCloset001", "RoomCloset002", "RoomCloset003", Win3, 480, 81, () => Nav.OpenWardrobe?.Invoke(), UiSfx.ButtonFloat);
            Btn("BangleButton", "Bangle0", "Bangle1", "Bangle0", Win3, 514, 82, null);                       // 手環
            Btn("NotesButton", "Emai0", "Emai1", "Emai0", Win3, 548, 82, null);                              // 信件
            Btn("tools", "Room55", "Room56", "Room57", Win3, 584, 85, null);                                // 道具包
            // 右邊改成藍色「旁觀」(look, BtnLook) —— 取代官方綠色「進入」(play, Room92/93/94)。
            Btn("look", "BtnLook_1", "BtnLook_2", "BtnLook_3", Win3, 651, 60, null);

            // 開始：按下不走預設 SE_0001，改由 OnStart 播 Start 音效 + 全螢幕漸暗再切舞台。
            _startBtn = Btn("start", "Room15", "Room16", "Room17", Win3, 706, 43, OnStart, null);
            _readyBtn = Btn("ready", "Room12", "Room13", "Room14", Win3, 706, 43, OnReadyToggle);
            _cancelReadyBtn = Btn("cancel_ready", "c_ready0", "c_ready1", "c_ready2", Win3, 706, 43, OnReadyToggle);

            // 5) 左上「左拉」收合鈕（官方 uihide/uidisplay，同一位置 11,83）。按 ◄(BtnMaypopLeft) → 三個面板往四周滑出；
            //    收合後原地換成 ►(BtnMaypopRight) 展開鈕。掛在 Root（不隨面板收合），且最後建立 → 疊在最上層永遠可點。
            // 收合/展開鈕：滑過 Buttonfloat、按下 Interfaceout（官方 uihide/uidisplay 滑動音）。
            _uiHideBtn = UIKit.AddSpriteButton(Root, "uihide",
                RoomUiArt.An("BtnMaypopLeft_1"), RoomUiArt.An("BtnMaypopLeft_2"), RoomUiArt.An("BtnMaypopLeft_3"), 11, 83);
            UiHoverSfx.Attach(_uiHideBtn, UiSfx.ButtonFloat);
            UiSfx.AttachPress(_uiHideBtn, UiSfx.WindowSlide);
            _uiHideBtn.onClick.AddListener(() => SetCollapsed(true));
            _uiShowBtn = UIKit.AddSpriteButton(Root, "uidisplay",
                RoomUiArt.An("BtnMaypopRight_1"), RoomUiArt.An("BtnMaypopRight_2"), RoomUiArt.An("BtnMaypopRight_3"), 11, 83);
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
                LocalizationManager.LanguageChanged += Render;   // 切語言時，房號/房名/位置標示即時重譯
                _subscribed = true;
            }

            bool localMale = Ctx != null && Ctx.Session != null && Ctx.Session.Gender == 1;
            // 從 id-based equippedItems 經 catalog 現算 (含合成 翅膀/表情/项链)，非讀可能過時的 equippedParts 快取 → 房間
            // 才會跟儲物櫃一致顯示飾品 (user: 儲物櫃有、room 沒有)。
            string[] localAvatarParts = ProfileManager.Active != null
                ? WardrobeStore.ResolveEquippedParts(ProfileManager.Active, localMale ? 1 : 0, id => AvatarItemCatalog.Instance.ById(id))
                : null;

            if (_scene == null)
            {
                var sceneGo = new GameObject("RoomScene3D");
                _scene = sceneGo.AddComponent<RoomScene3D>();
                _scene.Build(localMale, localAvatarParts);
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
                _localHead.Init(localMale, localAvatarParts);
                _localHead.WalkingProvider = () => _scene != null && _scene.IsWalking;   // framed head mirrors the avatar's motion
                _localHead.FacingProvider = () => _scene != null ? _scene.AvatarFacing : 0f;   // …and its left/right facing
            }

            // mask the room's 3D layers off the front-end UI camera (it renders ~0, so it would otherwise draw them flat)
            var ui = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            if (ui != null)
            {
                _maskedCam = ui; _savedMask = ui.cullingMask;
                ui.cullingMask &= ~((1 << RoomScene3D.SceneLayer) | (1 << HeadLayer));
            }

            // 儲物櫃換穿後 → 立即重建本機房間 avatar + 頭貼，讓新穿搭當場反映 (WardrobeScreen 已寫回 profile.json)。
            Nav.RefreshRoomAvatar = RefreshLocalAvatar;

            // 常駐單例：清掉上次「開始」漸暗殘留的黑幕，回房間才不會整片黑。
            _starting = false;
            if (_startFade != null) { _startFade.gameObject.SetActive(false); _startFade.color = new Color(0f, 0f, 0f, 0f); }

            ResetCollapse();             // 每次進場都從「完全展開」開始（常駐單例，避免上次收合狀態殘留）
            SeedDefaultSongIfNeeded();   // 進大廳預設選好 index 最大的歌(easy)，房間一進來就有歌
            // 聊天作用域切到本房間：之後的送話/廣播標記成此房，且只顯示此房 + 密語(跨場)。
            _chatScopeRoomId = Ctx.Rooms != null && Ctx.Rooms.CurrentRoom != null ? Ctx.Rooms.CurrentRoom.Id : 0;
            Ctx.Chat?.Clear();   // 換場地就清訊息欄：進房間(大廳→房間 / 遊戲→房間都會經過 OnShow)先清空
            Ctx.Chat?.SetScope(ChatScope.Room, _chatScopeRoomId);
            RebuildRoomChat();
            Render();
            // 進場廣播「X 進入舞台遊戲」只在「從大廳進來」時送；從舞台遊戲回房(打完一首回房)不重播。
            if (_returnedFromStage) _returnedFromStage = false;
            else AnnounceStagePresence(true);   // 只同房、只在「當前」分類
        }

        // 儲物櫃換穿 → 重建本機房間 3D avatar + 頭貼 (讀最新 EquippedAvatarParts；WardrobeScreen 已寫回 profile)。
        private void RefreshLocalAvatar()
        {
            bool male = Ctx != null && Ctx.Session != null && Ctx.Session.Gender == 1;
            string[] parts = ProfileManager.Active != null
                ? WardrobeStore.ResolveEquippedParts(ProfileManager.Active, male ? 1 : 0, id => AvatarItemCatalog.Instance.ById(id))
                : null;
            if (_scene != null) _scene.RebuildLocalAvatar(male, parts);
            // 頭貼要「整個重建」：RoomHeadPortrait.Init 每次都新建一隻頭 avatar/相機/RT 卻不清舊的 → 直接再 Init 只會疊一隻
            // 舊的、頭貼不更新。故銷毀整個 _localHead 再重建並重接 provider。
            if (_localHead != null) { Destroy(_localHead.gameObject); _localHead = null; }
            var headGo = new GameObject("RoomLocalHead");
            _localHead = headGo.AddComponent<RoomHeadPortrait>();
            _localHead.layer = HeadLayer;
            ApplyHeadFraming(_localHead, male);   // 男女各自的上下/遠近
            _localHead.Init(male, parts);
            _localHead.WalkingProvider = () => _scene != null && _scene.IsWalking;
            _localHead.FacingProvider = () => _scene != null ? _scene.AvatarFacing : 0f;
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
                var model = SongListModel.FromCatalog();          // 已按 fileId 由大到小排序
                if (model.All.Count == 0) return;
                var e = model.All[0];                             // [0] = index 最大 = 清單最上面
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
        }

        public override void OnHide()
        {
            // NOTE: 進選歌時房間「不會」走到這裡 —— 選歌是疊在房間上的 overlay，房間仍是 visible（見 FrontendApp.ShowOnly）。
            // OnHide 只在真正離開房間時觸發（回大廳 / 進遊戲），所以在這裡完整拆除 3D 場景是正確的。
            if (_subscribed)
            {
                if (Ctx.Rooms != null) Ctx.Rooms.RoomUpdated -= OnRoomUpdated;
                if (Ctx.Chat != null) Ctx.Chat.MessageReceived -= OnRoomChatMessage;
                LocalizationManager.LanguageChanged -= Render;
                _subscribed = false;
            }
            HideChatModeMenu();
            HideExpressionMenu();
            HideRoomChatBubble();
            ClearSentRoomBubbles();
            _chatInputSticky = false;   // 離開房間 → 放掉輸入框黏 focus，回來時不自動搶 focus
            Input.imeCompositionMode = IMECompositionMode.Auto;   // 還原，別影響遊戲/其他畫面的按鍵
            if (_maskedCam != null) { _maskedCam.cullingMask = _savedMask; _maskedCam = null; }
            if (_backdrop != null) { _backdrop.texture = null; _backdrop.color = Color.black; }
            for (int i = 0; i < _slotHead.Length; i++) if (_slotHead[i] != null) { _slotHead[i].texture = null; _slotHead[i].enabled = false; }
            if (_localHead != null) { Destroy(_localHead.gameObject); _localHead = null; }
            if (_scene != null) { Destroy(_scene.gameObject); _scene = null; }
        }

        private void OnRoomUpdated(int id) => Render();

        private void BuildRoomChatLog()
        {
            // 訊息欄底改成全透明（原本是灰色半透明 a=0.18）；文字直接疊在 3D 房間上。
            _chatScroll = UIKit.AddVerticalScroll(_win3Root, "AllChatList", out _chatContent, 0f, 3, new Color(0f, 0f, 0f, 0f));
            Place(_chatScroll.GetComponent<RectTransform>(), 14, 445, 360, 104);
            _chatScroll.scrollSensitivity = 18f;
            _chatLogGroup = _chatScroll.gameObject.AddComponent<CanvasGroup>();   // 收合時淡出(win3 下滑不足以完全移出訊息欄,見 ApplyCollapse)

            // 打字泡：固定一顆。已送出的泡另外 Spawn，可並存一串。掛在 _bubbleLayer(UI 底下)。
            _chatBubbleRoot = UIKit.NewRect(_bubbleLayer, "RoomChatTypingBubble");
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
            UIKit.AddSprite(_chatModeMenu, "Bg", RoomUiArt.An("Room_Pop16"), 0, 0);
            AddChatModeChoice("chatmode_family", ChatChannel.Family, 2, 2);
            AddChatModeChoice("chatmode_friend", ChatChannel.Friend, 2, 27);
            AddChatModeChoice("chatmode_cur", ChatChannel.Current, 2, 52);
            AddChatModeChoice("chatmode_talkback", ChatChannel.Reply, 2, 77);
            _chatModeMenu.gameObject.SetActive(false);
        }

        private void AddChatModeChoice(string name, ChatChannel channel, float x, float y)
        {
            ChatModeArt(channel, out var nrm, out var hov, out var psh);
            var b = UIKit.AddSpriteButton(_chatModeMenu, name, RoomUiArt.An(nrm), RoomUiArt.An(hov), RoomUiArt.An(psh), x, y);
            UiHoverSfx.Attach(b, UiSfx.ButtonFloat);
            UiSfx.AttachPress(b, UiSfx.Click);
            b.onClick.AddListener(() => SetChatChannel(channel));
        }

        private void SetChatChannel(ChatChannel channel)
        {
            _chatChannel = channel;
            UpdateChatModeButton();
            UpdateChatListName();
            HideChatModeMenu();
            RebuildRoomChat();
            if (_chatInput != null) _chatInput.ActivateInputField();
        }

        private void UpdateChatModeButton()
        {
            if (_chatModeBtn == null || !(_chatModeBtn.targetGraphic is Image img)) return;
            ChatModeArt(_chatChannel, out var nrm, out var hov, out var psh);
            UIKit.ApplySprite(img, RoomUiArt.An(nrm));
            var st = _chatModeBtn.spriteState;
            st.highlightedSprite = RoomUiArt.An(hov);
            st.pressedSprite = RoomUiArt.An(psh);
            st.selectedSprite = RoomUiArt.An(nrm);
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
                Ctx?.Chat?.SendExpression(id, _chatChannel);
                HideExpressionMenu();
                if (_chatInput != null) _chatInput.ActivateInputField();
            });
            var tip = hit.gameObject.AddComponent<ExpressionTipHandle>();
            tip.Owner = this;
            tip.Command = RoomChatCommand.ExpressionDisplayText(expressionId);
            tip.LocalPos = new Vector2(x, y);
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
            // 密語/進出舞台是私聊/廣播文字，不彈頭上藍泡、不觸發角色動作。
            if (m != null && m.Local && !m.System
                && m.Whisper == WhisperKind.None && m.Stage == StageEventKind.None)
            {
                ShowRoomChatBubble(m);
                PlayRoomChatAction(m);
            }
        }

        private void PlayRoomChatAction(ChatMessage m)
        {
            if (m == null || string.IsNullOrEmpty(m.RoomActionId)) return;
            if (!RoomChatCommand.TryGetRoomAction(m.RoomActionId, out var action) || action == null) return;
            // Gender picks BOTH the motion clip and the SE — same gender the id was parsed with (see MockChatService),
            // so female "再見"→action5→WREST0063+WOMAN_5 while male "88"→action6→MREST0076+MAN_6 stay self-consistent.
            bool male = Ctx != null && Ctx.Session != null && Ctx.Session.Gender == 1;
            string motion = action.MotionFor(male);
            if (!string.IsNullOrEmpty(motion))
            {
                if (_scene != null) _scene.PlayChatAction(motion);
                if (_localHead != null) _localHead.PlayChatAction(motion);   // 上面的頭貼跟著做同一個動作
            }
            UiSfx.Play(action.SoundFor(male));
        }

        private void AddRoomChatLine(ChatMessage m)
        {
            if (_chatContent == null || m == null) return;
            if (!ShouldShowChatMessage(m)) return;

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
            var t = UIKit.AddText(_chatContent, "line", line, 13, Color.white, TextAlignmentOptions.TopLeft, true);
            t.richText = true;
            UIKit.Layout(t.gameObject, 16);
            EnableWhisperNameClicks(t, m);
        }

        // 進出舞台廣播（顏色 #72c1fe）：「X 進入舞台遊戲」/「X 離開舞台」。
        private void AddRoomChatStageLine(ChatMessage m)
        {
            string key = m.Stage == StageEventKind.Enter ? "room.stage_enter" : "room.stage_leave";
            string text = LocalizationManager.Get(key, m.Sender ?? "");
            var t = UIKit.AddText(_chatContent, "stageLine",
                "<color=#" + StageHex + ">" + EscapeTmp(text) + "</color>", 13, Color.white, TextAlignmentOptions.TopLeft, true);
            t.richText = true;
            UIKit.Layout(t.gameObject, 16);
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

            var t = UIKit.AddText(_chatContent, "whisperLine",
                "<color=#" + WhisperHex + ">" + EscapeTmp(ChatDisplay.WhisperText(m)) + "</color>", 13, Color.white, TextAlignmentOptions.TopLeft, true);
            t.richText = true;
            UIKit.Layout(t.gameObject, 16);
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
            var head = UIKit.AddText(row, "head", open + EscapeTmp(headPlain) + "</color>", 13, Color.white,
                TextAlignmentOptions.MidlineLeft, true);
            head.richText = true;
            var headLe = head.gameObject.AddComponent<LayoutElement>();
            headLe.preferredWidth = head.GetPreferredValues(headPlain, 280f, 18f).x + 2f;
            headLe.flexibleWidth = 0f;

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
                var fb = UIKit.AddText(row, "cmd",
                    open + EscapeTmp(RoomChatCommand.ExpressionDisplayText(m.ExpressionId)) + "</color>", 13, Color.white,
                    TextAlignmentOptions.MidlineLeft, true);
                fb.richText = true;
                fb.gameObject.AddComponent<LayoutElement>().flexibleWidth = 0f;
            }

            string trail = (m.Text ?? "").Trim();
            if (trail.Length > 0)
            {
                var after = UIKit.AddText(row, "trail", open + " " + EscapeTmp(trail) + "</color>", 13, Color.white,
                    TextAlignmentOptions.MidlineLeft, true);
                after.richText = true;
                after.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            }
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
        private void AddRoomChatExpressionLine(ChatMessage m)
        {
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

            // 名字白色（原本 #7FB6FF 藍）+ 可點密語（別人才可點）。量寬用純名字，避免 <link> 標記影響。
            string plainLabel = EscapeTmp(m.Sender) + ":";
            string label = WhisperNameLink(m) + ":";
            var name = UIKit.AddText(row, "name", label, 13, Color.white, TextAlignmentOptions.MidlineLeft, true);
            name.richText = true;
            EnableWhisperNameClicks(name, m);
            var nameLe = name.gameObject.AddComponent<LayoutElement>();
            nameLe.preferredWidth = name.GetPreferredValues(plainLabel, 280f, 18f).x + 2f;
            nameLe.flexibleWidth = 0f;

            // 指令前的字：排在名字後、emoji 前（保留輸入時 emoji 的位置：前字〔emoji〕後字）。
            string lead = ExpressionLeadingText(m);
            if (lead.Length > 0)
            {
                var before = UIKit.AddText(row, "lead", EscapeTmp(lead), 13, Color.white,
                    TextAlignmentOptions.MidlineLeft, true);
                var beforeLe = before.gameObject.AddComponent<LayoutElement>();
                beforeLe.flexibleWidth = 0f;
            }

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
            {
                var fallback = UIKit.AddText(row, "cmd", EscapeTmp(RoomChatCommand.ExpressionDisplayText(m.ExpressionId)),
                    13, Color.white, TextAlignmentOptions.MidlineLeft, true);
                var fbLe = fallback.gameObject.AddComponent<LayoutElement>();
                fbLe.flexibleWidth = 0f;
            }

            // 尾隨任意字（中文／英文／數字／標點），舊訊息 Text=/指令 不算尾隨。
            if (HasExpressionTrailingText(m))
            {
                var after = UIKit.AddText(row, "trail", " " + EscapeTmp(m.Text.Trim()), 13, Color.white,
                    TextAlignmentOptions.MidlineLeft, true);
                var afterLe = after.gameObject.AddComponent<LayoutElement>();
                afterLe.flexibleWidth = 1f;
            }
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
            // 密語跨大廳/房間：不受作用域限制，出現在「當前」與「好友」頻道。
            if (m.Whisper != WhisperKind.None)
                return _chatChannel == ChatChannel.Current || _chatChannel == ChatChannel.Friend;
            // 其餘（一般聊天/系統/進出廣播）只顯示本房間，隔離別房與大廳訊息。
            if (m.Scope != ChatScope.Room || m.RoomId != _chatScopeRoomId) return false;
            if (m.System) return true;
            // 進出舞台廣播：只在「當前」分類顯示，其他分類過濾掉。
            if (m.Stage != StageEventKind.None) return _chatChannel == ChatChannel.Current;
            return m.Channel == _chatChannel;
        }

        private void ScrollRoomChatToBottom()
        {
            if (_chatScroll == null) return;
            Canvas.ForceUpdateCanvases();
            _chatScroll.verticalNormalizedPosition = 0f;
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
            // 密語 `[名字] 內容`：只選了對象還沒打內容 → 不送、不清、續打（讓使用者接著打字）。
            bool isWhisper = RoomChatCommand.TryParseWhisper(txt, out var whisperTarget, out var whisperBody);
            if (isWhisper && string.IsNullOrWhiteSpace(whisperBody)) return;
            // bubble 模式送出後續留 bubble-ready(ArmRoomBubbleInput)；輸入框模式送完保持 focus，繼續打下一則不退出。
            bool keepBubbleInput = _chatBubbleTyping || _chatBubbleInputArmed;
            if (_chatBubbleTyping) HideRoomChatBubble();
            else _chatBubbleTyping = false;
            // 密語優先於表情：`[X] /GO` 是把「/GO」密語給 X，不是送表情。
            if (isWhisper)
                Ctx.Chat.SendWhisper(whisperTarget, whisperBody, _chatChannel);
            else if (RoomChatCommand.TryParseExpression(txt, out var expressionId, out var leading, out var trailing))
                Ctx.Chat.SendExpression(expressionId, _chatChannel, leading, trailing);
            else
                Ctx.Chat.Send(txt, _chatChannel);
            HideChatModeMenu();
            HideExpressionMenu();
            _chatInput.text = "";
            if (keepBubbleInput) ArmRoomBubbleInput();
            else { _chatInputSticky = true; FocusRoomChatInput(); }   // 輸入框模式送完保持 focus 續打，不退出
        }

        private void ShowRoomChatBubble(ChatMessage m)
        {
            if (Root == null || m == null) return;
            // 舊泡保留：新泡另開一顆，串起來各自計時。
            if (_chatBubbleTyping) HideRoomChatBubble();

            var bubble = SpawnSentRoomBubble();
            bubble.PendingShow = true;
            bubble.ShownAt = Time.unscaledTime;   // 每顆泡以此為「年齡」起點，各自從肩錨往上飄
            bubble.HideAt = bubble.ShownAt + ChatBubbleLifetime;

            string lead = ExpressionLeadingText(m);
            string trail = HasExpressionTrailingText(m) ? (m.Text ?? "").Trim() : "";
            bool exprInline = m.ExpressionId > 0 && (lead.Length > 0 || trail.Length > 0);   // 表情 + 前/後字
            bool pureEmoji = m.ExpressionId > 0 && !exprInline;                              // 只有表情
            // 泡大小：純表情用固定小泡；表情+字用「前字 + emoji 寬 + 後字」估寬；一般訊息照原本量文字。
            string sizeText = pureEmoji
                ? ""
                : (exprInline ? lead + "　　" + trail : ChatLineText(m));
            int style = pureEmoji ? 1 : RoomBubbleStyleForText(sizeText, bubble.Text);
            ApplySentBubbleStyle(bubble, style, entering: true);
            var enterFrames = RoomBubbleArt.EnterFrames(style);
            bubble.TalkAt = bubble.ShownAt + Mathf.Clamp((enterFrames != null ? enterFrames.Length : 0) / 12f, 0.5f, 1.2f);
            if (string.IsNullOrEmpty(m.RoomActionId)) UiSfx.Play(UiSfx.Bubble);
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
                    bubble.Text.text = EscapeTmp(lead)
                        + "<space=" + ((int)BubbleEmojiGapPx) + ">"
                        + EscapeTmp(trail);
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
                    bubble.Text.text = EscapeTmp(trail);
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
                    bubble.Text.text = EscapeTmp(fb);
                }
            }
            else
            {
                bubble.Expression.gameObject.SetActive(false);
                if (bubble.ExpressionAnim != null) bubble.ExpressionAnim.Frames = null;
                bubble.Text.gameObject.SetActive(true);
                bubble.Text.text = EscapeTmp(ChatLineText(m));
            }

            _sentBubbles.Add(bubble);
            // 防洗版：太舊先踢（仍各自壽命為主）
            while (_sentBubbles.Count > 8)
                DestroySentRoomBubble(_sentBubbles[0]);
        }

        private SentRoomBubble SpawnSentRoomBubble()
        {
            var root = UIKit.NewRect(_bubbleLayer, "RoomChatBubble");   // 掛在 _bubbleLayer(UI 底下)
            root.anchorMin = root.anchorMax = new Vector2(0f, 1f);
            root.pivot = new Vector2(0f, 1f);
            root.sizeDelta = new Vector2(RoomBubbleArt.CanvasW, RoomBubbleArt.CanvasH);

            var bubble = new SentRoomBubble { Root = root };
            var drag = root.gameObject.AddComponent<RoomBubbleDragHandle>();
            drag.Owner = this;
            drag.Sent = bubble;

            bubble.Frame = UIKit.AddImage(root, "Frame", Color.white, raycast: true);
            UIKit.Stretch(bubble.Frame.rectTransform);
            UIKit.ApplySprite(bubble.Frame, RoomBubbleArt.Base(1));
            Place(bubble.Frame.rectTransform, 0, 0, RoomBubbleArt.CanvasW, RoomBubbleArt.CanvasH);
            bubble.FrameAnim = bubble.Frame.gameObject.AddComponent<SpriteSeqAnim>();
            bubble.FrameAnim.Fps = 12f;

            bubble.Add = UIKit.AddImage(root, "AddAni", Color.white);
            UIKit.Stretch(bubble.Add.rectTransform);
            bubble.AddAnim = bubble.Add.gameObject.AddComponent<SpriteSeqAnim>();
            bubble.AddAnim.Fps = 14f;

            bubble.Text = UIKit.AddText(root, "Text", "", 13, ChatBubbleTextColor, TextAlignmentOptions.MidlineLeft, true);
            Place(bubble.Text.rectTransform, 49, 43, 74, 28);
            bubble.Text.richText = true;
            bubble.Text.textWrappingMode = TextWrappingModes.Normal;
            bubble.Text.overflowMode = TextOverflowModes.Overflow;

            bubble.Expression = UIKit.AddImage(root, "Expression", Color.white);
            bubble.Expression.raycastTarget = false;
            bubble.Expression.preserveAspect = true;
            Place(bubble.Expression.rectTransform, 73, 43, 24, 24);
            bubble.ExpressionAnim = bubble.Expression.gameObject.AddComponent<SpriteSeqAnim>();
            bubble.ExpressionAnim.Fps = 8f;

            root.gameObject.SetActive(false);
            return bubble;
        }

        private void ApplySentBubbleStyle(SentRoomBubble bubble, int style, bool entering)
        {
            if (bubble == null || bubble.Root == null) return;
            style = Mathf.Clamp(style, 1, 11);
            bubble.Style = style;
            var frames = entering ? RoomBubbleArt.EnterFrames(style) : null;
            var sprite = frames != null && frames.Length > 0 ? frames[0] : RoomBubbleArt.Base(style);

            bubble.Root.sizeDelta = new Vector2(RoomBubbleArt.CanvasW, RoomBubbleArt.CanvasH);
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
            if (bubble.Root != null) Destroy(bubble.Root.gameObject);
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
            // 注意：空字時就讓 text=""（cc==0），走 UpdateBubbleCaretOverlay 的 rect 置中 fallback。
            // 不要塞空白當佔位——空白字元的 ascender/descender 近乎 0，會把游標釘在框頂(pivot=左上)。
            bool bodyChanged = body != _bubbleBodyFor;
            if (bodyChanged) { _chatBubbleText.text = body; _bubbleBodyFor = body; }
            // 游標字元索引 = 游標前的已上屏字數 + 組字內部游標（原生 IMM32；往回選會往回移，拿不到則落組字串尾端）。
            UpdateBubbleCaretOverlay(caret + ImeCompositionCursor(composition), bodyChanged);
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

            int style = RoomBubbleStyleForText(draft);
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

        private void ArmRoomBubbleInput()
        {
            if (_chatInput == null) return;
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
            _chatCaret.rectTransform.anchoredPosition = new Vector2(2f + w, 0f);
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
            if (!roomTop) { _chatInputSticky = false; return; }   // 切到別畫面(含選歌 overlay)→放掉，回來不自動搶 focus
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
                _roomIdLabel.SetText(room.Id.ToString());              // 1
                // 量實際字寬，左到右自動排版(固定 HeaderGap 間距):不論字長/語言都不會疊、間距一致。
                float lx = ServerX;
                _serverLabel.SetX(lx);  lx += _serverLabel.PreferredWidth + HeaderGap;
                _channelLabel.SetX(lx); lx += _channelLabel.PreferredWidth + HeaderGap;
                _roomIdLabel.SetX(lx);
                _roomNameLabel.text = RoomLabels.DisplayName(room.Name, room.HostName);  // 玩家001的舞蹈室 (或自訂)
            }

            RenderWin2();   // 歌名/模式/場景/CD/難度/BPM/速度/note/組隊/掉落 全部依 session 重畫

            // Head portrait lives in the FIXED top-left frame slot 0 (官方: 頭貼在頭像框裡), rendering the avatar's head
            // doing its live motion (RoomHeadPortrait mirrors the room avatar's walk/idle). Slots 1-5 are empty covers.
            for (int i = 0; i < RoomLayout.SeatCount; i++)
            {
                bool occupied = i == 0;
                if (_slotHead[i] != null)
                {
                    if (occupied && _localHead != null && _localHead.Texture != null)
                    {
                        _slotHead[i].texture = _localHead.Texture;
                        _slotHead[i].enabled = true;
                    }
                    else _slotHead[i].enabled = false;
                }
                if (_slotClose[i] != null) _slotClose[i].enabled = showEmptySeatCovers && !occupied;
                if (_slotMaster[i] != null) _slotMaster[i].enabled = occupied && isHost;
                if (_slotName[i] != null)
                {
                    _slotName[i].gameObject.SetActive(occupied);
                    if (occupied) _slotName[i].text = LocalName(room);
                }
            }
            // a NAME marker floats above the avatar in the room (官方: 人頭上的名字 + ▼), NOT the head portrait.
            if (_floatName != null) { _floatName.SetText(LocalName(room)); _floatName.gameObject.SetActive(true); }

            // host sees Start; guest sees Ready/Cancel (single-player host → Start visible)
            bool localReady = LocalReady(room);
            if (_startBtn != null) _startBtn.gameObject.SetActive(isHost);
            if (_readyBtn != null) _readyBtn.gameObject.SetActive(!isHost && !localReady);
            if (_cancelReadyBtn != null) _cancelReadyBtn.gameObject.SetActive(!isHost && localReady);
            if (_songSelectBtn != null) _songSelectBtn.gameObject.SetActive(isHost);
        }

        // ---- win2 右側面板：依 GameSession 重畫模式/場景/CD/難度/BPM/速度/note/組隊/掉落 ----
        private void RenderWin2()
        {
            var s = Ctx != null ? Ctx.Session : null;
            if (s == null) return;

            // 模式標題（自由模式/普通模式/ShowTime模式）—— 純文字 + 白邊
            if (_modeLabel != null)
                _modeLabel.SetText(L(s.GameMode == 2 ? "songselect.mode_showtime" : s.GameMode == 1 ? "songselect.mode_normal" : "songselect.mode_free"));

            // 場景縮圖：隨機 → RANDOM；具體 → Scene{id+1}（官方縮圖編號是 1-based）
            if (_sceneThumb != null)
            {
                Sprite sc = s.StageRandom
                    ? RoomUiArt.An("randomscene")
                    : (RoomUiArt.An("scene" + (s.StageId + 1)) ?? RoomUiArt.An("scene1"));
                UIKit.ApplySprite(_sceneThumb, sc);
            }

            // CD 光碟依難度換色（Difficult0/1/2）
            if (_diffDisc != null && _diffDiscFrames != null && _diffDiscFrames.Length > 0)
                UIKit.ApplySprite(_diffDisc, _diffDiscFrames[Mathf.Clamp((int)s.Difficulty, 0, _diffDiscFrames.Length - 1)]);

            // 歌名 + 難度 + BPM（從歌曲目錄查；沒選歌就空白）。歌名以 session 為準（離線單機 = 房主選的歌）。
            var entry = s.HasSong ? SongCatalog.Get(s.SongGn) : null;
            if (_songLabel != null)
                _songLabel.SetText(s.HasSong ? (s.SongTitle ?? "") : L("room.no_song"));
            if (_levelLabel != null)
            {
                int lvl = entry != null ? entry.Diff((int)s.Difficulty) : -1;
                _levelLabel.SetText(lvl >= 0 ? lvl.ToString() : "");
            }
            if (_bpmLabel != null)
                _bpmLabel.SetText((entry != null && entry.bpm > 0f) ? Mathf.RoundToInt(entry.bpm).ToString() : "");

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

            // 組隊單選：選到的顯示 pushed 圖，其餘顯示 normal
            for (int i = 0; i < _teamImg.Length; i++)
                if (_teamImg[i] != null) UIKit.ApplySprite(_teamImg[i], s.Team == i ? _teamPushed[i] : _teamNormal[i]);

            // 掉落方式的值由 SdoComboBox 自己維護（onPick → session.DropDirection）；此處不需重畫。
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

            // ESC → 退回選角色頁面（房間的上一層）。只在房間為當前畫面、非聊天輸入中、且無 modal(商城/儲物櫃/設定)疊層、
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

            HandleRoomBlankChatClick();
            HandleRoomChatTypingKeys();
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
            // panel every slot shows the head (to check alignment); normally only slot 0 (the local player) does.
            Texture headTex = _localHead != null ? _localHead.Texture : null;
            for (int i = 0; i < RoomLayout.SeatCount; i++)
            {
                if (_slotHead[i] == null) continue;
                var rt = _slotHead[i].rectTransform;
                rt.anchoredPosition = new Vector2(RoomLayout.HeadSlotX[i] + headSlotOffset.x, -(RoomLayout.HeadSlotY + headSlotOffset.y));
                rt.sizeDelta = headSlotSize;
                bool occ = (i == 0) || _debugOpen;
                if (occ && headTex != null) { _slotHead[i].texture = headTex; _slotHead[i].enabled = true; if (_slotClose[i] != null) _slotClose[i].enabled = false; }
                else { _slotHead[i].enabled = false; if (_slotClose[i] != null) _slotClose[i].enabled = showEmptySeatCovers; }
            }

            UpdateRoomChatBubble();
            UpdateSentRoomBubbles();

            if (_scene.TryHeadViewport(out var vp))
            {
                if (_floatName != null && _floatName.gameObject.activeSelf)
                    PlaceFollow(_floatName.Rect, vp, -8f);                      // name sits just ABOVE the avatar's head
            }

            bool needBubbleAnchor = _sentBubbles.Count > 0
                || (_chatBubbleRoot != null && (_chatBubbleRoot.gameObject.activeSelf || _chatBubblePendingShow));
            if (needBubbleAnchor)
            {
                if (_scene.TryChatBubbleViewport(out var bubbleVp))
                    PlaceRoomChatBubbles(bubbleVp);
                else if (_scene.TryHeadViewport(out var fallbackVp))
                    PlaceRoomChatBubbles(fallbackVp);
            }
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

        private void PlaceRoomChatBubbles(Vector2 vp)
        {
            float anchorTop = (1f - vp.y) * 600f;
            float visibleLeft = vp.x * 800f + ChatBubbleAnchorVisibleLeft;
            float visibleTop = anchorTop + ChatBubbleAnchorVisibleTop;

            bool typingVisible = _chatBubbleRoot != null
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

            var nodes = new List<RoomBubbleLayoutNode>(_sentBubbles.Count);
            for (int i = _sentBubbles.Count - 1; i >= 0; i--)
            {
                var b = _sentBubbles[i];
                if (b == null || b.Root == null) continue;
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
                    node.Root.anchoredPosition = node.Position;

                if (node.Sent != null)
                {
                    node.Sent.PhysicsPos = node.Position;
                    node.Sent.PhysicsVel = node.Velocity;
                    node.Sent.HasPhysics = node.HasPhysics;
                    if (node.Sent.PendingShow)
                    {
                        node.Sent.Root.gameObject.SetActive(true);
                        node.Sent.PendingShow = false;
                        LayoutSentBubbleInlineEmoji(node.Sent);   // 活化後才有 mesh，這時把行內 emoji 疊到打的位置
                    }
                }
            }
        }


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
            public RectTransform Root;
            public Image Frame, Add, Expression;
            public TextMeshProUGUI Text;
            public SpriteSeqAnim FrameAnim, AddAnim, ExpressionAnim;
            public int Style = 1;
            public float ShownAt, HideAt, TalkAt;
            public bool PendingShow;
            public bool HasPhysics;
            public Vector2 PhysicsPos, PhysicsVel;
            public int EmojiInlineLeadLen = -1;   // >=0：泡活化後把 Expression 疊到 Text 第 leadLen 個字之後；-1=不做
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

        private void HandleRoomBlankChatClick()
        {
            if (_scene == null || _chatInput == null || !Input.GetMouseButtonDown(0)) return;
            if (Ctx != null && Ctx.Flow != null && Ctx.Flow.Current != ScreenId.Room) return;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
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

        // 點聊天列的人名 → 把 `[名字] ` 塞進輸入框，切成輸入框打字模式，保留已打的內容。
        private void InsertWhisperTarget(string name)
        {
            if (_chatInput == null || string.IsNullOrWhiteSpace(name)) return;
            if (Ctx != null && Ctx.Flow != null && Ctx.Flow.Current != ScreenId.Room) return;

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

        private string LocalName(RoomInfo room)
        {
            if (room == null) return "";
            foreach (var s in room.Seats)
                if (!s.IsEmpty && s.Player.Id == Ctx.Session.LocalPlayerId) return s.Player.DisplayName;
            return Ctx.Session != null ? Ctx.Session.LocalPlayerName : "";
        }

        private bool LocalReady(RoomInfo room)
        {
            if (room == null) return false;
            foreach (var s in room.Seats)
                if (!s.IsEmpty && s.Player.Id == Ctx.Session.LocalPlayerId) return s.IsReady;
            return false;
        }

        private void OnReadyToggle()
        {
            var room = Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
            if (room == null) return;
            Ctx.Rooms.SetReady(!LocalReady(room));
        }

        private void OnStart()
        {
            if (_starting) return;   // 已在漸暗切場中，忽略重複按
            if (Ctx.Rooms == null || !Ctx.Rooms.CanStart())
            {
                var room = Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
                Toast.Show(L(room != null && string.IsNullOrEmpty(room.SongTitle) ? "room.need_song" : "room.waiting_players"));
                return;
            }
            _starting = true;
            _returnedFromStage = true;         // 記住:待會回房的那次 OnShow 不再廣播「進入舞台遊戲」
            Ctx.Chat?.Clear();                 // 換場地就清訊息欄：房間→遊戲時清空
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

        private void OnLeave()
        {
            AnnounceStagePresence(false);   // 廣播「X 離開舞台」（趁還在房間、名字還查得到）
            Ctx.Rooms?.LeaveRoom();
            // 回男女選擇：漸黑 → loading → 漸亮（同其它畫面進出效果）。切畫面(GoTo)在全黑時執行；
            // 男女選擇畫面無四邊滑入 UI → 不傳 onReveal。
            ScreenTransition.Run(() => GoTo(ScreenId.GenderSel));
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

        /// <summary>win(Win1/Win2/Win3) → 對應的收合容器；其他一律回 Root。</summary>
        private RectTransform WinRoot(Vector2 win)
            => win == Win1 ? _win1Root : win == Win2 ? _win2Root : win == Win3 ? _win3Root : Root;

        private Image Art(string an, Vector2 win, float x, float y, string name)
            => UIKit.AddSprite(WinRoot(win), name, RoomUiArt.An(an), win.x + x, win.y + y);

        // win2 文字定位：把線上 DDRROOM.XML 子座標 (x,y) 換成絕對畫布座標（相對 Win2 視窗原點）
        private static void PlaceW2(RectTransform rt, float x, float y, float w, float h)
            => Place(rt, Win2.x + x, Win2.y + y, w, h);

        // win2 難度/BPM 數字（淡紫粗體置中 + 白邊；座標 = Win2 + (x,y)）
        private OutlinedLabel MakeInfoNum(string name, float x, float y)
            => OutlinedLabel.Create(_win2Root, name, Win2.x + x, Win2.y + y, 21, 14, 12, InfoValueColor, Color.white, Win2EdgePx, true);

        // win2 難度/BPM 字幕（線上框沒烘這兩個字 → 自己畫；白邊；粗體；座標 = Win2 + (x,y)）
        private void MakeCaption(string name, string text, float x, float y)
            => OutlinedLabel.Create(_win2Root, name, Win2.x + x, Win2.y + y, 21, 14, 12, SongNameColor, Color.white, Win2EdgePx, true).SetText(text);

        // 組隊單選格：normal/pushed 兩態，點了把 GameSession.Team 設成 idx 並重畫（座標 = Win2 + (x,y)）
        private void BuildTeamToggle(int idx, string normalAn, string pushedAn, float x, float y)
        {
            _teamNormal[idx] = RoomUiArt.An(normalAn);
            _teamPushed[idx] = RoomUiArt.An(pushedAn);
            var img = UIKit.AddSprite(_win2Root, "Team" + idx, _teamNormal[idx], Win2.x + x, Win2.y + y, raycast: true);
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.None;
            UiSfx.AttachPress(btn, UiSfx.Click);   // 按下 SE_0001；win2 中間設定塊滑過不出聲 → 不掛 hover
            int i = idx;
            btn.onClick.AddListener(() => { Ctx.Session.Team = i; RenderWin2(); });
            _teamImg[idx] = img;
        }

        // 所有房間按鈕統一：滑過 Buttonfloat(圖1/圖2)、按下 SE_0001(圖3 預設)。少數例外用參數覆寫：
        //   pressSfx：房主設置→Buttonfloat；開始→null(由 OnStart 播 Start 音 + 漸暗)。
        //   hoverSfx：win2 中間設定塊(速度/note/組隊/掉落)→null(滑過不出聲)，其餘保留 Buttonfloat。
        private Button Btn(string objName, string nrm, string hov, string psh, Vector2 win, float x, float y,
            System.Action onClick, string pressSfx = UiSfx.Click, string hoverSfx = UiSfx.ButtonFloat)
        {
            var b = UIKit.AddSpriteButton(WinRoot(win), objName, RoomUiArt.An(nrm), RoomUiArt.An(hov), RoomUiArt.An(psh), win.x + x, win.y + y);
            if (hoverSfx != null) UiHoverSfx.Attach(b, hoverSfx);
            UiSfx.AttachPress(b, pressSfx);
            if (onClick != null) b.onClick.AddListener(() => onClick());
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
