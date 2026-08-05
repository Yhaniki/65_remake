using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Sdo.Settings.Vfs
{
    /// <summary>SDOPAK v1 的格式常數與二進位版面 —— 這裡是 C# 讀取器與 Python 打包器
    /// (<c>tools/build_pak.py</c>)的共同契約。改這個檔就要同步改那支,而且要昇版號。
    /// 完整規格見 <c>docs/architecture/data-packaging.md</c> §3。</summary>
    public static class PakFormat
    {
        /// <summary>檔頭前 8 bytes:<c>"SDOPAK\0"</c> + 版本 byte。</summary>
        public static readonly byte[] Magic = { (byte)'S', (byte)'D', (byte)'O', (byte)'P', (byte)'A', (byte)'K', 0, 1 };

        public const int FormatVersion = 1;
        public const int HeaderSize = 64;
        public const int EntrySize = 40;

        /// <summary>條目旗標:壓縮方式。</summary>
        public const ushort CompressionStore = 0;
        public const ushort CompressionDeflate = 1;

        /// <summary>條目旗標:加密範圍。</summary>
        public const ushort CryptNone = 0;
        public const ushort CryptWhole = 1;
        /// <summary>只加密前 <see cref="HeaderCryptBytes"/> bytes —— 音訊用。</summary>
        public const ushort CryptHeaderOnly = 2;
        public const int HeaderCryptBytes = 4096;

        /// <summary>檔頭 flags 的位元。</summary>
        public const uint FlagIndexEncrypted = 1u << 0;
        public const uint FlagDataEncrypted = 1u << 1;

        /// <summary><c>rawSize</c> 的哨兵值:這是一筆「刪除標記」,不是真的檔案。
        /// 命中它就停止往下層找、回報檔案不存在(見 data-packaging.md §4.3)。</summary>
        public const uint WhiteoutRawSize = 0xFFFFFFFFu;

        /// <summary>一筆索引條目(解開後的形式)。</summary>
        public struct Entry
        {
            public ulong PathHash;
            public uint PathOffset;
            public uint RawSize;
            public long DataOffset;     // 相對資料區起點
            public uint StoredSize;
            public ushort Compression;
            public ushort CryptRange;
            public uint Crc32;

            public bool IsWhiteout { get { return RawSize == WhiteoutRawSize; } }
        }

        /// <summary>檔頭(解開後的形式)。</summary>
        public struct Header
        {
            public uint FormatVersion;
            public uint Flags;
            public uint EntryCount;
            public uint PakId;
            public long IndexOffset;
            public uint IndexStored;
            public uint IndexRaw;
            public long DataOffset;
            public byte[] IndexMac;     // 16 bytes
        }

        // ---------------- 讀 ----------------

        /// <summary>解析 64-byte 檔頭。magic / 版本不符 → false(不丟例外:掛載時遇到不是 pak 的檔要能安靜跳過)。</summary>
        public static bool TryReadHeader(byte[] buf, out Header header)
        {
            header = default(Header);
            if (buf == null || buf.Length < HeaderSize) return false;
            for (int i = 0; i < Magic.Length; i++)
                if (buf[i] != Magic[i]) return false;

            header.FormatVersion = ReadU32(buf, 0x08);
            if (header.FormatVersion != FormatVersion) return false;

            header.Flags = ReadU32(buf, 0x0C);
            header.EntryCount = ReadU32(buf, 0x10);
            header.PakId = ReadU32(buf, 0x14);
            header.IndexOffset = (long)ReadU64(buf, 0x18);
            header.IndexStored = ReadU32(buf, 0x20);
            header.IndexRaw = ReadU32(buf, 0x24);
            header.DataOffset = (long)ReadU64(buf, 0x28);

            header.IndexMac = new byte[16];
            Buffer.BlockCopy(buf, 0x30, header.IndexMac, 0, 16);

            // 版面自洽性:壞掉的檔不該讓後面的讀取去算出天文數字的配置大小。
            if (header.IndexOffset < HeaderSize || header.DataOffset < HeaderSize) return false;
            if (header.EntryCount > 8_000_000) return false;
            return true;
        }

        /// <summary>寫 64-byte 檔頭。</summary>
        public static byte[] WriteHeader(Header h)
        {
            var buf = new byte[HeaderSize];
            Buffer.BlockCopy(Magic, 0, buf, 0, Magic.Length);
            WriteU32(buf, 0x08, h.FormatVersion == 0 ? FormatVersion : h.FormatVersion);
            WriteU32(buf, 0x0C, h.Flags);
            WriteU32(buf, 0x10, h.EntryCount);
            WriteU32(buf, 0x14, h.PakId);
            WriteU64(buf, 0x18, (ulong)h.IndexOffset);
            WriteU32(buf, 0x20, h.IndexStored);
            WriteU32(buf, 0x24, h.IndexRaw);
            WriteU64(buf, 0x28, (ulong)h.DataOffset);
            if (h.IndexMac != null) Buffer.BlockCopy(h.IndexMac, 0, buf, 0x30, Math.Min(16, h.IndexMac.Length));
            return buf;
        }

        /// <summary>解析索引區(已解密、已解壓的位元組)。
        /// 版面:<c>u32 pathBlobSize</c> + pathBlob + <c>Entry[entryCount]</c>。
        /// 壞掉 → false(掛載端要能安靜跳過壞卷,而不是讓整個遊戲開不起來)。</summary>
        public static bool TryReadIndex(byte[] buf, uint entryCount, out Entry[] entries, out string[] paths)
        {
            entries = null; paths = null;
            if (buf == null || buf.Length < 4) return false;

            uint blobSize = ReadU32(buf, 0);
            long need = 4L + blobSize + (long)entryCount * EntrySize;
            if (blobSize > int.MaxValue - 4 || need > buf.Length) return false;

            int entryBase = 4 + (int)blobSize;
            var es = new Entry[entryCount];
            var ps = new string[entryCount];

            for (uint i = 0; i < entryCount; i++)
            {
                int o = entryBase + (int)i * EntrySize;
                var e = new Entry
                {
                    PathHash = ReadU64(buf, o + 0x00),
                    PathOffset = ReadU32(buf, o + 0x08),
                    RawSize = ReadU32(buf, o + 0x0C),
                    DataOffset = (long)ReadU64(buf, o + 0x10),
                    StoredSize = ReadU32(buf, o + 0x18),
                    Compression = ReadU16(buf, o + 0x1C),
                    CryptRange = ReadU16(buf, o + 0x1E),
                    Crc32 = ReadU32(buf, o + 0x20),
                };
                if (e.PathOffset >= blobSize) return false;

                es[i] = e;
                ps[i] = ReadCString(buf, 4 + (int)e.PathOffset, 4 + (int)blobSize);
                if (ps[i] == null) return false;
            }

            entries = es; paths = ps;
            return true;
        }

        /// <summary>把條目 + 路徑組成索引區的位元組(尚未壓縮/加密)。打包器用;
        /// C# 這端保留是為了讓 round-trip 測試不必依賴 Python。</summary>
        public static byte[] WriteIndex(IList<Entry> entries, IList<string> paths)
        {
            if (entries == null || paths == null || entries.Count != paths.Count)
                throw new ArgumentException("entries/paths 長度必須一致");

            var blob = new MemoryStream();
            var offsets = new uint[paths.Count];
            for (int i = 0; i < paths.Count; i++)
            {
                offsets[i] = (uint)blob.Length;
                var b = Encoding.UTF8.GetBytes(paths[i] ?? "");
                blob.Write(b, 0, b.Length);
                blob.WriteByte(0);
            }
            var blobBytes = blob.ToArray();

            var buf = new byte[4 + blobBytes.Length + entries.Count * EntrySize];
            WriteU32(buf, 0, (uint)blobBytes.Length);
            Buffer.BlockCopy(blobBytes, 0, buf, 4, blobBytes.Length);

            int entryBase = 4 + blobBytes.Length;
            for (int i = 0; i < entries.Count; i++)
            {
                int o = entryBase + i * EntrySize;
                var e = entries[i];
                WriteU64(buf, o + 0x00, e.PathHash);
                WriteU32(buf, o + 0x08, offsets[i]);
                WriteU32(buf, o + 0x0C, e.RawSize);
                WriteU64(buf, o + 0x10, (ulong)e.DataOffset);
                WriteU32(buf, o + 0x18, e.StoredSize);
                WriteU16(buf, o + 0x1C, e.Compression);
                WriteU16(buf, o + 0x1E, e.CryptRange);
                WriteU32(buf, o + 0x20, e.Crc32);
                // o + 0x24 保留，留 0
            }
            return buf;
        }

        // ---------------- CRC32 ----------------

        private static uint[] _crcTable;

        /// <summary>標準 CRC-32(IEEE 802.3,多項式 0xEDB88320)—— Python 的 <c>zlib.crc32</c> 同一顆。
        /// 用途是**偵測損毀**,不是防竄改(金鑰在執行檔裡,重簽很容易)。</summary>
        public static uint Crc32(byte[] data, int offset = 0, int count = -1)
        {
            if (data == null) return 0;
            if (count < 0) count = data.Length - offset;

            var table = _crcTable;
            if (table == null)
            {
                table = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[i] = c;
                }
                _crcTable = table;
            }

            uint crc = 0xFFFFFFFFu;
            int end = offset + count;
            for (int i = offset; i < end; i++) crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        // ---------------- 小端序讀寫 ----------------

        public static ushort ReadU16(byte[] b, int o) { return (ushort)(b[o] | (b[o + 1] << 8)); }

        public static uint ReadU32(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        public static ulong ReadU64(byte[] b, int o)
        {
            return ReadU32(b, o) | ((ulong)ReadU32(b, o + 4) << 32);
        }

        public static void WriteU16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }

        public static void WriteU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        public static void WriteU64(byte[] b, int o, ulong v)
        {
            WriteU32(b, o, (uint)v); WriteU32(b, o + 4, (uint)(v >> 32));
        }

        /// <summary>從 <paramref name="start"/> 讀到 NUL 為止的 UTF-8 字串;沒有 NUL → null。</summary>
        private static string ReadCString(byte[] b, int start, int limit)
        {
            if (start < 0 || start >= limit) return null;
            int end = start;
            while (end < limit && b[end] != 0) end++;
            if (end >= limit) return null;   // 沒有終止符 = 索引壞了
            return Encoding.UTF8.GetString(b, start, end - start);
        }
    }
}
