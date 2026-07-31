using System;
using System.Collections.Generic;
using System.Globalization;
using Sdo.Game;

namespace Sdo.UI.Catalog
{
    /// <summary>How the 資料夾 category's browse panel slices the song pool into buckets.</summary>
    public enum SongGroupMode { Folder = 0, Title = 1, Artist = 2, Bpm = 3 }

    /// <summary>One bucket of the current grouping: its section name plus the songs in it (already ordered).</summary>
    public sealed class SongBucket
    {
        public string Key = "";   // section name — the folder name, "B"/"NUM"/"OTHER", or "140-159"
        public readonly List<SongCatalog.Entry> Songs = new List<SongCatalog.Entry>();
        public int Count => Songs.Count;
    }

    /// <summary>
    /// Groups a song pool into browsable sections — a port of StepMania's sort/section rules
    /// (SM-YHANIKI src/SongUtil.cpp: <c>MakeSortString</c>, <c>GetSectionNameFromSongAndSort</c>,
    /// <c>SortSongPointerArrayBySectionName</c>), which is what the song-select group panel lists:
    ///
    ///   資料夾 (Folder) — the song's group folder (SORT_GROUP).
    ///   歌名 / 歌手 (Title/Artist) — first character of the sort string: digits bucket to
    ///     <see cref="Num"/>, A–Z to that letter, anything else (CJK, symbols, blank) to <see cref="Other"/>.
    ///     Sections order NUM first, then A–Z, OTHER last — exactly SM's "0"/"1"+name/"2" ordering.
    ///   BPM — fixed <see cref="BpmGroupSize"/>-wide bands off the song's BPM ("100-149"); songs with no
    ///     BPM in the catalog (bpm ≤ 0) bucket to <see cref="UnknownBpm"/>, which sorts last.
    ///
    /// Pure logic (no Unity, no IO) — the panel only renders what comes out of <see cref="Build"/>.
    /// </summary>
    public static class SongGrouping
    {
        /// <summary>BPM band width (SM's <c>iBPMGroupSize</c> is 20; we use 50-wide bands — "100-149" — so the
        /// browse list has fewer, coarser BPM sections).</summary>
        public const int BpmGroupSize = 50;

        public const string Num = "NUM";          // title/artist starts with a digit
        public const string Other = "OTHER";      // title/artist starts with anything else (incl. CJK) or is blank
        public const string UnknownBpm = "???";   // song carries no BPM
        public const string Ungrouped = "";       // song carries no group folder

        /// <summary>The modes the panel offers, in tab order (資料夾 is the default / first).</summary>
        public static readonly SongGroupMode[] Modes =
        {
            SongGroupMode.Folder, SongGroupMode.Title, SongGroupMode.Artist, SongGroupMode.Bpm,
        };

        /// <summary>
        /// SM's <c>SongUtil::MakeSortString</c>: upper-case, drop a leading '.' (".59" → "59"), and prefix
        /// anything not starting alphanumeric with '~' (char 126) so it sorts to the very end. (We also trim
        /// first, so a stray leading space doesn't push an otherwise ordinary title into OTHER.)
        /// </summary>
        public static string MakeSortString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim().ToUpperInvariant();
            if (s.Length == 0) return "";
            if (s[0] == '.') s = s.Substring(1);
            if (s.Length == 0) return "";
            char c = s[0];
            if ((c < 'A' || c > 'Z') && (c < '0' || c > '9')) s = "~" + s;
            return s;
        }

        /// <summary>The section a song falls in — SM's <c>GetSectionNameFromSongAndSort</c>.</summary>
        public static string SectionName(SongCatalog.Entry e, SongGroupMode mode)
        {
            if (e == null) return Other;
            switch (mode)
            {
                case SongGroupMode.Folder: return e.group ?? Ungrouped;
                case SongGroupMode.Title: return InitialSection(e.title);
                case SongGroupMode.Artist: return InitialSection(e.artist);
                case SongGroupMode.Bpm: return BpmSection(e.bpm);
                default: return Other;
            }
        }

        /// <summary>First-letter section of a title/artist: <see cref="Num"/> / "A".."Z" / <see cref="Other"/>.</summary>
        public static string InitialSection(string s)
        {
            s = MakeSortString(s);
            if (s.Length == 0) return Other;   // SM returns "" here; an unnamed song reads as 其他 in the panel
            char c = s[0];
            if (c >= '0' && c <= '9') return Num;
            if (c < 'A' || c > 'Z') return Other;
            return s.Substring(0, 1);
        }

