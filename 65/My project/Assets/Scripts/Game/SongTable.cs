using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// **全部歌曲資料的唯一來源**：StreamingAssets/song_table.csv（一列 = 一個 .gn 檔）。
    ///
    /// 以前這些東西分散在四份 JSON（gn_header_catalog / gn_keytable / song_catalog /
    /// song_name_overrides），四份都以 .gn 檔名為 key、內容大量重疊，各有各的重建工具 ——
    /// 只要有一份沒重跑就對不起來。現在整併成一張表，runtime 只開這一個檔，
    /// <see cref="SongCatalog"/>（歌單）、<see cref="GnKeyTable"/>（解密 seed）、
    /// <see cref="GnHeaderCatalog"/>（原文歌名）全部是這張表的視圖。
    ///
    /// 為什麼是 CSV 而不是 JSON：這份表要能用 Excel 直接開來改歌名（UTF-8-BOM 就是為了繁中 Excel），
    /// 而 Unity 的 JsonUtility 只認得宣告過的欄位 —— 任何工具沒宣告到的欄位在來回序列化時會靜靜消失。
    /// CSV 是逐欄名對應的，多欄少欄都不會壞，寫回時也只動要動的那一格。
    ///
    /// 文字全部是 UTF-8：原始資料（.gn 表頭 / songlist.dat）是 GB2312(cp936)，這個 runtime
    /// （.NET Standard 2.1 / IL2CPP）沒有 cp936 codec，裝置上解只會得到 mojibake 而且隨 OS locale 飄。
    /// 所以解碼一律在 import 時由 tools/ 做掉，runtime 只碰 Unicode。
    /// </summary>
    public static class SongTable
    {
        public const string FileName = "song_table.csv";

        /// <summary>一個 .gn 檔的全部資料。欄位名對齊 CSV 表頭（見 tools/song_table.py 的 COLUMNS）。</summary>
        public class Row
        {
            // ── 顯示（同一首歌的 k/t 兩列相同；tools 寫檔時會同步）────────────────
            public int fileId;          // 歌曲編號：封面 NNNN.PNG / 試聽 exper/NNNN.ogg / 編舞 DANCE/NNNN.DPS
            public string title = "";   // 顯示歌名（繁體，可手改）
            public string artist = "";
            public string producer = "";// 譜師：k 譜與 t 譜常常不同人，屬於「每份譜自己的」資料
            public string gn = "";      // key：.gn 檔名（小寫）
            public string mode = "";    // K = 鍵盤譜、T = 毯子譜
            public float bpm = -1f;     // 顯示 BPM（判定與流速一律讀譜面本身）
            public float offsetMs;      // 這首的音訊校正（見 SongCatalog.Entry.offsetMs）
            public string src = "";     // 歌名來源標記，僅供辨識

            // ── 譜面數值（每個難度一格：0=easy 1=normal 2=hard）──────────────────
            public int[] levels = { -1, -1, -1 };
            public int[] noteCounts = { 0, 0, 0 };
            public int[] durations = { 0, 0, 0 };      // 秒
            public int[] measurements = { 0, 0, 0 };   // 小節
            public float chartBpm = -1f;               // .gn 表頭原始 BPM（bpm 是可手改的顯示值）

            // ── 原文歌名（.gn 表頭 GB2312 → UTF-8）────────────────────────────────
            public string titleZhCn = "", artistZhCn = "", titleEn = "", artistEn = "", origName = "";

            // ── 解密（GnChart 解 .gn 用）。seed 是 uint32 → 存 long，用的時候 cast。──
            public string enc = "";     // sdom / rewu / ddrm / plain / unknown
            public long seed, seed1, seed2;
            public int innerOff, size;
        }

        private static Dictionary<string, Row> _byGn;   // key = lowercase .gn filename
        private static List<Row> _rows;

        /// <summary>整張表，檔案順序（gn 升冪）。載入失敗 → 空。</summary>
        public static IReadOnlyList<Row> Rows { get { EnsureLoaded(); return _rows; } }

        /// <summary>以 .gn 路徑或檔名查（不分大小寫）。查不到 → null。</summary>
        public static Row Get(string gnPathOrName)
        {
            if (string.IsNullOrEmpty(gnPathOrName)) return null;
            EnsureLoaded();
            return _byGn.TryGetValue(Path.GetFileName(gnPathOrName).ToLowerInvariant(), out var r) ? r : null;
        }

        public static string FilePath => Path.Combine(Application.streamingAssetsPath, FileName);

        private static void EnsureLoaded()
        {
            if (_rows != null) return;
            _rows = new List<Row>();
            _byGn = new Dictionary<string, Row>(StringComparer.Ordinal);

            var path = FilePath;
            // Editor / standalone 直接讀檔即可。Android 上這個檔壓在 APK 裡，要改走 UnityWebRequest
            // （跟 ScreenGameplay 的 .ogg 載入同一套），打包 Android 時再接。
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[SongTable] {path} missing — run tools/build_song_table.py");
                return;
            }
            try
            {
                Load(File.ReadAllText(path, Encoding.UTF8));   // 明確指定 UTF-8，不吃 OS locale
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SongTable] failed to load {path}: {ex.Message}");
            }
        }

        /// <summary>把整份 CSV 文字灌進來（測試 / 重新載入用）。null 或空 → 清空。</summary>
        public static void Load(string csvText)
        {
            _rows = Parse(csvText);
            _byGn = new Dictionary<string, Row>(StringComparer.Ordinal);
            foreach (var r in _rows) _byGn[r.gn] = r;
        }

        /// <summary>丟掉快取，下次存取重讀檔（工具寫回 CSV 之後用）。</summary>
        public static void Invalidate() { _rows = null; _byGn = null; }

        /// <summary>
        /// 純函式版的解析（有測試）：CSV 文字 → 每列一個 <see cref="Row"/>。
        ///
        /// 欄位是**照表頭名字**對應的，不是照位置 —— 工具之後加欄位、調順序都不會讓 runtime 解錯格。
        /// 表頭沒有的欄位一律留預設值；gn 空白的列直接跳過。
        /// </summary>
        public static List<Row> Parse(string csvText)
        {
            var rows = new List<Row>();
            var lines = ParseCsv(csvText);
            if (lines.Count == 0) return rows;

            var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var header = lines[0];
            for (int i = 0; i < header.Length; i++)
                if (!string.IsNullOrEmpty(header[i])) col[header[i].Trim()] = i;
            if (!col.ContainsKey("gn")) return rows;      // 沒有 key 欄 = 這不是歌曲表

            for (int i = 1; i < lines.Count; i++)
            {
                var f = lines[i];
                var gn = Str(f, col, "gn").Trim().ToLowerInvariant();
                if (gn.Length == 0) continue;
                var r = new Row
                {
                    gn = gn,
                    fileId = Int(f, col, "fileId", 0),
                    title = Str(f, col, "title"),
                    artist = Str(f, col, "artist"),
                    producer = Str(f, col, "producer"),
                    mode = Str(f, col, "mode"),
                    bpm = Float(f, col, "bpm", -1f),
                    offsetMs = Float(f, col, "offsetMs", 0f),
                    src = Str(f, col, "src"),
                    chartBpm = Float(f, col, "chartBpm", -1f),
                    levels = new[] { Int(f, col, "lvEasy", -1), Int(f, col, "lvNormal", -1), Int(f, col, "lvHard", -1) },
                    noteCounts = new[] { Int(f, col, "notesEasy", 0), Int(f, col, "notesNormal", 0), Int(f, col, "notesHard", 0) },
                    durations = new[] { Int(f, col, "durEasy", 0), Int(f, col, "durNormal", 0), Int(f, col, "durHard", 0) },
                    measurements = new[] { Int(f, col, "measEasy", 0), Int(f, col, "measNormal", 0), Int(f, col, "measHard", 0) },
                    titleZhCn = Str(f, col, "titleZhCn"),
                    artistZhCn = Str(f, col, "artistZhCn"),
                    titleEn = Str(f, col, "titleEn"),
                    artistEn = Str(f, col, "artistEn"),
                    origName = Str(f, col, "origName"),
                    enc = Str(f, col, "enc"),
                    seed = Long(f, col, "seed"),
                    seed1 = Long(f, col, "seed1"),
                    seed2 = Long(f, col, "seed2"),
                    innerOff = Int(f, col, "innerOff", 0),
                    size = Int(f, col, "size", 0),
                };
                rows.Add(r);
            }
            return rows;
        }

        // ─────────────────────────── CSV 文法（RFC 4180）───────────────────────────

        /// <summary>
        /// CSV 文字 → 每列一個欄位陣列。支援雙引號欄（裡面可包含逗號、換行、以 "" 表示的引號）、
        /// CRLF/LF、開頭 BOM。空白列（只有一個空欄）會被丟掉。
        ///
        /// 自己寫而不用第三方：Unity runtime 沒有內建 CSV parser，而歌名裡真的有逗號
        /// （例："Yes, I do"），照 Split(',') 切會把歌名切成兩半、後面所有欄位一路錯位。
        /// </summary>
        public static List<string[]> ParseCsv(string text)
        {
            var rows = new List<string[]>();
            if (string.IsNullOrEmpty(text)) return rows;
            int i = 0;
            if (text[0] == '﻿') i = 1;                 // UTF-8 BOM

            var fields = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false, any = false;

            void EndField() { fields.Add(sb.ToString()); sb.Length = 0; any = true; }
            void EndRow()
            {
                EndField();
                if (fields.Count > 1 || fields[0].Length > 0) rows.Add(fields.ToArray());
                fields.Clear(); any = false;
            }

            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c != '"') { sb.Append(c); continue; }
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; continue; }  // "" = 一個 "
                    quoted = false;
                    continue;
                }
                switch (c)
                {
                    case '"': quoted = true; any = true; break;
                    case ',': EndField(); break;
                    case '\r': break;                        // CRLF：吃掉 \r，由 \n 收列
                    case '\n': EndRow(); break;
                    default: sb.Append(c); break;
                }
            }
            if (sb.Length > 0 || fields.Count > 0 || any) EndRow();   // 最後一列可能沒有換行
            return rows;
        }

        private static string Str(string[] f, Dictionary<string, int> col, string name)
        {
            if (!col.TryGetValue(name, out int i) || i >= f.Length) return "";
            return f[i] ?? "";
        }

        private static int Int(string[] f, Dictionary<string, int> col, string name, int fallback)
        {
            var s = Str(f, col, name).Trim();
            if (s.Length == 0) return fallback;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv) ? (int)fv : fallback;
        }

        private static long Long(string[] f, Dictionary<string, int> col, string name)
        {
            var s = Str(f, col, name).Trim();
            return s.Length > 0 && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0L;
        }

        private static float Float(string[] f, Dictionary<string, int> col, string name, float fallback)
        {
            var s = Str(f, col, name).Trim();
            return s.Length > 0 && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
        }
    }
}
