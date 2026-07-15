using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 把單首 offset 寫回 <c>StreamingAssets/song_name_overrides.json</c>（譜面編輯器 Ctrl+S）。
    ///
    /// **外科手術式的字串編輯，不是重新序列化整份 JSON。** 這是刻意的：
    /// 那個檔是 tools/build_song_name_overrides.py 產的，帶著 <c>schema</c> / <c>note</c> / <c>count</c>
    /// 以及每首歌的 <c>fileId</c> / <c>bpm</c> / <c>src</c>。JsonUtility 只認得你宣告的欄位，整份反序列化再
    /// 序列化回去，任何沒宣告到的欄位都會**靜靜消失**，而且 2175 首的排版全部被重寫 —— 一個「存 offset」的
    /// 動作不該把整個檔案炸掉，之後 git diff 也會完全沒法看。
    ///
    /// 所以這裡只做一件事：找到那一首的物件，改／插入／刪掉它的 <c>offsetMs</c>，其餘位元組原封不動。
    /// 檔案格式是我們自己的工具產的（json.dumps indent=1），穩定；解析失敗一律不寫，回 false。
    /// </summary>
    public static class SongOverridesWriter
    {
        public const string FileName = "song_name_overrides.json";

        public static string FilePath => Path.Combine(Application.streamingAssetsPath, FileName);

        /// <summary>
        /// 設定（或清除）某首歌的 offsetMs。<paramref name="stem"/> = gn 詞幹（sdomNNNN，去掉 k/t）。
        /// 值 ≈ 0 → 把欄位移掉（檔案不會越存越髒）。成功回 true；找不到那首歌 / 檔案壞掉 → false 且不寫檔。
        /// </summary>
        public static bool SetOffset(string stem, double ms, out string message)
        {
            message = "";
            if (string.IsNullOrEmpty(stem)) { message = "沒有歌"; return false; }

            string path = FilePath;
            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception e) { message = "讀不到 " + FileName + "：" + e.Message; return false; }

            // 找 "gn": "<stem>" —— 這個檔的 key 一律小寫詞幹
            string needle = "\"gn\": \"" + stem.ToLowerInvariant() + "\"";
            int at = text.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) { message = $"{FileName} 裡沒有 {stem}（新歌？先跑 add_songs_incremental.py）"; return false; }

            if (!ObjectSpan(text, at, out int objStart, out int objEnd))
            { message = "JSON 結構看不懂，沒有寫入"; return false; }

            bool clear = Math.Abs(ms) < 0.0005;
            string field = "\"offsetMs\": " + ms.ToString("0.###", CultureInfo.InvariantCulture);

            int fieldAt = text.IndexOf("\"offsetMs\"", objStart, objEnd - objStart, StringComparison.Ordinal);
            string updated;
            if (fieldAt >= 0)
            {
                // 既有欄位：換掉整個 "offsetMs": <值>（值到下一個 , 或 } 為止）
                int valEnd = fieldAt;
                while (valEnd < objEnd && text[valEnd] != ',' && text[valEnd] != '}' && text[valEnd] != '\n') valEnd++;
                if (clear)
                {
                    // 連同前面那個逗號一起吃掉（它一定是接在某個欄位後面的）
                    int cut = fieldAt;
                    while (cut > objStart && (text[cut - 1] == ' ' || text[cut - 1] == '\n' || text[cut - 1] == '\r')) cut--;
                    if (cut > objStart && text[cut - 1] == ',') cut--;
                    updated = text.Substring(0, cut) + text.Substring(valEnd);
                }
                else
                {
                    updated = text.Substring(0, fieldAt) + field + text.Substring(valEnd);
                }
            }
            else
            {
                if (clear) { message = $"{stem} 的 offset 本來就是 0，沒有變動"; return true; }
                // 插在物件最後一個欄位後面：找 } 之前最後一個非空白字元，接上 ",\n  <field>"
                int tail = objEnd - 1;
                while (tail > objStart && char.IsWhiteSpace(text[tail])) tail--;
                string indent = IndentOf(text, objStart);
                updated = text.Substring(0, tail + 1) + ",\n" + indent + " " + field + text.Substring(tail + 1);
            }

            try { File.WriteAllText(path, updated, new UTF8Encoding(false)); }
            catch (Exception e) { message = "寫不進 " + FileName + "：" + e.Message; return false; }

            message = clear ? $"{stem} 的 offset 已清除 → {FileName}"
                            : $"{stem} offset {ms:+0.#;-0.#;0} ms 已存進 {FileName}";
            return true;
        }

        /// <summary>包住 <paramref name="inside"/> 的那個 JSON 物件的 [start, end)（end 指向它的 '}' 之後）。</summary>
        private static bool ObjectSpan(string text, int inside, out int start, out int end)
        {
            start = end = -1;
            int i = inside;
            while (i >= 0 && text[i] != '{') i--;      // 歌曲物件是扁平的（沒有巢狀）→ 往回找第一個 { 就是它
            if (i < 0) return false;
            start = i;
            int j = text.IndexOf('}', inside);
            if (j < 0) return false;
            end = j + 1;
            return true;
        }

        /// <summary>物件內欄位的縮排（拿 '{' 那一行的行首空白 → 欄位再多一格，對上 json.dumps(indent=1) 的排版）。</summary>
        private static string IndentOf(string text, int objStart)
        {
            int ls = objStart;
            while (ls > 0 && text[ls - 1] != '\n') ls--;
            var sb = new StringBuilder();
            for (int i = ls; i < objStart && (text[i] == ' ' || text[i] == '\t'); i++) sb.Append(text[i]);
            return sb.ToString();
        }
    }
}
