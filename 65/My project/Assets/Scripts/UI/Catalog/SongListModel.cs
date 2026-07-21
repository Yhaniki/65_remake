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
        /// Browse-list curation: keep only the keyboard ('k') chart of each sdomNNNNk/t pair
        /// (<see cref="SongCatalog.IsPrimaryVariant"/> — that's where the k/t story is written down),
        /// then order by gn filename DESCENDING (highest sdomNNNN first / at the top).
        /// </summary>
        public static List<SongCatalog.Entry> Curate(IEnumerable<SongCatalog.Entry> entries)
        {
            var res = new List<SongCatalog.Entry>();
            if (entries == null) return res;
            foreach (var e in entries)
                if (e != null && SongCatalog.IsPrimaryVariant(e.gn)) res.Add(e);
            res.Sort((a, b) => string.CompareOrdinal(b.gn, a.gn));   // by filename sdomNNNNk.gn 降冪(最大號在最上)
            return res;
        }

        public int Count => _all.Count;
        public IReadOnlyList<SongCatalog.Entry> All => _all;

        /// <summary>
        /// NEW 標籤 = 歌單「最上面」的 N 首（第一頁前 N 列），純看清單順序，不看 fileId。
        /// 清單本身已由 <see cref="Curate"/> 依 gn 檔名降冪排好，新歌自然在最上面；fileId 與檔名順序
        /// 並不一致（外掛/補號的歌會有大 fileId 卻排在中間），所以標籤只認位置。回傳 gn 集合（唯一鍵）。
        /// </summary>
        public static HashSet<string> NewBadgeKeys(IReadOnlyList<SongCatalog.Entry> list, int count)
        {
            var res = new HashSet<string>(StringComparer.Ordinal);
            if (list == null) return res;
            for (int i = 0; i < list.Count && res.Count < count; i++)
                if (list[i] != null && !string.IsNullOrEmpty(list[i].gn)) res.Add(list[i].gn);
            return res;
        }

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

        /// <summary>隨機 (random) difficulty-range options — shown as the rows of the 隨機 tab, and used as the
        /// label carried into the room ("隨機難度 X"). Min/Max are inclusive level bounds at the active difficulty;
        /// Key is the localization key. Single source of truth: the screen renders it, FrontendApp re-rolls off it.</summary>
        public struct RandRange { public string Key; public int Min, Max; }
        public static readonly RandRange[] RandRanges =
        {
            new RandRange { Key = "songselect.rand_1_5",  Min = 1,  Max = 5 },
            new RandRange { Key = "songselect.rand_1_9",  Min = 1,  Max = 9 },
            new RandRange { Key = "songselect.rand_5_9",  Min = 5,  Max = 9 },
            new RandRange { Key = "songselect.rand_all",  Min = 0,  Max = 99 },
            new RandRange { Key = "songselect.rand_5up",  Min = 5,  Max = 99 },
            new RandRange { Key = "songselect.rand_9up",  Min = 9,  Max = 99 },
            new RandRange { Key = "songselect.rand_13up", Min = 13, Max = 99 },
            new RandRange { Key = "songselect.rand_20up", Min = 20, Max = 99 },
            new RandRange { Key = "songselect.rand_25up", Min = 25, Max = 99 },
        };

        /// <summary>Clamp an arbitrary index into <see cref="RandRanges"/>.</summary>
        public static int ClampRange(int rangeIndex)
            => rangeIndex < 0 ? 0 : (rangeIndex >= RandRanges.Length ? RandRanges.Length - 1 : rangeIndex);

        /// <summary>A random-eligible candidate: a song plus the specific difficulty (0/1/2) whose level fell in
        /// the range. A song can appear up to 3 times (once per qualifying difficulty).</summary>
        public struct RandomCandidate { public SongCatalog.Entry Song; public int Difficulty; }

        /// <summary>All (song, difficulty) candidates for RandRanges[rangeIndex]: searches easy/normal/hard TOGETHER —
        /// a song qualifies at ANY difficulty whose level is inside the range AND carries a playable chart. The picked
        /// difficulty is the one that matched (not a pre-selected tab). "全部" = every playable chart. Call afresh
        /// each time to re-roll.</summary>
        public static List<RandomCandidate> RandomCandidates(IReadOnlyList<SongCatalog.Entry> list, int rangeIndex)
        {
            var res = new List<RandomCandidate>();
            if (list == null) return res;
            var r = RandRanges[ClampRange(rangeIndex)];
            bool all = r.Min <= 0 && r.Max >= 99;
            foreach (var e in list)
            {
                if (e == null) continue;
                for (int d = 0; d < 3; d++)
                {
                    if (!e.HasChart(d)) continue;   // empty difficulty can't be a random pick
                    if (all) { res.Add(new RandomCandidate { Song = e, Difficulty = d }); continue; }
                    int lvl = e.Diff(d);
                    if (lvl >= r.Min && lvl <= r.Max) res.Add(new RandomCandidate { Song = e, Difficulty = d });
                }
            }
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

        public SongCatalog.Entry PickRandom(int seed)
            => _all.Count == 0 ? null : _all[new Random(seed).Next(_all.Count)];

        public static SongCatalog.Entry PickRandomFrom(IReadOnlyList<SongCatalog.Entry> list, int seed)
            => (list == null || list.Count == 0) ? null : list[new Random(seed).Next(list.Count)];

        private static bool Contains(string hay, string needle)
            => !string.IsNullOrEmpty(hay) && hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
