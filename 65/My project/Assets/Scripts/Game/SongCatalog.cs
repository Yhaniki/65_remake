using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Runtime lookup for song display text (title / artist) that was decoded GB2312 -> UTF-8 at
    /// IMPORT time.
    ///
    /// Why a catalog instead of decoding in <see cref="Sdo.Osu.GnChart"/>: original SDO data
    /// (SongList.dat and the .gn header) stores names in GB2312 (cp936). This runtime
    /// (.NET Standard 2.1 / Android IL2CPP) ships no cp936 codec, so on-device decoding is
    /// impossible and would only produce mojibake. tools/build_song_catalog.py decodes once on a
    /// machine that has gb18030 and writes StreamingAssets/song_catalog.json as pure UTF-8.
    /// Runtime therefore only ever reads Unicode -> no locale-dependent garbling, on any platform.
    /// </summary>
    public static class SongCatalog
    {
        [Serializable] public class Entry
        {
            public string gn; public int fileId; public string title; public string artist;
            // Optional metadata (emitted by build_song_catalog.py; absent in older catalogs -> defaults).
            public float bpm = -1f;
            public int diffEasy = -1, diffNormal = -1, diffHard = -1;
            public int notesEasy, notesNormal, notesHard;
            public int durEasy, durNormal, durHard;   // seconds per difficulty

            // ---- external (user Songs/ folder: osu / StepMania) — absent/false for the official .gn catalog ----
            public bool external;          // true → this row is a scanned external song, not an official .gn
            public string group = "";      // group folder name (the drill-in 資料夾 category groups by this)
            public string audioPath = "";  // absolute audio file (full-song ogg/mp3/wav) for gameplay + preview
            public string imagePath = "";  // absolute SOURCE cover (jacket→banner→background); "" → placeholder disc
            public string folderPath = ""; // the song's folder — where its CD disc + sdo.header sidecar live
            public string songKey = "";    // identity WITHIN the folder ("" = the folder's only song); see ExternalSongGrouper
            public string cdPath = "";     // absolute generated CD disc image; "" → compose it from imagePath on first use
            public int chartFormat;        // 0=none, 1=osu, 2=sm (Sdo.Osu.SongFormat)
            public string chartEasy = "", chartNormal = "", chartHard = "";   // absolute chart file per slot ("" if empty)
            public int chartIdxEasy, chartIdxNormal, chartIdxHard;            // .sm #NOTES block index per slot (osu: 0)
            public int previewStartMs = -1;   // 試聽起點(ms)：osu PreviewTime / SM #SAMPLESTART；-1 = 未指定→中段
            public int previewLengthMs;       // 試聽長度(ms)：SM #SAMPLELENGTH；0 = 未指定→預設長度

            /// <summary>Absolute chart file path for difficulty d (external songs only); "" if that slot is empty.</summary>
            public string ChartPath(int d) => d <= 0 ? chartEasy : (d == 1 ? chartNormal : chartHard);
            /// <summary>.sm note-block index for difficulty d (external songs only; osu always 0).</summary>
            public int ChartIndex(int d) => d <= 0 ? chartIdxEasy : (d == 1 ? chartIdxNormal : chartIdxHard);

            /// <summary>
            /// 單首 offset（毫秒）——StepMania 的 song offset、osu 的 beatmap offset。補「這首譜跟音檔沒對齊」
            /// （譜是別人打的、音檔換過版本）。**動的是音樂＋舞蹈（DPS 掛在音樂時間軸上），不動音符/判定**：
            /// 正 = 音樂延後播放（音檔跑在譜面前面時用），負 = 提早（負得比前導還多時，ScreenGameplay 走
            /// GameRate.ScheduleMusic 從 clip 中途切入）。
            ///
            /// 來源是 song_name_overrides.json 的 <c>offsetMs</c> 欄，跟歌名一樣是**手改**的（見
            /// <see cref="ApplyOverrides"/>）；不在 song_catalog.json（那個是工具重建的，手改會被蓋掉）。
            /// 沒寫就是 0。key 是 stem（sdomNNNN）→ k/t 兩份譜共用同一個值（同一個音檔）。
            ///
            /// 跟機器的音訊延遲**無關**（那個在 ScreenGameplay 的時鐘上自動補掉，全部歌一體適用）。
            /// </summary>
            public float offsetMs;

            /// <summary>Difficulty level for d (0=easy,1=normal,2=hard); -1 if unknown.</summary>
            public int Diff(int d) => d <= 0 ? diffEasy : (d == 1 ? diffNormal : diffHard);
            public int NoteCount(int d) => d <= 0 ? notesEasy : (d == 1 ? notesNormal : notesHard);
            public int DurationSec(int d) => d <= 0 ? durEasy : (d == 1 ? durNormal : durHard);

            /// <summary>Whether difficulty d has a real, playable chart. Judged by the actual NOTE COUNT
            /// (an empty chart has 0 notes), NOT the level field — some songs store level 0 for a
            /// difficulty that carries no notes (e.g. 動畫歌曲串燒 sdom2140k: easy=3417 notes, normal/hard=0).
            /// Those empty difficulties are greyed out / non-selectable in song-select.</summary>
            public bool HasChart(int d) => NoteCount(d) > 0;
        }
        [Serializable] private class Catalog { public Entry[] songs = new Entry[0]; }   // populated by JsonUtility; init to silence CS0649

        // Hand-editable per-song data (StreamingAssets/song_name_overrides.json), seeded from the official
        // songlist.dat open songs. See ApplyOverrides / tools/build_song_name_overrides.py.
        // 名字 + 顯示 bpm + 單首 offset 都住在這裡：它是**唯一**一份手改的歌曲資料，song_catalog.json 是工具重建的。
        [Serializable] private class Override { public string gn = ""; public string title = ""; public string artist = ""; public float bpm = -1f; public float offsetMs = 0f; }   // init to silence CS0649 (JsonUtility fills these)
        [Serializable] private class OverrideDoc { public Override[] songs = new Override[0]; }

        /// <summary>Sanity bound for a hand-typed offsetMs. A stray extra digit (30 -> 3000000) would otherwise
        /// push the music start minutes away / off the end of the clip. ±60 s: imported charts (osu/StepMania)
        /// often carry a long silent lead-in or plainly mis-cut audio, so several seconds is legitimate — this
        /// only guards against runaway typos. Kept in lock-step with the editor nudge clamp (ChartEditorScreen).</summary>
        public const float MaxOffsetMs = 60000f;

        private const string FileName = "song_catalog.json";
        private const string OverrideFileName = "song_name_overrides.json";
        private static Dictionary<string, Entry> _byGn;   // key = lowercase .gn filename
        private static List<Entry> _all;                  // in file order
        private static List<Entry> _primary;              // k-only view of _all (lazy)

        /// <summary>All catalog entries in file order (empty if no catalog). Includes BOTH chart variants —
        /// for a browsable list you almost always want <see cref="Primary"/> instead.</summary>
        public static IReadOnlyList<Entry> All { get { EnsureLoaded(); return _all; } }

        /// <summary>
        /// 每首歌在原始資料裡有**兩份譜**：sdomNNNN<b>k</b>.gn（鍵盤）與 sdomNNNN<b>t</b>.gn（毯子）。
        /// 兩者共用同一個標題／曲師／音檔，但難度與音符數不同 —— 例 sdom0001：k = LV 3/4/5、easy 510 notes；
        /// t = LV 1/3/5、easy 284 notes（毯子譜比較鬆）。
        ///
        /// 重製版是**純鍵盤**，所以任何「給人瀏覽的清單」都只該出現 k：不濾的話目錄的 4346 筆會讓每首歌
        /// 在清單裡出現兩次（2175 首 × 2），而且因為 <see cref="ApplyNameOverrides"/> 是照 stem 蓋名字，
        /// 兩列連標題都一模一樣，看起來就是整份清單重複。
        ///
        /// <see cref="All"/> 刻意**不濾**：以 gn 反查標題／曲師、字型預熱都需要兩種變體都在。
        /// </summary>
        public static bool IsPrimaryVariant(string gnPathOrName)
        {
            if (string.IsNullOrEmpty(gnPathOrName)) return false;
            var name = Path.GetFileName(gnPathOrName).ToLowerInvariant();
            if (name.EndsWith(".gn")) name = name.Substring(0, name.Length - 3);
            return name.Length > 0 && name[name.Length - 1] == 'k';
        }

        /// <summary>只有鍵盤譜（k）的清單，檔案順序。**所有給人瀏覽的清單都該用這個**（選歌畫面、譜面編輯器），
        /// 不是 <see cref="All"/>。名字已經套過 song_name_overrides.json。</summary>
        public static IReadOnlyList<Entry> Primary
        {
            get
            {
                EnsureLoaded();
                if (_primary == null)
                {
                    _primary = new List<Entry>();
                    foreach (var e in _all) if (e != null && IsPrimaryVariant(e.gn)) _primary.Add(e);
                }
                return _primary;
            }
        }

        /// <summary>Look up by a .gn path or filename (case-insensitive). Null if absent / no catalog.</summary>
        public static Entry Get(string gnPathOrName)
        {
            if (string.IsNullOrEmpty(gnPathOrName)) return null;
            EnsureLoaded();
            var key = Path.GetFileName(gnPathOrName).ToLowerInvariant();
            return _byGn.TryGetValue(key, out var e) ? e : null;
        }

        public static string Title(string gnPathOrName) => Get(gnPathOrName)?.title;
        public static string Artist(string gnPathOrName) => Get(gnPathOrName)?.artist;

        /// <summary>Merge scanned external songs (user Songs/ folder — osu / StepMania) into the catalog, AFTER the
        /// official catalog is loaded. Entries whose gn already exists are skipped (idempotent). The song-select
        /// list picks these up because <see cref="All"/> now includes them (browsed via the 資料夾 group category;
        /// they are excluded from the 全部 tab — see SongSelectScreen.CategoryBase).</summary>
        public static void RegisterExternal(IEnumerable<Entry> entries)
        {
            if (entries == null) return;
            EnsureLoaded();
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrEmpty(e.gn)) continue;
                var key = e.gn.ToLowerInvariant();
                if (_byGn.ContainsKey(key)) continue;
                _byGn[key] = e;
                _all.Add(e);
            }
        }

        /// <summary>這首譜的單首 offset（毫秒）；沒設過 = 0。見 <see cref="Entry.offsetMs"/>。</summary>
        public static float OffsetMs(string gnPathOrName) => Get(gnPathOrName)?.offsetMs ?? 0f;

        private static float ClampOffsetMs(float ms)
            => float.IsNaN(ms) ? 0f : (ms < -MaxOffsetMs ? -MaxOffsetMs : (ms > MaxOffsetMs ? MaxOffsetMs : ms));

        /// <summary>
        /// 這首歌是否符合搜尋字串 —— 比對 標題／曲師／gn 檔名／<b>fileId 編號</b>，全部是 case-insensitive 子字串。
        /// 空字串／null／全空白 → 一律符合（＝不過濾）。純邏輯、不碰硬碟，給歌單搜尋用（見 SongCatalogSearchTests）。
        ///
        /// fileId 是歌曲編號，封面圖（<c>NNNN.PNG</c>）、試聽（<c>exper/NNNN.ogg</c>）、編舞（<c>DANCE/NNNN.DPS</c>）都用它，
        /// 跟 gn 檔名裡的號碼**不一樣**：sdom0001<b>k</b>.gn 的 fileId 是 10001、sdom0001<b>t</b>.gn 是 1。
        /// 所以「照封面上的編號找歌」只有這個欄位辦得到 —— 拿編號去比 gn 子字串是找不到的。
        /// </summary>
        public static bool Matches(Entry e, string query)
        {
            if (e == null) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;
            query = query.Trim();
            return Has(e.title, query) || Has(e.artist, query) || Has(e.gn, query)
                || e.fileId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                       .IndexOf(query, StringComparison.Ordinal) >= 0;   // 編號一律 ASCII 數字 → Ordinal
        }

        private static bool Has(string hay, string needle)
            => !string.IsNullOrEmpty(hay) && hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void EnsureLoaded()
        {
            if (_byGn != null) return;
            _byGn = new Dictionary<string, Entry>(StringComparer.Ordinal);
            _all = new List<Entry>();

            var path = Path.Combine(Application.streamingAssetsPath, FileName);
            // NOTE: direct File IO from StreamingAssets works in Editor / standalone. On Android the
            // catalog lives compressed inside the APK and must be read via UnityWebRequest instead
            // (same as the .ogg loader in ScreenGameplay). Wire that when packaging for Android.
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[SongCatalog] {path} missing — run tools/build_song_catalog.py");
                return;
            }

            try
            {
                // explicit UTF-8 so the read never falls back to the OS/locale default encoding
                var cat = JsonUtility.FromJson<Catalog>(File.ReadAllText(path, Encoding.UTF8));
                if (cat?.songs == null) return;
                foreach (var e in cat.songs)
                    if (!string.IsNullOrEmpty(e?.gn)) { _byGn[e.gn.ToLowerInvariant()] = e; _all.Add(e); }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SongCatalog] failed to load {path}: {ex.Message}");
            }

            var ovPath = Path.Combine(Application.streamingAssetsPath, OverrideFileName);
            if (File.Exists(ovPath))                                        // optional: no overrides -> keep k.gn values
                ApplyOverrides(_all, File.ReadAllText(ovPath, Encoding.UTF8), ovPath);
        }

        /// <summary>
        /// Overlay the hand-editable list (StreamingAssets/song_name_overrides.json) onto catalog
        /// entries: for every song whose gn-stem (sdomNNNN, without the k/t chart suffix) is listed,
        /// replace title / artist / bpm / offsetMs. Songs absent from the list keep their k.gn-derived
        /// values, as does any field left blank (title/artist) or &lt;= 0 (bpm).
        ///
        /// **這是唯一一份手改的歌曲資料** —— song_catalog.json 是工具從 .gn 重建的（bpm / 難度 / 音符數以實際
        /// 譜面為準，重掃會蓋掉），所以任何「人決定的東西」都只能住在這裡。title/artist/bpm 只改**顯示**
        /// （選歌資訊面板 + 房間 BPM 標籤），一個都不會 desync 遊戲時間軸（音符時間/流速仍全部來自譜面本身）。
        /// 唯一會進到 gameplay 的是 <c>offsetMs</c>（挪音樂起點，見 <see cref="Entry.offsetMs"/>）；缺/0 = 不位移。
        /// 檔案不存在／空的／壞掉 → 目錄原樣不動。

        /// </summary>
        public static void ApplyOverrides(IEnumerable<Entry> entries, string json, string srcLabel = OverrideFileName)
        {
            if (entries == null || string.IsNullOrWhiteSpace(json)) return;

            Dictionary<string, Override> byStem;
            try
            {
                var doc = JsonUtility.FromJson<OverrideDoc>(json);
                if (doc?.songs == null || doc.songs.Length == 0) return;
                byStem = new Dictionary<string, Override>(StringComparer.Ordinal);
                foreach (var o in doc.songs)
                    if (!string.IsNullOrEmpty(o?.gn)) byStem[Stem(o.gn)] = o;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SongCatalog] failed to load {srcLabel}: {ex.Message}");
                return;
            }

            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrEmpty(e.gn)) continue;
                if (!byStem.TryGetValue(Stem(e.gn), out var o)) continue;   // not listed -> keep k.gn values, offset 0
                if (!string.IsNullOrEmpty(o.title)) e.title = o.title;
                if (!string.IsNullOrEmpty(o.artist)) e.artist = o.artist;
                if (o.bpm > 0f) e.bpm = o.bpm;
                e.offsetMs = ClampOffsetMs(o.offsetMs);   // 0 / 缺欄 = 不位移（k/t 共用同一個值：同一個音檔）
            }
        }

        /// <summary>
        /// Full-song audio filename for a chart gn: "sdom2784k.gn" -> "sdom2784.ogg" (the two charts of a
        /// song share one .ogg, which is named after the stem). Null for an empty/degenerate name.
        ///
        /// Single source of truth for gameplay (FrontendApp) AND the song-select preview fallback, which
        /// used to derive it two different ways — a regex 'sdom\d+' vs this stem. They agree for a plain
        /// sdomNNNN{k,t}.gn, but not for a hand-added chart whose stem carries a suffix (sdom1234_1k.gn,
        /// used to slot in a song whose number is already taken): the regex would silently resolve it to
        /// sdom1234.ogg — the OTHER song's audio.
        /// </summary>
        public static string MainOggName(string gnPathOrName)
        {
            var stem = Stem(gnPathOrName);
            return stem.Length > 0 ? stem + ".ogg" : null;
        }

        /// <summary>gn filename/path -> chart-pair stem, e.g. "sdom0001k.gn" / "SDOM0001T" -> "sdom0001".
        /// The k/t suffix distinguishes the two charts of one song; both share a title.</summary>
        private static string Stem(string gnPathOrName)
        {
            var name = Path.GetFileName(gnPathOrName ?? string.Empty).ToLowerInvariant();
            if (name.EndsWith(".gn")) name = name.Substring(0, name.Length - 3);
            if (name.Length > 0 && (name[name.Length - 1] == 'k' || name[name.Length - 1] == 't'))
                name = name.Substring(0, name.Length - 1);
            return name;
        }
    }
}
