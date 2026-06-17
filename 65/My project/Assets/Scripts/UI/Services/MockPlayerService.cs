using System;
using System.Collections.Generic;

namespace Sdo.UI.Services
{
    /// <summary>Offline mock: a fixed pool of "online" players for the lobby roster.</summary>
    public sealed class MockPlayerService : IPlayerService
    {
        private readonly List<PlayerProfile> _players = new List<PlayerProfile>();
        public event Action PlayersChanged;

        public MockPlayerService(int seed = 2024)
        {
            var rng = new Random(seed);
            string[] names =
            {
                "小舞", "DanceKing", "莉莉", "風之舞", "Neo", "櫻花", "阿傑",
                "Momo", "星塵", "夜貓", "狂風", "蝴蝶", "RhythmX", "可可",
            };
            for (int i = 0; i < names.Length; i++)
                _players.Add(new PlayerProfile("p" + i, names[i], rng.Next(1, 60)));
        }

        public IReadOnlyList<PlayerProfile> GetOnlinePlayers() => _players;

        public void Raise() => PlayersChanged?.Invoke();
    }
}
