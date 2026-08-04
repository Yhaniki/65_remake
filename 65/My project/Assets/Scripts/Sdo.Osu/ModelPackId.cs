using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Sdo.Osu
{
    /// <summary>
    /// MMD 模型資料夾的內容指紋(<c>packId</c>)—— 模型在網路上的**唯一身份**,語意與
    /// <see cref="SongPackId"/> 對歌曲完全相同:兩台機器上同一份模型算出同一個 id,
    /// 換一張貼圖就換一個 id。玩家的外觀(<c>NetAvatarLook.MmdPack</c>)帶的就是這個字串,
    /// server 的 blob 倉庫也用它當 key。
    ///
    /// 🔴 <b>與歌曲的差別只有一個:每個檔都算完整 SHA-256。</b>
    /// 歌曲那邊只 hash 譜面,因為 packId 要在**每次開機掃整個歌庫**時算得出來,而歌庫的音檔是好幾 GB。
    /// 模型不一樣:只有「我現在穿的這一個」需要 id(而且只在分享時算),一份模型約 10 MB,
    /// 全部讀完雜湊約 30 ms —— 為了省這 30 ms 去接受「貼圖被換掉但長度剛好一樣 → id 不變」
    /// 這個洞,划不來。全 hash 之後 id 與內容是嚴格一對一的。
    ///
    /// packId 的字串格式與歌曲共用(<c>sha256:</c> + 32 hex),所以 <see cref="SongPackId.IsWellFormed"/>
    /// 對兩者都適用,server 也用同一個內容尋址倉庫存兩種包 —— 但**包本身帶 kind**,
    /// 因為收檔時要套的過濾表不同(見 <see cref="ModelPackFilter"/>)。
    ///
    /// <see cref="BuildManifest"/> 是純函式(可直接單元測試);只有 <see cref="ScanFolder"/> 碰 IO。
    /// 零 UnityEngine。
    /// </summary>
    public static class ModelPackId
    {
        /// <summary>manifest 每一行的欄位分隔符。與歌曲同一個(ASCII Unit Separator),理由見 <see cref="SongPackId.FieldSep"/>。</summary>
        public const char FieldSep = SongPackId.FieldSep;

        /// <summary>
        /// 組出 manifest(純函式)。每個檔一行:
        /// <c>&lt;正規化路徑&gt; 0x1F &lt;長度&gt; 0x1F &lt;sha256&gt;</c>,以 Ordinal 排序後用 <c>\n</c> 串起來。
        ///
        /// 排序是必要的:檔案系統的列舉順序不保證穩定,不排序的話同一個資料夾會算出不同的 packId。
        ///
        /// **沒有 hash 的項目一律略過**,不是「當成空字串照樣寫進去」—— 後者會讓「還沒算 hash 的清單」
        /// 算出一個看起來合法、但跟真正的 id 不同的 packId,而那個錯誤會一路傳到 server 才被發現。
        /// 寧可回空字串(= 這份清單還不能當身分用)。
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
                if (string.IsNullOrEmpty(f.Sha256)) return string.Empty;   // 見上:寧可算不出來

                lines.Add(rel + FieldSep + f.Length.ToString(CultureInfo.InvariantCulture)
                              + FieldSep + f.Sha256.ToLowerInvariant());
            }
            if (lines.Count == 0) return string.Empty;

            lines.Sort(StringComparer.Ordinal);
            return string.Join("\n", lines.ToArray());
        }

        /// <summary>檔案清單 → packId(純函式)。任何一個檔缺 hash 就回空字串。</summary>
        public static string Compute(IList<PackFileEntry> files) => SongPackId.FromManifest(BuildManifest(files));

        /// <summary>
        /// 這份清單構成一個合法的模型包嗎?(server 收上傳時要驗,client 上傳前也自己驗一次)
        ///
        /// 條件:非空、每個路徑都過 <see cref="ModelPackFilter"/>、檔案數與總量在上限內、
        /// **而且至少有一個 .pmx** —— 沒有模型本體的一包貼圖不是模型,收下來也沒有任何用途。
        /// </summary>
        public static bool IsValidPack(IList<PackFileEntry> files, out string reason)
        {
            reason = "";
            if (files == null || files.Count == 0) { reason = "empty"; return false; }
            if (files.Count > NetPackLimits.MaxPackFiles) { reason = "tooManyFiles:" + files.Count; return false; }

            bool hasModel = false;
            long total = 0;
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                var v = ModelPackFilter.Classify(f.RelPath, f.Length);
                if (v != PackFileVerdict.Include) { reason = v + ":" + f.RelPath; return false; }
                if (ModelPackFilter.IsModelFile(f.RelPath)) hasModel = true;
                total += f.Length;
            }
            if (!hasModel) { reason = "noPmx"; return false; }
            if (total > MaxPackBytes) { reason = "packTooBig:" + total; return false; }
            return true;
        }

        /// <summary>
        /// 一份模型包的總量上限。實測初音那份 ~10 MB(十張 2048² 貼圖是大宗);
        /// 帶多套服裝的模型可以到 50-60 MB。128 MB 留了很寬的餘裕,同時擋住「把整個素材庫當模型上傳」。
        /// 這是**分享**的上限,不是本機能用的模型上限 —— 自己放在 DATA/MODEL 的模型多大都能用,
        /// 只是超過這個大小就不會傳給別人(別人看到你的 SDO 穿搭)。
        /// </summary>
        public const long MaxPackBytes = 128L * 1024 * 1024;

        // ---- 以下碰 IO ----

        /// <summary>
        /// 掃一個模型資料夾,回傳「可傳輸」的檔案清單(每個檔都算好 SHA-256)。
        ///
        /// 與 <see cref="SongPackId.ScanFolder"/> 一樣手動掃兩層而不是 <c>SearchOption.AllDirectories</c> ——
        /// 後者會跟隨 junction/symlink,指回自己的連結就是無限遞迴。<see cref="ModelPackFilter.MaxDepth"/>
        /// 是 1,所以兩層就夠,迴圈風險歸零。
        ///
        /// 回 false = 資料夾讀不到、裡面沒有任何可傳的檔、或檔案數爆掉。
        /// </summary>
        public static bool ScanFolder(string folderPath, out List<PackFileEntry> files, out PackScanStats stats)
        {
            files = new List<PackFileEntry>();
            stats = new PackScanStats();
            if (string.IsNullOrEmpty(folderPath)) return false;

            try
            {
                if (!Directory.Exists(folderPath)) return false;
                if (!ScanOneDir(folderPath, folderPath, files, ref stats)) return false;
                foreach (var sub in Directory.EnumerateDirectories(folderPath))
                    if (!ScanOneDir(folderPath, sub, files, ref stats)) return false;
            }
            catch { return false; }

            return files.Count > 0;
        }

        /// <summary>掃資料夾直接得到 packId。算不出來(讀不到 / 不是合法模型包)回空字串。</summary>
        public static string ForFolder(string folderPath)
        {
            List<PackFileEntry> files;
            PackScanStats stats;
            if (!ScanFolder(folderPath, out files, out stats)) return string.Empty;
            string why;
            if (!IsValidPack(files, out why)) return string.Empty;
            return Compute(files);
        }

        private static bool ScanOneDir(string root, string dir, List<PackFileEntry> files, ref PackScanStats stats)
        {
            foreach (var full in Directory.EnumerateFiles(dir))
            {
                string rel = ToRelative(root, full);
                if (rel.Length == 0) continue;

                long len;
                try { len = new FileInfo(full).Length; }
                catch { len = -1; }

                var verdict = ModelPackFilter.Classify(rel, len);
                if (verdict != PackFileVerdict.Include) { Tally(ref stats, verdict, len); continue; }

                files.Add(new PackFileEntry(SafeRelPath.Normalize(rel), len, SongPackId.HashFile(full)));
                stats.IncludedFiles++;
                stats.IncludedBytes += len;

                if (files.Count > NetPackLimits.MaxPackFiles) return false;
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
                case PackFileVerdict.UnknownType: s.SkippedUnknown++; break;
                case PackFileVerdict.TooBig: s.SkippedTooBig++; break;
                case PackFileVerdict.UnsafePath: s.SkippedUnsafe++; break;
                case PackFileVerdict.TooDeep: s.SkippedTooDeep++; break;
            }
        }

        /// <summary>絕對路徑 → 相對於資料夾的路徑。不用 Path.GetRelativePath(netstandard2.0 沒有)。</summary>
        private static string ToRelative(string folderPath, string fullPath)
        {
            string root = folderPath.TrimEnd('/', '\\');
            if (fullPath.Length <= root.Length + 1) return string.Empty;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            return fullPath.Substring(root.Length + 1);
        }
    }
}
