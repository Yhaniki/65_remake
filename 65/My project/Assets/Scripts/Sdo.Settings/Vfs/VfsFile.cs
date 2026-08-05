using System;
using System.Collections.Generic;
using System.IO;

namespace Sdo.Settings.Vfs
{
    /// <summary>
    /// <c>File</c> / <c>Directory</c> 的門面:路徑落在 DATA 樹底下就走 <see cref="SdoVfs"/>,否則直接走真實檔案系統。
    ///
    /// 存在的理由是**遷移成本**。專案裡有 275 處直接 IO 散在 60 個檔,絕大多數長成
    /// <c>File.ReadAllBytes(Path.Combine(SdoExtracted.SomeDir, name))</c> —— 也就是「先拼出絕對路徑,再讀」。
    /// 要它們全部改成傳相對路徑,是 60 個檔的大改動,每一處都是回歸風險。改成這個門面之後,遷移退化成
    /// <c>File.</c> → <c>VfsFile.</c> 的機械式替換,語意完全不變:今天散裝層就是真實檔案系統,行為零差異;
    /// 等 pak 掛上去,同一段程式碼自動改讀 pak。
    ///
    /// <para><b>路徑往返</b>:<see cref="GetFiles"/> 回傳的是 <c>&lt;Root&gt;/&lt;相對路徑&gt;</c> 形式的
    /// 「看起來像絕對路徑」的字串 —— 即使那個檔只存在於 pak 裡。把它再餵回這裡的任何方法都會正確地
    /// 轉回相對路徑並命中 pak。**唯一不能做的是把它交給原生 <c>File</c> 或 <c>UnityWebRequest</c>** ——
    /// 那種場合要先問 <see cref="ResolveRealPath"/>,拿到 null 就表示這個檔沒有實體、必須改走記憶體。</para>
    ///
    /// <para>找不到一律回 null / false / -1,不丟例外 —— 呼叫端原本就都是 <c>File.Exists</c> 先問再讀。</para>
    /// </summary>
    public static class VfsFile
    {
        private static readonly string[] NoPaths = new string[0];

        // ---------------- 路徑換算 ----------------

        /// <summary>絕對(或相對)路徑 → 相對 DATA 根的正規化路徑;不在根底下 → null。
        ///
        /// 熱路徑:AVATAR 有 67,503 個檔,商城捲動時每幀都在問。所以先試「字串前綴直接命中」這條快路,
        /// 只有前綴對不上時才退回 <see cref="Path.GetFullPath"/>(它會做完整正規化,不便宜)。</summary>
        public static string RelUnderRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string root;
            try { root = SdoDataRoot.Root; } catch { return null; }
            if (string.IsNullOrEmpty(root)) return null;

            var rel = TryStrip(path, root);
            // 前綴命中不代表就乾淨了：Path.Combine 不做摺疊，所以 <root>\UI\..\UI\GAMEPLAY\x.png 會直接
            // 剝成 "UI/../UI/GAMEPLAY/x.png"。查表鍵必須是正規形式，否則同一個檔會因為寫法不同而算成兩筆
            // （快取各存一份、pak 查不到）。有 "." / ".." 段就折一次，沒有就走快路徑不配置記憶體。
            if (rel != null) return HasDotSegment(rel) ? VfsPath.Normalize(rel) : rel;

            // 前綴對不上：可能是相對路徑、或分隔符/大小寫以外的差異 —— 正規化後再試一次。
            string full, fullRoot;
            try { full = Path.GetFullPath(path); } catch { return null; }
            try { fullRoot = Path.GetFullPath(root); } catch { return null; }
            var rel2 = TryStrip(full, fullRoot);
            return rel2 != null && HasDotSegment(rel2) ? VfsPath.Normalize(rel2) : rel2;
        }

        /// <summary>有沒有任何一段剛好是 <c>.</c> 或 <c>..</c>。單次掃描、不配置記憶體 ——
        /// 檔名裡的點(<c>x.png</c>)不算。</summary>
        private static bool HasDotSegment(string s)
        {
            int n = s.Length, i = 0;
            while (i <= n)
            {
                int j = s.IndexOf('/', i);
                int end = j < 0 ? n : j;
                int len = end - i;
                if (len == 1 && s[i] == '.') return true;
                if (len == 2 && s[i] == '.' && s[i + 1] == '.') return true;
                if (j < 0) break;
                i = j + 1;
            }
            return false;
        }

