using System.Collections.Generic;
using System.IO;

namespace Sdo.Settings.Vfs
{
    /// <summary>VFS 裡的一筆條目。<see cref="RealPath"/> 只有真實檔案系統那層才有值 —— pak 內的條目沒有
    /// 真實路徑,這正是 <see cref="SdoVfs.ResolveRealPath"/> 存在的理由(<c>UnityWebRequestMultimedia</c>
    /// 之類只吃 <c>file://</c> 的 API 得靠它判斷「這個檔到底有沒有實體」)。</summary>
    public struct VfsEntry
    {
        /// <summary>正規化路徑(相對 DATA 根)。</summary>
        public string Path;

        /// <summary>解開後的位元組數。<see cref="IsWhiteout"/> 為 true 時無意義。</summary>
        public long Size;

        /// <summary>這是一筆「刪除標記」:命中它就停止往下層找,回報檔案不存在。只有 patch 卷會產生。</summary>
        public bool IsWhiteout;

        /// <summary>真實檔案系統上的絕對路徑;pak 內的條目 → null。</summary>
        public string RealPath;
    }

    /// <summary>VFS 的一層。掛載順序與解析規則見 <c>docs/architecture/data-packaging.md</c> §4.2。
    ///
    /// 實作必須是**執行緒安全的唯讀**:<c>Sdo.Game.AvatarAssetCache</c> 會在背景執行緒預讀資產。
    /// 所有方法收的都是已正規化的路徑(<see cref="VfsPath.Normalize"/> 的輸出),實作不必再正規化一次。</summary>
    public interface IVfsProvider
    {
        /// <summary>診斷用的短名(例:<c>loose:H:\…\DATA</c>、<c>pak:base_avatar</c>)。</summary>
        string Name { get; }

        /// <summary>這一層有沒有這個路徑。找到 whiteout 也要回 true(<c>entry.IsWhiteout</c> = true)——
        /// 上層的呼叫端要靠它停止往下找。</summary>
        bool TryGet(string normalized, out VfsEntry entry);

        /// <summary>整份讀出。不存在或是 whiteout → null。</summary>
        byte[] ReadAllBytes(string normalized);

        /// <summary>開串流。不存在或是 whiteout → null。呼叫端負責 Dispose。</summary>
        Stream OpenRead(string normalized);

        /// <summary><paramref name="normalizedDir"/> 底下的條目(空字串 = 根)。含 whiteout ——
        /// 合併多層時要靠它把低層的同名檔剔掉。順序不保證。</summary>
        IEnumerable<VfsEntry> EnumerateUnder(string normalizedDir, bool recursive);
    }
}
