using System.Collections.Generic;
using System.Text;

namespace Sdo.Shop
{
    /// <summary>
    /// Decoder for the official client outfit-set file <c>setinfo.dat</c> — the component list of every 套装 (OUTFIT/
    /// SET). Sits beside <c>iteminfo.dat</c> in the online 閉撰敃氪 pack (absent from the offline extract). A shop set
    /// row (iteminfo category 200 / 201 / 203) links here by <see cref="ShopItem.ModelId"/> == <see cref="OutfitSet.SetId"/>.
    ///
    /// FORMAT (little-endian), validated against the real CN file (192,561 bytes, 529 records):
    ///   int32 count            — RAW, NOT encrypted (= 529).
    ///   count × Record (364 = 0x16C bytes each) — EACH record byte-wise encrypted.
    ///   1 trailing checksum byte — ignored.
    ///   (4 + 529*364 + 1 = 192,561 ✓)
    /// Record (decrypt first): int32 setId @0x00, then 6 × Component (60 = 0x3C bytes):
    ///   int32 modelId @0x00 (0x7FFFFFFF = EMPTY slot), int16 flag @0x04 (-1 for garment sets; qty for gift bundles),
    ///   int16 pad @0x06, char[52] name (GBK) @0x08.
    /// Cipher <c>dec = (0xF9 - b) &amp; 0xFF</c> — the same self-inverse family as iteminfo's (0x1F9-b) (both = 249-b mod 256).
    /// </summary>
    public static class SetinfoReader
    {
        public const int RecordLen = 364;    // 0x16C
        public const int CompLen = 60;       // 0x3C
        public const int CompCount = 6;
        public const int EmptyModelId = 0x7FFFFFFF;

        private const int OffSetId = 0x00, OffComps = 0x04;
        private const int CompOffModelId = 0x00, CompOffFlag = 0x04, CompOffName = 0x08, CompNameMax = 52;

        /// <summary>Self-inverse byte cipher (same function encrypts and decrypts).</summary>
        public static byte Crypt(byte b) => (byte)((0xF9 - b) & 0xFF);

        /// <summary>Parse a whole <c>setinfo.dat</c> image into a setId → <see cref="OutfitSet"/> map.
        /// <paramref name="nameEncoding"/> decodes the GBK component names (usually empty); null → Latin1.</summary>
        public static Dictionary<int, OutfitSet> Parse(byte[] data, Encoding nameEncoding = null)
        {
            var sets = new Dictionary<int, OutfitSet>();
            if (data == null || data.Length < 4 + RecordLen) return sets;

            int count = ReadI32(data, 0);   // raw, not encrypted
            if (count <= 0 || count > 1_000_000) return sets;

            var rec = new byte[RecordLen];
            for (int k = 0; k < count; k++)
            {
                int pos = 4 + k * RecordLen;
                if (pos + RecordLen > data.Length) break;
                for (int i = 0; i < RecordLen; i++) rec[i] = Crypt(data[pos + i]);

                var set = new OutfitSet { SetId = ReadI32(rec, OffSetId) };
                for (int c = 0; c < CompCount; c++)
                {
                    int b = OffComps + c * CompLen;
                    int modelId = ReadI32(rec, b + CompOffModelId);
                    if (modelId == EmptyModelId || modelId == 0) continue;   // empty slot
                    set.Components.Add(new OutfitComponent
                    {
                        ModelId = modelId,
                        Flag = ReadI16(rec, b + CompOffFlag),
                        Name = ReadName(rec, b + CompOffName, CompNameMax, nameEncoding),
                    });
                }
                if (set.Components.Count > 0) sets[set.SetId] = set;
            }
            return sets;
        }

        private static int ReadI32(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
        private static short ReadI16(byte[] b, int o) => (short)(b[o] | (b[o + 1] << 8));

        private static string ReadName(byte[] r, int off, int max, Encoding enc)
        {
            int limit = System.Math.Min(off + max, r.Length);
            int end = off;
            while (end < limit && r[end] != 0) end++;
            int len = end - off;
            if (len <= 0) return string.Empty;
            if (enc != null) return enc.GetString(r, off, len);
            var ch = new char[len];
            for (int i = 0; i < len; i++) ch[i] = (char)r[off + i];
            return new string(ch);
        }
    }
}