        /// <summary>相對路徑 → <c>&lt;Root&gt;/&lt;相對路徑&gt;</c>。這個字串**不保證是真實存在的檔案** ——
        /// 它只是能被這個門面認回去的往返形式。要真實路徑請用 <see cref="ResolveRealPath"/>。</summary>
        public static string AbsFor(string rel)
        {
            if (rel == null) return null;
            try { return Path.Combine(SdoDataRoot.Root, rel.Replace('/', Path.DirectorySeparatorChar)); }
            catch { return rel; }
        }

        private static string TryStrip(string path, string root)
        {
            int rlen = root.Length;
            while (rlen > 0 && (root[rlen - 1] == '\\' || root[rlen - 1] == '/')) rlen--;
            if (rlen == 0 || path.Length <= rlen + 1) return null;
            if (string.Compare(path, 0, root, 0, rlen, StringComparison.OrdinalIgnoreCase) != 0) return null;

            char sep = path[rlen];
            if (sep != '\\' && sep != '/') return null;   // "DATA2" 不是 "DATA" 底下的東西

            return path.Substring(rlen + 1).Replace('\\', '/');
        }

        // ---------------- 檔案 ----------------

        public static bool Exists(string path)
        {
            var rel = RelUnderRoot(path);
            if (rel != null) return SdoVfs.Exists(rel);
            try { return !string.IsNullOrEmpty(path) && File.Exists(path); }
            catch { return false; }
        }

        public static byte[] ReadAllBytes(string path)
        {
            var rel = RelUnderRoot(path);
            if (rel != null) return SdoVfs.ReadAllBytes(rel);
            try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
            catch { return null; }
        }

        /// <summary>UTF-8 文字(自動吃 BOM);不存在 → null。</summary>
        public static string ReadAllText(string path)
        {
            var rel = RelUnderRoot(path);
            if (rel != null) return SdoVfs.ReadAllText(rel);
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }

        /// <summary>指定編碼的文字;不存在 → null。
        ///
        /// 對齊 <c>File.ReadAllText(path, encoding)</c>:那個多載仍然會**偵測並剝掉 BOM**
        /// (<c>StreamReader</c> 預設 <c>detectEncodingFromByteOrderMarks: true</c>),
        /// 所以這裡也要剝,否則第一個欄位會多帶一個 U+FEFF —— 商城的 tsv sidecar 是用第一欄當鍵去查表的,
        /// 那樣整張表會全部查不到。</summary>
        public static string ReadAllText(string path, System.Text.Encoding encoding)
        {
            if (encoding == null) return ReadAllText(path);

            var bytes = ReadAllBytes(path);
            if (bytes == null) return null;
            try
            {
                var preamble = encoding.GetPreamble();
                int skip = 0;
                if (preamble != null && preamble.Length > 0 && bytes.Length >= preamble.Length)
                {
                    skip = preamble.Length;
                    for (int i = 0; i < preamble.Length; i++)
                        if (bytes[i] != preamble[i]) { skip = 0; break; }
                }
                return encoding.GetString(bytes, skip, bytes.Length - skip);
            }
            catch { return null; }
        }

        /// <summary>切行後的文字;不存在 → null。CRLF / LF / CR 都吃。
        ///
        /// 結尾那個換行**不會**變成一個空字串元素 —— <c>File.ReadAllLines</c> 就是這個語意
        /// (<c>"a\nb\n"</c> → <c>["a","b"]</c>),呼叫端多半直接 foreach 然後對每行做 Split/Parse,
        /// 多一個空元素會讓它們走進「格式錯誤」的分支。</summary>
        public static string[] ReadAllLines(string path)
        {
            var text = ReadAllText(path);
            if (text == null) return null;

            var norm = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (norm.Length == 0) return new string[0];
            if (norm[norm.Length - 1] == '\n') norm = norm.Substring(0, norm.Length - 1);
            return norm.Split('\n');
        }

