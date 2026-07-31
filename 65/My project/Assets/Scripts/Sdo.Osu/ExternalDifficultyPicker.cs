using System.Collections.Generic;

namespace Sdo.Osu
{
    /// <summary>
    /// Maps a set of 4K difficulty candidates onto the game's three slots — easy / normal / hard — HARD-FIRST:
    /// the hardest chart becomes hard, the next normal, the next easy. When fewer than three candidates exist the
    /// LOW slots are left empty (−1) rather than duplicated, so a single chart shows as "hard only" and the empty
    /// easy/normal rows grey out (matching SongCatalog.HasChart). Only the three hardest candidates are ever used;
    /// any beyond three are dropped.
    ///
    /// "Hardest" is the caller's <paramref name="difficulty"/> score — pass the DISPLAYED LEVEL (round(star × 7)),
    /// NOT the note count. Note count is a poor proxy: a chart can have MORE notes but an easier pattern (a lower
    /// star), and slotting it as "hard" then shows a hard slot whose level number is BELOW the normal slot's — the
    /// lapis case, where the 1395-note chart rates 4.19★ (LV29) yet the 1345-note chart rates 4.94★ (LV35). Ordering
    /// by the level keeps the three slots reading hard ≥ normal ≥ easy. Ties fall back to the secondary score
    /// (note count) then the original index, so equal-level charts keep the denser one on top and stay stable.
    ///
    /// Pure/testable.
    /// </summary>
    public static class ExternalDifficultyPicker
    {
        /// <summary>Legacy single-key overload: order by one score (note count) alone, tie-break on index.</summary>
        public static int[] Assign(IReadOnlyList<int> noteCounts) => Assign(noteCounts, null);

        /// <summary>Returns [easyIdx, normalIdx, hardIdx] into the candidate lists; −1 = slot unfilled. Candidates
        /// are ordered by <paramref name="difficulty"/> DESC, then <paramref name="tieBreak"/> DESC (when given),
        /// then original index ASC (stable). Pass the displayed level as <paramref name="difficulty"/> and the note
        /// count as <paramref name="tieBreak"/>.</summary>
        public static int[] Assign(IReadOnlyList<int> difficulty, IReadOnlyList<int> tieBreak)
        {
            var slots = new[] { -1, -1, -1 };   // easy, normal, hard
            int n = difficulty?.Count ?? 0;
            if (n == 0) return slots;

            var order = new List<int>(n);
            for (int i = 0; i < n; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                int c = difficulty[b].CompareTo(difficulty[a]);
                if (c != 0) return c;
                if (tieBreak != null)
                {
                    int t = tieBreak[b].CompareTo(tieBreak[a]);
                    if (t != 0) return t;
                }
                return a.CompareTo(b);
            });

            if (order.Count > 0) slots[2] = order[0];   // hard   = hardest
            if (order.Count > 1) slots[1] = order[1];   // normal = 2nd
            if (order.Count > 2) slots[0] = order[2];   // easy   = 3rd
            return slots;
        }
    }
}
