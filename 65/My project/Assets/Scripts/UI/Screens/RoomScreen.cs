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
        private TextMeshProUGUI _modeLabel, _roomIdLabel, _roomNameLabel, _songLabel;
        private Button _songSelectBtn, _startBtn, _readyBtn, _cancelReadyBtn;

        private RoomScene3D _scene;
        private RoomHeadPortrait _localHead;
        private Camera _maskedCam; private int _savedMask;
        private bool _subscribed;

        /// <summary>If the room renders upside-down on a given platform, flip the backdrop V (RT vertical convention).</summary>
        public bool flipBackdropV = false;

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

            _modeLabel = UIKit.AddText(Root, "GameMode", "", 16, new Color32(0x52, 0xC0, 0x49, 0xff), TextAlignmentOptions.Center);
            Place(_modeLabel.rectTransform, 33 + Win1.x, 5, 54, 30);
            _roomIdLabel = UIKit.AddText(Root, "RoomId", "1", 13, Color.white, TextAlignmentOptions.Center);
            Place(_roomIdLabel.rectTransform, 170 + Win1.x, 11, 36, 18);
            _roomNameLabel = UIKit.AddText(Root, "RoomName", "", 12, Color.white, TextAlignmentOptions.Center);
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
            if (!_subscribed && Ctx.Rooms != null) { Ctx.Rooms.RoomUpdated += OnRoomUpdated; _subscribed = true; }

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
            if (_subscribed && Ctx.Rooms != null) { Ctx.Rooms.RoomUpdated -= OnRoomUpdated; _subscribed = false; }
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
                _modeLabel.text = L(room.Mode == GameMode.Free ? "mode.free" : "mode.normal");
                _roomIdLabel.text = room.Id.ToString("000");
                _roomNameLabel.text = L("room.title").Replace("{0}", room.Id.ToString("000"));
                _songLabel.text = string.IsNullOrEmpty(room.SongTitle) ? L("room.no_song") : room.SongTitle;
            }

            // local player occupies slot 0 (host); other slots are the empty close cover (single-player offline).
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
                if (_slotClose[i] != null) _slotClose[i].enabled = !occupied;
                if (_slotMaster[i] != null) _slotMaster[i].enabled = occupied && isHost;
                if (_slotName[i] != null)
                {
                    _slotName[i].gameObject.SetActive(occupied);
                    if (occupied) _slotName[i].text = LocalName(room);
                }
            }

            // host sees Start; guest sees Ready/Cancel (single-player host → Start visible)
            bool localReady = LocalReady(room);
            if (_startBtn != null) _startBtn.gameObject.SetActive(isHost);
            if (_readyBtn != null) _readyBtn.gameObject.SetActive(!isHost && !localReady);
            if (_cancelReadyBtn != null) _cancelReadyBtn.gameObject.SetActive(!isHost && localReady);
            if (_songSelectBtn != null) _songSelectBtn.gameObject.SetActive(isHost);
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
