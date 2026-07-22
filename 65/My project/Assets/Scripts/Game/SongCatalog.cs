using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 遊戲歌單視圖：歌名 / 曲師 / BPM / 難度 / 音符數 / 單首 offset。
    ///
    /// 資料來自 <see cref="SongTable"/>（StreamingAssets/song_table.csv，全部歌曲資料的唯一來源）；
    /// 這個類別只負責「歌單怎麼看」——k/t 兩份譜的過濾、搜尋比對、.ogg 檔名推導。
    /// 文字在 import 時就已經 GB2312 → UTF-8 解好（見 SongTable 的型別註解），runtime 只碰 Unicode。
    /// </summary>
    public static class SongCatalog
    {
        [Serializable] public class Entry
        {
            public string gn; public int fileId; public string title; public string artist;
            public float bpm = -1f;
            public int diffEasy = -1, diffNormal = -1, diffHard = -1;
            public int notesEasy, notesNormal, notesHard;
            public int durEasy, durNormal, durHard;   // seconds per difficulty

            /// <summary>
            /// 單首 offset（毫秒）——StepMania 的 song offset、osu 的 beatmap offset。補「這首譜跟音檔沒對齊」
            /// （譜是別人打的、音檔換過版本）。**動的是音樂＋舞蹈（DPS 掛在音樂時間軸上），不動音符/判定**：
            /// 正 = 音樂延後播放（音檔跑在譜面前面時用），負 = 提早（負得比前導還多時，ScreenGameplay 走
            /// GameRate.ScheduleMusic 從 clip 中途切入）。
            ///
            /// 來源是 song_table.csv 的 <c>offsetMs</c> 欄，跟歌名一樣是**手改**的；同一首歌的 k/t 兩列共用
            /// 同一個值（同一個音檔），寫檔的工具會自動同步兩列。沒填就是 0。
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

        /// <summary>Sanity bound for a hand-typed offsetMs. A stray extra digit (30 -> 3000000) would otherwise
        /// push the music start minutes away / off the end of the clip. ±60 s: imported charts (osu/StepMania)
        /// often carry a long silent lead-in or plainly mis-cut audio, so several seconds is legitimate — this
        /// only guards against runaway typos. Kept in lock-step with the editor nudge clamp (ChartEditorScreen).</summary>
        public const float MaxOffsetMs = 60000f;

        private static Dictionary<string, Entry> _byGn;   // key = lowercase .gn filename
        private static List<Entry> _all;                  // in file order
        private static List<Entry> _primary;              // k-only view of _all (lazy)

        /// <summary>All catalog entries in file order (empty if no table). Includes BOTH chart variants —
        /// for a browsable list you almost always want <see cref="Primary"/> instead.</summary>
        public static IReadOnlyList<Entry> All { get { EnsureLoaded(); return _all; } }

        /// <summary>
        /// 每首歌在原始資料裡有**兩份譜**：sdomNNNN<b>k</b>.gn（鍵盤）與 sdomNNNN<b>t</b>.gn（毯子）。
        /// 兩者共用同一個標題／曲師／音檔，但難度與音符數不同 —— 例 sdom0001：k = LV 3/4/5、easy 510 notes；
        /// t = LV 1/3/5、easy 284 notes（毯子譜比較鬆）。
        ///
        /// 重製版是**純鍵盤**，所以任何「給人瀏覽的清單」都只該出現 k：不濾的話目錄的 4325 筆會讓每首歌
        /// 在清單裡出現兩次（2166 首 × 2），而且因為兩列的歌名是同步的，連標題都一模一樣，
        /// 看起來就是整份清單重複。
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
        /// 不是 <see cref="All"/>。</summary>
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

        /// <summary>Look up by a .gn path or filename (case-insensitive). Null if absent / no table.</summary>
        public static Entry Get(string gnPathOrName)
        {
            if (string.IsNullOrEmpty(gnPathOrName)) return null;
            EnsureLoaded();
            var key = Path.GetFileName(gnPathOrName).ToLowerInvariant();
            return _byGn.TryGetValue(key, out var e) ? e : null;
        }

        public static string Title(string gnPathOrName) => Get(gnPathOrName)?.title;
        public static string Artist(string gnPathOrName) => Get(gnPathOrName)?.artist;

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

        /// <summary>丟掉快取（改過 song_table.csv 之後用）。下次存取重新從 <see cref="SongTable"/> 建。</summary>
        public static void Invalidate()
        {
            _byGn = null; _all = null; _primary = null;
            SongTable.Invalidate();
        }

        private static void EnsureLoaded()
        {
            if (_byGn != null) return;
            _byGn = new Dictionary<string, Entry>(StringComparer.Ordinal);
            _all = new List<Entry>();
            _primary = null;
            foreach (var r in SongTable.Rows)
            {
                var e = FromRow(r);
                if (e == null) continue;
                _byGn[e.gn] = e; _all.Add(e);
            }
        }

        /// <summary><see cref="SongTable.Row"/> → 歌單 entry（純轉換，有測試）。null / 沒有 gn → null。</summary>
        public static Entry FromRow(SongTable.Row r)
        {
            if (r == null || string.IsNullOrEmpty(r.gn)) return null;
            return new Entry
            {
                gn = r.gn.ToLowerInvariant(),
                fileId = r.fileId,
                title = r.title,
                artist = r.artist,
                bpm = r.bpm,
                diffEasy = At(r.levels, 0, -1), diffNormal = At(r.levels, 1, -1), diffHard = At(r.levels, 2, -1),
                notesEasy = At(r.noteCounts, 0, 0), notesNormal = At(r.noteCounts, 1, 0), notesHard = At(r.noteCounts, 2, 0),
                durEasy = At(r.durations, 0, 0), durNormal = At(r.durations, 1, 0), durHard = At(r.durations, 2, 0),
                offsetMs = ClampOffsetMs(r.offsetMs),
            };
        }

        private static int At(int[] a, int i, int fallback) => a != null && i < a.Length ? a[i] : fallback;

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
        public static string Stem(string gnPathOrName)
        {
            var name = Path.GetFileName(gnPathOrName ?? string.Empty).ToLowerInvariant();
            if (name.EndsWith(".gn")) name = name.Substring(0, name.Length - 3);
            if (name.Length > 0 && (name[name.Length - 1] == 'k' || name[name.Length - 1] == 't'))
                name = name.Substring(0, name.Length - 1);
            return name;
        }
    }
}
