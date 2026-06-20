using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Sdo.Localization;
using Sdo.UI.Core;
using Sdo.UI.Services;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>大廳：房間列表 + 線上玩家 + 左下聊天 + 建房 / 選音符 / 設定。</summary>
    public sealed class LobbyScreen : UIScreenBase
    {
        public override ScreenId Id => ScreenId.Lobby;

        private RectTransform _roomContent;
        private RectTransform _playerContent;
        private RectTransform _chatContent;
        private TMP_InputField _chatInput;
        private ScrollRect _chatScroll;
        private bool _subscribed;

        private static string L(string k) => LocalizationManager.Get(k);

        protected override void BuildUI()
        {
            UIKit.AddImage(Root, "Bg", UITheme.Bg);

            // ---- header ----
            var header = UIKit.AddImage(Root, "Header", UITheme.Header).rectTransform;
            UIKit.Anchor(header, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
            header.sizeDelta = new Vector2(0, 56);
            header.anchoredPosition = Vector2.zero;

            var title = UIKit.AddLocText(header, "Title", "app.title", 24, UITheme.Text);
            UIKit.Anchor(title.rectTransform, new Vector2(0, 0), new Vector2(0.4f, 1), new Vector2(0, 0.5f));
            title.rectTransform.offsetMin = new Vector2(18, 0); title.rectTransform.offsetMax = new Vector2(0, 0);
            title.alignment = TextAlignmentOptions.Left;

            var pname = UIKit.AddText(header, "Player", Ctx.Session.LocalPlayerName, 15, UITheme.TextDim, TextAlignmentOptions.Center);
            UIKit.Anchor(pname.rectTransform, new Vector2(0.33f, 0), new Vector2(0.55f, 1), new Vector2(0.5f, 0.5f));

            // right-side buttons (fit the 800-wide 4:3 header: span ≈ 540..790)
            var note = UIKit.AddLocButton(header, "NoteBtn", "lobby.select_note", UITheme.Secondary, UITheme.Text, 14);
            PlaceTopRight(note.GetComponent<RectTransform>(), 96, -166);
            note.onClick.AddListener(() => Nav.OpenNoteSkinPicker?.Invoke());

            var settings = UIKit.AddLocButton(header, "SettingsBtn", "lobby.settings", UITheme.Secondary, UITheme.Text, 14);
            PlaceTopRight(settings.GetComponent<RectTransform>(), 64, -96);
            settings.onClick.AddListener(() => Nav.OpenSettings?.Invoke());

            var logout = UIKit.AddLocButton(header, "LogoutBtn", "lobby.logout", UITheme.Danger, UITheme.OnPrimary, 14);
            PlaceTopRight(logout.GetComponent<RectTransform>(), 64, -12);
            logout.onClick.AddListener(Quit);   // 登出 → 直接關閉遊戲 (暫定)

            // ---- left column (room list + chat) ----
            var left = UIKit.NewRect(Root, "Left");
            left.anchorMin = new Vector2(0, 0); left.anchorMax = new Vector2(1, 1);
            left.offsetMin = new Vector2(12, 12); left.offsetMax = new Vector2(-(228 + 12), -(56 + 8));

            // room list panel (above chat)
            var roomPanel = UIKit.AddImage(left, "RoomPanel", UITheme.Panel).rectTransform;
            roomPanel.anchorMin = new Vector2(0, 0); roomPanel.anchorMax = new Vector2(1, 1);
            roomPanel.offsetMin = new Vector2(0, 212); roomPanel.offsetMax = Vector2.zero;

            var roomTitle = UIKit.AddLocText(roomPanel, "RoomsTitle", "lobby.rooms", 18, UITheme.Text);
            UIKit.Anchor(roomTitle.rectTransform, new Vector2(0, 1), new Vector2(0.6f, 1), new Vector2(0, 1));
            roomTitle.rectTransform.sizeDelta = new Vector2(0, 34); roomTitle.rectTransform.anchoredPosition = new Vector2(12, -6);

            var create = UIKit.AddLocButton(roomPanel, "CreateBtn", "lobby.create_room", UITheme.Primary, UITheme.OnPrimary, 16);
            var crt = create.GetComponent<RectTransform>();
            UIKit.Anchor(crt, new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));
            crt.sizeDelta = new Vector2(140, 34); crt.anchoredPosition = new Vector2(-10, -6);
            create.onClick.AddListener(OnCreate);

            // column header
            var colHead = UIKit.AddImage(roomPanel, "ColHead", new Color(1, 1, 1, 0.05f)).rectTransform;
            UIKit.Anchor(colHead, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
            colHead.sizeDelta = new Vector2(-16, 26); colHead.anchoredPosition = new Vector2(0, -44);
            Col(colHead, L("lobby.col_room"), 0.00f, 0.14f, UITheme.TextDim, TextAlignmentOptions.Left);
            Col(colHead, L("lobby.col_host"), 0.14f, 0.46f, UITheme.TextDim, TextAlignmentOptions.Left);
            Col(colHead, L("lobby.col_count"), 0.46f, 0.62f, UITheme.TextDim, TextAlignmentOptions.Center);
            Col(colHead, L("lobby.col_mode"), 0.62f, 0.80f, UITheme.TextDim, TextAlignmentOptions.Center);
            Col(colHead, L("lobby.col_status"), 0.80f, 1.00f, UITheme.TextDim, TextAlignmentOptions.Center);

            var roomScroll = UIKit.AddVerticalScroll(roomPanel, "RoomScroll", out _roomContent, 4f, 6);
            var rsrt = roomScroll.GetComponent<RectTransform>();
            UIKit.Stretch(rsrt, 8, 8, 8, 74);

            // chat panel (bottom)
            var chatPanel = UIKit.AddImage(left, "ChatPanel", UITheme.Panel).rectTransform;
            chatPanel.anchorMin = new Vector2(0, 0); chatPanel.anchorMax = new Vector2(1, 0);
            chatPanel.pivot = new Vector2(0.5f, 0);
            chatPanel.sizeDelta = new Vector2(0, 200); chatPanel.anchoredPosition = Vector2.zero;

            var chatTitle = UIKit.AddLocText(chatPanel, "ChatTitle", "lobby.chat", 16, UITheme.Text);
            UIKit.Anchor(chatTitle.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 1));
            chatTitle.rectTransform.sizeDelta = new Vector2(0, 26); chatTitle.rectTransform.anchoredPosition = new Vector2(12, -4);

            _chatScroll = UIKit.AddVerticalScroll(chatPanel, "ChatScroll", out _chatContent, 2f, 6, new Color(0, 0, 0, 0.25f));
            var csrt = _chatScroll.GetComponent<RectTransform>();
            UIKit.Stretch(csrt, 8, 40, 8, 32);

            _chatInput = UIKit.AddInputField(chatPanel, "ChatInput", L("lobby.chat_hint"), 15);
            var cirt = _chatInput.GetComponent<RectTransform>();
            cirt.anchorMin = new Vector2(0, 0); cirt.anchorMax = new Vector2(1, 0); cirt.pivot = new Vector2(0.5f, 0);
            cirt.sizeDelta = new Vector2(-90, 30); cirt.anchoredPosition = new Vector2(-37, 6);
            _chatInput.onSubmit.AddListener(_ => SendChat());

            var send = UIKit.AddLocButton(chatPanel, "SendBtn", "lobby.send", UITheme.Accent, UITheme.OnPrimary, 15);
            var sirt = send.GetComponent<RectTransform>();
            sirt.anchorMin = new Vector2(1, 0); sirt.anchorMax = new Vector2(1, 0); sirt.pivot = new Vector2(1, 0);
            sirt.sizeDelta = new Vector2(74, 30); sirt.anchoredPosition = new Vector2(-8, 6);
            send.onClick.AddListener(SendChat);

            // ---- right column (online players) ----
            var playerPanel = UIKit.AddImage(Root, "PlayerPanel", UITheme.Panel).rectTransform;
            playerPanel.anchorMin = new Vector2(1, 0); playerPanel.anchorMax = new Vector2(1, 1);
            playerPanel.pivot = new Vector2(1, 0.5f);
            playerPanel.sizeDelta = new Vector2(228, -(56 + 8 + 12));
            playerPanel.anchoredPosition = new Vector2(-12, -((56 + 8) - 12) * 0.5f);

            var ptitle = UIKit.AddLocText(playerPanel, "PlayersTitle", "lobby.online_players", 18, UITheme.Text);
            UIKit.Anchor(ptitle.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 1));
            ptitle.rectTransform.sizeDelta = new Vector2(0, 30); ptitle.rectTransform.anchoredPosition = new Vector2(12, -6);

            var pscroll = UIKit.AddVerticalScroll(playerPanel, "PlayerScroll", out _playerContent, 3f, 6);
            UIKit.Stretch(pscroll.GetComponent<RectTransform>(), 8, 8, 8, 40);
        }

        private void PlaceTopRight(RectTransform rt, float w, float x)
        {
            UIKit.Anchor(rt, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f));
            rt.sizeDelta = new Vector2(w, 34);
            rt.anchoredPosition = new Vector2(x, 0);
        }

        private TextMeshProUGUI Col(Transform parent, string txt, float xMin, float xMax, Color c, TextAlignmentOptions a)
        {
            var t = UIKit.AddText(parent, "c", txt, 15, c, a);
            var rt = t.rectTransform;
            rt.anchorMin = new Vector2(xMin, 0); rt.anchorMax = new Vector2(xMax, 1);
            rt.offsetMin = new Vector2(8, 0); rt.offsetMax = new Vector2(-8, 0);
            return t;
        }

        // ---------- data binding ----------

        public override void OnShow()
        {
            if (!_subscribed)
            {
                Ctx.Rooms.RoomsChanged += RefreshRooms;
                Ctx.Players.PlayersChanged += RefreshPlayers;
                Ctx.Chat.MessageReceived += OnChatMessage;
                _subscribed = true;
            }
            RefreshRooms();
            RefreshPlayers();
            RebuildChat();
        }

        public override void OnHide()
        {
            if (!_subscribed) return;
            Ctx.Rooms.RoomsChanged -= RefreshRooms;
            Ctx.Players.PlayersChanged -= RefreshPlayers;
            Ctx.Chat.MessageReceived -= OnChatMessage;
            _subscribed = false;
        }

        private void RefreshRooms()
        {
            UIKit.Clear(_roomContent);
            var rooms = Ctx.Rooms.GetRooms();
            if (rooms.Count == 0)
            {
                var empty = UIKit.AddLocText(_roomContent, "Empty", "lobby.empty", 16, UITheme.TextDim, TextAlignmentOptions.Center);
                UIKit.Layout(empty.gameObject, 60);
                return;
            }
            foreach (var r in rooms) AddRoomRow(r);
        }

        private void AddRoomRow(RoomInfo r)
        {
            var rowImg = UIKit.AddImage(_roomContent, "Room" + r.Id, UITheme.Row, true);
            UIKit.Layout(rowImg.gameObject, 38);
            var btn = rowImg.gameObject.AddComponent<Button>();
            btn.targetGraphic = rowImg;
            btn.onClick.AddListener(() => OnRoomClicked(r));

            Col(rowImg.transform, r.Id.ToString("000"), 0.00f, 0.14f, UITheme.Text, TextAlignmentOptions.Left);
            Col(rowImg.transform, r.HostName, 0.14f, 0.46f, UITheme.Text, TextAlignmentOptions.Left);
            Col(rowImg.transform, $"{r.Count}/{r.Capacity}", 0.46f, 0.62f, UITheme.Text, TextAlignmentOptions.Center);
            Col(rowImg.transform, L(r.Mode == GameMode.Free ? "mode.free" : "mode.normal"), 0.62f, 0.80f, UITheme.TextDim, TextAlignmentOptions.Center);
            var waiting = r.Status == RoomStatus.Waiting;
            Col(rowImg.transform, L(waiting ? "status.waiting" : "status.ingame"), 0.80f, 1.00f,
                waiting ? UITheme.Ready : UITheme.Warn, TextAlignmentOptions.Center);
        }

        private void RefreshPlayers()
        {
            UIKit.Clear(_playerContent);
            foreach (var p in Ctx.Players.GetOnlinePlayers())
            {
                var row = UIKit.AddText(_playerContent, "P" + p.Id, $"<color=#5CD86F>●</color> {p.DisplayName}   <color=#B0A8C8>Lv.{p.Level}</color>", 15, UITheme.Text);
                UIKit.Layout(row.gameObject, 24);
            }
        }

        // ---------- chat ----------

        private void RebuildChat()
        {
            UIKit.Clear(_chatContent);
            foreach (var m in Ctx.Chat.History) AddChatLine(m);
            ScrollChatToBottom();
        }

        private void OnChatMessage(ChatMessage m)
        {
            AddChatLine(m);
            ScrollChatToBottom();
        }

        private void AddChatLine(ChatMessage m)
        {
            string line = m.System ? $"<color=#F0C24A>{m.Text}</color>"
                                    : $"<color=#7FB6FF>{m.Sender}</color>: {m.Text}";
            var t = UIKit.AddText(_chatContent, "line", line, 14, UITheme.Text, TextAlignmentOptions.TopLeft, true);
            t.richText = true;
            UIKit.Layout(t.gameObject, 20);
        }

        private void ScrollChatToBottom()
        {
            if (_chatScroll != null) Canvas.ForceUpdateCanvases();
            if (_chatScroll != null) _chatScroll.verticalNormalizedPosition = 0f;
        }

        private void SendChat()
        {
            var txt = _chatInput.text;
            if (string.IsNullOrWhiteSpace(txt)) return;
            Ctx.Chat.Send(txt);
            _chatInput.text = "";
            _chatInput.ActivateInputField();
        }

        // ---------- actions ----------

        // 登出 = 暫時直接結束程式 (尚無帳號/登入流程)。
        private void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
            // Hard-kill: on PCs with many HID devices the new Input System stalls Unity's shutdown for several
            // seconds, leaving the borderless-fullscreen frame frozen ("按了登出卡住，離不開遊戲"). Terminate the
            // process now so quit is instant. Safe here — settings persist on change, there is no unsaved state.
            System.Diagnostics.Process.GetCurrentProcess().Kill();
#endif
        }

        private void OnCreate()
        {
            Ctx.Rooms.CreateRoom(GameMode.Normal);
            GoTo(ScreenId.Room);
        }

        private void OnRoomClicked(RoomInfo r)
        {
            switch (Ctx.Rooms.JoinRoom(r.Id))
            {
                case JoinResult.Ok: GoTo(ScreenId.Room); break;
                case JoinResult.Full: Toast.Show(L("join.full")); break;
                case JoinResult.InGame: Toast.Show(L("join.ingame")); break;
                default: Toast.Show(L("join.notfound")); break;
            }
        }
    }
}
