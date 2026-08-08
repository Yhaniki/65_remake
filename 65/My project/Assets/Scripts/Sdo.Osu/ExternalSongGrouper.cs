using System;
using System.Collections.Generic;
using System.Globalization;

namespace Sdo.Osu
{
    /// <summary>
    /// Splits the charts found in ONE folder into songs. A folder is not always one song: people drop several
    /// beatmap sets in flat next to each other, and StepMania packs put several .sm files side by side.
    ///
    /// The grouping key is the AUDIO FILE, because that is a hard constraint of the engine, not a preference:
    /// <see cref="ExternalSong"/> carries a single AudioPath and a single preview window, so two charts pointing at
    /// different audio files can never be the same song — merge them and one of the two plays against the wrong music.
    /// Fallbacks only matter when a chart names no audio at all:
    ///   1. <c>audio:&lt;filename&gt;</c> — [General] AudioFilename (every .osu has it; matches the engine's constraint)
    ///   2. <c>set:&lt;id&gt;</c>        — [Metadata] BeatmapSetID, only when &gt; 0 (converted/local charts are −1)
    ///   3. <c>meta:&lt;artist&gt;|&lt;title&gt;</c> — when either is non-empty
    ///   4. <c>file:&lt;chart filename&gt;</c> — last resort: rather split one song in two than merge two songs into one
    /// Keys are content-derived (never scan order) because the catalog gn — and therefore favourites — hashes them.
    ///
    /// Pure/testable: metadata in, index groups out.
    /// </summary>
    public static class ExternalSongGrouper
    {
        /// <summary>One song's worth of charts inside a folder.</summary>
        public sealed class SongGroup
        {
            public string Key = "";
            /// <summary>Indices into the caller's candidate lists, hardest (most notes) first.</summary>
            public readonly List<int> Charts = new List<int>();

            /// <summary>這一首曲子**自己的**名字，只有純 keysound 合輯拆出來的組才有("" = 沒拆過的組)。
            /// 合輯裡每張譜的 Title 都是整包的標籤("Piano Beatmap Set")，曲名藏在難度名的前綴裡
            /// ("Canon - 4K EX" → "Canon")—— 見 <see cref="SongNameOf"/>。呼叫端拿它當標題。</summary>
            public string SongName = "";
        }

        /// <summary>Group osu candidates into songs. Groups come out in first-appearance order and their charts are
        /// sorted by note count DESC (ties keep input order), which is the order the difficulty slots and the display
        /// metadata are taken in.</summary>
        public static List<SongGroup> GroupOsu(IReadOnlyList<OsuMeta> metas, IReadOnlyList<string> chartFileNames)
        {
            var groups = new List<SongGroup>();
            if (metas == null) return groups;

            var byKey = new Dictionary<string, SongGroup>(StringComparer.OrdinalIgnoreCase);
            var coarse = new List<SongGroup>();
            for (int i = 0; i < metas.Count; i++)
            {
                string file = chartFileNames != null && i < chartFileNames.Count ? chartFileNames[i] : "";
                string key = KeyOf(metas[i], file);
                if (!byKey.TryGetValue(key, out var g))
                {
                    g = new SongGroup { Key = key };
                    byKey[key] = g;
                    coarse.Add(g);
                }
                g.Charts.Add(i);
            }

            // 純 keysound 合輯要再切一層(見 SplitKeysoundCompilation)。
            foreach (var g in coarse) SplitKeysoundCompilation(metas, chartFileNames, g, groups);

            foreach (var g in groups)
                g.Charts.Sort((a, b) =>
                {
                    int c = metas[b].NoteCount.CompareTo(metas[a].NoteCount);
                    return c != 0 ? c : a.CompareTo(b);
                });
            return groups;
        }

        /// <summary>兩張**曲名不同**的純 keysound 譜,長度差在這個範圍內就當成同一首曲子的兩種難度命名
        /// (BMS 移植的 SP/DP 那種)。抓得這麼緊是實測來的:同一首曲子的難度們最後一顆音落在**同一處**
        /// (量過 18 首 × 7 難度的合輯,同曲同鍵數的譜長差 0 ms),而合輯裡兩首不同曲子最接近的也差 770 ms。</summary>
        public const int SameTrackTolMs = 500;