        public static Stream OpenRead(string path)
        {
            var rel = RelUnderRoot(path);
            if (rel != null) return SdoVfs.OpenRead(rel);
            try { return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null; }
            catch { return null; }
        }

        /// <summary>解開後的位元組數;不存在 → -1。</summary>
        public static long Length(string path)
        {
            var rel = RelUnderRoot(path);
            if (rel != null) return SdoVfs.GetSize(rel);
            try { return File.Exists(path) ? new FileInfo(path).Length : -1L; }
            catch { return -1L; }
        }

        /// <summary>真實檔案系統上的位置;只存在於 pak 裡 → null。
        /// <c>UnityWebRequestMultimedia</c> / 外部工具 / 就地覆寫都要先問這個。</summary>
        public static string ResolveRealPath(string path)
        {
            var rel = RelUnderRoot(path);
            if (rel != null) return SdoVfs.ResolveRealPath(rel);
            try { return File.Exists(path) ? path : null; }
            catch { return null; }
        }

        /// <summary>拿到一條**保證能餵給 <c>file://</c> 的真實路徑**;檔案不存在 → null。
        ///
        /// 散裝層命中就直接回傳那條路徑(零成本,開發時的常態)。只存在於 pak 裡的檔會被解出來寫到
        /// <c>DATA/CACHE/AUDIO/</c> 再回傳那個位置。
        ///
        /// <para><b>為什麼需要這個</b>:Unity 沒有記憶體 ogg 解碼器,<c>UnityWebRequestMultimedia</c> 只吃
        /// <c>file://</c>,而 <c>Mp3Decoder.Decode</c> 吃的是路徑。與其把每一條音訊路徑改寫成 byte 版
        /// (會碰到 gapless 對拍與試聽那些很敏感的東西),不如在這裡把檔案具現化一次 —— 對 wav/ogg/mp3
        /// 一致,而且未來多一種格式也不用再改。</para>
        ///
        /// <para>⚠️ <b>取捨</b>:解出來的檔在 CACHE 裡是**明碼**。這會讓「不能整包拷走直接用」弱一點 ——
        /// 但只弱在玩家真的播放過的那些檔,而且 CACHE 本來就是可刪的。對「防君子」這個目標而言可以接受;
        /// 反正決心要拆的人直接讀記憶體就好。</para>
        ///
        /// <para>已經解過的檔會沿用(比對大小),不會每次重寫。</para>
        /// </summary>
        public static string MaterialiseRealPath(string path)
        {
            var real = ResolveRealPath(path);
            if (real != null) return real;          // 散裝層／DATA 之外 —— 常態，零成本

            var rel = RelUnderRoot(path);
            if (rel == null) return null;

            VfsEntry entry;
            if (!SdoVfs.TryGetEntry(rel, out entry)) return null;

            // 檔名帶雜湊避免不同目錄的同名檔互撞，副檔名保留讓 AudioType 判斷仍然管用。
            string ext;
            try { ext = Path.GetExtension(rel) ?? ""; } catch { ext = ""; }
            var cacheRel = Path.Combine("AUDIO", VfsPath.Hash(rel).ToString("x16") + ext);

            string cachePath;
            try { cachePath = SdoDataRoot.CachePath(cacheRel); }
            catch { return null; }
            if (cachePath == null) return null;

            try
            {
                var fi = new FileInfo(cachePath);
                if (fi.Exists && fi.Length == entry.Size) return cachePath;   // 已經解過了
            }
            catch { }

            var bytes = SdoVfs.ReadAllBytes(rel);
            if (bytes == null) return null;

            try
            {
                // 先寫暫存再改名：解到一半被中斷（當掉／關機）不會留下一個長度剛好、內容截斷的檔，
                // 那種檔會通過上面的大小檢查然後永遠餵出壞音訊。
                var tmp = cachePath + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(cachePath)) File.Delete(cachePath);
                File.Move(tmp, cachePath);
            }
            catch { return null; }

