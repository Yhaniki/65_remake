using System;
using System.Collections.Generic;
using Sdo.Game;

namespace Sdo.UI.Catalog
{
    /// <summary>
    /// Pure-logic view over the song catalog for the song-select screen: text search + random pick.
    /// (Difficulty is a per-song property, not a list filter. Most songs carry easy/normal/hard, but
    /// some ship only a subset — a difficulty with 0 notes is empty and non-selectable; see
    /// <see cref="SongCatalog.Entry.HasChart"/> and <see cref="NearestPlayableDifficulty"/>.)
    /// </summary>
    public sealed class SongListModel
    {
        private readonly List<SongCatalog.Entry> _all;

        public SongListModel(IEnumerable<SongCatalog.Entry> entries)
            => _all = entries == null ? new List<SongCatalog.Entry>() : new List<SongCatalog.Entry>(entries);

        public static SongListModel FromCatalog() => new SongListModel(Curate(SongCatalog.All));

        /// <summary>
        /// Browse-list curation. The original data ships a paired chart per song —
        /// sdomNNNN<b>k</b>.gn and sdomNNNN<b>t</b>.gn share one title — which made every song appear
        /// twice in the list. Keep only the 'k' variant, then order by gn filename DESCENDING
        /// (sdomNNNN, highest number first / at the top). (SongCatalog.All stays unfiltered: gn-based
        /// title/artist lookups and font warmup still need both variants.)
        /// </summary>
        public static List<SongCatalog.Entry> Curate(IEnumerable<SongCatalog.Entry> entries)
        {
            var res = new List<SongCatalog.Entry>();
            if (entries == null) return res;
            foreach (var e in entries)
                if (e != null && IsPrimaryVariant(e.gn)) res.Add(e);
            res.Sort((a, b) => string.CompareOrdinal(b.gn, a.gn));   // by filename sdomNNNNk.gn 降冪(最大號在最上)
            return res;
        }

        /// <summary>True for the 'k' chart of a sdomNNNNk/t.gn pair (the one we list); false for 't'.</summary>
        private static bool IsPrimaryVariant(string gn)
        {
            if (string.IsNullOrEmpty(gn)) return false;
            var name = gn.ToLowerInvariant();
            if (name.EndsWith(".gn")) name = name.Substring(0, name.Length - 3);
            return name.Length > 0 && name[name.Length - 1] == 'k';
        }

        public int Count => _all.Count;
        public IReadOnlyList<SongCatalog.Entry> All => _all;

        public List<SongCatalog.Entry> Filter(string query) => Filter(_all, query);

        /// <summary>Text search (title/artist/gn, case-insensitive) over an arbitrary list — lets the screen
        /// search WITHIN a category subset. Empty/blank query returns a copy of the whole list.</summary>
        public static List<SongCatalog.Entry> Filter(IReadOnlyList<SongCatalog.Entry> list, string query)
        {
            var res = new List<SongCatalog.Entry>();
            if (list == null) return res;
            if (string.IsNullOrWhiteSpace(query)) { res.AddRange(list); return res; }
            query = query.Trim();
            foreach (var e in list)
                if (e != null && (Contains(e.title, query) || Contains(e.artist, query) || Contains(e.gn, query)))
                    res.Add(e);
            return res;
        }

        /// <summary>Songs whose level at <paramref name="difficulty"/> is within [min,max] — the pool for the
        /// 隨機 (random) ranges. min&lt;=0 &amp;&amp; max&gt;=99 means "全部" (no level filter; unknown levels included).</summary>
        public static List<SongCatalog.Entry> InLevelRange(IReadOnlyList<SongCatalog.Entry> list, int difficulty, int min, int max)
        {
            var res = new List<SongCatalog.Entry>();
            if (list == null) return res;
            bool all = min <= 0 && max >= 99;
            foreach (var e in list)
            {
                if (e == null) continue;
                if (all) { res.Add(e); continue; }
                int lvl = e.Diff(difficulty);
                if (lvl >= min && lvl <= max) res.Add(e);
            }
            return res;
        }

        /// <summary>
        /// The difficulty a song should actually open on when the user prefers <paramref name="want"/> (0/1/2)
        /// but that difficulty may be empty for this song. Returns <paramref name="want"/> if it has a chart;
        /// otherwise the nearest difficulty that does (ties break toward the easier side). If the song has no
        /// playable chart at all (all empty), or <paramref name="e"/> is null, returns <paramref name="want"/>
        /// unchanged so callers keep the user's preference (e.g. the 隨機 pool, where no single song is focused).
        /// </summary>
        public static int NearestPlayableDifficulty(SongCatalog.Entry e, int want)
        {
            want = want < 0 ? 0 : (want > 2 ? 2 : want);
            if (e == null || e.HasChart(want)) return want;
            for (int off = 1; off <= 2; off++)
            {
                int lo = want - off, hi = want + off;
                if (lo >= 0 && e.HasChart(lo)) return lo;
                if (hi <= 2 && e.HasChart(hi)) return hi;
            }
            return want;   // no chart at any difficulty (degenerate entry) -> leave the preference untouched
        }

        public SongCatalog.Entry PickRandom(int seed)
            => _all.Count == 0 ? null : _all[new Random(seed).Next(_all.Count)];

        public static SongCatalog.Entry PickRandomFrom(IReadOnlyList<SongCatalog.Entry> list, int seed)
            => (list == null || list.Count == 0) ? null : list[new Random(seed).Next(list.Count)];

        private static bool Contains(string hay, string needle)
            => !string.IsNullOrEmpty(hay) && hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