        /// <summary>連曲名都取不到時,只能純靠長度分曲子 —— 那條路上容差放寬(取 ms 與比例的較寬者),
        /// 寧可少拆一首,也不要把某首歌的難度們拆成好幾首。</summary>
        public const int DurationTolMs = 1500;
        public const double DurationTolFrac = 0.03;

        /// <summary>
        /// 一個 beatmap set 裝了**好幾首不同曲子**的合輯(osu 上常見的「鋼琴精選集」那種),而且全部是純 keysound
        /// (<c>AudioFilename: virtual</c>)—— 這時 <see cref="KeyOf"/> 的每一層都失效:沒有音檔可分、setId 只有一個、
        /// Title 是整包的標籤、Artist 也一樣 → 15 首曲子被併成一首歌,而一首歌只有三個難度槽,剩下的**整首消失**。
        ///
        /// 分兩個線索,而且兩個都要:
        ///   • **曲名** —— 合輯的難度名就是「曲名 - 難度」("Canon - 4K EX"),所以前綴就是身分(<see cref="SongNameOf"/>)。
        ///     這是主鍵:長度會撞(合輯裡最接近的兩首只差 770 ms),名字不會。
        ///   • **譜長** —— 純 keysound 譜的音樂就是它自己的音符,譜長就是曲長。用來擋命名的反例:BMS 移植那種
        ///     「同一首曲子 SP ANOTHER / DP HYPER」名字不同卻是同一首,長度一模一樣 → 併回同一組。
        /// 有任何一張譜的難度名取不到曲名(或只是「4K EX」這種純難度標籤)就整組退回純長度分群 —— 名字不齊
        /// 的話按名分會把同一首曲子切開。
        /// </summary>
        private static void SplitKeysoundCompilation(IReadOnlyList<OsuMeta> metas, IReadOnlyList<string> chartFileNames,
            SongGroup g, List<SongGroup> output)
        {
            if (g.Charts.Count < 2 || !AllVirtualAudio(metas, g.Charts)) { output.Add(g); return; }

            var parts = SplitByName(metas, g.Charts);
            if (parts != null) MergeSameLengthTracks(metas, parts);   // 名字不同卻一樣長 → 同一首曲子
            else parts = SplitByDuration(metas, g.Charts);            // 沒有曲名可用 → 只剩長度

            if (parts.Count < 2) { output.Add(g); return; }   // 就是同一首歌的難度們

            // 每一組的曲名(取不到 → "")。名字齊全而且互不相同時才拿它當 key 的一部分 —— 否則退回
            // 組內字典序最小的譜面檔名(同一份內容在每台機器上都算得出同一個 key,收藏與缺歌傳檔靠它比對)。
            var names = new List<string>(parts.Count);
            bool nameable = true;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts)
            {
                string name = CommonSongName(metas, part);
                names.Add(name);
                if (name.Length == 0 || !seen.Add(name)) nameable = false;
            }

            // 出場順序照「第一次出現」,與沒拆過的組同一個規則(見類別註解)。
            var order = new List<int>(parts.Count);
            for (int p = 0; p < parts.Count; p++) order.Add(p);
            order.Sort((a, b) => Least(parts[a]).CompareTo(Least(parts[b])));

            foreach (var p in order)
            {
                string slug = nameable ? names[p] : LeastChartFileName(chartFileNames, parts[p]);
                var sub = new SongGroup
                {
                    Key = g.Key + "|song:" + slug.ToLowerInvariant(),
                    SongName = names[p],
                };
                sub.Charts.AddRange(parts[p]);
                output.Add(sub);
            }
        }

        private static int Least(List<int> charts)
        {
            int least = int.MaxValue;
            foreach (var i in charts) if (i < least) least = i;
            return least;
        }

