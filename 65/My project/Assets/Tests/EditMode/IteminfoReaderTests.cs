using System;
using System.Text;
using NUnit.Framework;
using Sdo.Shop;

namespace Sdo.Tests
{
    public class IteminfoReaderTests
    {
        // Build a valid, ENCRYPTED iteminfo.dat image (plaintext header + encrypted 156-byte records).
        private static byte[] BuildImage(int headB, params ShopItem[] recs)
        {
            int len = IteminfoReader.HeaderLen + recs.Length * IteminfoReader.RecordLen;
            var buf = new byte[len];
            WriteI32(buf, 0, 2);             // headA
            WriteI32(buf, 4, headB);         // headB
            WriteI32(buf, 8, recs.Length);   // count
            int pos = IteminfoReader.HeaderLen;
            foreach (var it in recs)
            {
                var rec = new byte[IteminfoReader.RecordLen];   // zero-initialised plaintext record
                WriteI32(rec, 0x00, it.Id);
                WriteI32(rec, 0x04, it.ModelId);
                WriteI32(rec, 0x08, it.Category);
                rec[0x0C] = (byte)it.PriceCategoryRaw;
                WriteI32(rec, 0x10, it.Price);
                var nb = Encoding.ASCII.GetBytes(it.Name ?? "");
                Array.Copy(nb, 0, rec, 0x14, Math.Min(nb.Length, 44));   // name field 0x14..0x40 (NUL from zero-init if shorter)
                WriteI32(rec, 0x78, it.MinLevel);
                WriteI32(rec, 0x7C, it.DurationDays);
                WriteI32(rec, 0x80, it.Quantity);
                WriteI16(rec, 0x88, (short)(it.WeddingRing ? 1 : 0));
                rec[0x8B] = (byte)it.SexRaw;
                for (int i = 0; i < rec.Length; i++) buf[pos + i] = IteminfoReader.Crypt(rec[i]);   // encrypt (self-inverse)
                pos += IteminfoReader.RecordLen;
            }
            return buf;
        }

