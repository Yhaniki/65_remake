using System;
using System.Collections.Generic;

namespace Sdo.Net.Server
{
    /// <summary>server 上一個已存放歌曲包的紀錄(packs/&lt;packId&gt;.json 的內容摘要)。</summary>
    public struct BlobPackRecord
    {
        /// <summary>歌曲包的跨電腦身分(<c>Sdo.Osu.SongPackId</c>)。</summary>
        public string PackId;

        /// <summary>
        /// 最後一次被用到的時間(UTC,毫秒)。上傳/下載/查詢命中都要更新它。
        ///
        /// 🔴 **不可以用檔案系統的 atime 代替**:Linux 上 <c>noatime</c> 是很常見的掛載選項,
        /// 那時 <c>LastAccessTime</c> 根本不會動 —— 清理邏輯會以為每個包都「剛用過」而永遠不刪,
        /// 磁碟就這樣一直長大。所以明確寫進 json 自己管。
        /// </summary>
        public long LastUsedUtcMs;

        /// <summary>這個包引用到的檔案內容雜湊(去重之後,同一個 sha 只會出現一次)。</summary>
        public string[] Shas;

        /// <summary>這個包所有檔案的位元組總和(統計/配額用;去重前的邏輯大小)。</summary>
        public long TotalBytes;
    }

    /// <summary>清理要做的事。空的 plan = 什麼都不用動。</summary>
    public sealed class BlobSweepPlan
    {
        public readonly List<string> PacksToDelete = new List<string>();
        public readonly List<string> BlobsToDelete = new List<string>();

        public bool Empty => PacksToDelete.Count == 0 && BlobsToDelete.Count == 0;

        public override string ToString()
            => "packs=-" + PacksToDelete.Count + " blobs=-" + BlobsToDelete.Count;
    }

    /// <summary>
    /// 「哪些歌曲包該刪、哪些檔案沒人引用了」的**純決策**。不碰磁碟 → 可以注入時鐘直接單元測試。
    ///
    /// 儲存是**內容尋址**的:檔案本體放 <c>files/&lt;sha[0:2]&gt;/&lt;sha&gt;</c>,歌曲包只記一張
    /// 「相對路徑 → sha」的清單。這樣做的好處是需求裡最實際的那兩件事:
    ///   • 同一首歌第二次有人玩 → 一個檔都不用上傳;
    ///   • 只改了一張譜 → 只上傳那一張(音檔/圖片早就在了)。
    ///
    /// 刪除的順序很重要:先決定哪些**包**要走,再用剩下的包重算引用計數,計數 0 的檔案才刪。
    /// 反過來(先掃檔案)會刪掉還被別的包引用的檔案 —— 那個包之後下載給人就少了一個檔,
    /// 而且症狀是「別人下載完譜面對不上」,完全指不到清理邏輯。
    /// </summary>
    public static class BlobIndex
    {
        /// <summary>預設保存時間(小時)。需求:「檔案最多留一天」。</summary>
        public const int DefaultTtlHours = 24;

        /// <summary>
        /// 算出這一輪要刪什麼。
        /// </summary>
        /// <param name="packs">目前存放的所有歌曲包。</param>
        /// <param name="blobs">目前 <c>files/</c> 底下所有檔案的 sha 清單。</param>
        /// <param name="nowUtcMs">現在(UTC 毫秒)。注入 → 可測。</param>
        /// <param name="ttlHours">超過這麼久沒被用到就刪。&lt;=0 → 不依時間刪(只做配額)。</param>
        /// <param name="pinned">
        /// **絕對不能刪**的包 —— 存活房間「現在選的那首歌」。少了這一條,一場正在等人下載的比賽
        /// 會在中途被自己的清理程序把來源刪掉。
        /// </param>
        /// <param name="capBytes">總量上限。超過就從**最久沒用**的未 pinned 包開始刪到低於上限。&lt;=0 = 不限。</param>
        public static BlobSweepPlan Plan(IEnumerable<BlobPackRecord> packs, IEnumerable<string> blobs,
                                        long nowUtcMs, int ttlHours,
                                        ICollection<string> pinned, long capBytes)
        {
            var plan = new BlobSweepPlan();
            var live = new List<BlobPackRecord>();
            if (packs != null)
                foreach (var p in packs)
                    if (!string.IsNullOrEmpty(p.PackId)) live.Add(p);

            bool IsPinned(string id) => pinned != null && pinned.Contains(id);

            // ① 過期(且沒被 pin)的包
            if (ttlHours > 0)
            {
                long ttlMs = (long)ttlHours * 3600L * 1000L;
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    var p = live[i];
                    if (IsPinned(p.PackId)) continue;
                    if (nowUtcMs - p.LastUsedUtcMs <= ttlMs) continue;
                    plan.PacksToDelete.Add(p.PackId);
                    live.RemoveAt(i);
                }
            }

            // ② 還是超過總量上限 → 從最久沒用的開始丟(pinned 的跳過)
            if (capBytes > 0)
            {
                long total = 0;
                for (int i = 0; i < live.Count; i++) total += live[i].TotalBytes;
                if (total > capBytes)
                {
                    var byAge = new List<BlobPackRecord>(live);
                    byAge.Sort((a, b) => a.LastUsedUtcMs.CompareTo(b.LastUsedUtcMs));   // 舊的在前
                    for (int i = 0; i < byAge.Count && total > capBytes; i++)
                    {
                        if (IsPinned(byAge[i].PackId)) continue;
                        plan.PacksToDelete.Add(byAge[i].PackId);
                        total -= byAge[i].TotalBytes;
                        live.Remove(byAge[i]);
                    }
                }
            }

            // ③ 用**剩下的**包重算引用計數 → 沒人引用的檔案才刪
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < live.Count; i++)
            {
                var shas = live[i].Shas;
                if (shas == null) continue;
                for (int j = 0; j < shas.Length; j++)
                    if (!string.IsNullOrEmpty(shas[j])) referenced.Add(shas[j]);
            }
            if (blobs != null)
                foreach (var sha in blobs)
                    if (!string.IsNullOrEmpty(sha) && !referenced.Contains(sha))
                        plan.BlobsToDelete.Add(sha);

            return plan;
        }

        /// <summary>這個包過期了嗎(單獨判斷用;<see cref="Plan"/> 內部走同一條算式)。</summary>
        public static bool IsExpired(long lastUsedUtcMs, long nowUtcMs, int ttlHours)
            => ttlHours > 0 && nowUtcMs - lastUsedUtcMs > (long)ttlHours * 3600L * 1000L;

        /// <summary>內容尋址的存放路徑(相對 <c>files/</c>):<c>&lt;sha[0:2]&gt;/&lt;sha&gt;</c>。
        /// 分兩層是為了不要讓一個目錄裡塞幾十萬個檔案(某些檔案系統會明顯變慢)。</summary>
        public static string BlobRelPath(string sha)
        {
            if (string.IsNullOrEmpty(sha) || sha.Length < 4) return null;
            return sha.Substring(0, 2) + "/" + sha;
        }
    }
}
