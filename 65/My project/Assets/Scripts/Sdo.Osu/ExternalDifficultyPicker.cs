using System.Collections.Generic;

namespace Sdo.Osu
{
    /// <summary>
    /// Maps a set of 4K difficulty candidates (by note count) onto the game's three slots — easy / normal / hard —
    /// HARD-FIRST: the highest note-count chart becomes hard, the next normal, the next easy. When fewer than three
    /// candidates exist the LOW slots are left empty (−1) rather than duplicated, so a single chart shows as
    /// "hard only" and the empty easy/normal rows grey out (matching SongCatalog.HasChart). Only the three
    /// highest-note-count candidates are ever used; any beyond three are dropped.
    ///
    /// Pure/testable. Tie-break on the original index (stable) so equal note counts keep input order.
    /// </summary>
    public static class ExternalDifficultyPicker
    {
        /// <summary>Returns [easyIdx, normalIdx, hardIdx] into <paramref name="noteCounts"/>; −1 = slot unfilled.</summary>
        public static int[] Assign(IReadOnlyList<int> noteCounts)
        {
            var slots = new[] { -1, -1, -1 };   // easy, normal, hard
            int n = noteCounts?.Count ?? 0;
            if (n == 0) return slots;

            // indices sorted by note count DESC, tie-break by index ASC (stable).
            var order = new List<int>(n);
            for (int i = 0; i < n; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                int c = noteCounts[b].CompareTo(noteCounts[a]);
                return c != 0 ? c : a.CompareTo(b);
            });

            if (order.Count > 0) slots[2] = order[0];   // hard   = most notes
            if (order.Count > 1) slots[1] = order[1];   // normal = 2nd
            if (order.Count > 2) slots[0] = order[2];   // easy   = 3rd
            return slots;
        }
    }
}
