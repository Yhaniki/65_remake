using System;
using System.Collections.Generic;

namespace Sdo.Ruleset
{
    /// <summary>
    /// One player on the in-game ranking board: a display name, the current score, and whether
    /// this is the locally-controlled player. Pure data — no UnityEngine types (Sdo.Ruleset is
    /// <c>noEngineReferences</c>); colours/positions are decided by the renderer in Sdo.Game.
    /// </summary>
    public readonly struct PlayerEntry
    {
        public readonly string Name;
        public readonly long Score;
        public readonly bool IsLocal;

        public PlayerEntry(string name, long score, bool isLocal)
        {
            Name = name;
            Score = score;
            IsLocal = isLocal;
        }
    }

    /// <summary>
    /// Pure ranking logic for the gameplay roster (right-side name+score list and the centre
    /// "rank N / M" readout). Ordering is fully deterministic so the list never flickers on ties:
    /// higher score first; on equal score the local player ranks ahead; otherwise the lower
    /// original index wins. This total order means the sort need not be stable.
    /// </summary>
    public static class RankingBoard
    {
        /// <summary>
        /// Compare players a, b (by original index) for descending-rank order:
        /// returns &lt;0 if a outranks b, &gt;0 if b outranks a.
        /// </summary>
        private static int Compare(IReadOnlyList<PlayerEntry> players, int a, int b)
        {
            long sa = players[a].Score, sb = players[b].Score;
            if (sa != sb) return sb.CompareTo(sa);          // higher score first
            bool la = players[a].IsLocal, lb = players[b].IsLocal;
            if (la != lb) return la ? -1 : 1;               // local wins ties
            return a.CompareTo(b);                          // stable fallback: original order
        }

        /// <summary>
        /// Indices into <paramref name="players"/> ordered best-rank-first. Never null
        /// (empty array for an empty/null roster). Does not mutate the input.
        /// </summary>
        public static int[] SortedIndices(IReadOnlyList<PlayerEntry> players)
        {
            int n = players?.Count ?? 0;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) => Compare(players, a, b));
            return order;
        }

        /// <summary>
        /// 1-based rank of the local player among everyone, and the total player count.
        /// Returns rank 0 when there is no local entry (total still reflects the roster size).
        /// </summary>
        public static (int rank, int total) LocalRank(IReadOnlyList<PlayerEntry> players)
        {
            int n = players?.Count ?? 0;
            int localIdx = -1;
            for (int i = 0; i < n; i++)
            {
                if (players[i].IsLocal) { localIdx = i; break; }
            }
            if (localIdx < 0) return (0, n);

            int better = 0;   // how many players outrank the local player
            for (int i = 0; i < n; i++)
            {
                if (i != localIdx && Compare(players, i, localIdx) < 0) better++;
            }
            return (better + 1, n);
        }
    }
}
