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
        private readonly string[] _botNames;
        private readonly string[] _botLines;
        private double _nextBotMs;
        private int _line;

        public event Action<ChatMessage> MessageReceived;
        public IReadOnlyList<ChatMessage> History => _history;

        public MockChatService(IClock clock)
        {
            _clock = clock;
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

        public void Send(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Add(new ChatMessage("我", text.Trim(), _clock.NowMs));
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
