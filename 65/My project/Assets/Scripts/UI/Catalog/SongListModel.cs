using System;
using System.Collections.Generic;
using Sdo.Game;

namespace Sdo.UI.Catalog
{
    /// <summary>
    /// Pure-logic view over the song catalog for the song-select screen: text search + random pick.
    /// (Difficulty is a per-song property, not a list filter. Most songs carry easy/normal/hard, but
    /// some ship only a subset — a difficulty with 0 notes is empty, so that song's row is greyed out
    /// and non-selectable while that difficulty is active; see <see cref="SongCatalog.Entry.HasChart"/>.)
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
        /// First entry in <paramref name="list"/> at/after <paramref name="from"/> that has a real chart at
        /// <paramref name="difficulty"/> (non-empty), searching forward then wrapping to the start; -1 if none.
        /// Used to move the selection off a row that just became empty when the active difficulty changed.
        /// </summary>
        public static int FirstPlayableIndex(IReadOnlyList<SongCatalog.Entry> list, int difficulty, int from)
        {
            if (list == null || list.Count == 0) return -1;
            if (from < 0) from = 0;
            for (int k = 0; k < list.Count; k++)
            {
                int i = (from + k) % list.Count;
                if (list[i] != null && list[i].HasChart(difficulty)) return i;
            }
            return -1;
        }

        // ---- external (user Songs/ folder) songs — the pool the 資料夾 category's group panel slices ----

        /// <summary>Every external (user Songs/) song. <see cref="SongGrouping"/> buckets these by folder /
        /// title / artist / BPM for the browse panel; empty when no user Songs/ were scanned.</summary>
        public List<SongCatalog.Entry> Externals() => Externals(_all);

        public static List<SongCatalog.Entry> Externals(IReadOnlyList<SongCatalog.Entry> list)
        {
            var res = new List<SongCatalog.Entry>();
            if (list == null) return res;
            foreach (var e in list)
                if (e != null && e.external) res.Add(e);
            return res;
        }

        public SongCatalog.Entry PickRandom(int seed)
            => _all.Count == 0 ? null : _all[new Random(seed).Next(_all.Count)];

        public static SongCatalog.Entry PickRandomFrom(IReadOnlyList<SongCatalog.Entry> list, int seed)
            => (list == null || list.Count == 0) ? null : list[new Random(seed).Next(list.Count)];

        private static bool Contains(string hay, string needle)
            => !string.IsNullOrEmpty(hay) && hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
