using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Sdo.Settings.Vfs
{
    /// <summary>一個 <c>.pak</c> 檔當成 VFS 的一層。規格見 <c>docs/architecture/data-packaging.md</c>。
    ///
    /// 開檔時把索引整份讀進來(解密 → 解壓 → 建 <c>Dictionary</c>),之後每次讀取只 seek + 讀該條目那一段。
    /// 10 萬筆索引約 4 MB,載入是毫秒級 —— 這正是打包的意義:原本 67,503 個小檔每個都要一次冷開檔
    /// (7–11ms),現在只有一次。
    ///
    /// <para><b>執行緒</b>:查詢與列舉是唯讀、執行緒安全的;讀取內容會鎖住 <see cref="FileStream"/>
    /// (單一檔案控制代碼,seek+read 必須是原子的)。背景預讀資產會踩到這條路。</para>
    ///
    /// <para><b>壞檔一律安靜跳過</b>:<see cref="TryOpen"/> 回 false 而不是丟例外 —— 一個壞掉或被改到的
    /// 卷不該讓整個遊戲開不起來,少一層頂多是某些資產讀不到。</para>
    /// </summary>
    public sealed class PakProvider : IVfsProvider, IDisposable
    {
        private readonly FileStream _stream;
        private readonly object _readGate = new object();
        private readonly PakFormat.Header _header;
        private readonly Dictionary<ulong, int> _byHash;
        private readonly PakFormat.Entry[] _entries;
        private readonly string[] _paths;
        private readonly byte[] _dataKey;

        public string Name { get; private set; }

        /// <summary>這一卷的 id —— 決定金鑰派生與 patch 卷的先後。</summary>
        public uint PakId { get { return _header.PakId; } }

        /// <summary>條目數(含 whiteout)。</summary>
        public int Count { get { return _entries.Length; } }

        private PakProvider(string path, FileStream stream, PakFormat.Header header,
                            PakFormat.Entry[] entries, string[] paths, Dictionary<ulong, int> byHash)
        {
            _stream = stream; _header = header; _entries = entries; _paths = paths; _byHash = byHash;
            _dataKey = (header.Flags & PakFormat.FlagDataEncrypted) != 0 ? PakCrypto.DataKey(header.PakId) : null;
            Name = "pak:" + Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>開一個 pak。檔案不存在 / 不是 pak / 索引壞掉 / MAC 對不上 → false(不丟例外)。</summary>
        public static bool TryOpen(string path, out PakProvider provider)
        {
            provider = null;
            FileStream fs = null;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

                var head = new byte[PakFormat.HeaderSize];
                if (ReadAt(fs, 0, head, 0, head.Length) != head.Length) { fs.Dispose(); return false; }

                PakFormat.Header header;
                if (!PakFormat.TryReadHeader(head, out header)) { fs.Dispose(); return false; }

                var indexRaw = ReadIndex(fs, header);
                if (indexRaw == null) { fs.Dispose(); return false; }

                PakFormat.Entry[] entries; string[] paths;
                if (!PakFormat.TryReadIndex(indexRaw, header.EntryCount, out entries, out paths)) { fs.Dispose(); return false; }

                var byHash = new Dictionary<ulong, int>(entries.Length);
                for (int i = 0; i < entries.Length; i++) byHash[entries[i].PathHash] = i;   // 碰撞由打包器擋，這裡後蓋前

                provider = new PakProvider(path, fs, header, entries, paths, byHash);
                return true;
            }
            catch
            {
                if (fs != null) { try { fs.Dispose(); } catch { } }
                return false;
            }
        }

        /// <summary>讀索引區:驗 MAC → 解密 → 解壓。任何一關不過 → null。</summary>
        private static byte[] ReadIndex(FileStream fs, PakFormat.Header header)
        {
            var stored = new byte[header.IndexStored];
            if (ReadAt(fs, header.IndexOffset, stored, 0, stored.Length) != stored.Length) return null;

            bool encrypted = (header.Flags & PakFormat.FlagIndexEncrypted) != 0;
            if (encrypted)
            {
                // MAC 算在**密文**上 —— 先驗再解，壞檔不會被拿去解壓（zip bomb 之類的防線）。
                if (!PakCrypto.MacEquals(header.IndexMac, PakCrypto.IndexMac(header.PakId, stored))) return null;
                PakCrypto.XorKeystream(PakCrypto.IndexKey(header.PakId), stored, 0, stored.Length, 0);
            }

            if (header.IndexRaw == header.IndexStored) return stored;   // 沒壓縮
            return Inflate(stored, 0, stored.Length, (int)header.IndexRaw);
        }

        // ---------------- IVfsProvider ----------------

        public bool TryGet(string normalized, out VfsEntry entry)
        {
            entry = default(VfsEntry);
            int i = IndexOf(normalized);
            if (i < 0) return false;

            var e = _entries[i];
            entry = new VfsEntry
            {
                Path = _paths[i],
                Size = e.IsWhiteout ? 0 : e.RawSize,
                IsWhiteout = e.IsWhiteout,
                RealPath = null,          // pak 內的檔沒有實體 —— file:// 那條路要靠這個 null 判斷
            };
            return true;
        }

        public byte[] ReadAllBytes(string normalized)
        {
            int i = IndexOf(normalized);
            if (i < 0) return null;

            var e = _entries[i];
            if (e.IsWhiteout) return null;

            byte[] stored = new byte[e.StoredSize];
            lock (_readGate)
            {
                long at = _header.DataOffset + e.DataOffset;
                if (ReadAt(_stream, at, stored, 0, stored.Length) != stored.Length) return null;
            }

            Decrypt(stored, e);

            byte[] raw;
            if (e.Compression == PakFormat.CompressionStore) raw = stored;
            else
            {
                raw = Inflate(stored, 0, stored.Length, (int)e.RawSize);
                if (raw == null) return null;
            }

            // CRC 對不上 = 這一段壞了。回 null 讓下層(或散裝覆寫)接手，比餵出壞資料好 ——
            // 壞掉的 DDS/MSH 會變成整個畫面亂掉，那種問題查起來很貴。
            if (PakFormat.Crc32(raw) != e.Crc32) return null;
            return raw;
        }

        public Stream OpenRead(string normalized)
        {
            var bytes = ReadAllBytes(normalized);
            return bytes == null ? null : new MemoryStream(bytes, false);
        }

        public IEnumerable<VfsEntry> EnumerateUnder(string normalizedDir, bool recursive)
        {
            string dir = normalizedDir ?? "";
            for (int i = 0; i < _paths.Length; i++)
            {
                if (!VfsPath.IsUnder(_paths[i], dir, recursive)) continue;
                var e = _entries[i];
                yield return new VfsEntry
                {
                    Path = _paths[i],
                    Size = e.IsWhiteout ? 0 : e.RawSize,
                    IsWhiteout = e.IsWhiteout,
                    RealPath = null,
                };
            }
        }

        // ---------------- 內部 ----------------

        private int IndexOf(string normalized)
        {
            if (string.IsNullOrEmpty(normalized)) return -1;
            int i;
            if (!_byHash.TryGetValue(VfsPath.Hash(normalized), out i)) return -1;

            // 雜湊碰撞的最後一道防線:比對真正的路徑。打包器會在打包時擋掉碰撞並直接失敗,
            // 但讀取端不該把「索引是好的」當成前提 —— 這一次字串比對換來的是不會餵出錯的檔案。
            return string.Equals(_paths[i], normalized, StringComparison.OrdinalIgnoreCase) ? i : -1;
        }

        private void Decrypt(byte[] buf, PakFormat.Entry e)
        {
            if (_dataKey == null || e.CryptRange == PakFormat.CryptNone) return;

            int count = e.CryptRange == PakFormat.CryptHeaderOnly
                ? Math.Min(PakFormat.HeaderCryptBytes, buf.Length)
                : buf.Length;

            // streamPos = 這一段在資料區裡的絕對位移 —— 就是「同金鑰絕不重用 counter」的保證。
            PakCrypto.XorKeystream(_dataKey, buf, 0, count, e.DataOffset);
        }

        /// <summary>從 <paramref name="at"/> 讀滿 <paramref name="count"/> bytes;回實際讀到的數量。</summary>
        private static int ReadAt(FileStream fs, long at, byte[] buf, int offset, int count)
        {
            if (count == 0) return 0;
            fs.Seek(at, SeekOrigin.Begin);
            int got = 0;
            while (got < count)
            {
                int n = fs.Read(buf, offset + got, count - got);
                if (n <= 0) break;
                got += n;
            }
            return got;
        }

        /// <summary>raw deflate 解壓成剛好 <paramref name="expected"/> bytes;對不上 → null。</summary>
        private static byte[] Inflate(byte[] src, int offset, int count, int expected)
        {
            if (expected < 0) return null;
            try
            {
                var outBuf = new byte[expected];
                using (var ms = new MemoryStream(src, offset, count, false))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                {
                    int got = 0;
                    while (got < expected)
                    {
                        int n = ds.Read(outBuf, got, expected - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got != expected) return null;
                    // 多出來的位元組代表 rawSize 寫錯了 —— 當成壞檔。
                    if (ds.ReadByte() != -1) return null;
                }
                return outBuf;
            }
            catch { return null; }
        }

        public void Dispose()
        {
            try { _stream.Dispose(); } catch { }
        }
    }
}