            TrimAudioCache(Path.GetDirectoryName(cachePath), cachePath);
            return cachePath;
        }

        /// <summary>具現化快取的上限（bytes）。超過就從最久沒被讀過的開始刪。
        ///
        /// 沒有上限的話,把 8.3 GB 的曲庫全播過一輪就會在 CACHE 裡再長出 8.3 GB —— 而且玩家不會知道
        /// 那是什麼、可不可以刪。512 MB 大約是一百多首歌,足夠讓常玩的那些一直命中快取。</summary>
        public static long AudioCacheLimitBytes = 512L * 1024 * 1024;

        /// <summary>把 <paramref name="dir"/> 修剪到 <see cref="AudioCacheLimitBytes"/> 以下 ——
        /// 依「最後存取時間」由舊到新刪。<paramref name="keep"/> 是這次剛解出來的檔,絕不刪它
        /// (不然呼叫端拿到的路徑當場就失效)。
        ///
        /// 全程 best-effort:刪不掉(檔案正被播放中鎖住)就跳過,下次再說。</summary>
        private static void TrimAudioCache(string dir, string keep)
        {
            if (string.IsNullOrEmpty(dir) || AudioCacheLimitBytes <= 0) return;
            try
            {
                var info = new DirectoryInfo(dir);
                if (!info.Exists) return;

                var files = info.GetFiles();
                long total = 0;
                foreach (var f in files) total += f.Length;
                if (total <= AudioCacheLimitBytes) return;

                Array.Sort(files, (a, b) => a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc));
                foreach (var f in files)
                {
                    if (total <= AudioCacheLimitBytes) break;
                    if (string.Equals(f.FullName, keep, StringComparison.OrdinalIgnoreCase)) continue;
                    long len = f.Length;
                    try { f.Delete(); total -= len; }
                    catch { /* 正在播、被鎖住 → 下次再刪 */ }
                }
            }
            catch { }
        }

        // ---------------- 目錄 ----------------

        /// <summary>目錄底下有沒有東西。VFS 沒有「空目錄」的概念 —— pak 不存目錄項,所以這裡問的是
        /// 「有沒有任何檔案在它底下」。散裝層則是真的問磁碟(空資料夾也算存在)。</summary>
        public static bool DirectoryExists(string path)
        {
            var rel = RelUnderRoot(path);
            if (rel == null)
            {
                try { return !string.IsNullOrEmpty(path) && Directory.Exists(path); }
                catch { return false; }
            }

            // 散裝層：磁碟上真的有這個資料夾就算存在（連空的也算，跟 Directory.Exists 一致）。
            try
            {
                var abs = AbsFor(rel);
                if (!string.IsNullOrEmpty(abs) && Directory.Exists(abs)) return true;
            }
            catch { }

            // pak 層：沒有目錄項，只能問「底下有沒有任何檔案」。找到一個就夠，不必列完。
            foreach (var _ in SdoVfs.EnumerateFiles(rel, "*", true)) return true;
            return false;
        }

        /// <summary><paramref name="dir"/> 底下的檔案,回傳 <see cref="AbsFor"/> 形式(可餵回這個門面)。
        /// 找不到 → 空陣列(不是 null)—— 對齊 <c>Directory.GetFiles</c> 的用法。</summary>
        public static string[] GetFiles(string dir, string pattern = "*", bool recursive = false)
        {
            var rel = RelUnderRoot(dir);
            if (rel == null)
            {
                try
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return NoPaths;
                    return Directory.GetFiles(dir, string.IsNullOrEmpty(pattern) ? "*" : pattern,
                        recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                }
                catch { return NoPaths; }
            }

            var list = new List<string>();
            foreach (var p in SdoVfs.EnumerateFiles(rel, pattern, recursive)) list.Add(AbsFor(p));
            return list.ToArray();
        }

        /// <summary><paramref name="dir"/> 底下的直接子目錄,回傳 <see cref="AbsFor"/> 形式。</summary>
        public static string[] GetDirectories(string dir)
        {
            var rel = RelUnderRoot(dir);
            if (rel == null)
            {
                try
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return NoPaths;
                    return Directory.GetDirectories(dir);
                }
                catch { return NoPaths; }
            }

            var list = new List<string>();
            foreach (var p in SdoVfs.EnumerateDirectories(rel)) list.Add(AbsFor(p));
            return list.ToArray();
        }
    }
}
