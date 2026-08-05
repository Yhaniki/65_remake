using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Sdo.Osu
{
    /// <summary>歌曲資料夾裡的一個可傳輸檔案。</summary>
    public struct PackFileEntry
    {
        /// <summary>相對歌曲資料夾的路徑，已經過 <see cref="SafeRelPath.Normalize"/>(小寫、<c>/</c> 分隔)。</summary>
        public string RelPath;

        /// <summary>位元組數。</summary>
        public long Length;

        /// <summary>
        /// 內容的 SHA-256(小寫 hex，64 字)。**兩種用途下的填法不同,見 <see cref="SongPackId"/> 的說明**:
        /// 算 packId 時只有譜面檔需要填;要上傳時每個檔都要填。
        /// </summary>
        public string Sha256;

        /// <summary>
        /// 這個檔**壓縮之後**有幾個位元組(<see cref="PackCompression"/>)—— 線路上真正流動的量。
        /// 0 = 還不知道 / 不壓縮(舊 client 送來的清單沒有這個欄位,那時就照原始長度傳)。
        ///
        /// 🔴 **不參與 packId**(<see cref="SongPackId.BuildManifest"/> 只看 RelPath/Length/Sha256)——
        /// 壓縮是傳輸與儲存的細節,不是模型/歌曲的身分。壓縮率哪天變了,既有的包也不該換一個 id。
        /// </summary>
        public long CompressedLength;

        public PackFileEntry(string relPath, long length, string sha256)
        {
            RelPath = relPath;
            Length = length;
            Sha256 = sha256;
            CompressedLength = 0;
        }

        public PackFileEntry(string relPath, long length, string sha256, long compressedLength)
        {
            RelPath = relPath;
            Length = length;
            Sha256 = sha256;
            CompressedLength = compressedLength;
        }

        /// <summary>線路上要傳幾個位元組:有壓縮版就是壓縮後的長度,否則是原始長度。</summary>
        public long WireLength => CompressedLength > 0 ? CompressedLength : Length;

        /// <summary>這個檔在線路上是壓縮的嗎。</summary>
        public bool IsCompressed => CompressedLength > 0;
    }

    /// <summary>掃一個歌曲資料夾的結果統計 —— 用來回報給 host 看「跳過了什麼」。</summary>
    public struct PackScanStats
    {
        public int IncludedFiles;
        public long IncludedBytes;
        public int SkippedVideos;
        public long SkippedVideoBytes;
        public int SkippedExecutables;
        public int SkippedArchives;
        public int SkippedGenerated;
        public int SkippedUnknown;
        public int SkippedTooBig;
        public int SkippedUnsafe;
        public int SkippedTooDeep;
    }

    /// <summary>
    /// 歌曲資料夾的內容指紋(<c>packId</c>)—— 這是外部歌在網路上的**唯一身份**。
    ///
    /// **為什麼需要它**:現有的識別方式跨機器完全不一致，一個都不能用 ——
    ///   • <c>ExternalSongLibrary</c> 的 <c>gn</c> = <c>"ext_" + FNV(資料夾絕對路徑)</c>
    ///     → 對方的歌庫放在別的磁碟就完全不同
    ///   • <c>fileId</c> = <c>-(掃描索引 + 1000)</c>
    ///     → 只要增刪一首歌，後面所有歌的 id 全部平移
    ///   • <c>ExternalScanCache.Signature</c> = (檔名, 大小, **mtime**)
    ///     → 同一份譜複製到另一台機器 mtime 就變了
    ///
    /// ---
    /// 🔴 **兩種 manifest,不要搞混:**
    ///
    /// 1. **packId manifest**(本類別的 <see cref="BuildManifest"/>)——
    ///    只有**譜面檔**算完整 SHA-256，音檔與圖只記 (路徑, 大小)。
    ///    理由:packId 要在**每次開機掃描**時算得出來，而一個大歌庫的音檔是好幾 GB，
    ///    全部讀完雜湊會讓開機變成無法接受的慢。譜面是 kB 級，讀它不痛。
    ///    而「改一張譜」一定要讓 packId 改變(否則對方拿到舊譜，判定全錯)，
    ///    偏偏改幾個 note 的位置很可能不改變檔案長度 —— 所以譜面非 hash 不可。
    ///
    /// 2. **傳輸 manifest**(<c>blobUploadBegin.files[]</c>)——
    ///    **每一個檔**都要算 SHA-256。它用在 per-file 內容尋址(server 已經有的檔就不用重傳)
    ///    與下載後的逐檔驗證。這個只在真的要上傳/下載時才算，那時讀全部位元組是可接受的成本。
    ///
    /// 所以音檔被換掉但長度剛好一樣時，packId 不會變 —— 最壞的後果是「以為對方有這首歌」，
    /// 而下載端的逐檔 SHA-256 驗證會發現不符並重新取檔。不會拿到壞檔，只是多花一次流量。
    /// 這是刻意的成本取捨。
    /// ---
    ///
    /// <see cref="BuildManifest"/> 與 <see cref="FromManifest"/> 是純函式(可直接單元測試);
    /// 只有 <see cref="ScanFolder"/> / <see cref="HashFile"/> 碰 IO。零 UnityEngine。
    /// </summary>
    public static class SongPackId
    {
        /// <summary>packId 的前綴 —— 帶著演算法名字，將來要換 hash 才有辦法平順過渡。</summary>
        public const string Prefix = "sha256:";

        /// <summary>
        /// packId 取 SHA-256 前幾個 hex 字。128 bit 對「這是不是同一個資料夾」遠遠足夠
        /// (碰撞機率可忽略)，而且比全長 64 字好讀、放進 JSON 也小。
        /// </summary>
        public const int HexChars = 32;

        /// <summary>manifest 每一行的欄位分隔符:ASCII Unit Separator(0x1F)。
        /// 用它而不是逗號/tab —— 那些可能出現在檔名裡。0x1F 不可能出現在合法路徑中(SafeRelPath 把所有
        /// 小於 0x20 的控制字元都擋掉了)。寫成 escape 而不是直接嵌控制字元 —— 後者在編輯器、diff
        /// 與 git 裡都是隱形的,改到它完全看不出來。</summary>
        public const char FieldSep = '\u001F';

        /// <summary>
        /// 需要算完整內容 hash 的副檔名 = 譜面與歌包索引。全部是 kB 級的小檔。
        /// <c>.tsv</c> 是歌包的 <c>sdo_pack.tsv</c>(歌名/seed/封面路徑都從它來，改了必須讓 packId 變)。
        /// </summary>
        private static readonly string[] ContentHashed = { ".osu", ".sm", ".gn", ".mc", ".tsv" };

        /// <summary>這個檔在算 packId 時需要它的內容 hash 嗎?(= 它是譜面嗎)</summary>
        public static bool NeedsContentHash(string relPath)
        {
            string ext = SongPackFilter.ExtensionOf(SongPackFilter.FileNameOf(relPath));
            for (int i = 0; i < ContentHashed.Length; i++)
                if (string.Equals(ContentHashed[i], ext, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// 組出 packId manifest(純函式)。
        ///
        /// 每個檔一行:<c>&lt;正規化路徑&gt; 0x1F &lt;長度&gt; 0x1F &lt;譜面的 sha256，其餘空&gt;</c>，
        /// 全部行以 Ordinal 排序後用 <c>\n</c> 串起來。
        ///
        /// 排序是必要的:檔案系統的列舉順序不保證穩定(同一顆硬碟重開機都可能不同)，
        /// 不排序的話同一個資料夾會算出不同的 packId。
        ///
        /// **即使呼叫端把每個檔的 sha256 都填好了,這裡也只會採用譜面的那些** ——
        /// 這樣 packId 對同一份檔案集合永遠一致，不管呼叫端當時手上有多少 hash 資訊
        /// (上傳流程會算全部檔案的 hash，開機掃描只算譜面的,兩邊必須得到同一個 packId)。
        /// </summary>
        public static string BuildManifest(IList<PackFileEntry> files)
        {
            if (files == null || files.Count == 0) return string.Empty;

            var lines = new List<string>(files.Count);
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                string rel = SafeRelPath.Normalize(f.RelPath);
                if (rel.Length == 0) continue;

                // 只取譜面的 hash —— 見上面的說明。
                string hash = NeedsContentHash(rel) ? (f.Sha256 ?? string.Empty) : string.Empty;

                lines.Add(rel + FieldSep + f.Length.ToString(CultureInfo.InvariantCulture) + FieldSep + hash.ToLowerInvariant());
            }
            if (lines.Count == 0) return string.Empty;

            lines.Sort(StringComparer.Ordinal);
            return string.Join("\n", lines.ToArray());
        }

        /// <summary>manifest → packId(純函式)。空 manifest 回空字串(= 這個資料夾沒有可傳的東西)。</summary>
        public static string FromManifest(string manifest)
        {
            if (string.IsNullOrEmpty(manifest)) return string.Empty;
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(manifest));
                return Prefix + ToHex(hash, HexChars);
            }
        }

        /// <summary>檔案清單 → packId(純函式)。</summary>
        public static string Compute(IList<PackFileEntry> files) => FromManifest(BuildManifest(files));

        /// <summary>這是格式正確的 packId 嗎?(server 收到 client 送來的字串要驗)</summary>
        public static bool IsWellFormed(string packId)
        {
            if (string.IsNullOrEmpty(packId)) return false;
            if (!packId.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            if (packId.Length != Prefix.Length + HexChars) return false;
            for (int i = Prefix.Length; i < packId.Length; i++)
            {
                char c = packId[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex) return false;
            }
            return true;
        }

        // ---- 以下碰 IO ----

        /// <summary>算單一檔案的 SHA-256(小寫 hex 全長 64 字)。讀不到回空字串。</summary>
        public static string HashFile(string fullPath)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024))
                {
                    return ToHex(sha.ComputeHash(fs), 64);
                }
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// 掃一個歌曲資料夾，回傳「可傳輸」的檔案清單。
        ///
        /// <paramref name="hashAllFiles"/>:
        ///   • <c>false</c>(開機掃描 / 算 packId)—— 只讀譜面算 hash。便宜。
        ///   • <c>true</c>(準備上傳)—— 每個檔都算 hash。會讀完所有位元組。
        ///
        /// 回 false = 資料夾讀不到或裡面沒有任何可傳的檔案。
        /// </summary>
        public static bool ScanFolder(string folderPath, bool hashAllFiles,
                                      out List<PackFileEntry> files, out PackScanStats stats)
        {
            files = new List<PackFileEntry>();
            stats = new PackScanStats();

            if (string.IsNullOrEmpty(folderPath)) return false;

            try
            {
                if (!Directory.Exists(folderPath)) return false;

                // 手動掃兩層(根目錄 + 一層子夾)而不是 SearchOption.AllDirectories。
                // 理由:AllDirectories 會跟隨 junction/symlink,指回自己的連結就是無限遞迴
                // (ExternalSongScanner 為此特別維護了一個 visited 集合)。既然
                // SongPackFilter.MaxDepth == 1,根本不需要遞迴 —— 直接寫死兩層，
                // 迴圈風險歸零，而且比先全掃再過濾快得多。
                if (!ScanOneDir(folderPath, folderPath, hashAllFiles, files, ref stats)) return false;

                foreach (var sub in Directory.EnumerateDirectories(folderPath))
                {
                    if (!ScanOneDir(folderPath, sub, hashAllFiles, files, ref stats)) return false;
                }
            }
            catch { return false; }

            return files.Count > 0;
        }

        /// <summary>掃資料夾直接得到 packId(開機掃描用的便宜版本)。算不出來回空字串。</summary>
        public static string ForFolder(string folderPath)
        {
            List<PackFileEntry> files;
            PackScanStats stats;
            if (!ScanFolder(folderPath, false, out files, out stats)) return string.Empty;
            return Compute(files);
        }

        // ---- 內部 ----

        /// <summary>
        /// 掃一個目錄裡的檔案(不遞迴)。回 false = 檔案數超過上限，整個 pack 作廢。
        /// <paramref name="root"/> 是歌曲資料夾(算相對路徑的基準)，<paramref name="dir"/> 是這次要掃的目錄。
        /// </summary>
        private static bool ScanOneDir(string root, string dir, bool hashAllFiles,
                                       List<PackFileEntry> files, ref PackScanStats stats)
        {
            foreach (var full in Directory.EnumerateFiles(dir))
            {
                string rel = ToRelative(root, full);
                if (rel.Length == 0) continue;

                long len;
                try { len = new FileInfo(full).Length; }
                catch { len = -1; }

                var verdict = SongPackFilter.Classify(rel, len);
                if (verdict != PackFileVerdict.Include)
                {
                    Tally(ref stats, verdict, len);
                    continue;
                }

                string hash = string.Empty;
                if (hashAllFiles || NeedsContentHash(rel)) hash = HashFile(full);

                files.Add(new PackFileEntry(SafeRelPath.Normalize(rel), len, hash));
                stats.IncludedFiles++;
                stats.IncludedBytes += len;

                if (files.Count > NetPackLimits.MaxPackFiles) return false;   // 檔案數爆掉 → 當成不可傳
            }
            return true;
        }

        private static void Tally(ref PackScanStats s, PackFileVerdict v, long len)
        {
            switch (v)
            {
                case PackFileVerdict.Video:
                    s.SkippedVideos++;
                    if (len > 0) s.SkippedVideoBytes += len;
                    break;
                case PackFileVerdict.Executable: s.SkippedExecutables++; break;
                case PackFileVerdict.Archive: s.SkippedArchives++; break;
                case PackFileVerdict.Generated: s.SkippedGenerated++; break;
                case PackFileVerdict.UnknownType: s.SkippedUnknown++; break;
                case PackFileVerdict.TooBig: s.SkippedTooBig++; break;
                case PackFileVerdict.UnsafePath: s.SkippedUnsafe++; break;
                case PackFileVerdict.TooDeep: s.SkippedTooDeep++; break;
            }
        }

        /// <summary>把絕對路徑轉成相對於資料夾的路徑。不用 Path.GetRelativePath(netstandard2.0 沒有)。</summary>
        private static string ToRelative(string folderPath, string fullPath)
        {
            string root = folderPath.TrimEnd('/', '\\');
            if (fullPath.Length <= root.Length + 1) return string.Empty;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            return fullPath.Substring(root.Length + 1);
        }

        private static string ToHex(byte[] bytes, int chars)
        {
            var sb = new StringBuilder(chars);
            for (int i = 0; i < bytes.Length && sb.Length < chars; i++)
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            if (sb.Length > chars) sb.Length = chars;
            return sb.ToString();
        }
    }
}
