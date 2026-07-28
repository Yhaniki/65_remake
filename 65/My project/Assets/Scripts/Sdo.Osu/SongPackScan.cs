using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Sdo.Osu
{
    /// <summary>
    /// 把一個**歌曲資料夾**掃成 <see cref="PackFileEntry"/> 清單 —— packId 計算與上傳清單都用它。
    ///
    /// 為什麼要有這一層:<see cref="SongPackId"/> 與 <see cref="SongPackFilter"/> 都是純規則
    /// (吃清單、吐結果),真正去碰磁碟的只有這裡。切開之後:
    ///   • 規則本身可以在沒有檔案的情況下單元測試(它們已經有 40 幾條測試);
    ///   • **server 端可以用同一支程式碼重新驗算**上傳者送來的 packId —— 兩邊各寫一份
    ///     就會變成「本機算出來與 server 算出來不一樣」這種最難查的 bug。
    ///
    /// 純 <c>System.IO</c> + <c>System.Security.Cryptography</c>,沒有 Unity 依賴,所以可以在
    /// 掃描的 worker thread 上跑(開機掃歌庫時就是這樣用的)。
    /// </summary>
    public static class SongPackScan
    {
        /// <summary>
        /// 列出資料夾裡「可以傳」的檔案。
        ///
        /// <paramref name="hashEverything"/>:
        ///   • false(算 packId 用)—— 只有譜面檔算 SHA-256。音檔不算:大歌庫幾 GB,開機掃描不可能讀完;
        ///     而 (檔名, 長度) 對「這是不是同一個資料夾」已經足夠強,下載完之後每個檔還會逐一驗 hash。
        ///   • true(要上傳時)—— 每個檔都算,收端才能逐檔驗證。
        ///
        /// 回傳已依 <see cref="SongPackId.BuildManifest"/> 需要的方式正規化(相對路徑、小寫、<c>/</c>)。
        /// 讀不到的檔案直接跳過(權限/被佔用)—— 寧可少一個檔也不要讓整次掃描炸掉。
        /// </summary>
        public static List<PackFileEntry> Enumerate(string folder, bool hashEverything)
        {
            var list = new List<PackFileEntry>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return list;

            string root;
            try { root = Path.GetFullPath(folder); } catch { return list; }

            foreach (var abs in SafeEnumerateFiles(root))
            {
                string rel;
                try { rel = SafeRelPath.Normalize(abs.Substring(root.Length).TrimStart('\\', '/')); }
                catch { continue; }
                if (string.IsNullOrEmpty(rel) || !SafeRelPath.IsSafe(rel)) continue;

                long len;
                try { len = new FileInfo(abs).Length; } catch { continue; }
                if (!SongPackFilter.IsTransferable(rel, len)) continue;

                var e = new PackFileEntry { RelPath = rel, Length = len };
                if (hashEverything || SongPackId.NeedsContentHash(rel))
                {
                    var sha = TryHash(abs);
                    if (sha == null) continue;      // 譜面讀不到 → 這個資料夾的 id 會不完整,寧可略過該檔
                    e.Sha256 = sha;
                }
                list.Add(e);
            }
            return list;
        }

        /// <summary>資料夾的 packId(內部先 <see cref="Enumerate"/>)。空資料夾 → null。</summary>
        public static string Compute(string folder)
        {
            var files = Enumerate(folder, hashEverything: false);
            return files.Count == 0 ? null : SongPackId.Compute(files);
        }

        /// <summary>
        /// 歌曲資料夾本身 + **一層**子資料夾(與 <see cref="SongPackFilter.Depth"/> 的上限一致)。
        /// 不用 AllDirectories:一首歌不該有無限深的樹,而且那會把整個歌庫的兄弟資料夾都掃進來。
        /// </summary>
        private static IEnumerable<string> SafeEnumerateFiles(string root)
        {
            foreach (var f in SafeFiles(root)) yield return f;
            string[] subs;
            try { subs = Directory.GetDirectories(root); } catch { yield break; }
            foreach (var d in subs)
                foreach (var f in SafeFiles(d)) yield return f;
        }

        private static string[] SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir); } catch { return new string[0]; }
        }

        private static string TryHash(string abs)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var h = sha.ComputeHash(fs);
                    var sb = new System.Text.StringBuilder(h.Length * 2);
                    for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return null; }
        }
    }
}