        /// <summary>按難度名的曲名前綴分組(保持首次出現順序)。回 null = 有譜取不到曲名,這條線索作廢。</summary>
        private static List<List<int>> SplitByName(IReadOnlyList<OsuMeta> metas, List<int> charts)
        {
            var parts = new List<List<int>>();
            var byName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in charts)
            {
                string name = SongNameOf(metas[i]?.Version);
                if (name.Length == 0 || IsDifficultyLabel(name)) return null;
                if (!byName.TryGetValue(name, out var part))
                {
                    part = new List<int>();
                    byName[name] = part;
                    parts.Add(part);
                }
                part.Add(i);
            }
            return parts;
        }

        /// <summary>名字不同、長度卻幾乎一樣(<see cref="SameTrackTolMs"/>)的組併回同一首曲子 —— 同一份音樂鋪成
        /// SP/DP 兩張譜就是這樣。量不到長度(0)的組誰也不併(沒有證據就別動)。就地改 <paramref name="parts"/>。</summary>
        private static void MergeSameLengthTracks(IReadOnlyList<OsuMeta> metas, List<List<int>> parts)
        {
            if (parts.Count < 2) return;

            var order = new List<int>(parts.Count);
            for (int p = 0; p < parts.Count; p++) order.Add(p);
            order.Sort((a, b) => TrackLength(metas, parts[a]).CompareTo(TrackLength(metas, parts[b])));

            var absorbed = new HashSet<int>();
            for (int k = 1; k < order.Count; k++)
            {
                int prev = order[k - 1], cur = order[k];
                int a = TrackLength(metas, parts[prev]), b = TrackLength(metas, parts[cur]);
                if (a <= 0 || b <= 0 || Math.Abs(a - b) > SameTrackTolMs) continue;
                parts[prev].AddRange(parts[cur]);
                parts[prev].Sort();
                parts[cur] = parts[prev];        // 讓下一輪繼續拿併好的那組比(鏈式)
                absorbed.Add(cur);
            }
            if (absorbed.Count == 0) return;

            var merged = new List<List<int>>(parts.Count - absorbed.Count);
            for (int p = 0; p < parts.Count; p++) if (!absorbed.Contains(p)) merged.Add(parts[p]);
            parts.Clear();
            parts.AddRange(merged);
        }

        /// <summary>一組譜代表的曲長 = 最長的那張(短難度可能少了尾巴的裝飾音)。0 = 量不到。</summary>
        private static int TrackLength(IReadOnlyList<OsuMeta> metas, List<int> charts)
        {
            int max = 0;
            foreach (var i in charts)
            {
                int ms = metas[i]?.LastNoteMs ?? 0;
                if (ms > max) max = ms;
            }
            return max;
        }

        /// <summary>這一組的每張譜都是純 keysound 嗎(沒有一個指向真的音檔)?</summary>
        private static bool AllVirtualAudio(IReadOnlyList<OsuMeta> metas, List<int> charts)
        {
            foreach (var i in charts)
            {
                var m = metas[i];
                if (m == null) return false;
                if (!OsuBeatmapParser.IsVirtualAudioFilename(BaseName(m.AudioFilename))) return false;
            }
            return true;
        }

        /// <summary>按譜長把一組譜切成幾首曲子(<see cref="DurationTolMs"/> / <see cref="DurationTolFrac"/> 的容差)。
        /// 任何一張譜量不到長度(0)就整組不切 —— 寧可維持現況,也不要照著壞資料亂拆。
        /// 回傳的每一組都保持輸入順序,組本身按最短的先出來。</summary>
        private static List<List<int>> SplitByDuration(IReadOnlyList<OsuMeta> metas, List<int> charts)
        {
            var single = new List<List<int>> { new List<int>(charts) };
            foreach (var i in charts) if (metas[i] == null || metas[i].LastNoteMs <= 0) return single;

            var byLength = new List<int>(charts);
            byLength.Sort((a, b) =>
            {
                int c = metas[a].LastNoteMs.CompareTo(metas[b].LastNoteMs);
                return c != 0 ? c : a.CompareTo(b);
            });

            var parts = new List<List<int>>();
            var current = new List<int>();
            int anchor = 0;
            foreach (var i in byLength)
            {
                int ms = metas[i].LastNoteMs;
                // 與這一組的**第一張**(最短的那張)比,不是與前一張:同曲的難度們長度幾乎一樣,
                // 而逐張接力比會讓一整排長度遞增的曲子被串成同一首(容差一路往後累積)。
                if (current.Count == 0) { anchor = ms; current.Add(i); }
                else if (SameLength(anchor, ms)) { current.Add(i); }
                else { parts.Add(current); anchor = ms; current = new List<int> { i }; }
            }
            if (current.Count > 0) parts.Add(current);

            foreach (var part in parts) part.Sort();   // 回到輸入順序
            return parts;
        }

        private static bool SameLength(int a, int b)
        {
            int diff = Math.Abs(a - b);
            double tol = Math.Max(DurationTolMs, Math.Max(a, b) * DurationTolFrac);
            return diff <= tol;
        }

        /// <summary>這一組譜共同的曲名:每張譜的難度名前綴(<see cref="SongNameOf"/>)都一樣才採用,
        /// 而且不能是「EX」「4K Full」那種純難度標籤。取不到 → ""(呼叫端就不改標題)。</summary>
        private static string CommonSongName(IReadOnlyList<OsuMeta> metas, List<int> charts)
        {
            string name = null;
            foreach (var i in charts)
            {
                string n = SongNameOf(metas[i]?.Version);
                if (n.Length == 0 || IsDifficultyLabel(n)) return "";
                if (name == null) name = n;
                else if (!string.Equals(name, n, StringComparison.OrdinalIgnoreCase)) return "";
            }
            return name ?? "";
        }

        /// <summary>合輯的難度名是「曲名 - 難度」("Canon - 4K EX" → "Canon");沒有分隔符就是整串
        /// ("4K HELL CIRCUS")。難度標籤在後面,所以切在**最後**一個 " - "。</summary>
        public static string SongNameOf(string version)
        {
            string v = (version ?? "").Trim();
            if (v.Length == 0) return "";
            int cut = v.LastIndexOf(" - ", StringComparison.Ordinal);
            return cut > 0 ? v.Substring(0, cut).Trim() : v;
        }

        /// <summary>常見的難度名(去掉 "4K"/"7K" 這類鍵數標記之後整串比對)。命名品質用的,不參與分組
        /// —— 猜錯只是少改一次標題,不會讓歌消失。</summary>
        private static readonly string[] DifficultyWords =
        {
            "easy", "normal", "hard", "insane", "expert", "extra", "extreme", "another", "hyper", "beginner",
            "basic", "advanced", "master", "lunatic", "maniac", "crazy", "challenge", "full", "light", "overjoy",
            "ex", "ez", "nm", "hd", "shd", "mx", "sc", "oni", "futsuu", "muzukashii", "kantan", "edit",
        };

        private static bool IsDifficultyLabel(string name)
        {
            string s = (name ?? "").Trim();
            // 前後的鍵數標記("4K EX" / "EX 7k")拿掉再看剩下什麼。
            for (int guard = 0; guard < 2; guard++)
            {
                int sp = s.IndexOf(' ');
                if (sp > 0 && IsKeyCountToken(s.Substring(0, sp))) { s = s.Substring(sp + 1).Trim(); continue; }
                sp = s.LastIndexOf(' ');
                if (sp > 0 && IsKeyCountToken(s.Substring(sp + 1))) { s = s.Substring(0, sp).Trim(); continue; }
                break;
            }
            if (s.Length == 0) return true;   // 只有鍵數 → 不是曲名
            foreach (var w in DifficultyWords)
                if (string.Equals(s, w, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>"4k" / "10K" 這種鍵數標記?</summary>
        private static bool IsKeyCountToken(string token)
        {
            string s = (token ?? "").Trim();
            if (s.Length < 2 || (s[s.Length - 1] != 'k' && s[s.Length - 1] != 'K')) return false;
            for (int i = 0; i < s.Length - 1; i++) if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        /// <summary>組內字典序最小的譜面檔名(小寫)—— 曲名取不到時的 key。</summary>
        private static string LeastChartFileName(IReadOnlyList<string> chartFileNames, List<int> charts)
        {
            string least = null;
            foreach (var i in charts)
            {
                string f = BaseName(chartFileNames != null && i < chartFileNames.Count ? chartFileNames[i] : "")
                    .ToLowerInvariant();
                if (f.Length == 0) continue;
                if (least == null || string.CompareOrdinal(f, least) < 0) least = f;
            }
            return least ?? "";
        }

        /// <summary>The song identity of one .osu inside its folder (see the fallback chain in the class doc).</summary>
        public static string KeyOf(OsuMeta m, string chartFileName)
        {
            if (m != null)
            {
                string audio = BaseName(m.AudioFilename);
                if (audio.Length > 0 && !OsuBeatmapParser.IsVirtualAudioFilename(audio)) return "audio:" + audio.ToLowerInvariant();

                if (m.BeatmapSetId > 0) return "set:" + m.BeatmapSetId.ToString(CultureInfo.InvariantCulture);

                string artist = (m.Artist ?? "").Trim();
                string title = (m.Title ?? "").Trim();
                if (artist.Length > 0 || title.Length > 0)
                    return "meta:" + artist.ToLowerInvariant() + "|" + title.ToLowerInvariant();
            }
            return "file:" + BaseName(chartFileName).ToLowerInvariant();
        }

        /// <summary>Song identity of a .sm file: one file = one song, so the filename IS the key.</summary>
        public static string SmKeyOf(string smFileName) => "file:" + BaseName(smFileName).ToLowerInvariant();

        /// <summary>Group Malody .mc charts into songs, exactly like <see cref="GroupOsu"/>: one .mc is one difficulty, and
        /// the difficulties of a song share its audio file (the bgm note), so the audio is the grouping key. Parallel
        /// input lists (audio filename + note count + chart filename, one entry per .mc). Groups come out in
        /// first-appearance order; each group's charts are sorted by note count DESC (ties keep input order), which is the
        /// order difficulty slots and display metadata are taken in. Pure/testable.</summary>
        public static List<SongGroup> GroupMalody(IReadOnlyList<string> audioNames, IReadOnlyList<int> noteCounts,
            IReadOnlyList<string> chartFileNames)
        {
            var groups = new List<SongGroup>();
            if (audioNames == null) return groups;

            var byKey = new Dictionary<string, SongGroup>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < audioNames.Count; i++)
            {
                string file = chartFileNames != null && i < chartFileNames.Count ? chartFileNames[i] : "";
                string key = McKeyOf(audioNames[i], file);
                if (!byKey.TryGetValue(key, out var g))
                {
                    g = new SongGroup { Key = key };
                    byKey[key] = g;
                    groups.Add(g);
                }
                g.Charts.Add(i);
            }

            foreach (var g in groups)
                g.Charts.Sort((a, b) =>
                {
                    int c = noteCounts[b].CompareTo(noteCounts[a]);
                    return c != 0 ? c : a.CompareTo(b);
                });
            return groups;
        }

        /// <summary>The song identity of one .mc inside its folder: its audio file (from the bgm note) if named, else the
        /// chart filename (rather split one song in two than merge two into one, like the osu fallback chain).</summary>
        public static string McKeyOf(string audioName, string chartFileName)
        {
            string audio = BaseName(audioName);
            if (audio.Length > 0) return "audio:" + audio.ToLowerInvariant();
            return "file:" + BaseName(chartFileName).ToLowerInvariant();
        }

        /// <summary>The audio a group plays, as a bare lower-case filename ("" = the charts name none). Used to let
        /// an .osu song shadow the .sm of the same song sitting in the same folder.</summary>
        public static string AudioNameOf(string key)
            => key != null && key.StartsWith("audio:", StringComparison.Ordinal) ? key.Substring(6) : "";

        /// <summary>Filename part of a chart-declared path (osu/StepMania tags may carry folders and backslashes).</summary>
        public static string BaseName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string s = path.Replace('\\', '/').Trim();
            int slash = s.LastIndexOf('/');
            if (slash >= 0) s = s.Substring(slash + 1);
            return s.Trim();
        }
    }
}
