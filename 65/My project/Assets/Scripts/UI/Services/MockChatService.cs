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
        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        private readonly IClock _clock;
        private readonly Func<bool> _localIsMale;   // room-action keyword table is gender-specific (再見→F act5 / M act6)
        private readonly Func<string> _localName;   // 本機發言者顯示名(active profile 的 id/名)；null → 回退 "我"
        private readonly string[] _botNames;
        private readonly string[] _botLines;
        private double _nextBotMs;
        private int _line;

        public event Action<ChatMessage> MessageReceived;
        public IReadOnlyList<ChatMessage> History => _history;

        public MockChatService(IClock clock, Func<bool> localIsMale = null, Func<string> localName = null)
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
            _nextBotMs = _clock.NowMs + 3000;
            Add(new ChatMessage("系統", "歡迎來到熱舞 Online！", _clock.NowMs, true));
        }

        public void Send(string text, ChatChannel channel = ChatChannel.Current)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (RoomChatCommand.TryParseExpression(text, out var expressionId, out var trailing))
            {
                SendExpression(expressionId, channel, trailing);
                return;
            }

            string trimmed = text.Trim();
            bool male = _localIsMale != null && _localIsMale();
            RoomChatCommand.TryParseRoomAction(trimmed, male, out var action);
            Add(new ChatMessage(LocalSender(), trimmed, _clock.NowMs, local: true, channel: channel, roomActionId: action?.Id));
        }

        public void SendExpression(int expressionId, ChatChannel channel = ChatChannel.Current)
            => SendExpression(expressionId, channel, null);

        public void SendExpression(int expressionId, ChatChannel channel, string trailingText)
        {
            if (!RoomChatCommand.IsValidExpression(expressionId)) return;
            string trail = trailingText != null ? trailingText.Trim() : "";
            Add(new ChatMessage(LocalSender(), trail, _clock.NowMs,
                expressionId: expressionId, local: true, channel: channel));
        }

        // 本機發言者名：active profile 的 id/名（跟頭頂名字一致）；沒給就回退 "我"。
        private string LocalSender()
        {
            if (_localName == null) return "我";
            string n = _localName();
            return string.IsNullOrEmpty(n) ? "我" : n;
        }

        public void Tick()
        {
            var now = _clock.NowMs;
            if (now < _nextBotMs) return;
            _nextBotMs = now + 5000 + (_line % 6) * 1000;   // 5–10s, deterministic cadence
            var name = _botNames[_line % _botNames.Length];
            var text = _botLines[(_line * 7 + 3) % _botLines.Length];
            _line++;
            Add(new ChatMessage(name, text, now));
        }

        private void Add(ChatMessage m)
        {
            _history.Add(m);
            if (_history.Count > 200) _history.RemoveAt(0);
            MessageReceived?.Invoke(m);
        }
    }
}
