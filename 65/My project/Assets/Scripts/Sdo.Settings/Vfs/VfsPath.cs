using System.Collections.Generic;
using System.Text;

namespace Sdo.Settings.Vfs
{
    /// <summary>
    /// VFS 路徑的正規化、雜湊與萬用字元比對 —— 全部是純函式,不碰磁碟也不碰 Unity。
    /// 規格見 <c>docs/architecture/data-packaging.md</c> §4.1。
    ///
    /// VFS 對外的路徑一律是「相對 <see cref="SdoDataRoot.Root"/> 的正規化路徑」:'/' 分隔、無前導斜線、
    /// 無 <c>.</c> / <c>..</c>。<see cref="Normalize"/> 會把呼叫端寫的各種形式收斂成同一個字串,
    /// <see cref="Hash"/> 再把它變成 pak 索引的查表鍵。
    /// </summary>
    public static class VfsPath
    {
        /// <summary>不參與 pak 解析的頂層目錄 —— 這四個永遠走真實檔案系統,而且打包器必須排除它們。
        /// 這就是「DATA 底下 pak 唯讀、這四個可寫」那條分界線。</summary>
        public static readonly string[] ReservedRoots = { "PROFILE", "ADDON", "CACHE", "REPLAY" };

        /// <summary>把任意寫法的相對路徑收斂成正規形式;無效 → null(不丟例外,呼叫端當「檔案不存在」處理)。
        ///
        /// 無效的情況:null/空、含 <c>:</c>(絕對路徑或 NTFS 資料流)、或 <c>..</c> 摺疊後逃出了根。
        /// 逃出根一定要擋 —— 否則 pak 內一條 <c>../../windows/system32/…</c> 就能讓解包寫到任意位置。
        ///
        /// 注意回傳空字串是合法的:那代表「根目錄自己」,<see cref="SdoVfs.EnumerateFiles"/> 會用到。</summary>
        public static string Normalize(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            if (rel.IndexOf(':') >= 0) return null;

            var parts = rel.Replace('\\', '/').Split('/');
            var stack = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                if (p.Length == 0 || p == ".") continue;
                if (p == "..")
                {
                    if (stack.Count == 0) return null;   // 逃出根
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }
                stack.Add(p);
            }
            return string.Join("/", stack.ToArray());
        }

        /// <summary>正規化路徑的第一段是不是 <see cref="ReservedRoots"/> 之一(大小寫不敏感)。
        /// null / 空 → false。</summary>
        public static bool IsReserved(string normalized)
        {
            if (string.IsNullOrEmpty(normalized)) return false;
            int slash = normalized.IndexOf('/');
            int len = slash < 0 ? normalized.Length : slash;
            foreach (var r in ReservedRoots)
            {
                if (r.Length != len) continue;
                bool same = true;
                for (int i = 0; i < len; i++)
                    if (UpperAscii(normalized[i]) != r[i]) { same = false; break; }
                if (same) return true;
            }
            return false;
        }

        /// <summary>正規化路徑 → pak 索引的 64-bit 查表鍵(FNV-1a,對 ASCII 大寫後的 UTF-8 bytes)。
        ///
        /// 大寫只轉 ASCII 的 <c>a</c>–<c>z</c>:原始資料樹是純 ASCII 檔名而 NTFS 大小寫不敏感,程式碼裡對同一個檔
        /// 大小寫混用,所以查表必須大小寫不敏感;但用 <c>ToUpperInvariant()</c> 會踩到土耳其語 <c>i</c>/<c>İ</c>
        /// 那類 locale 陷阱。UTF-8 的續接位元組都 ≥ 0x80,所以只動 ASCII 區間對多位元組字元完全無害
        /// (而玩家的 unicode 檔名都在 ADDON 底下,走真實檔案系統,根本不進這張表)。
        ///
        /// FNV-1a 而不是 xxHash:C# 與 Python 兩邊各五行就寫得完、不會寫錯,而打包器本來就會硬檢查碰撞。</summary>
        public static ulong Hash(string normalized)
        {
            const ulong Offset = 14695981039346656037UL;
            const ulong Prime = 1099511628211UL;

            ulong h = Offset;
            var bytes = Encoding.UTF8.GetBytes(normalized ?? "");
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                if (b >= (byte)'a' && b <= (byte)'z') b -= 32;
                h ^= b;
                h *= Prime;
            }
            return h;
        }

        /// <summary>檔名的萬用字元比對(<c>*</c> 與 <c>?</c>),大小寫不敏感。
        ///
        /// <c>null</c> / <c>"*"</c> / <c>"*.*"</c> 一律視為「全部命中」—— 最後那個是為了對齊 .NET
        /// <c>Directory.GetFiles</c> 的歷史行為:那裡的 <c>*.*</c> 連沒有副檔名的檔也照收,呼叫端搬過來時
        /// 不該因為換了實作就少拿到檔。</summary>
        public static bool GlobMatch(string name, string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || pattern == "*" || pattern == "*.*") return true;
            if (name == null) return false;

            int n = name.Length, m = pattern.Length;
            int i = 0, j = 0, star = -1, mark = 0;
            while (i < n)
            {
                if (j < m && (pattern[j] == '?' || UpperAscii(pattern[j]) == UpperAscii(name[i]))) { i++; j++; }
                else if (j < m && pattern[j] == '*') { star = j++; mark = i; }
                else if (star >= 0) { j = star + 1; i = ++mark; }
                else return false;
            }
            while (j < m && pattern[j] == '*') j++;
            return j == m;
        }

        /// <summary>正規化路徑的檔名部分(最後一段);沒有斜線 → 整串。</summary>
        public static string FileName(string normalized)
        {
            if (string.IsNullOrEmpty(normalized)) return normalized;
            int slash = normalized.LastIndexOf('/');
            return slash < 0 ? normalized : normalized.Substring(slash + 1);
        }

        /// <summary><paramref name="normalized"/> 是否在 <paramref name="dir"/> 底下(大小寫不敏感)。
        /// <paramref name="dir"/> 為空 = 根,任何路徑都算在底下。
        /// <paramref name="recursive"/> 為 false 時只算直接子項,不含更深的層。</summary>
        public static bool IsUnder(string normalized, string dir, bool recursive)
        {
            if (string.IsNullOrEmpty(normalized)) return false;

            int start;
            if (string.IsNullOrEmpty(dir)) start = 0;
            else
            {
                if (normalized.Length <= dir.Length + 1) return false;
                for (int i = 0; i < dir.Length; i++)
                    if (UpperAscii(normalized[i]) != UpperAscii(dir[i])) return false;
                if (normalized[dir.Length] != '/') return false;
                start = dir.Length + 1;
            }

            if (recursive) return true;
            return normalized.IndexOf('/', start) < 0;   // 非遞迴:剩下的部分不能再有斜線
        }

        private static char UpperAscii(char c)
        {
            return (c >= 'a' && c <= 'z') ? (char)(c - 32) : c;
        }
    }
}
