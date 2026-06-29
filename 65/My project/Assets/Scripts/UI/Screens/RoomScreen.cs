using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.Localization;
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

        // DDRROOM window resting targets (TransForm targetx/targety); child coords are relative to these.
        private static readonly Vector2 Win1 = new Vector2(0f, 1f);     // top head panel
        private static readonly Vector2 Win2 = new Vector2(649f, 177f); // right song/scene/mode panel
        private static readonly Vector2 Win3 = new Vector2(0f, 481f);   // bottom chat + ready/start bar
        private const int HeadLayer = 11;

        private RawImage _backdrop;
        private readonly RawImage[] _slotHead = new RawImage[RoomLayout.SeatCount];
        private readonly Image[] _slotClose = new Image[RoomLayout.SeatCount];
        private readonly Image[] _slotMaster = new Image[RoomLayout.SeatCount];
        private readonly TextMeshProUGUI[] _slotName = new TextMeshProUGUI[RoomLayout.SeatCount];
        private OutlinedLabel _serverLabel, _channelLabel, _roomIdLabel;   // 白字 + 藍邊 (rgb 70,74,152)
        private TextMeshProUGUI _roomNameLabel, _songLabel;
        private OutlinedLabel _floatName;       // name marker that floats above the avatar in the room (官方頭上名字)；字 rgb(250,252,214) 描黑邊
        private Button _songSelectBtn, _startBtn, _readyBtn, _cancelReadyBtn;

        private RoomScene3D _scene;
        private RoomHeadPortrait _localHead;
        private Camera _maskedCam; private int _savedMask;
        private bool _subscribed;

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
                _slotName[i] = UIKit.AddText(Root, "Name" + i, "", 13, Color.white, TextAlignmentOptions.Center);
                Place(_slotName[i].rectTransform, nameX[i] + Win1.x, 141, RoomLayout.HeadSlotW, 18);
                _slotName[i].gameObject.SetActive(false);
            }

            // head-bar buttons (win1)
            Btn("changeroomname", "Room45", "Room46", "Room47", Win1, 461, 7, () => Toast.Show(L("room.title")));
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
            _serverLabel  = OutlinedLabel.Create(Root, "ServerName", ServerX, align_y, 120, align_h, HeaderFontSz, Color.white, LeftEdge, HeaderEdgePx, true, TextAlignmentOptions.Left);
            _channelLabel = OutlinedLabel.Create(Root, "ChannelNum", ServerX, align_y, 60, align_h, HeaderFontSz, Color.white, LeftEdge, HeaderEdgePx, true, TextAlignmentOptions.Left);
            _roomIdLabel  = OutlinedLabel.Create(Root, "RoomId", ServerX, align_y, 30, align_h, HeaderFontSz, Color.white, LeftEdge, HeaderEdgePx, true, TextAlignmentOptions.Left);
            // 中央房名 (DDRROOM roomname) — 粗體白字(無描邊)，文字內容由 RoomLabels.DisplayName 決定。
            _roomNameLabel = UIKit.AddText(Root, "RoomName", "", 12, Color.white, TextAlignmentOptions.Center);
            _roomNameLabel.fontStyle = FontStyles.Bold;
            Place(_roomNameLabel.rectTransform, 239 + Win1.x, 8, 188, 18);

            // 3) win2 — right song/scene/mode panel
            Art("Room72", Win2, -3, -5, "Win2Panel");
            Art("scene1", Win2, 7, 28, "SceneThumb");        // selected-stage thumbnail (placeholder default)
            Art("Difficult", Win2, 7, 109, "SongLevel");
            _songLabel = UIKit.AddText(Root, "SongName", "", 12, new Color32(0x83, 0x5c, 0xe1, 0xff), TextAlignmentOptions.Center);
            Place(_songLabel.rectTransform, 12 + Win2.x, 128, 112, 20);
            // mode-name slot (mode label sits at win2 WinRoomModeSdo (8,-4))
            // team toggles (decorative in single-player)
            Art2("Room33", Win2, 69, 207, "TeamA");
            Art2("Room36", Win2, 96, 206, "TeamB");
            _songSelectBtn = Btn("songselect", "BtnRoomMaster_1", "BtnRoomMaster_2", "BtnRoomMaster_3", Win2, 14, 296, () => GoTo(ScreenId.SongSelect));
            // arrow-key walk hint (WinMoveUpHelp rests at win2+(40,0); ButtonMoveUpHelp child (-25,1))
            Art("moveuphelp0", Win2, 40 + (-25), 0 + 1, "MoveUpHelp");

            // 4) win3 — bottom chat bar + ready/start
            Art("Room0", Win3, 8, 37, "Win3Panel");
            Btn("chatmode", "Room4", "Room5", "Room6", Win3, 17, 88, null);
            Btn("expression1", "BtnExpression_1", "BtnExpression_2", "BtnExpression_3", Win3, 311, 82, null);
            Btn("ChatSendButton", "BtnSpeaker_1", "BtnSpeaker_2", "BtnSpeaker_3", Win3, 343, 82, null);
            Btn("RoomPet", "BtnPet_1", "BtnPet_2", "BtnPet_3", Win3, 411, 83, null);
            Btn("tools", "Room55", "Room56", "Room57", Win3, 584, 85, null);
            Btn("look", "BtnLook_1", "BtnLook_2", "BtnLook_3", Win3, 651, 60, null);
            Btn("play", "Room92", "Room93", "Room94", Win3, 649, 59, null);
            var chatEdit = Art("EditBlank", Win3, 72, 92, "ChatEdit");   // (no EditBlank art → invisible placeholder strip)
            if (chatEdit != null) chatEdit.color = new Color(1f, 1f, 1f, 0f);

            _startBtn = Btn("start", "Room15", "Room16", "Room17", Win3, 706, 43, OnStart);
            _readyBtn = Btn("ready", "Room12", "Room13", "Room14", Win3, 706, 43, OnReadyToggle);
            _cancelReadyBtn = Btn("cancel_ready", "c_ready0", "c_ready1", "c_ready2", Win3, 706, 43, OnReadyToggle);
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

            Render();
        }

        public override void OnHide()
        {
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
                _songLabel.text = string.IsNullOrEmpty(room.SongTitle) ? L("room.no_song") : room.SongTitle;
            }

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

        // Make the local head portrait FOLLOW the avatar: each frame project the avatar's head through the scene camera
        // and place the floating head (+ name) there (EXE Player_ComputeHeadRect: the looker's head portrait tracks the
        // projected Bip01_Head). Runs only while the room is mounted (_scene != null, cleared on OnHide).
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F2)) _debugOpen = !_debugOpen;
            if (_scene == null) return;

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
            if (Ctx.Rooms == null || !Ctx.Rooms.CanStart())
            {
                var room = Ctx.Rooms != null ? Ctx.Rooms.CurrentRoom : null;
                Toast.Show(L(room != null && string.IsNullOrEmpty(room.SongTitle) ? "room.need_song" : "room.waiting_players"));
                return;
            }
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
        private const float HeaderGap = 15f;     // 欄與欄的固定間距(px);調大=更開、調小=更擠
        private const float HeaderFontSz = 14f;  // 字級(這串比官方「新手一区」長,比 14 小一點才連間距一起塞進紫框)

        /// <summary>頭上漂浮名字的黑邊厚度(canvas px)。字色/粗體跟遊戲內頭頂名字共用 <see cref="Sdo.Game.TextStyles.FaceCream"/>。</summary>
        private const float HeadNameEdgePx = 1.4f;

        private Image Art(string an, Vector2 win, float x, float y, string name)
            => UIKit.AddSprite(Root, name, RoomUiArt.An(an), win.x + x, win.y + y);

        // raycast-enabled decorative sprite (e.g. team toggles) — same as Art but flagged interactive-looking
        private Image Art2(string an, Vector2 win, float x, float y, string name)
            => UIKit.AddSprite(Root, name, RoomUiArt.An(an), win.x + x, win.y + y);

        private Button Btn(string objName, string nrm, string hov, string psh, Vector2 win, float x, float y, System.Action onClick)
        {
            var b = UIKit.AddSpriteButton(Root, objName, RoomUiArt.An(nrm), RoomUiArt.An(hov), RoomUiArt.An(psh), win.x + x, win.y + y);
            if (onClick != null) b.onClick.AddListener(() => onClick());
            return b;
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
