using System;
using System.Collections.Generic;
using Sdo.Game;
using Sdo.Osu;

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
        /// then order by <see cref="BrowseKey"/> DESCENDING.
        /// </summary>
        public static List<SongCatalog.Entry> Curate(IEnumerable<SongCatalog.Entry> entries)
        {
            var res = new List<SongCatalog.Entry>();
            if (entries == null) return res;
            var keyed = new List<KeyValuePair<BrowseSortKey, SongCatalog.Entry>>();
            foreach (var e in entries)
                if (e != null && SongCatalog.IsPrimaryVariant(e.gn))
                    keyed.Add(new KeyValuePair<BrowseSortKey, SongCatalog.Entry>(BrowseKey(e), e));
            keyed.Sort((a, b) => BrowseSortKey.Compare(a.Key, b.Key));   // 每首先算一次鍵再排：比較只看鍵 → 保證是全序
            foreach (var kv in keyed) res.Add(kv.Value);
            return res;
        }

        /// <summary>
        /// 歌單排序鍵。三段字典序，全部**降冪**（大的在最上面）：
        /// <list type="bullet">
        /// <item><b>Tier</b>：官方歌 1、外部歌 0 → 官方歌永遠在外部歌上面（維持原本 "sdom…" &gt; "ext_…" 的結果）。</item>
        /// <item><b>Group</b>：外部歌依群組(歌包)聚在一起；官方歌一律 ""。</item>
        /// <item><b>Within</b>：官方歌 = gn 檔名（sdomNNNNk.gn 降冪，最大號在最上）；外部歌有 packOrder
        ///   (歌包自帶 serverconfig)的照**包自己的順序** —— 官方選單是反序畫的、表的最後一列在最上面，
        ///   所以列號補零後降冪剛好就是那個順序；沒有的沿用 gn 降冪，並排在同群組有序的下面。</item>
        /// </list>
        /// 用結構化的鍵而不是接成一個字串，是為了不必塞分隔字元、也不會被群組名裡的字元干擾；
        /// 比較只看鍵 → 是嚴格全序，List.Sort 不會抱怨比較不一致。純函式，好測。
        /// </summary>
        public struct BrowseSortKey
        {
            public int Tier;
            public string Group;
            public string Within;

            /// <summary>降冪比較（回傳 &lt;0 表示 a 應排在 b 前面／更上面）。</summary>
            public static int Compare(BrowseSortKey a, BrowseSortKey b)
            {
                if (a.Tier != b.Tier) return b.Tier.CompareTo(a.Tier);
                int c = string.CompareOrdinal(b.Group ?? "", a.Group ?? "");
                return c != 0 ? c : string.CompareOrdinal(b.Within ?? "", a.Within ?? "");
            }
        }

        /// <summary>一首歌的 <see cref="BrowseSortKey"/>。</summary>
        public static BrowseSortKey BrowseKey(SongCatalog.Entry e)
        {
            if (e == null) return new BrowseSortKey { Tier = 0, Group = "", Within = "" };
            if (!e.external) return new BrowseSortKey { Tier = 1, Group = "", Within = e.gn ?? "" };
            return new BrowseSortKey
            {
                Tier = 0,
                Group = e.group ?? "",
                // "1"+列號 排在 "0"+gn 之上 → 同一包裡有序的先出，其餘照舊
                Within = e.packOrder >= 0 ? "1" + e.packOrder.ToString("D8") : "0" + (e.gn ?? ""),
            };
        }

        /// <summary>
        /// 每一列要掛哪個標籤（NEW / HOT / 推薦 / 古典）。兩個來源，**歌包說了算**：
        /// <list type="number">
        /// <item>歌包自帶的 serverconfig（<c>entry.badge</c>，見 <see cref="Sdo.Osu.SdoServerConfig"/>）。</item>
        /// <item>其餘的官方歌沿用近似規則「歌單最上面的 N 首 = NEW」—— 官方那份 serverconfig 不在我們手上，
        ///       而清單本身已由 <see cref="Curate"/> 依檔名降冪排好，新歌自然在最上面。外部歌不套這條
        ///       （它們沒有官方編號，也不該因為剛好排在頂端就變 NEW）。</item>
        /// </list>
        /// 回傳 gn → 標籤（gn 是唯一鍵；沒有標籤的歌不會出現在字典裡）。純函式，好測。
        /// </summary>
        public static Dictionary<string, SongBadge> BadgeMap(IReadOnlyList<SongCatalog.Entry> list, int autoNewCount)
        {
            var res = new Dictionary<string, SongBadge>(StringComparer.Ordinal);
            if (list == null) return res;
            foreach (var e in list)
            {
                if (e == null || string.IsNullOrEmpty(e.gn)) continue;
                var b = (SongBadge)e.badge;
                if (b != SongBadge.None) res[e.gn] = b;
            }
            int n = 0;
            for (int i = 0; i < list.Count && n < autoNewCount; i++)
            {
                var e = list[i];
                if (e == null || e.external || string.IsNullOrEmpty(e.gn)) continue;
                n++;
                if (!res.ContainsKey(e.gn)) res[e.gn] = SongBadge.New;
            }
            return res;
        }

        /// <summary>查一首歌的標籤（<paramref name="map"/> 來自 <see cref="BadgeMap"/>）。</summary>
        public static SongBadge BadgeOf(Dictionary<string, SongBadge> map, SongCatalog.Entry e)
            => map != null && e != null && !string.IsNullOrEmpty(e.gn) && map.TryGetValue(e.gn, out var b) ? b : SongBadge.None;

        public int Count => _all.Count;
        public IReadOnlyList<SongCatalog.Entry> All => _all;

        public List<SongCatalog.Entry> Filter(string query) => Filter(_all, query);

        /// <summary>Text search (title/artist/group/gn, case-insensitive) over an arbitrary list — lets the screen
        /// search WITHIN a category subset. group matches an external song's pack/folder label (e.g. "SDO Pack8"), so
        /// a pack is findable by its name as well as by song title. Empty/blank query returns a copy of the whole list.</summary>
        public static List<SongCatalog.Entry> Filter(IReadOnlyList<SongCatalog.Entry> list, string query)
        {
            var res = new List<SongCatalog.Entry>();
            if (list == null) return res;
            if (string.IsNullOrWhiteSpace(query)) { res.AddRange(list); return res; }
            query = query.Trim();
            foreach (var e in list)
                if (e != null && (Contains(e.title, query) || Contains(e.artist, query)
                                  || Contains(e.group, query) || Contains(e.gn, query)))
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
