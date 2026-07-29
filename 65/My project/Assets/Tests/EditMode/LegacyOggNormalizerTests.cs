using System;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    public class LegacyOggNormalizerTests
    {
        [Test]
        public void Coalesces_Split_Vorbis_Setup_Header_Without_Changing_Audio_Packet()
        {
            byte[] identification = HeaderPacket(1, 30);
            byte[] comment = HeaderPacket(3, 94);
            byte[] setup = HeaderPacket(5, 315);
            byte[] audio = { 0x12, 0x34, 0x56, 0x78 };
            const uint serial = 0xD903;

            byte[] source = Join(
                Page(2, 0, serial, 0, new byte[] { 30 }, identification),
                Page(0, 0, serial, 1, new byte[] { 94, 255 },
                    Join(comment, Slice(setup, 0, 255))),
                Page(1, 0, serial, 2, new byte[] { 60 }, Slice(setup, 255, 60)),
                Page(4, 5120, serial, 3, new byte[] { 4 }, audio));

            Assert.IsTrue(LegacyOggNormalizer.TryNormalize(source, out var normalized));
            Assert.AreEqual(source.Length - 27, normalized.Length, "one redundant Ogg page header is removed");
            Assert.AreEqual(3, CountPages(normalized));
            AssertPagesHaveValidCrc(normalized);
            CollectionAssert.AreEqual(audio, Slice(normalized, normalized.Length - audio.Length, audio.Length));

            int lastPage = FindPage(normalized, 2);
            Assert.Greater(lastPage, 0);
            Assert.AreEqual(2u, ReadUInt32(normalized, lastPage + 18), "audio page sequence is renumbered");
        }

        [Test]
        public void Rejects_Source_With_Bad_Page_Crc()
        {
            byte[] source = ValidSplitHeaderSource();
            source[source.Length - 1] ^= 0x40;

            Assert.IsFalse(LegacyOggNormalizer.TryNormalize(source, out _));
        }

        [Test]
        public void Rejects_Source_With_Gapped_Page_Sequence()
        {
            Assert.IsFalse(LegacyOggNormalizer.TryNormalize(
                ValidSplitHeaderSource(thirdSequence: 9), out _));
        }

        [Test]
        public void Rejects_Source_With_Missing_Continuation_Flag()
        {
            Assert.IsFalse(LegacyOggNormalizer.TryNormalize(
                ValidSplitHeaderSource(thirdPageType: 0), out _));
        }

        [Test]
        public void Rejects_Source_With_Eos_Flag_On_Header_Page()
        {
            Assert.IsFalse(LegacyOggNormalizer.TryNormalize(
                ValidSplitHeaderSource(thirdPageType: 5), out _));
        }

        private static byte[] ValidSplitHeaderSource(uint thirdSequence = 2, byte thirdPageType = 1)
        {
            byte[] identification = HeaderPacket(1, 30);
            byte[] comment = HeaderPacket(3, 94);
            byte[] setup = HeaderPacket(5, 315);
            byte[] audio = { 0x12, 0x34, 0x56, 0x78 };
            const uint serial = 0xD903;
            return Join(
                Page(2, 0, serial, 0, new byte[] { 30 }, identification),
                Page(0, 0, serial, 1, new byte[] { 94, 255 }, Join(comment, Slice(setup, 0, 255))),
                Page(thirdPageType, 0, serial, thirdSequence, new byte[] { 60 }, Slice(setup, 255, 60)),
                Page(4, 5120, serial, 3, new byte[] { 4 }, audio));
        }

        private static byte[] HeaderPacket(byte kind, int length)
        {
            var result = new byte[length];
            result[0] = kind;
            result[1] = (byte)'v'; result[2] = (byte)'o'; result[3] = (byte)'r';
            result[4] = (byte)'b'; result[5] = (byte)'i'; result[6] = (byte)'s';
            for (int i = 7; i < result.Length; i++) result[i] = (byte)(i * 31);
            return result;
        }

        private static byte[] Page(byte type, ulong granule, uint serial, uint sequence,
            byte[] lacing, byte[] payload)
        {
            var result = new byte[27 + lacing.Length + payload.Length];
            result[0] = (byte)'O'; result[1] = (byte)'g'; result[2] = (byte)'g'; result[3] = (byte)'S';
            result[5] = type;
            WriteUInt64(result, 6, granule);
            WriteUInt32(result, 14, serial);
            WriteUInt32(result, 18, sequence);
            result[26] = (byte)lacing.Length;
            Buffer.BlockCopy(lacing, 0, result, 27, lacing.Length);
            Buffer.BlockCopy(payload, 0, result, 27 + lacing.Length, payload.Length);
            WriteUInt32(result, 22, OggCrc(result, 0, result.Length));
            return result;
        }

        private static byte[] Join(params byte[][] arrays)
        {
            int length = 0;
            foreach (var array in arrays) length += array.Length;
            var result = new byte[length];
            int offset = 0;
            foreach (var array in arrays)
            {
                Buffer.BlockCopy(array, 0, result, offset, array.Length);
                offset += array.Length;
            }
            return result;
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        private static void AssertPagesHaveValidCrc(byte[] source)
        {
            int offset = 0;
            while (offset < source.Length)
            {
                int segments = source[offset + 26];
                int length = 27 + segments;
                for (int i = 0; i < segments; i++) length += source[offset + 27 + i];
                Assert.LessOrEqual(offset + length, source.Length);
                Assert.AreEqual(ReadUInt32(source, offset + 22), OggCrc(source, offset, length));
                offset += length;
            }
            Assert.AreEqual(source.Length, offset);
        }

        private static uint OggCrc(byte[] data, int offset, int length)
        {
            uint crc = 0;
            for (int i = 0; i < length; i++)
            {
                byte value = i >= 22 && i < 26 ? (byte)0 : data[offset + i];
                crc ^= (uint)value << 24;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04c11db7u : crc << 1;
            }
            return crc;
        }

        private static int CountPages(byte[] source)
        {
            int count = 0;
            while (FindPage(source, count) >= 0) count++;
            return count;
        }

        private static int FindPage(byte[] source, int occurrence)
        {
            for (int i = 0; i + 3 < source.Length; i++)
            {
                if (source[i] != 'O' || source[i + 1] != 'g' ||
                    source[i + 2] != 'g' || source[i + 3] != 'S') continue;
                if (occurrence-- == 0) return i;
            }
            return -1;
        }

        private static uint ReadUInt32(byte[] data, int offset) =>
            (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);

        private static void WriteUInt32(byte[] data, int offset, uint value)
        {
            for (int i = 0; i < 4; i++) data[offset + i] = (byte)(value >> (i * 8));
        }

        private static void WriteUInt64(byte[] data, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++) data[offset + i] = (byte)(value >> (i * 8));
        }
    }
}
