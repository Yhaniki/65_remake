using System;
using System.Collections.Generic;
using System.IO;

namespace Sdo.Settings.Vfs
{
    /// <summary>把一個真實資料夾當成 VFS 的一層 —— 也就是「散裝檔案」層。
    ///
    /// 這一層的優先權高於所有 pak(見 <c>docs/architecture/data-packaging.md</c> §4.2):丟一個
    /// <c>DATA/AVATAR/xxx.dds</c> 就能蓋掉 pak 內的同名檔,開發覆寫、熱修、mod 都靠它。
    ///
    /// 這也是為什麼 editor 下什麼都不用改就會動:<c>assets/sdox_offline/Extracted</c> 底下沒有任何 pak,
    /// 所以每一個查詢都命中這一層,行為跟現在直接讀檔完全一樣。既有那一大票直接讀真實 DATA 路徑的
    /// EditMode 測試因此一行都不用動。
    ///
    /// 永遠不產生 whiteout —— 真實檔案系統沒有「刪除標記」這種東西,那是 patch 卷才有的概念。</summary>
    public sealed class LooseDirProvider : IVfsProvider
    {
        private readonly string _root;

        /// <param name="root">真實資料夾的絕對路徑。不存在也可以建構(當成空的一層),因為 DATA 樹在
        /// 某些工具/測試情境下要到後面才出現。</param>
        public LooseDirProvider(string root)
        {
            _root = string.IsNullOrEmpty(root) ? "" : root;
            Name = "loose:" + _root;
        }

        public string Name { get; private set; }

        /// <summary>這一層的真實根目錄。</summary>
        public string RootDir { get { return _root; } }

        /// <summary>正規化路徑 → 真實絕對路徑;根為空或路徑無效 → null。
        /// <paramref name="normalized"/> 已經被 <see cref="VfsPath.Normalize"/> 擋掉逃出根的寫法,
        /// 這裡只做拼接。</summary>
        public string RealPathFor(string normalized)
        {
            if (string.IsNullOrEmpty(_root) || normalized == null) return null;
            if (normalized.Length == 0) return _root;
            try { return Path.Combine(_root, normalized.Replace('/', Path.DirectorySeparatorChar)); }
            catch { return null; }
        }

        public bool TryGet(string normalized, out VfsEntry entry)
        {
            entry = default(VfsEntry);
            var real = RealPathFor(normalized);
            if (string.IsNullOrEmpty(real)) return false;

            try
            {
                var fi = new FileInfo(real);
                if (!fi.Exists) return false;
                entry = new VfsEntry { Path = normalized, Size = fi.Length, IsWhiteout = false, RealPath = real };
                return true;
            }
            catch { return false; }
        }

        public byte[] ReadAllBytes(string normalized)
        {
            var real = RealPathFor(normalized);
            if (string.IsNullOrEmpty(real)) return null;
            try { return File.Exists(real) ? File.ReadAllBytes(real) : null; }
            catch { return null; }
        }

        public Stream OpenRead(string normalized)
        {
            var real = RealPathFor(normalized);
            if (string.IsNullOrEmpty(real)) return null;
            try { return File.Exists(real) ? new FileStream(real, FileMode.Open, FileAccess.Read, FileShare.Read) : null; }
            catch { return null; }
        }

        public IEnumerable<VfsEntry> EnumerateUnder(string normalizedDir, bool recursive)
        {
            var dir = RealPathFor(normalizedDir ?? "");
            if (string.IsNullOrEmpty(dir)) return NoEntries;

            // 用 FileInfo 而不是 GetFiles(string[]) + 逐檔 new FileInfo:Windows 上 EnumerateFiles 回來的
            // FileInfo 已經帶著 find data 裡的長度,少掉第二次 stat —— AVATAR 有 67,503 個檔,那是 67,503 次
            // 系統呼叫的差別。整份收集起來(而不是 yield)是因為列舉中途可能拋例外,而 iterator 不能包 try/catch。
            var list = new List<VfsEntry>();
            try
            {
                var info = new DirectoryInfo(dir);
                if (!info.Exists) return NoEntries;

                var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var fi in info.EnumerateFiles("*", opt))
                {
                    var rel = ToRelative(fi.FullName);
                    if (rel == null) continue;
                    list.Add(new VfsEntry { Path = rel, Size = fi.Length, IsWhiteout = false, RealPath = fi.FullName });
                }
            }
            catch { /* 權限不足 / 掃到一半被刪 → 回報已經拿到的那些 */ }

            return list;
        }

        private static readonly VfsEntry[] NoEntries = new VfsEntry[0];

        /// <summary>真實絕對路徑 → 相對這一層根的正規化路徑;不在根底下 → null。</summary>
        private string ToRelative(string realPath)
        {
            if (string.IsNullOrEmpty(_root) || string.IsNullOrEmpty(realPath)) return null;

            string root = _root;
            if (root[root.Length - 1] != Path.DirectorySeparatorChar && root[root.Length - 1] != '/')
                root += Path.DirectorySeparatorChar;

            if (realPath.Length <= root.Length) return null;
            if (!realPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

            return realPath.Substring(root.Length).Replace('\\', '/');
        }
    }
}
