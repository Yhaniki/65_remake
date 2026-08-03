using System.Collections.Generic;

namespace Sdo.Net.Server
{
    /// <summary>
    /// 每個使用者的上傳配額(M10:公網化的上傳限制)。
    ///
    /// MVP 已經有「單檔 32 MB、整包 200 MB、blob 目錄 20 GB」這些上限,但它們擋不住
    /// **一個人反覆上傳不同的歌**:每一包都合法,加起來卻能在幾分鐘內把磁碟吃光
    /// (而且每一包都會被 pin 住直到房間換歌,janitor 也救不了)。
    /// 這裡加的是「一個人每小時最多傳多少位元組」。
    ///
    /// 用滑動的「時段桶」而不是精確的滑動視窗:記住這個小時傳了多少、上一個小時傳了多少,
    /// 換小時就把前者移到後者。精確度到不了分鐘級,但配額本來就是粗粒度的東西,
    /// 而這種做法的狀態是 O(人數) 而不是 O(上傳次數)。
    ///
    /// 純邏輯 + 注入時間 → 可以直接單元測試。
    /// </summary>
    public sealed class UploadQuota
    {
        /// <summary>預設每人每小時 1 GB。一首歌上限 200 MB,所以正常玩家一小時傳五首都還在額度內。</summary>
        public const long DefaultBytesPerHour = 1024L * 1024L * 1024L;

        private sealed class Bucket
        {
            public long HourIndex;      // 從 epoch 起算的第幾個小時
            public long Used;           // 這個小時已用
        }

        private readonly Dictionary<int, Bucket> _byUser = new Dictionary<int, Bucket>();

        /// <summary>每人每小時的位元組上限。&lt;=0 = 不限(預設的 LAN 行為)。</summary>
        public long BytesPerHour = 0;

        public bool Enabled => BytesPerHour > 0;

        /// <summary>
        /// 這個人現在還能不能再傳 <paramref name="bytes"/>。**只查不記**
        /// (要在 <c>blobUploadBegin</c> 那一刻先問,不然清單都收下了才發現超額)。
        /// </summary>
        public bool Allows(int userId, long bytes, long nowMs)
        {
            if (!Enabled || bytes <= 0) return true;
            return Used(userId, nowMs) + bytes <= BytesPerHour;
        }

        /// <summary>記下實際用掉的量(收完之後叫)。</summary>
        public void Add(int userId, long bytes, long nowMs)
        {
            if (!Enabled || bytes <= 0) return;
            var b = Roll(userId, nowMs);
            b.Used += bytes;
        }

        /// <summary>這個人這個小時已經用掉多少。</summary>
        public long Used(int userId, long nowMs)
        {
            Bucket b;
            if (!_byUser.TryGetValue(userId, out b)) return 0;
            return HourOf(nowMs) == b.HourIndex ? b.Used : 0;
        }

        /// <summary>還剩多少額度(沒啟用時回 long.MaxValue)。</summary>
        public long Remaining(int userId, long nowMs)
            => Enabled ? System.Math.Max(0, BytesPerHour - Used(userId, nowMs)) : long.MaxValue;

        /// <summary>那個人離開之後把他的紀錄清掉(不然這張表會跟著 server 的執行時間一直長)。</summary>
        public void Forget(int userId) => _byUser.Remove(userId);

        private Bucket Roll(int userId, long nowMs)
        {
            long h = HourOf(nowMs);
            Bucket b;
            if (!_byUser.TryGetValue(userId, out b)) { b = new Bucket { HourIndex = h }; _byUser[userId] = b; }
            if (b.HourIndex != h) { b.HourIndex = h; b.Used = 0; }
            return b;
        }

        private static long HourOf(long nowMs) => nowMs / (3600L * 1000L);
    }
}
