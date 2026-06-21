using System;
using System.Collections.Generic;
using Sdo.Game;

namespace Sdo.UI.Catalog
{
    /// <summary>
    /// Pure-logic view over the song catalog for the song-select screen: text search + random pick.
    /// (Difficulty is a per-song property, not a list filter — every SDO song has easy/normal/hard.)
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
        /// twice in the list. Keep only the 'k' variant, then order by fileId descending so the
        /// highest-numbered (newest) songs come first. (SongCatalog.All stays unfiltered: gn-based
        /// title/artist lookups and font warmup still need both variants.)
        /// </summary>
        public static List<SongCatalog.Entry> Curate(IEnumerable<SongCatalog.Entry> entries)
        {
            var res = new List<SongCatalog.Entry>();
            if (entries == null) return res;
            foreach (var e in entries)
                if (e != null && IsPrimaryVariant(e.gn)) res.Add(e);
            res.Sort((a, b) => b.fileId.CompareTo(a.fileId));
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

        public List<SongCatalog.Entry> Filter(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<SongCatalog.Entry>(_all);
            query = query.Trim();
            var res = new List<SongCatalog.Entry>();
            foreach (var e in _all)
                if (Contains(e.title, query) || Contains(e.artist, query) || Contains(e.gn, query))
                    res.Add(e);
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
