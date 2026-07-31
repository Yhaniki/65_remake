using System;
using System.Collections.Generic;
using System.IO;

namespace Sdo.Game
{
    /// <summary>
    /// Re-pages legacy Ogg Vorbis headers without decoding or changing any Vorbis packet. Some old keysound packs
    /// split the setup header across two Ogg pages; Unity/FMOD can reject otherwise-valid files with
    /// "Couldn't perform seek operation". Coalescing the three Vorbis headers fixes that native decoder edge case.
    /// </summary>
    public static class LegacyOggNormalizer
    {
        private static readonly byte[] Capture = { (byte)'O', (byte)'g', (byte)'g', (byte)'S' };
        private static readonly byte[] Vorbis = { (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s' };
        private static readonly uint[] CrcLookup = BuildCrcLookup();

        public static bool TryNormalize(byte[] source, out byte[] normalized)
        {
            normalized = null;
            if (source == null || source.Length < 64) return false;

            var packets = new List<byte[]>(3);
            var packet = new List<byte>(4096);
            int offset = 0;
            int audioOffset = -1;
            uint serial = 0;
            uint expectedSequence = 0;
            bool firstPage = true;
            bool expectsContinuation = false;

            while (offset < source.Length && packets.Count < 3)
            {
                if (!TryReadPage(source, offset, out var page)) return false;
                bool continued = (page.HeaderType & 1) != 0;
                if ((page.HeaderType & ~7) != 0 ||
                    (page.HeaderType & 4) != 0 ||
                    page.Sequence != expectedSequence++ ||
                    continued != expectsContinuation ||
                    page.Segments.Length == 0)
                    return false;
                if (firstPage)
                {
                    if ((page.HeaderType & 2) == 0) return false;
                    serial = page.Serial;
                    firstPage = false;
                }
                else if (page.Serial != serial || (page.HeaderType & 2) != 0)
                {
                    return false; // chained/multiplexed streams are outside this narrowly-scoped normalizer.
                }

                int payload = page.PayloadOffset;
                for (int i = 0; i < page.Segments.Length; i++)
                {
                    int count = page.Segments[i];
                    for (int j = 0; j < count; j++) packet.Add(source[payload + j]);
                    payload += count;
                    if (count >= 255) continue;

                    packets.Add(packet.ToArray());
                    packet.Clear();
                    if (packets.Count != 3) continue;

                    // Do not rewrite a page which also contains audio packets; the legacy files this fixes end their
                    // setup header at a page boundary, allowing all audio pages to remain byte-for-byte packet-identical.
                    if (i != page.Segments.Length - 1) return false;
                    audioOffset = offset + page.Length;
                    break;
                }
                expectsContinuation = PageContinuesPacket(page);
                offset += page.Length;
            }

            if (packets.Count != 3 || audioOffset < 0 || audioOffset >= source.Length ||
                !IsVorbisHeader(packets[0], 1) ||
                !IsVorbisHeader(packets[1], 3) ||
                !IsVorbisHeader(packets[2], 5))
                return false;

            var firstLacing = PacketLacing(packets[0].Length);
            var headerLacing = PacketLacing(packets[1].Length);
            headerLacing.AddRange(PacketLacing(packets[2].Length));
            if (firstLacing.Count > 255 || headerLacing.Count > 255) return false;

            using (var output = new MemoryStream(source.Length))
            {
                WritePage(output, headerType: 2, granule: 0, serial: serial, sequence: 0,
                    firstLacing, packets[0]);

                var combinedHeaders = new byte[packets[1].Length + packets[2].Length];
                Buffer.BlockCopy(packets[1], 0, combinedHeaders, 0, packets[1].Length);
                Buffer.BlockCopy(packets[2], 0, combinedHeaders, packets[1].Length, packets[2].Length);
                WritePage(output, headerType: 0, granule: 0, serial: serial, sequence: 1,
                    headerLacing, combinedHeaders);

                uint sequence = 2;
                offset = audioOffset;
                expectsContinuation = false;
                while (offset < source.Length)
                {
                    if (!TryReadPage(source, offset, out var page) ||
                        page.Serial != serial ||
                        page.Sequence != expectedSequence++ ||
                        (page.HeaderType & ~7) != 0 ||
                        (page.HeaderType & 2) != 0 ||
                        ((page.HeaderType & 1) != 0) != expectsContinuation)
                        return false;
                    int nextOffset = offset + page.Length;
                    expectsContinuation = PageContinuesPacket(page);
                    if ((page.HeaderType & 4) != 0 &&
                        (expectsContinuation || nextOffset != source.Length))
                        return false;
                    var copy = new byte[page.Length];
                    Buffer.BlockCopy(source, offset, copy, 0, page.Length);
                    WriteUInt32(copy, 18, sequence++);
                    WriteUInt32(copy, 22, 0);
                    WriteUInt32(copy, 22, OggCrc(copy));
                    output.Write(copy, 0, copy.Length);
                    offset = nextOffset;
                }
                if (expectsContinuation) return false;

                normalized = output.ToArray();
                return true;
            }
        }

        private static bool IsVorbisHeader(byte[] packet, byte kind)
        {
            if (packet == null || packet.Length < 7 || packet[0] != kind) return false;
            for (int i = 0; i < Vorbis.Length; i++)
                if (packet[i + 1] != Vorbis[i]) return false;
            return true;
        }

        private static List<byte> PacketLacing(int length)
        {
            var result = new List<byte>(Math.Max(1, length / 255 + 1));
            while (length >= 255)
            {
                result.Add(255);
                length -= 255;
            }
            result.Add((byte)length); // includes the required zero terminator for an exact multiple of 255.
            return result;
        }

        private static void WritePage(Stream output, byte headerType, ulong granule, uint serial, uint sequence,
            List<byte> lacing, byte[] payload)
        {
            int headerLength = 27 + lacing.Count;
            var page = new byte[headerLength + payload.Length];
            Buffer.BlockCopy(Capture, 0, page, 0, Capture.Length);
            page[4] = 0;
            page[5] = headerType;
            WriteUInt64(page, 6, granule);
            WriteUInt32(page, 14, serial);
            WriteUInt32(page, 18, sequence);
            page[26] = (byte)lacing.Count;
            for (int i = 0; i < lacing.Count; i++) page[27 + i] = lacing[i];
            Buffer.BlockCopy(payload, 0, page, headerLength, payload.Length);
            WriteUInt32(page, 22, OggCrc(page));
            output.Write(page, 0, page.Length);
        }

        private static bool PageContinuesPacket(Page page) =>
            page.Segments.Length > 0 &&
            page.Segments[page.Segments.Length - 1] == 255;

        private static bool TryReadPage(byte[] source, int offset, out Page page)
        {
            page = default;
            if (offset < 0 || offset + 27 > source.Length) return false;
            for (int i = 0; i < Capture.Length; i++)
                if (source[offset + i] != Capture[i]) return false;
            if (source[offset + 4] != 0) return false;

            int segmentCount = source[offset + 26];
            int payloadOffset = offset + 27 + segmentCount;
            if (payloadOffset > source.Length) return false;
            var segments = new byte[segmentCount];
            int payloadLength = 0;
            for (int i = 0; i < segmentCount; i++)
            {
                segments[i] = source[offset + 27 + i];
                payloadLength += segments[i];
            }
            int length = 27 + segmentCount + payloadLength;
            if (offset + length > source.Length) return false;
            uint storedCrc = ReadUInt32(source, offset + 22);
            if (OggCrc(source, offset, length) != storedCrc) return false;

            page = new Page
            {
                HeaderType = source[offset + 5],
                Serial = ReadUInt32(source, offset + 14),
                Sequence = ReadUInt32(source, offset + 18),
                PayloadOffset = payloadOffset,
                Segments = segments,
                Length = length
            };
            return true;
        }

        private static uint OggCrc(byte[] data) => OggCrc(data, 0, data.Length);

        private static uint OggCrc(byte[] data, int offset, int length)
        {
            uint crc = 0;
            for (int i = 0; i < length; i++)
            {
                byte value = i >= 22 && i < 26 ? (byte)0 : data[offset + i];
                int tableIndex = (int)(((crc >> 24) ^ value) & 0xff);
                crc = (crc << 8) ^ CrcLookup[tableIndex];
            }
            return crc;
        }

        private static uint[] BuildCrcLookup()
        {
            var lookup = new uint[256];
            for (int i = 0; i < lookup.Length; i++)
            {
                uint value = (uint)i << 24;
                for (int bit = 0; bit < 8; bit++)
                    value = (value & 0x80000000u) != 0 ? (value << 1) ^ 0x04c11db7u : value << 1;
                lookup[i] = value;
            }
            return lookup;
        }

        private static uint ReadUInt32(byte[] data, int offset) =>
            (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);

        private static void WriteUInt32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteUInt64(byte[] data, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++) data[offset + i] = (byte)(value >> (i * 8));
        }

        private struct Page
        {
            public byte HeaderType;
            public uint Serial;
            public uint Sequence;
            public int PayloadOffset;
            public byte[] Segments;
            public int Length;
        }
    }
}
