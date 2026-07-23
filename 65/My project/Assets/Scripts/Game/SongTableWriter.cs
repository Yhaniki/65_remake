using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Sdo.Game
{
    /// <summary>
    /// 把單首 offset 寫回 <c>StreamingAssets/song_table.csv</c>（譜面編輯器 Ctrl+S）。
    ///
    /// **只動那一格，其餘位元組原封不動**：整份讀進來重寫的話，4325 列的排版／引號／小數寫法
    /// 全部會被重新產生一次，一個「存 offset」的動作不該把整個檔案炸掉（之後 git diff 也沒法看）。
    /// 所以這裡是字串手術：找到那首歌的兩列（k/t 共用同一個音檔 → 同一個 offset），
    /// 把 <c>offsetMs</c> 那一格的字元換掉。解析不出來就一律不寫，回 false。
    /// </summary>
    public static class SongTableWriter
    {
        public const string FileName = SongTable.FileName;

        public static string FilePath => SongTable.FilePath;

        /// <summary>
        /// 設定某首歌的 offsetMs。<paramref name="stem"/> = gn 詞幹（sdomNNNN，去掉 k/t）。
        /// 成功回 true（並讓 <see cref="SongCatalog"/> 重讀）；找不到那首歌 / 檔案壞掉 → false 且不寫檔。
        ///
        /// 字串手術本身在 <see cref="TrySetOffsetInText"/>（純函式、有測試）；這裡只負責讀寫檔。
        /// </summary>
        public static bool SetOffset(string stem, double ms, out string message)
        {
            string path = FilePath;
            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception e) { message = "讀不到 " + FileName + "：" + e.Message; return false; }

            if (!TrySetOffsetInText(text, stem, ms, out string updated, out message)) return false;
            if (updated == null) return true;   // 值沒變 → 成功但無需寫檔

            try { File.WriteAllText(path, updated, new UTF8Encoding(true)); }   // 保留 BOM（Excel 繁中要）
            catch (Exception e) { message = "寫不進 " + FileName + "：" + e.Message; return false; }

            SongCatalog.Invalidate();   // 下次查就是新值
            GnKeyTable.Invalidate();
            return true;
        }

        /// <summary>
        /// <see cref="SetOffset"/> 的純字串核心：把 <paramref name="csv"/>（整份 song_table.csv）裡
        /// 詞幹為 <paramref name="stem"/> 的**每一列**（k 譜與 t 譜）的 offsetMs 換成 <paramref name="ms"/>，
        /// 結果放進 <paramref name="updated"/>。
        ///
        /// 回傳 true＝邏輯上成功。<paramref name="updated"/> 為 null 代表「成功但不用寫檔」（值本來就一樣）。
        /// 回 false＝失敗（空詞幹／表頭看不懂／找不到那首歌），<paramref name="updated"/> 為 null。
        /// </summary>
        public static bool TrySetOffsetInText(string csv, string stem, double ms, out string updated, out string message)
        {
            updated = null;
            message = "";
            if (string.IsNullOrEmpty(stem)) { message = "沒有歌"; return false; }
            if (string.IsNullOrEmpty(csv)) { message = "沒有內容"; return false; }

            stem = stem.ToLowerInvariant();
            var lines = LineSpans(csv);
            if (lines.Count < 2) { message = FileName + " 是空的"; return false; }

            var header = Fields(csv, lines[0]);
            int gnCol = -1, offCol = -1;
            for (int i = 0; i < header.Count; i++)
            {
                var name = csv.Substring(header[i].Start, header[i].Length).Trim().Trim('"');
                if (string.Equals(name, "gn", StringComparison.OrdinalIgnoreCase)) gnCol = i;
                else if (string.Equals(name, "offsetMs", StringComparison.OrdinalIgnoreCase)) offCol = i;
            }
            if (gnCol < 0 || offCol < 0) { message = FileName + " 的表頭缺 gn / offsetMs 欄，沒有寫入"; return false; }

            string field = ms.ToString("0.###", CultureInfo.InvariantCulture);
            var sb = new StringBuilder(csv.Length + 8);
            int cursor = 0, hits = 0; bool changed = false;

            for (int li = 1; li < lines.Count; li++)
            {
                var fields = Fields(csv, lines[li]);
                if (gnCol >= fields.Count || offCol >= fields.Count) continue;
                var gn = csv.Substring(fields[gnCol].Start, fields[gnCol].Length).Trim().Trim('"');
                if (SongCatalog.Stem(gn) != stem) continue;

                hits++;
                var span = fields[offCol];
                var old = csv.Substring(span.Start, span.Length).Trim();
                if (old == field) continue;                       // 這一列本來就是這個值
                sb.Append(csv, cursor, span.Start - cursor).Append(field);
                cursor = span.Start + span.Length;
                changed = true;
            }

            if (hits == 0)
            {
                message = $"{FileName} 裡沒有 {stem}（新歌？先跑 add_songs_incremental.py）";
                return false;
            }
            if (!changed) { message = $"{stem} 的 offset 本來就是 {field} ms，沒有變動"; return true; }

            sb.Append(csv, cursor, csv.Length - cursor);
            updated = sb.ToString();
            message = $"{stem} offset {ms:+0.#;-0.#;0} ms 已存進 {FileName}（{hits} 列）";
            return true;
        }

        // ────────────────────────────── 字串切片 ──────────────────────────────

        /// <summary>一段 [Start, Start+Length) 的字元範圍。</summary>
        public struct Span
        {
            public int Start, Length;
            public Span(int start, int length) { Start = start; Length = length; }
        }

        /// <summary>整份 CSV → 每一列（不含換行字元）的範圍。引號欄裡的換行算在同一列。</summary>
        private static List<Span> LineSpans(string text)
        {
            var spans = new List<Span>();
            bool quoted = false;
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"') { quoted = !quoted; continue; }
                if (quoted || c != '\n') continue;
                int end = i;
                if (end > start && text[end - 1] == '\r') end--;
                if (end > start) spans.Add(new Span(start, end - start));
                start = i + 1;
            }
            if (start < text.Length) spans.Add(new Span(start, text.Length - start));
            return spans;
        }

        /// <summary>一列 → 每一格的範圍（含引號本身；空格不 trim，這樣拼回去才是原樣）。</summary>
        private static List<Span> Fields(string text, Span line)
        {
            var spans = new List<Span>();
            bool quoted = false;
            int start = line.Start, end = line.Start + line.Length;
            for (int i = start; i < end; i++)
            {
                char c = text[i];
                if (c == '"') { quoted = !quoted; continue; }
                if (quoted || c != ',') continue;
                spans.Add(new Span(start, i - start));
                start = i + 1;
            }
            spans.Add(new Span(start, end - start));
            return spans;
        }
    }
}
