using System;
using System.Collections.Generic;
using System.Text;

namespace Sdo.Shop
{
    /// <summary>
    /// Decoder for the official client item catalog file <c>iteminfo.dat</c> — the single source of every avatar
    /// item's id + name + price + classification. The shop window's name/price list is 100% local to this file; the
    /// server never sends it.
    ///
    /// FORMAT (little-endian), validated against the real CN client file (4,923,841 bytes, 31,563 items):
    ///   Header (12 bytes, NOT encrypted):
    ///     int32 headA        — must be 2 (version). Verified.
    ///     int32 headB        — region/version tag. The real file is 43515, NOT the 7008 the arrowgene reader hard-
    ///                          checks; so we do NOT validate it.
    ///     int32 headCount    — item count (= (fileSize-12)/156).
    ///   Then headCount records × <see cref="RecordLen"/> = 156 bytes each (+ possible 1 trailing pad byte).
    ///
    /// IMPORTANT: each record is 156 (0x9C) bytes, NOT 152 — the community arrowgene reader's ITEM_LENGTH=152 is a bug
    /// that desyncs after the first record (the CN file's stride is 156; (size-12)/156 divides evenly, /152 does not).
    /// Every record is byte-wise encrypted with the self-inverse transform <c>dec = (0x1F9 - b) &amp; 0xFF</c>; decrypt,
    /// then read the fixed field offsets. Names are GBK/CP936 — pass the matching <see cref="Encoding"/>; the default
    /// preserves the raw bytes as Latin1 so this stays free of any codepage dependency (a build/import step converts
    /// GBK → UTF-8 where a codepage is available).
    /// </summary>
    public static class IteminfoReader
    {
        public const int HeaderLen = 12;
        public const int RecordLen = 156;   // 0x9C — the real stride (arrowgene's 152 is wrong)
        public const int ExpectedHeadA = 2;

        // field offsets within a decrypted record
        private const int OffId = 0x00, OffModelId = 0x04, OffCategory = 0x08, OffPriceCat = 0x0C, OffPrice = 0x10,
                          OffName = 0x14, OffMinLevel = 0x78, OffDuration = 0x7C, OffQuantity = 0x80,
                          OffWeddingRing = 0x88, OffSex = 0x8B;
        private const int NameMax = 44;   // name field is 0x14..0x40

        /// <summary>Apply the self-inverse byte cipher (same function encrypts and decrypts).</summary>
        public static byte Crypt(byte b) => (byte)((0x1F9 - b) & 0xFF);

        /// <summary>Parse a whole <c>iteminfo.dat</c> image into shop items. <paramref name="nameEncoding"/> decodes
        /// the GBK names (pass <c>Encoding.GetEncoding(936)</c> where available); null → byte-preserving Latin1.
        /// When <paramref name="strict"/>, a bad headA throws; otherwise it returns an empty list.</summary>
        public static List<ShopItem> Parse(byte[] data, Encoding nameEncoding = null, bool strict = false)
        {
            var items = new List<ShopItem>();
            if (data == null || data.Length < HeaderLen) return items;

            int headA = ReadI32(data, 0);
            if (headA != ExpectedHeadA)
            {
                if (strict) throw new InvalidOperationException($"iteminfo: unexpected headA {headA} (expected {ExpectedHeadA})");
                return items;
            }
            // headB (data[4..8]) intentionally NOT validated — see class doc.

            int pos = HeaderLen;
            var rec = new byte[RecordLen];
            while (pos + RecordLen <= data.Length)
            {
                for (int i = 0; i < RecordLen; i++) rec[i] = Crypt(data[pos + i]);
                items.Add(ReadItem(rec, nameEncoding));
                pos += RecordLen;
            }
            return items;
        }

        /// <summary>Header item count field (does not require decrypting the body). Returns 0 for a too-short image.</summary>
        public static int ReadHeaderCount(byte[] data) => (data != null && data.Length >= HeaderLen) ? ReadI32(data, 8) : 0;

        private static ShopItem ReadItem(byte[] r, Encoding enc)
        {
            return new ShopItem
            {
                Id = ReadI32(r, OffId),
                ModelId = ReadI32(r, OffModelId),
                Category = ReadI32(r, OffCategory),
                PriceCategoryRaw = r[OffPriceCat],
                Price = ReadI32(r, OffPrice),
                Name = ReadName(r, OffName, NameMax, enc),
                MinLevel = ReadI32(r, OffMinLevel),
                DurationDays = ReadI32(r, OffDuration),
                Quantity = ReadI32(r, OffQuantity),
                WeddingRing = ReadI16(r, OffWeddingRing) > 0,
                SexRaw = r[OffSex],
            };
        }

        private static int ReadI32(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
        private static short ReadI16(byte[] b, int o) => (short)(b[o] | (b[o + 1] << 8));

        private static string ReadName(byte[] r, int off, int max, Encoding enc)
        {
            int limit = Math.Min(off + max, r.Length);
            int end = off;
            while (end < limit && r[end] != 0) end++;
            int len = end - off;
            if (len <= 0) return string.Empty;
            if (enc != null) return enc.GetString(r, off, len);
            var c = new char[len];               // default: byte-preserving Latin1 (no codepage dependency)
            for (int i = 0; i < len; i++) c[i] = (char)r[off + i];
            return new string(c);
        }
    }
}