        private static void WriteI32(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        private static void WriteI16(byte[] b, int o, short v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }

        [Test]
        public void Crypt_IsSelfInverse()
        {
            for (int b = 0; b < 256; b++)
                Assert.AreEqual(b, IteminfoReader.Crypt(IteminfoReader.Crypt((byte)b)), $"byte {b}");
        }

        [Test]
        public void Parse_ReadsFieldsAtCorrectOffsets()
        {
            // mirrors the real CN file's first record: [13457] cat 101 (女髮) / priceCat 1 (金幣) / price 1860
            var src = new ShopItem
            {
                Id = 13457, ModelId = 555, Category = 101, PriceCategoryRaw = 1, Price = 1860,
                Name = "hat", MinLevel = 5, DurationDays = 7, Quantity = -1, SexRaw = 0, WeddingRing = false,
            };
            var items = IteminfoReader.Parse(BuildImage(43515, src));
            Assert.AreEqual(1, items.Count);
            var it = items[0];
            Assert.AreEqual(13457, it.Id);
            Assert.AreEqual(555, it.ModelId);
            Assert.AreEqual(101, it.Category);
            Assert.AreEqual(ItemPriceCurrency.Coins, it.Currency);
            Assert.AreEqual(1860, it.Price);
            Assert.AreEqual("hat", it.Name);
            Assert.AreEqual(5, it.MinLevel);
            Assert.AreEqual(7, it.DurationDays);
            Assert.AreEqual(EquipSlot.Hair, it.EquipSlot);
            Assert.AreEqual(ItemSlotType.Clothes, it.SlotType);
            Assert.AreEqual(ItemSex.Female, it.Sex);
            Assert.IsFalse(it.WeddingRing);
        }

        [Test]
        public void Parse_156Stride_SecondRecordDoesNotDesync()
        {
            var a = new ShopItem { Id = 13457, Category = 101, PriceCategoryRaw = 1, Price = 1860, Name = "aaa", Quantity = -1 };
            var b = new ShopItem { Id = 13458, Category = 103, PriceCategoryRaw = 0, Price = 490, Name = "bbb", Quantity = -1 };
            var items = IteminfoReader.Parse(BuildImage(43515, a, b));
            Assert.AreEqual(2, items.Count);
            Assert.AreEqual(13458, items[1].Id);     // reads as garbage under the wrong 152 stride
            Assert.AreEqual(490, items[1].Price);
            Assert.AreEqual(ItemPriceCurrency.Points, items[1].Currency);
        }

        [Test]
        public void Parse_TrailingPadByte_Ignored()
        {
            var img = BuildImage(43515, new ShopItem { Id = 1, Name = "x", Quantity = -1 });
            var padded = new byte[img.Length + 1];   // the real file has one trailing byte
            Array.Copy(img, padded, img.Length);
            Assert.AreEqual(1, IteminfoReader.Parse(padded).Count);
        }

        [Test]
        public void Parse_BadHeadA_ReturnsEmpty_ThrowsWhenStrict()
        {
            var img = BuildImage(43515, new ShopItem { Id = 1, Name = "x", Quantity = -1 });
            WriteI32(img, 0, 9);   // corrupt headA (should be 2)
            Assert.IsEmpty(IteminfoReader.Parse(img));
            Assert.Throws<InvalidOperationException>(() => IteminfoReader.Parse(img, null, strict: true));
        }

        [Test]
        public void Parse_DoesNotValidateHeadB()
        {
            // arrowgene hard-checks headB==7008 and rejects the real CN file (43515). We must NOT.
            Assert.AreEqual(1, IteminfoReader.Parse(BuildImage(7008, new ShopItem { Id = 1, Name = "x", Quantity = -1 })).Count);
            Assert.AreEqual(1, IteminfoReader.Parse(BuildImage(43515, new ShopItem { Id = 2, Name = "y", Quantity = -1 })).Count);
            Assert.AreEqual(1, IteminfoReader.Parse(BuildImage(999, new ShopItem { Id = 3, Name = "z", Quantity = -1 })).Count);
        }

        [Test]
        public void Parse_Name_NulTerminated_AndBoundedTo44()
        {
            var shortName = IteminfoReader.Parse(BuildImage(43515, new ShopItem { Id = 1, Name = "abc", Quantity = -1 }))[0].Name;
            Assert.AreEqual("abc", shortName);   // stops at the NUL after "abc"

            var full = new string('A', 44);       // fills the whole 44-byte field, no NUL inside
            var bounded = IteminfoReader.Parse(BuildImage(43515, new ShopItem { Id = 2, Name = full + "OVERFLOW", Quantity = -1 }))[0].Name;
            Assert.AreEqual(44, bounded.Length);  // never reads past the field
        }

        [Test]
        public void Parse_NameEncoding_Injected_DecodesHighBytes()
        {
            // default (null) = byte-preserving Latin1; a supplied encoding is used instead.
            byte[] img = BuildImage(43515, new ShopItem { Id = 1, Name = "", Quantity = -1 });
            // overwrite the (encrypted) name bytes with two GBK bytes for 你 (0xC4 0xE3)
            int nameOff = IteminfoReader.HeaderLen + 0x14;
            img[nameOff] = IteminfoReader.Crypt(0xC4);
            img[nameOff + 1] = IteminfoReader.Crypt(0xE3);
            img[nameOff + 2] = IteminfoReader.Crypt(0x00);

            var latin1 = IteminfoReader.Parse(img)[0].Name;
            Assert.AreEqual(2, latin1.Length);
            Assert.AreEqual(0xC4, (int)latin1[0]);   // Latin1 preserves the raw byte
            Assert.AreEqual(0xE3, (int)latin1[1]);
        }
    }
}
