using System.Collections.Generic;
using Sdo.Net.Server;

namespace Sdo.Server.Net
{
    /// <summary>
    /// Keeps the authoritative live-score leader stable between lossy frame pushes.
    /// A challenger must lead by at least <see cref="SwitchLead"/> points before the
    /// displayed leader changes.
    /// </summary>
    public sealed class LiveLeaderTracker
    {
        public const long SwitchLead = 300;

        private readonly NetMatchPlayerSnapshot[] _players;
        private readonly Dictionary<int, long> _scores = new Dictionary<int, long>();

        public int CurrentUserId { get; private set; }

        public LiveLeaderTracker(NetMatchPlayerSnapshot[] players)
        {
            _players = players ?? new NetMatchPlayerSnapshot[0];
            CurrentUserId = FirstUserId(_players);
            for (int i = 0; i < _players.Length; i++) _scores[_players[i].UserId] = 0;
        }

        public void Record(int[] activeUserIds, int userId, long score)
        {
            if (!Contains(activeUserIds, userId)) return;
            _scores[userId] = score;
        }

        public int Resolve(int[] activeUserIds)
        {
            int firstUserId = FirstActiveUserId(activeUserIds);
            if (firstUserId == 0)
            {
                CurrentUserId = 0;
                return 0;
            }

            if (!Contains(activeUserIds, CurrentUserId))
                CurrentUserId = firstUserId;

            long currentScore = ScoreOf(CurrentUserId);
            NetMatchPlayerSnapshot best = null;
            long bestScore = long.MinValue;

            for (int i = 0; i < _players.Length; i++)
            {
                var player = _players[i];
                if (!Contains(activeUserIds, player.UserId) || player.UserId == CurrentUserId) continue;

                long score = ScoreOf(player.UserId);
                if (best == null || score > bestScore ||
                    (score == bestScore && ComesFirst(player, best)))
                {
                    best = player;
                    bestScore = score;
                }
            }

            if (best != null && LeadsByAtLeast(bestScore, currentScore, SwitchLead))
                CurrentUserId = best.UserId;

            return CurrentUserId;
        }

        private long ScoreOf(int userId)
        {
            long score;
            return _scores.TryGetValue(userId, out score) ? score : 0;
        }

        private static int FirstUserId(NetMatchPlayerSnapshot[] players)
        {
            if (players == null || players.Length == 0) return 0;

            var first = players[0];
            for (int i = 1; i < players.Length; i++)
                if (ComesFirst(players[i], first)) first = players[i];
            return first.UserId;
        }

        private int FirstActiveUserId(int[] activeUserIds)
        {
            NetMatchPlayerSnapshot first = null;
            for (int i = 0; i < _players.Length; i++)
            {
                var player = _players[i];
                if (!Contains(activeUserIds, player.UserId)) continue;
                if (first == null || ComesFirst(player, first)) first = player;
            }
            return first != null ? first.UserId : 0;
        }

        private static bool Contains(int[] userIds, int userId)
        {
            if (userIds == null) return false;
            for (int i = 0; i < userIds.Length; i++)
                if (userIds[i] == userId) return true;
            return false;
        }

        private static bool ComesFirst(NetMatchPlayerSnapshot a, NetMatchPlayerSnapshot b)
            => a.Seat < b.Seat || (a.Seat == b.Seat && a.UserId < b.UserId);

        private static bool LeadsByAtLeast(long challenger, long current, long margin)
        {
            if (challenger < current) return false;
            if (current > long.MaxValue - margin) return false;
            return challenger >= current + margin;
        }
    }
}
