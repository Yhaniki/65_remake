using System;
using System.Collections.Generic;

namespace Sdo.UI.Services
{
    /// <summary>
    /// Offline mock chat: echoes the local player's lines and injects scripted "bot" traffic on a
    /// timer driven by an injectable <see cref="IClock"/> (so tests are deterministic).
    /// </summary>
    public sealed class MockChatService : IChatService
    {
        private enum WhisperReach { NoSuchId, OffChannel, Reachable }

        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        private readonly IClock _clock;
        private readonly Func<bool> _localIsMale;   // room-action keyword table is gender-specific (再見→F act5 / M act6)
        private readonly Func<string> _localName;   // 本機發言者顯示名(active profile 的 id/名)；null → 回退 "我"
        private readonly Func<IEnumerable<string>> _onlineNames;   // 目前在同一伺服器/頻道、可被密語（會回話）的人
        private readonly Func<IEnumerable<string>> _offlineNames;  // 帳號存在但不在本頻道 → 密語會得到「不在當前頻道」
        private readonly string[] _botNames;
        private readonly string[] _botLines;
        private readonly string[] _whisperReplies;
        private double _nextBotMs;
        private int _line;
        private int _whisperLine;
        private ChatScope _scope = ChatScope.Lobby;   // 目前作用域（由畫面 SetScope 設定）
        private int _scopeRoomId;

        public event Action<ChatMessage> MessageReceived;
        public IReadOnlyList<ChatMessage> History => _history;

        public MockChatService(IClock clock, Func<bool> localIsMale = null, Func<string> localName = null,
            Func<IEnumerable<string>> onlineNames = null, Func<IEnumerable<string>> offlineNames = null)
        {
            _clock = clock;
            _localIsMale = localIsMale;
            _localName = localName;
            _botNames = new[] { "小舞", "風之舞", "Neo", "櫻花", "阿傑" };
            _botLines = new[]
            {
                "有人一起跳嗎？", "這首歌超讚！", "求大神帶", "房間還有位子嗎",
                "gg 剛剛太緊張了", "2486 開一局", "哈囉大家好", "等等一起",
                "這舞台好漂亮", "衝分中…",
            };
            _whisperReplies = new[] { "好啊～", "等我一下", "收到", "哈哈好", "在的在的" };
            // 密語名冊：預設「在頻道（會回話）」= 會講話的 bot；「帳號存在但離線」= 另一組固定名（示範「不在當前頻道」）。
            // AppContext 可注入真實名冊；其餘皆判為「無此id」。
            _onlineNames = onlineNames ?? (() => _botNames);
            _offlineNames = offlineNames ?? (() => new[] { "小雨", "阿明", "Kevin", "夜風" });
            _nextBotMs = _clock.NowMs + 3000;
        }

        public void Send(string text, ChatChannel channel = ChatChannel.Current)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // 密語優先於表情/動作：`[X] /GO` 是把「/GO」密語給 X，不是表情。
            if (RoomChatCommand.TryParseWhisper(text, out var target, out var body))
            {
                SendWhisper(target, body, channel);
                return;
            }
            if (RoomChatCommand.TryParseExpression(text, out var expressionId, out var leading, out var trailing))
            {
                SendExpression(expressionId, channel, leading, trailing);
                return;
            }

            string trimmed = text.Trim();
            bool male = _localIsMale != null && _localIsMale();
            RoomChatCommand.TryParseRoomAction(trimmed, male, out var action);
            Emit(new ChatMessage(LocalSender(), trimmed, _clock.NowMs, local: true, channel: channel, roomActionId: action?.Id));
        }

        public void SendExpression(int expressionId, ChatChannel channel = ChatChannel.Current)
            => SendExpression(expressionId, channel, null, null);

        public void SendExpression(int expressionId, ChatChannel channel, string trailingText)
            => SendExpression(expressionId, channel, null, trailingText);

        public void SendExpression(int expressionId, ChatChannel channel, string leadingText, string trailingText)
        {
            if (!RoomChatCommand.IsValidExpression(expressionId)) return;
            string lead = leadingText != null ? leadingText.Trim() : "";
            string trail = trailingText != null ? trailingText.Trim() : "";
            Emit(new ChatMessage(LocalSender(), trail, _clock.NowMs,
                expressionId: expressionId, local: true, channel: channel, leadingText: lead));
        }

