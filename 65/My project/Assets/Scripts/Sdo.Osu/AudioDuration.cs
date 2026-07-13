using System;
using System.IO;

namespace Sdo.Osu
{
    /// <summary>
    /// How long an external song's audio file actually plays — the 時間 column for user songs (the chart's last note
    /// is NOT the song's length: charts routinely stop before the outro).
    ///
    /// Read from the file's HEADERS, never by decoding:
    ///   .wav — RIFF: data-chunk size ÷ byte rate.
    ///   .ogg — Vorbis: sample rate from the identification header, then the granule position (= total samples) off
    ///          the LAST Ogg page, found by scanning back from the end of the file.
    ///   .mp3 — NLayer's MpegFile.Duration (it handles Xing/VBR; a header-only guess would be wrong for VBR).
    /// Anything unreadable/unknown returns 0, and the caller falls back to the chart length.
    /// </summary>
    public static class AudioDuration
    {
        /// <summary>Play time of an audio file in whole seconds; 0 if it can't be read.</summary>
        public static int Seconds(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            try
            {
                if (!File.Exists(path)) return 0;
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".mp3") return Mp3Seconds(path);
                using (var fs = File.OpenRead(path))
                {
                    double sec = ext == ".wav" ? WavSeconds(fs) : ext == ".ogg" ? OggSeconds(fs) : 0.0;
                    return sec > 0.0 ? (int)Math.Round(sec) : 0;
                }
            }
            catch { return 0; }
        }

        private static int Mp3Seconds(string path)
        {
            try
            {
                using (var mp3 = new NLayer.MpegFile(path))
                {
                    double sec = mp3.Duration.TotalSeconds;
                    return sec > 0.0 ? (int)Math.Round(sec) : 0;
                }
            }
            catch { return 0; }
        }

        /// <summary>RIFF/WAVE: walk the chunk list for "fmt " (byte rate) and "data" (payload size). 0 if malformed.
        /// Stream-based so it is unit-testable without touching disk.</summary>
        public static double WavSeconds(Stream s)
        {
            if (s == null || !s.CanRead || !s.CanSeek) return 0.0;
            using (var r = new BinaryReader(s, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                if (s.Length < 12) return 0.0;
                if (Tag(r) != "RIFF") return 0.0;
                r.ReadUInt32();                       // riff size
                if (Tag(r) != "WAVE") return 0.0;

                uint byteRate = 0, dataSize = 0;
                while (s.Position + 8 <= s.Length)
                {
                    string id = Tag(r);
                    uint size = r.ReadUInt32();
                    long next = s.Position + size + (size & 1);   // chunks are word-aligned
                    if (id == "fmt " && size >= 16)
                    {
                        r.ReadUInt16();               // audio format
                        r.ReadUInt16();               // channels
                        r.ReadUInt32();               // sample rate
                        byteRate = r.ReadUInt32();    // bytes per second — all we need
                    }
                    else if (id == "data")
                    {
                        // A streamed wav can declare size 0; fall back to what's actually left in the file.
                        dataSize = size > 0 ? size : (uint)Math.Max(0, s.Length - s.Position);
                        if (byteRate > 0) break;      // fmt always precedes data in practice
                    }
                    if (next <= s.Position || next > s.Length) break;
                    s.Position = next;
                }
                return byteRate > 0 && dataSize > 0 ? dataSize / (double)byteRate : 0.0;
            }
        }

        /// <summary>Ogg/Vorbis: sample rate from the identification packet on the first page, total samples from the
        /// granule position on the last page (scanned back from EOF). 0 if it isn't a Vorbis stream.</summary>
        public static double OggSeconds(Stream s)
        {
            if (s == null || !s.CanRead || !s.CanSeek || s.Length < 58) return 0.0;

            // ---- first page: "OggS" ver flags granule(8) serial(4) seq(4) crc(4) segCount(1) segTable(segCount)
            //      then the packet: 0x01 "vorbis" ver(4) channels(1) sampleRate(4)
            var head = Read(s, 0, 64);
            if (head.Length < 64 || !Match(head, 0, "OggS")) return 0.0;
            int segCount = head[26];
            int packet = 27 + segCount;
            if (packet + 16 > head.Length) return 0.0;
            if (head[packet] != 0x01 || !Match(head, packet + 1, "vorbis")) return 0.0;
            uint rate = U32(head, packet + 12);
            if (rate == 0) return 0.0;

            // ---- last page: scan backwards for the final "OggS" and take its granule position (total sample count)
            int window = (int)Math.Min(65536L, s.Length);
            var tail = Read(s, s.Length - window, window);
            for (int i = tail.Length - 27; i >= 0; i--)
            {
                if (!Match(tail, i, "OggS")) continue;
                ulong granule = U64(tail, i + 6);
                if (granule == ulong.MaxValue) continue;   // -1 = page carries no packet end; keep scanning back
                return granule / (double)rate;
            }
            return 0.0;
        }

        // ---- helpers ----

        private static string Tag(BinaryReader r) => new string(r.ReadChars(4));

        private static byte[] Read(Stream s, long offset, int count)
        {
            if (offset < 0) { count += (int)offset; offset = 0; }
            if (count <= 0) return new byte[0];
            s.Position = offset;
            var buf = new byte[count];
            int got = 0, n;
            while (got < count && (n = s.Read(buf, got, count - got)) > 0) got += n;
            if (got != count) Array.Resize(ref buf, got);
            return buf;
        }

        private static bool Match(byte[] b, int at, string ascii)
        {
            if (at < 0 || at + ascii.Length > b.Length) return false;
            for (int i = 0; i < ascii.Length; i++) if (b[at + i] != (byte)ascii[i]) return false;
            return true;
        }

        private static uint U32(byte[] b, int at) =>
            (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));

        private static ulong U64(byte[] b, int at)
        {
            ulong v = 0;
            for (int i = 7; i >= 0; i--) v = (v << 8) | b[at + i];   // little-endian
            return v;
        }
    }
}
