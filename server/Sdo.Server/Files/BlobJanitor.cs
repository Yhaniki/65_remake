using System;
using System.Collections.Generic;
using Sdo.Net.Server;

namespace Sdo.Server.Files
{
    /// <summary>一次清理的結果(寫進日誌用)。</summary>
    public struct BlobSweepResult
    {
        public int PacksDeleted;
        public int BlobsDeleted;
        public long BytesFreed;
        public long UsedBytesAfter;

        public bool DidAnything => PacksDeleted > 0 || BlobsDeleted > 0;

        public override string ToString()
            => "刪了 " + PacksDeleted + " 個歌曲包、" + BlobsDeleted + " 個檔案,釋出 " +
               (BytesFreed / (1024 * 1024)) + " MB;目前佔用 " + (UsedBytesAfter / (1024 * 1024)) + " MB";
    }

    /// <summary>
    /// 定期清掉沒人用的歌曲暫存(需求:「檔案最多留一天,定期清除」)。
    ///
    /// 決策全部在 <see cref="BlobIndex"/>(純函式、可單測);這裡只做兩件事:
    /// 到時間了沒、以及把 plan 套到磁碟上。
    ///
    /// ★ 為什麼跑在 Hub 的單執行緒上而不是自己開一條:清理與上傳/下載會動到同一批檔案。
    ///   另開執行緒就要處理「正在被下載的包剛好過期」這種競態 —— 而那種 bug 的症狀是
    ///   「偶爾有人下載到一半失敗」,重現不了也查不到。掃幾千個檔案是幾十毫秒的事,
    ///   15 分鐘一次卡住 hub 那麼一下完全可以接受,換來的是零競態。
    /// </summary>
    public sealed class BlobJanitor
    {
        /// <summary>掃描間隔:15 分鐘。TTL 是 24 小時,不需要更密。</summary>
        public const int SweepIntervalMs = 15 * 60 * 1000;

        private readonly DiskBlobIo _io;
        private readonly int _ttlHours;
        private readonly long _capBytes;
        private long _nextSweepMs;

        public BlobJanitor(DiskBlobIo io, int ttlHours, long capBytes, long nowMs)
        {
            _io = io;
            _ttlHours = ttlHours;
            _capBytes = capBytes;

            // 開機**不要**立刻掃:那時候還沒有任何房間,pinned 集合是空的 → 剛好把上一輪
            // 還在用的包全部當成沒人要的刪掉。等一個間隔,讓房間先重新建立起來。
            _nextSweepMs = nowMs + SweepIntervalMs;
        }

        public bool Due(long nowMs) => nowMs >= _nextSweepMs;

        /// <summary>
        /// 掃一次。<paramref name="pinned"/> = 存活房間現在選的那些 packId —— **必須傳,不能傳 null**
        /// 當作「沒有」:那會讓一場正在等人下載的比賽在中途失去來源。
        /// </summary>
        public BlobSweepResult Sweep(long nowMs, ICollection<string> pinned)
        {
            _nextSweepMs = nowMs + SweepIntervalMs;

            var packs = _io.ListPackRecords();
            var blobs = new List<string>(_io.ListBlobShas());
            var plan = BlobIndex.Plan(packs, blobs, nowMs, _ttlHours, pinned, _capBytes);

            var r = new BlobSweepResult();
            for (int i = 0; i < plan.PacksToDelete.Count; i++)
                if (_io.DeletePack(plan.PacksToDelete[i])) r.PacksDeleted++;

            for (int i = 0; i < plan.BlobsToDelete.Count; i++)
            {
                long len = _io.BlobLength(plan.BlobsToDelete[i]);
                if (!_io.DeleteBlob(plan.BlobsToDelete[i])) continue;
                r.BlobsDeleted++;
                if (len > 0) r.BytesFreed += len;
            }

            r.UsedBytesAfter = _io.UsedBytes();
            return r;
        }
    }
}
