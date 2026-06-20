using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Sdo.Localization;
using Sdo.UI.Core;
using Sdo.UI.Services;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>房間：座位格 + 模式 + 選歌 + 準備/開始（開始為 stub）+ 離開。</summary>
    public sealed class RoomScreen : UIScreenBase
    {
        public override ScreenId Id => ScreenId.Room;

        private TextMeshProUGUI _title;
        private RectTransform _seatGrid;
        private Cycler _modeCycler;
        private TextMeshProUGUI _songLabel;
        private Button _selectSongBtn;
        private Button _readyBtn;
        private TextMeshProUGUI _readyLabel;
        private Button _startBtn;
        private bool _subscribed;

        private static string L(string k) => LocalizationManager.Get(k);

        protected override void BuildUI()
        {
            UIKit.AddImage(Root, "Bg", UITheme.Bg);

            // header
            var header = UIKit.AddImage(Root, "Header", UITheme.Header).rectTransform;
            UIKit.Anchor(header, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
            header.sizeDelta = new Vector2(0, 56); header.anchoredPosition = Vector2.zero;

            _title = UIKit.AddText(header, "Title", "", 22, UITheme.Text, TextAlignmentOptions.Left);
            UIKit.Anchor(_title.rectTransform, new Vector2(0, 0), new Vector2(0.5f, 1), new Vector2(0, 0.5f));
            _title.rectTransform.offsetMin = new Vector2(18, 0);

            var leave = UIKit.AddLocButton(header, "LeaveBtn", "room.leave", UITheme.Danger, UITheme.OnPrimary, 15);
            var lrt = leave.GetComponent<RectTransform>();
            UIKit.Anchor(lrt, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f));
            lrt.sizeDelta = new Vector2(120, 34); lrt.anchoredPosition = new Vector2(-12, 0);
            leave.onClick.AddListener(OnLeave);

            // seat grid panel
            var seatPanel = UIKit.AddImage(Root, "SeatPanel", UITheme.Panel).rectTransform;
            seatPanel.anchorMin = new Vector2(0, 1); seatPanel.anchorMax = new Vector2(1, 1); seatPanel.pivot = new Vector2(0.5f, 1);
            seatPanel.sizeDelta = new Vector2(-24, 300); seatPanel.anchoredPosition = new Vector2(0, -(56 + 12));

            _seatGrid = UIKit.NewRect(seatPanel, "SeatGrid");
            UIKit.Stretch(_seatGrid, 12, 12, 12, 12);
            var glg = _seatGrid.gameObject.AddComponent<GridLayoutGroup>();
            glg.cellSize = new Vector2(396, 128);
            glg.spacing = new Vector2(14, 14);
            glg.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            glg.constraintCount = 3;
            glg.childAlignment = TextAnchor.MiddleCenter;

            // control bar
            var ctrl = UIKit.AddImage(Root, "Control", UITheme.PanelAlt).rectTransform;
            ctrl.anchorMin = new Vector2(0, 1); ctrl.anchorMax = new Vector2(1, 1); ctrl.pivot = new Vector2(0.5f, 1);
            ctrl.sizeDelta = new Vector2(-24, 70); ctrl.anchoredPosition = new Vector2(0, -(56 + 12 + 300 + 12));

            var modeTitle = UIKit.AddLocText(ctrl, "ModeTitle", "room.mode", 16, UITheme.TextDim);
            UIKit.Anchor(modeTitle.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f));
            modeTitle.rectTransform.sizeDelta = new Vector2(60, 30); modeTitle.rectTransform.anchoredPosition = new Vector2(58, 0);
            modeTitle.alignment = TextAlignmentOptions.Left;

            _modeCycler = UIKit.AddCycler(ctrl, "ModeCycler",
                new[] { L("mode.free"), L("mode.normal") }, 1, out var mrt);
            UIKit.Anchor(mrt, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f));
            mrt.sizeDelta = new Vector2(160, 38); mrt.anchoredPosition = new Vector2(216, 0);
            _modeCycler.Changed += i =>
            {
                if (Ctx.Rooms.IsHost) Ctx.Rooms.SetMode(i == 0 ? GameMode.Free : GameMode.Normal);
            };

            _songLabel = UIKit.AddText(ctrl, "SongLabel", "", 16, UITheme.Text, TextAlignmentOptions.Left);
            UIKit.Anchor(_songLabel.rectTransform, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(0, 0.5f));
            _songLabel.rectTransform.offsetMin = new Vector2(420, -19); _songLabel.rectTransform.offsetMax = new Vector2(-180, 19);

            _selectSongBtn = UIKit.AddLocButton(ctrl, "SelectSong", "room.select_song", UITheme.Accent, UITheme.OnPrimary, 15);
            var ssrt = _selectSongBtn.GetComponent<RectTransform>();
            UIKit.Anchor(ssrt, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f));
            ssrt.sizeDelta = new Vector2(150, 40); ssrt.anchoredPosition = new Vector2(-14, 0);
            _selectSongBtn.onClick.AddListener(() => GoTo(ScreenId.SongSelect));

            // bottom actions
            _readyBtn = UIKit.AddButton(Root, "ReadyBtn", out _readyLabel, UITheme.Primary, UITheme.OnPrimary, 20);
            var rrt = _readyBtn.GetComponent<RectTransform>();
            UIKit.Anchor(rrt, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0));
            rrt.sizeDelta = new Vector2(200, 54); rrt.anchoredPosition = new Vector2(-120, 40);
            _readyBtn.onClick.AddListener(OnReadyToggle);

            _startBtn = UIKit.AddLocButton(Root, "StartBtn", "room.start", UITheme.Ready, UITheme.OnPrimary, 20);
            var strt = _startBtn.GetComponent<RectTransform>();
            UIKit.Anchor(strt, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0));
            strt.sizeDelta = new Vector2(200, 54); strt.anchoredPosition = new Vector2(120, 40);
            _startBtn.onClick.AddListener(OnStart);
        }

        public override void OnShow()
        {
            if (!_subscribed) { Ctx.Rooms.RoomUpdated += OnRoomUpdated; _subscribed = true; }
            Render();
        }

        public override void OnHide()
        {
            if (!_subscribed) return;
            Ctx.Rooms.RoomUpdated -= OnRoomUpdated;
            _subscribed = false;
        }

        private void OnRoomUpdated(int id) => Render();

        private void Render()
        {
            var room = Ctx.Rooms.CurrentRoom;
            if (room == null) return;
            bool isHost = Ctx.Rooms.IsHost;

            _title.text = L("room.title").Replace("{0}", room.Id.ToString("000"));
            _modeCycler.Set(room.Mode == GameMode.Free ? 0 : 1, false);
            _songLabel.text = string.IsNullOrEmpty(room.SongTitle) ? L("room.no_song") : "♪ " + room.SongTitle;

            _selectSongBtn.gameObject.SetActive(isHost);
            _startBtn.gameObject.SetActive(isHost);
            UIKit.SetInteractable(_startBtn, Ctx.Rooms.CanStart());

            var localReady = LocalReady(room);
            _readyLabel.text = L(localReady ? "room.cancel_ready" : "room.ready");

            RenderSeats(room);

            // center single action if not host (no start button)
            var rrt = _readyBtn.GetComponent<RectTransform>();
            rrt.anchoredPosition = new Vector2(isHost ? -120 : 0, 40);
        }

        private bool LocalReady(RoomInfo room)
        {
            foreach (var s in room.Seats)
                if (!s.IsEmpty && s.Player.Id == Ctx.Session.LocalPlayerId) return s.IsReady;
            return false;
        }

        private void RenderSeats(RoomInfo room)
        {
            UIKit.Clear(_seatGrid);
            for (int i = 0; i < room.Capacity; i++)
            {
                var seat = i < room.Seats.Count ? room.Seats[i] : new SeatInfo();
                var cell = UIKit.AddImage(_seatGrid, "Seat" + i, seat.IsEmpty ? new Color(1, 1, 1, 0.03f) : UITheme.RowAlt);

                if (seat.IsEmpty)
                {
                    var e = UIKit.AddLocText(cell.transform, "Empty", "room.empty_seat", 16, UITheme.TextDim, TextAlignmentOptions.Center);
                    UIKit.Stretch(e.rectTransform);
                    continue;
                }

                var avatar = UIKit.AddImage(cell.transform, "Avatar", new Color(0.35f, 0.28f, 0.5f, 1f)).rectTransform;
                UIKit.Anchor(avatar, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f));
                avatar.sizeDelta = new Vector2(76, 96); avatar.anchoredPosition = new Vector2(54, 0);
                var initial = UIKit.AddText(avatar.transform, "I", seat.Player.DisplayName.Substring(0, 1), 34, UITheme.Text, TextAlignmentOptions.Center);
                UIKit.Stretch(initial.rectTransform);

                var name = UIKit.AddText(cell.transform, "Name", seat.Player.DisplayName, 18, UITheme.Text, TextAlignmentOptions.Left);
                UIKit.Anchor(name.rectTransform, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(0, 0.5f));
                name.rectTransform.offsetMin = new Vector2(104, 6); name.rectTransform.offsetMax = new Vector2(-8, 40);

                var badge = UIKit.AddText(cell.transform, "Badge",
                    (seat.IsHost ? "<color=#FFD24C>★ " + L("room.host") + "</color>   " : "") +
                    (seat.IsReady ? "<color=#5CD86F>✓ " + L("room.ready") + "</color>" : ""),
                    15, UITheme.TextDim, TextAlignmentOptions.Left);
                badge.richText = true;
                UIKit.Anchor(badge.rectTransform, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(0, 0.5f));
                badge.rectTransform.offsetMin = new Vector2(104, -40); badge.rectTransform.offsetMax = new Vector2(-8, -6);
            }
        }

        private void OnReadyToggle()
        {
            var room = Ctx.Rooms.CurrentRoom;
            if (room == null) return;
            Ctx.Rooms.SetReady(!LocalReady(room));
        }

        private void OnStart()
        {
            if (!Ctx.Rooms.CanStart())
            {
                var room = Ctx.Rooms.CurrentRoom;
                Toast.Show(L(room != null && string.IsNullOrEmpty(room.SongTitle) ? "room.need_song" : "room.waiting_players"));
                return;
            }
            Nav.StartGame?.Invoke();
        }

        private void OnLeave()
        {
            Ctx.Rooms.LeaveRoom();
            GoTo(ScreenId.Lobby);
        }
    }
}