        public void SendWhisper(string target, string body, ChatChannel channel = ChatChannel.Current)
        {
            string tgt = target != null ? target.Trim() : "";
            string msg = body != null ? body.Trim() : "";
            if (tgt.Length == 0 || msg.Length == 0) return;   // 只選了對象還沒打內容 → 不送

            var reach = ResolveWhisper(tgt, out var canonical);
            if (reach == WhisperReach.NoSuchId)
            {
                Emit(WhisperNotice(tgt, WhisperKind.NoId, channel));
                return;
            }
            if (reach == WhisperReach.OffChannel)
            {
                Emit(WhisperNotice(canonical, WhisperKind.OffChannel, channel));
                return;
            }

            // 你對 X 說（本機、顯示在當前+好友頻道）。內容可夾表情指令（[X] /GO）→ 存進 ExpressionId + Leading/Text，
            // 讓聊天列畫 inline emoji（RoomScreen.AddRoomChatWhisperLine），但密語不彈頭上藍泡。
            var outgoing = new ChatMessage
            {
                Sender = LocalSender(),
                WhisperParty = canonical,
                TimeMs = _clock.NowMs,
                Local = true,
                Whisper = WhisperKind.Outgoing,
                Channel = channel,
            };
            if (RoomChatCommand.TryParseExpression(msg, out var exprId, out var lead, out var trail))
            {
                outgoing.ExpressionId = exprId;
                outgoing.LeadingText = lead;
                outgoing.Text = trail;
            }
            else outgoing.Text = msg;
            Emit(outgoing);
            // 對方回話（示範「X 對你說」形式）——單機離線模擬，取一句罐頭回覆。
            string reply = _whisperReplies[_whisperLine % _whisperReplies.Length];
            _whisperLine++;
            Emit(new ChatMessage
            {
                Sender = canonical,
                WhisperParty = canonical,
                Text = reply,
                TimeMs = _clock.NowMs,
                Whisper = WhisperKind.Incoming,
                Channel = channel,
            });
        }

        public void AnnounceStageEnter(string name) => AddStage(name, StageEventKind.Enter);
        public void AnnounceStageLeave(string name) => AddStage(name, StageEventKind.Leave);

        public void SetScope(ChatScope scope, int roomId = 0)
        {
            _scope = scope;
            _scopeRoomId = roomId;
        }

        // 本機發言者名：active profile 的 id/名（跟頭頂名字一致）；沒給就回退 "我"。
        private string LocalSender()
        {
            if (_localName == null) return "我";
            string n = _localName();
            return string.IsNullOrEmpty(n) ? "我" : n;
        }

        private WhisperReach ResolveWhisper(string name, out string canonical)
        {
            canonical = name;
            if (TryMatchName(_onlineNames, name, out var online)) { canonical = online; return WhisperReach.Reachable; }
            if (TryMatchName(_offlineNames, name, out var offline)) { canonical = offline; return WhisperReach.OffChannel; }
            return WhisperReach.NoSuchId;
        }

        private static bool TryMatchName(Func<IEnumerable<string>> source, string name, out string canonical)
        {
            canonical = name;
            var list = source != null ? source() : null;
            if (list == null) return false;
            foreach (var n in list)
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                if (string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    canonical = n.Trim();
                    return true;
                }
            }
            return false;
        }

        private ChatMessage WhisperNotice(string party, WhisperKind kind, ChatChannel channel)
            => new ChatMessage
            {
                Sender = "系統",
                WhisperParty = party,
                TimeMs = _clock.NowMs,
                Whisper = kind,
                Channel = channel,
            };

        private void AddStage(string name, StageEventKind kind)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            Emit(new ChatMessage { Sender = name.Trim(), TimeMs = _clock.NowMs, Stage = kind });
        }

        public void Tick()
        {
            var now = _clock.NowMs;
            if (now < _nextBotMs) return;
            _nextBotMs = now + 5000 + (_line % 6) * 1000;   // 5–10s, deterministic cadence
            var name = _botNames[_line % _botNames.Length];
            var text = _botLines[(_line * 7 + 3) % _botLines.Length];
            _line++;
            // bot 閒聊固定屬「大廳」世界頻道（不論當下作用域）：在房間裡不會看到，回大廳才看得到。
            Add(new ChatMessage(name, text, now) { Scope = ChatScope.Lobby, RoomId = 0 });
        }

        // 蓋上目前作用域再廣播（密語也蓋，但顯示端不看它 → 等同跨場）。
        private void Emit(ChatMessage m)
        {
            m.Scope = _scope;
            m.RoomId = _scopeRoomId;
            Add(m);
        }

        private void Add(ChatMessage m)
        {
            _history.Add(m);
            if (_history.Count > 200) _history.RemoveAt(0);
            MessageReceived?.Invoke(m);
        }
    }
}