        /// <summary>BPM band containing <paramref name="bpm"/>, e.g. 100 or 145 → "100-149"; ≤0 → <see cref="UnknownBpm"/>.</summary>
        public static string BpmSection(double bpm)
        {
            if (!(bpm > 0)) return UnknownBpm;   // catches 0, negatives and NaN (catalog stores -1 = unknown)
            int max = (int)bpm;
            max += BpmGroupSize - (max % BpmGroupSize) - 1;   // SM: round up to the top of the band
            int min = max - (BpmGroupSize - 1);
            return min.ToString("000", CultureInfo.InvariantCulture) + "-" +
                   max.ToString("000", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Ordering key for a section — SM's <c>SortSongPointerArrayBySectionName</c>: NUM first ("0"),
        /// named sections in the middle ("1"+sort string), OTHER last ("2"). BPM bands order by their NUMERIC low
        /// bound (see below); the unknown-BPM bucket sorts last.
        /// </summary>
        public static string SortKey(string section, SongGroupMode mode)
        {
            switch (mode)
            {
                case SongGroupMode.Bpm:
                    if (section == UnknownBpm) return "2";
                    // Order BPM bands by their numeric low bound, NOT as raw strings: "10000-…" string-compares below
                    // "200-…" (2nd char '0' < '2'), so a 4–5 digit BPM band wrongly wedges in just after "100-…". Key
                    // on the parsed low bound, zero-padded wide so the comparison stays numeric for any real BPM.
                    int dash = section.IndexOf('-');
                    string lo = dash > 0 ? section.Substring(0, dash) : section;
                    return int.TryParse(lo, NumberStyles.Integer, CultureInfo.InvariantCulture, out int min)
                        ? "1" + min.ToString("D8", CultureInfo.InvariantCulture)
                        : "1" + section;
                case SongGroupMode.Title:
                case SongGroupMode.Artist:
                    if (section == Num) return "0";
                    if (section == Other) return "2";
                    return "1" + MakeSortString(section);
                default:   // Folder: an unnamed folder sorts last, like OTHER
                    return string.IsNullOrEmpty(section) ? "2" : "1" + MakeSortString(section);
            }
        }

        /// <summary>
        /// Slice <paramref name="songs"/> into ordered buckets. Sections are ordered by <see cref="SortKey"/>;
        /// the songs inside each bucket are ordered by what the mode groups on — 資料夾 keeps the pack's OWN
        /// order when it ships a serverconfig (see <see cref="ByPackOrderThenTitle"/>), 歌名 is plain title,
        /// 歌手/BPM are artist/BPM then title — always tie-broken by gn so the list is stable across scans.
        /// </summary>
        public static List<SongBucket> Build(IReadOnlyList<SongCatalog.Entry> songs, SongGroupMode mode)
        {
            var buckets = new List<SongBucket>();
            if (songs == null) return buckets;

            // OrdinalIgnoreCase: folder names differing only in case ("Anime"/"anime", e.g. two roots) are one bucket.
            var byKey = new Dictionary<string, SongBucket>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in songs)
            {
                if (e == null) continue;
                string key = SectionName(e, mode);
                if (!byKey.TryGetValue(key, out var b))
                {
                    b = new SongBucket { Key = key };
                    byKey[key] = b;
                    buckets.Add(b);
                }
                b.Songs.Add(e);
            }

            buckets.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(SortKey(a.Key, mode), SortKey(b.Key, mode));
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            foreach (var b in buckets) SortSongs(b.Songs, mode);
            return buckets;
        }

        /// <summary>Index of the bucket named <paramref name="key"/> (case-insensitive); -1 if absent.</summary>
        public static int IndexOfKey(IReadOnlyList<SongBucket> buckets, string key)
        {
            if (buckets == null || key == null) return -1;
            for (int i = 0; i < buckets.Count; i++)
                if (buckets[i] != null && string.Equals(buckets[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        private static void SortSongs(List<SongCatalog.Entry> list, SongGroupMode mode)
        {
            switch (mode)
            {
                case SongGroupMode.Artist:
                    list.Sort((a, b) =>
                    {
                        int c = string.CompareOrdinal(MakeSortString(a.artist), MakeSortString(b.artist));
                        return c != 0 ? c : ByTitle(a, b);
                    });
                    break;
                case SongGroupMode.Bpm:
                    list.Sort((a, b) =>
                    {
                        int c = a.bpm.CompareTo(b.bpm);
                        return c != 0 ? c : ByTitle(a, b);
                    });
                    break;
                case SongGroupMode.Folder:
                    list.Sort(ByPackOrderThenTitle);   // 資料夾 = 「照這個歌包本來的樣子」
                    break;
                default:   // Title: plain title order
                    list.Sort(ByTitle);
                    break;
            }
        }

        /// <summary>
        /// 資料夾模式的排序：歌包自帶 serverconfig 的（<c>packOrder &gt;= 0</c>）**照包自己的順序** ——
        /// 官方選單是反序畫的、歌曲表的最後一列在最上面，所以列號降冪；沒有的沿用歌名序，排在有序的後面。
        /// 使用者明確選 歌名/歌手/BPM 時不套這條（那是他指定的排序）。
        /// 見 docs/reverse-engineering/SDO_SERVERCONFIG.md。
        /// </summary>
        private static int ByPackOrderThenTitle(SongCatalog.Entry a, SongCatalog.Entry b)
        {
            bool pa = a != null && a.packOrder >= 0, pb = b != null && b.packOrder >= 0;
            if (pa != pb) return pa ? -1 : 1;
            if (pa)
            {
                int c = b.packOrder.CompareTo(a.packOrder);
                if (c != 0) return c;
            }
            return ByTitle(a, b);
        }

        private static int ByTitle(SongCatalog.Entry a, SongCatalog.Entry b)
        {
            int c = string.CompareOrdinal(MakeSortString(a.title ?? a.gn), MakeSortString(b.title ?? b.gn));
            return c != 0 ? c : string.CompareOrdinal(a.gn ?? "", b.gn ?? "");
        }
    }
}
