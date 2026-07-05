using System.Collections;
using TMPro;
using UnityEngine;
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

        // DDRROOM window resting targets (TransForm targetx/targety); win2 子座標逐字取自「線上」DDRROOM.XML
        // (閉撰敃氪 那套，RoomUiArt 實際載入的就是這套；它的 win2 target=(649,177)，子座標直接相對 Win2，不要再加 offset)。
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

        private RawImage _backdrop;
        private readonly RawImage[] _slotHead = new RawImage[RoomLayout.SeatCount];
        private readonly Image[] _slotClose = new Image[RoomLayout.SeatCount];
        private readonly Image[] _slotMaster = new Image[RoomLayout.SeatCount];
        private readonly TextMeshProUGUI[] _slotName = new TextMeshProUGUI[RoomLayout.SeatCount];
        private OutlinedLabel _serverLabel, _channelLabel, _roomIdLabel;   // 白字 + 藍邊 (rgb 70,74,152)
        private TextMeshProUGUI _roomNameLabel;
        private OutlinedLabel _songLabel;   // 歌名(白邊)
        private OutlinedLabel _floatName;       // name marker that floats above the avatar in the room (官方頭上名字)；字 rgb(250,252,214) 描黑邊
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

        // Head-portrait placement: a SHARED offset from the DDRROOM AvatarView base coords + a size, applied to ALL 6
        // head slots. Tune LIVE via the F2 panel (sliders + borders + all 6 heads shown). Default y+13 centres the face
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
            Btn("roomexchange", "BtnHeadExchange_1", "BtnHeadExchange_2", "BtnHeadExchange_3", Win1, 652, 5, null);
            Btn("invite", "BtnHeadInvite_1", "BtnHeadInvite_2", "BtnHeadInvite_3", Win1, 688, 5, null);
            Btn("setting", "BtnHeadOption_1", "BtnHeadOption_2", "BtnHeadOption_3", Win1, 724, 5, () => Nav.OpenSettings?.Invoke());
            Btn("leaveroom", "BtnHeadReturn_1", "BtnHeadReturn_2", "BtnHeadReturn_3", Win1, 760, 5, OnLeave);

            // 左上角所在位置：自由練習場 / 頻道 / 房號 (DDRROOM servername/channelnum/roomid) — 白字 + 藍邊(70,74,152) 粗體。
            // 藍邊用 OutlinedLabel(位移複製)畫，不用 TMP SDF 材質描邊(那條在執行期動態 CJK 字型上畫不出來)。
            // 三欄都左對齊;初始 x 不重要，Render() 會量實際字寬後左到右排版(ServerX 起、欄間 HeaderGap)。
            const float align_y = 11f, align_h = 18f;
            _serverLabel  = OutlinedLabel.Create(_win1Root, "ServerName", ServerX, align_y, 120, align_h, HeaderFontSz, Color.white, LeftEdge, HeaderEdgePx, true, TextAlignmentOptions.Left);
            _channelLabel = OutlinedLabel.Create(_win1Root, "ChannelNum", ServerX, align_y, 60, align_h, HeaderFontSz, Color.white, LeftEdge, HeaderEdgePx, true, TextAlignmentOptions.Left);
            _roomIdLabel  = OutlinedLabel.Create(_win1Root, "RoomId", ServerX, align_y, 30, align_h, HeaderFontSz, Color.white, LeftEdge, HeaderEdgePx, true, TextAlignmentOptions.Left);
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
            _songLabel = OutlinedLabel.Create(_win2Root, "SongName", Win2.x + 12, Win2.y + 128, 112, 20, 12, SongNameColor, Color.white, Win2EdgePx, false);

            // 速度 ◄ 值 ►（檔位清單與預設來自 config.ini，可改）
            _speedLabel = UIKit.AddText(_win2Root, "SpeedValue", "", 13, SpeedColor, TextAlignmentOptions.Center);
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
                i => Ctx.Session.DropDirection = i, expandDown: true, listX: Win2.x + 70, listWidth: 38f,
                valueOffsetY: 2f);   // 只把「向上/向下」值往上 2px，▼ 鈕位置不動
            // 掉落方式 ▼ 開關鈕按下 → SE_0001（清單列本來就有；此為開關鈕本身。中間設定塊仍不掛滑過音）。
            UiSfx.AttachClick(_dropCombo.GetComponent<Button>());

            // 房主設置（= 選歌入口）。線上原版 BtnRoomMaster_1/2/3。按下音效改 Buttonfloat（非預設 SE_0001）。
            _songSelectBtn = Btn("songselect", "BtnRoomMaster_1", "BtnRoomMaster_2", "BtnRoomMaster_3", Win2, 14, 296, () => GoTo(ScreenId.SongSelect), UiSfx.ButtonFloat);

            // 註：官方 WinMoveUpHelp(moveuphelp0.an) 其實是一張「黃底問號」的方向鍵提示圖，靜態擺在面板左上角就變成
            // 使用者看到的那顆問號 → 依需求移除（要做方向鍵提示應改成floating動畫貼在 3D 場景，不放面板裡）。

            // 4) win3 — bottom chat bar:官方 DDRROOM win3 一整排功能鈕(座標/圖名逐字取自 XML),目前都是裝飾(onClick=null)。
            Art("Room0", Win3, 8, 37, "Win3Panel");
            Btn("chatmode", "Room4", "Room5", "Room6", Win3, 17, 88, null);                                   // 聊天模式
            var chatEdit = Art("EditBlank", Win3, 72, 92, "ChatEdit");   // 聊天輸入框(無 EditBlank 圖 → 透明佔位)
            if (chatEdit != null) chatEdit.color = new Color(1f, 1f, 1f, 0f);
            Btn("OpenRecord", "OpenRecord_a", "OpenRecord_b", "OpenRecord_c", Win3, 279, 82, null);           // 錄製
            Btn("expression1", "BtnExpression_1", "BtnExpression_2", "BtnExpression_3", Win3, 311, 82, null); // 表情
            Btn("ChatSendButton", "BtnSpeaker_1", "BtnSpeaker_2", "BtnSpeaker_3", Win3, 343, 82, null);       // 喇叭/送出
            Btn("LoudSpeaker", "LoudSpeaker_1", "LoudSpeaker_2", "LoudSpeaker_3", Win3, 376, 82, null);       // 大聲公
            Btn("RoomPet", "BtnPet_1", "BtnPet_2", "BtnPet_3", Win3, 411, 83, null);                         // 寵物
            Btn("WingButton", "RoomWing", "RoomWing1", "RoomWing", Win3, 447, 82, null);                     // 翅膀
            Btn("ClosetButton", "RoomCloset001", "RoomCloset002", "RoomCloset003", Win3, 480, 81, null);     // 衣櫥
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

        /// <summary>把三個面板容器依 _collapseT(0..1) 補到收合位移（SmoothStep 緩動）。</summary>
        private void ApplyCollapse()
        {
            float e = Mathf.SmoothStep(0f, 1f, _collapseT);
            if (_win1Root != null) _win1Root.anchoredPosition = Win1Hidden * e;
            if (_win2Root != null) _win2Root.anchoredPosition = Win2Hidden * e;
            if (_win3Root != null) _win3Root.anchoredPosition = Win3Hidden * e;
        }

        // ---- lifecycle: spawn / tear down the 3D room ----

        public override void OnShow()
        {
            if (!_subscribed)
            {
                if (Ctx.Rooms != null) Ctx.Rooms.RoomUpdated += OnRoomUpdated;
                LocalizationManager.LanguageChanged += Render;   // 切語言時，房號/房名/位置標示即時重譯
                _subscribed = true;
            }

            if (_scene == null)
            {
                var sceneGo = new GameObject("RoomScene3D");
                _scene = sceneGo.AddComponent<RoomScene3D>();
                _scene.Build();
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
                _localHead.Init();
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

            // 常駐單例：清掉上次「開始」漸暗殘留的黑幕，回房間才不會整片黑。
            _starting = false;
            if (_startFade != null) { _startFade.gameObject.SetActive(false); _startFade.color = new Color(0f, 0f, 0f, 0f); }

            ResetCollapse();             // 每次進場都從「完全展開」開始（常駐單例，避免上次收合狀態殘留）
            SeedDefaultSongIfNeeded();   // 進大廳預設選好 index 最大的歌(easy)，房間一進來就有歌
            Render();
        }

        // 進房間時，若還沒選過歌就預設選「index(fileId) 最大的那首」easy。玩家之後自己選歌就蓋過去（HasSong 守門只做一次）。
        private void SeedDefaultSongIfNeeded()
        {
            var s = Ctx != null ? Ctx.Session : null;
            if (s == null || s.HasSong) return;
            var model = SongListModel.FromCatalog();          // 已按 fileId 由大到小排序
            if (model.All.Count == 0) return;
            var e = model.All[0];                              // [0] = index 最大的歌
            s.SongGn = e.gn;
            s.SongFileId = e.fileId;
            s.SongTitle = e.title ?? e.gn;
            s.SongArtist = e.artist;
            s.Difficulty = Difficulty.Easy;
            Ctx.Rooms?.SetSong(s.SongTitle);                  // 同步房間顯示（單機=房主）
        }

        public override void OnHide()
        {
            // NOTE: 進選歌時房間「不會」走到這裡 —— 選歌是疊在房間上的 overlay，房間仍是 visible（見 FrontendApp.ShowOnly）。
            // OnHide 只在真正離開房間時觸發（回大廳 / 進遊戲），所以在這裡完整拆除 3D 場景是正確的。
            if (_subscribed)
            {
                if (Ctx.Rooms != null) Ctx.Rooms.RoomUpdated -= OnRoomUpdated;
                LocalizationManager.LanguageChanged -= Render;
                _subscribed = false;
            }
            if (_maskedCam != null) { _maskedCam.cullingMask = _savedMask; _maskedCam = null; }
            if (_backdrop != null) { _backdrop.texture = null; _backdrop.color = Color.black; }
            for (int i = 0; i < _slotHead.Length; i++) if (_slotHead[i] != null) { _slotHead[i].texture = null; _slotHead[i].enabled = false; }
            if (_localHead != null) { Destroy(_localHead.gameObject); _localHead = null; }
            if (_scene != null) { Destroy(_scene.gameObject); _scene = null; }
        }

        private void OnRoomUpdated(int id) => Render();

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
            RenderWin2();
        }

        private void StepNote(int d)
        {
            int n = NoteEftArt.Length + 1;                 // +1 = 隨機
            int cur = ((Ctx.Session.NoteType + 1 + d) % n + n) % n;   // 內部索引：0=隨機, 1..n=指定+1
            Ctx.Session.NoteType = cur - 1;
            RenderWin2();
        }

        // Make the local head portrait FOLLOW the avatar: each frame project the avatar's head through the scene camera
        // and place the floating head (+ name) there (EXE Player_ComputeHeadRect: the looker's head portrait tracks the
        // projected Bip01_Head). Runs only while the room is mounted (_scene != null, cleared on OnHide).
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F2)) _debugOpen = !_debugOpen;

            // UI 收合/展開補間（官方 uihide/uidisplay 面板滑動）。與 3D 掛載無關，永遠推進到目標狀態。
            float ct = _uiCollapsed ? 1f : 0f;
            if (!Mathf.Approximately(_collapseT, ct))
            {
                _collapseT = Mathf.MoveTowards(_collapseT, ct, Time.unscaledDeltaTime * CollapseSpeed);
                ApplyCollapse();
            }

            if (_scene == null) return;

            // 房間仍在選歌畫面底下即時 render，但選歌疊在上面時要凍結走動（否則方向鍵會把底下的角色走來走去）。
            // 只有房間是最上層(當前畫面)時才收方向鍵。
            _scene.InputEnabled = Ctx == null || Ctx.Flow == null || Ctx.Flow.Current == ScreenId.Room;

            // place ALL 6 head slots from the shared offset+size (base = DDRROOM AvatarView coords). In the F2 debug
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

            if (_floatName != null && _floatName.gameObject.activeSelf && _scene.TryHeadViewport(out var vp))
                PlaceFollow(_floatName.Rect, vp, -8f);                      // name sits just ABOVE the avatar's head
        }

        // F2 tuning panel: sliders for the shared head-slot offset/size + a green border around each of the 6 slots
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
            Ctx.Rooms?.LeaveRoom();
            GoTo(ScreenId.Lobby);
        }

        // ---- art placement helpers (XML window-target + child offset → top-left pixel) ----

        /// <summary>Blue text edge on the location labels — rgb(70,74,152), per the official 白字藍邊 look.</summary>
        private static readonly Color32 LeftEdge = new Color32(70, 74, 152, 255);

        /// <summary>How thick (canvas px) the blue edge on 自由練習場/頻道/房號 is. Bump it for a heavier stroke.</summary>
        private const float HeaderEdgePx = 1.2f;

        // 左上位置標示(自由練習場 / 頻道 / 房號):左對齊,Render() 量實際字寬後左到右自動排版。
        private const float ServerX = 19f;       // 起始左緣(紫框左邊)
        private const float HeaderGap = 23f;     // 欄與欄的固定間距(px);調大=更開、調小=更擠
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

        // win2 難度/BPM 字幕（線上框沒烘這兩個字 → 自己畫；白邊；座標 = Win2 + (x,y)）
        private void MakeCaption(string name, string text, float x, float y)
            => OutlinedLabel.Create(_win2Root, name, Win2.x + x, Win2.y + y, 21, 14, 12, SongNameColor, Color.white, Win2EdgePx, false).SetText(text);

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
