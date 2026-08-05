using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Sdo.Settings.Vfs
{
    /// <summary>寫出一個 <c>.pak</c>。規格見 <c>docs/architecture/data-packaging.md</c> §3。
    ///
    /// <para>正式打包走 <c>tools/build_pak.py</c>(它才做分卷、manifest、patch diff)。這一份存在的理由是
    /// **讓 round-trip 測試不必依賴 Python** —— 讀取器與寫入器由同一份 <see cref="PakFormat"/> 定義,
    /// 測試能直接證明「寫出去的讀得回來」。兩邊的實作對不上時,這裡的測試會先紅。</para>
    ///
    /// <para>輸出是 deterministic 的:條目依 <c>pathHash</c> 排序、沒有時間戳。同樣的輸入產生
    /// byte-for-byte 相同的檔 —— 那是做 patch diff 的前提。</para>
    /// </summary>
    public sealed class PakWriter
    {
        private class Item
        {
            public string Path;          // 正規化
            public byte[] Data;          // null = whiteout
            public bool Compress;
            public ushort CryptRange;
        }

        private readonly List<Item> _items = new List<Item>();
        private readonly uint _pakId;
        private readonly bool _encrypt;

        /// <param name="pakId">卷 id —— 決定金鑰派生,以及 patch 卷之間的先後。</param>
        /// <param name="encrypt">false = 明碼(開發/測試用,格式其餘部分完全相同)。</param>
        public PakWriter(uint pakId, bool encrypt = false)
        {
            _pakId = pakId;
            _encrypt = encrypt;
        }

        /// <summary>加一個檔。<paramref name="path"/> 會被正規化;無效路徑(逃出根之類)直接丟例外 ——
        /// 打包時就該爆,不該讓壞路徑進到成品裡。</summary>
        public PakWriter Add(string path, byte[] data, bool compress = true, ushort cryptRange = PakFormat.CryptWhole)
        {
            var norm = VfsPath.Normalize(path);
            if (string.IsNullOrEmpty(norm)) throw new ArgumentException("無效的 pak 路徑: " + path, "path");
            if (VfsPath.IsReserved(norm))
                throw new ArgumentException("reserved 目錄不得打包(PROFILE/ADDON/CACHE/REPLAY): " + norm, "path");

            _items.Add(new Item
            {
                Path = norm,
                Data = data ?? new byte[0],
                Compress = compress,
                CryptRange = _encrypt ? cryptRange : PakFormat.CryptNone,
            });
            return this;
        }

        /// <summary>加一筆刪除標記 —— patch 卷用來「拿掉」低層的檔。</summary>
        public PakWriter AddWhiteout(string path)
        {
            var norm = VfsPath.Normalize(path);
            if (string.IsNullOrEmpty(norm)) throw new ArgumentException("無效的 pak 路徑: " + path, "path");
            _items.Add(new Item { Path = norm, Data = null });
            return this;
        }

        /// <summary>寫出檔案。</summary>
        public void WriteTo(string outputPath)
        {
            var bytes = Build();
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(outputPath, bytes);
        }

        /// <summary>組出整個 pak 的位元組。</summary>
        public byte[] Build()
        {
            // 依 pathHash 排序 → deterministic，而且讀取端可以二分搜尋。
            var items = new List<Item>(_items);
            items.Sort((a, b) =>
            {
                int c = VfsPath.Hash(a.Path).CompareTo(VfsPath.Hash(b.Path));
                return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
            });

            // pathHash 碰撞 → 直接失敗。10 萬條路徑的碰撞機率約 2.7e-10，真的撞到必須是「改個檔名」
            // 而不是靜默帶過 —— 靜默帶過的後果是某個資產永遠讀到另一個檔的內容。
            for (int i = 1; i < items.Count; i++)
                if (VfsPath.Hash(items[i].Path) == VfsPath.Hash(items[i - 1].Path) &&
                    !string.Equals(items[i].Path, items[i - 1].Path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "pathHash 碰撞: " + items[i - 1].Path + " vs " + items[i].Path + " —— 改掉其中一個檔名");

            var entries = new List<PakFormat.Entry>(items.Count);
            var paths = new List<string>(items.Count);
            var blobs = new List<byte[]>(items.Count);
            long dataCursor = 0;

            foreach (var it in items)
            {
                paths.Add(it.Path);

                if (it.Data == null)   // whiteout：不佔資料區
                {
                    entries.Add(new PakFormat.Entry
                    {
                        PathHash = VfsPath.Hash(it.Path),
                        RawSize = PakFormat.WhiteoutRawSize,
                        DataOffset = dataCursor,
                        StoredSize = 0,
                        Compression = PakFormat.CompressionStore,
                        CryptRange = PakFormat.CryptNone,
                        Crc32 = 0,
                    });
                    blobs.Add(new byte[0]);
                    continue;
                }

                uint crc = PakFormat.Crc32(it.Data);
                byte[] stored = it.Data;
                ushort comp = PakFormat.CompressionStore;

                if (it.Compress && it.Data.Length > 0)
                {
                    var deflated = Deflate(it.Data);
                    // 壓不小就存原樣 —— DDS/mp3 這類已壓縮的資料 deflate 後常常反而變大。
                    if (deflated != null && deflated.Length < it.Data.Length) { stored = deflated; comp = PakFormat.CompressionDeflate; }
                }

                if (it.CryptRange != PakFormat.CryptNone)
                {
                    stored = (byte[])stored.Clone();   // 別動到呼叫端的陣列（也可能是 it.Data 本身）
                    int count = it.CryptRange == PakFormat.CryptHeaderOnly
                        ? Math.Min(PakFormat.HeaderCryptBytes, stored.Length)
                        : stored.Length;
                    PakCrypto.XorKeystream(PakCrypto.DataKey(_pakId), stored, 0, count, dataCursor);
                }

                entries.Add(new PakFormat.Entry
                {
                    PathHash = VfsPath.Hash(it.Path),
                    RawSize = (uint)it.Data.Length,
                    DataOffset = dataCursor,
                    StoredSize = (uint)stored.Length,
                    Compression = comp,
                    CryptRange = it.CryptRange,
                    Crc32 = crc,
                });
                blobs.Add(stored);
                dataCursor += stored.Length;
            }

            // 索引：組 → 壓 → 加密 → 算 MAC。
            var indexRaw = PakFormat.WriteIndex(entries, paths);
            var indexStored = Deflate(indexRaw);
            bool indexCompressed = indexStored != null && indexStored.Length < indexRaw.Length;
            if (!indexCompressed) indexStored = indexRaw;

            uint flags = 0;
            byte[] mac = new byte[16];
            if (_encrypt)
            {
                indexStored = (byte[])indexStored.Clone();
                PakCrypto.XorKeystream(PakCrypto.IndexKey(_pakId), indexStored, 0, indexStored.Length, 0);
                mac = PakCrypto.IndexMac(_pakId, indexStored);
                flags |= PakFormat.FlagIndexEncrypted;
                foreach (var e in entries) if (e.CryptRange != PakFormat.CryptNone) { flags |= PakFormat.FlagDataEncrypted; break; }
            }

            long dataOffset = PakFormat.HeaderSize;
            long indexOffset = dataOffset + dataCursor;

            var header = PakFormat.WriteHeader(new PakFormat.Header
            {
                FormatVersion = PakFormat.FormatVersion,
                Flags = flags,
                EntryCount = (uint)entries.Count,
                PakId = _pakId,
                IndexOffset = indexOffset,
                IndexStored = (uint)indexStored.Length,
                IndexRaw = indexCompressed ? (uint)indexRaw.Length : (uint)indexStored.Length,
                DataOffset = dataOffset,
                IndexMac = mac,
            });

            using (var ms = new MemoryStream())
            {
                ms.Write(header, 0, header.Length);
                foreach (var b in blobs) ms.Write(b, 0, b.Length);
                ms.Write(indexStored, 0, indexStored.Length);
                return ms.ToArray();
            }
        }

        /// <summary>raw deflate(無 zlib/gzip 表頭)—— 對應 Python 的
        /// <c>zlib.compressobj(wbits=-15)</c>,以及 C# 的 <see cref="DeflateStream"/>。</summary>
        private static byte[] Deflate(byte[] data)
        {
            try
            {
                using (var ms = new MemoryStream())
                {
                    using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
                        ds.Write(data, 0, data.Length);
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }
    }
}
