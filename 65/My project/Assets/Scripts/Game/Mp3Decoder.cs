using System;
using NLayer;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>Decoded interleaved-float PCM (thread-safe POCO — no Unity types, so decoding can run off the main thread).</summary>
    public sealed class Mp3Pcm
    {
        public float[] Samples;   // interleaved [-1,1]
        public int Channels;
        public int SampleRate;
    }

    /// <summary>
    /// Runtime .mp3 → AudioClip decoder using the bundled NLayer managed decoder (Assets/Plugins/NLayer). Unity's
    /// UnityWebRequestMultimedia can only decode ogg/wav on desktop/editor (mp3 only on mobile), but external
    /// osu/StepMania song audio is usually mp3 — this makes it play everywhere. Decoding is CPU-only (no Unity API),
    /// so <see cref="Decode"/> is safe on a background thread; <see cref="ToClip"/> (AudioClip.Create) must run on the
    /// main thread.
    /// </summary>
    public static class Mp3Decoder
    {
        public static bool IsMp3(string path)
            => !string.IsNullOrEmpty(path) && path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// How to place a decoded mp3 so an imported chart lines up at global-offset 0 like it does in its home game.
        /// The two source games decode the SAME mp3 to DIFFERENT positions (verified by decoding real charts with NLayer):
        ///   • <see cref="StepMania"/> — MAD keeps the LAME encoder-delay priming (NO trim) and emits a CBR "Info"
        ///     header frame as ~26 ms of silence (RageSoundReader_MP3.cpp: <c>if(type==INFO) return false</c>).
        ///   • <see cref="Osu"/> — modern BASS gapless-trims the priming (~<see cref="OsuGaplessTrim"/> samples).
        /// Picking the wrong one shifts the song ~50 ms. Measured on Be Crazy For Me (SM, ±3 ms) and SDO Pack9's six
        /// osu charts (clean-kick ones within ±5 ms of the grid).
        /// </summary>
        public enum Mp3Sync { StepMania, Osu }

        /// <summary>Samples-per-channel osu!/BASS trims off the front of an mp3 (gapless priming = 576 LAME encoder
        /// delay + 529 decoder delay). Empirically the same for tagged and untagged files. ≈25 ms @ 44.1 kHz.</summary>
        public const int OsuGaplessTrim = 576 + 529;   // 1105

        /// <summary>Decode an .mp3 file to interleaved PCM, positioned to match the chart's home game
        /// (<paramref name="sync"/>). Returns null on failure. Background-thread safe.</summary>
        public static Mp3Pcm Decode(string path, Mp3Sync sync = Mp3Sync.StepMania)
        {
            try
            {
                int ch, sr, len;
                var data = DecodeSequential(path, out ch, out sr, out len, out int expectedPerChannel);
                if (data == null || len == 0) return null;
                // NLayer SILENTLY DROPS frames it fails to decode — the rest of the song then plays that much early,
                // and the drift accumulates (擬態ごっこ: 3 frames = 72 ms by the end, matching StepMania at the start
                // and 24/48/72 ms early after each drop). Its own frame index still knows the true length, so a
                // mismatch here is a reliable "this decode lost frames" flag → re-decode the timeline-exact way.
                if (expectedPerChannel > 0 && len / ch != expectedPerChannel)
                {
                    int lostMs = sr > 0 ? (expectedPerChannel - len / ch) * 1000 / sr : 0;
                    var fixedUp = DecodeSliced(path, ch);
                    if (fixedUp != null && fixedUp.Length > 0) { data = fixedUp; len = fixedUp.Length; }
                    // Debug.Log is queued, so this is safe off the main thread. Worth saying out loud: without the
                    // re-decode this song would have run `lostMs` ahead of the chart by the end.
                    Debug.LogWarning($"[mp3] {System.IO.Path.GetFileName(path)}: NLayer dropped frames "
                                   + $"({lostMs} ms) → re-decoded frame-by-frame"
                                   + (len / ch == expectedPerChannel ? "" : " (STILL SHORT — song will drift)"));
                }
                // Position the PCM to match the chart's home game (see Mp3Sync). osu → drop BASS's gapless priming;
                // StepMania → re-insert the "Info" header frame MAD emits as silence. Both verified against real charts.
                if (sync == Mp3Sync.Osu)
                    len = ApplyOsuGapless(data, len, ch);
                else
                    data = ApplyStepManiaInfoFrame(path, data, ref len, ch);
                if (len == 0) return null;
                if (len != data.Length) Array.Resize(ref data, len);
                return new Mp3Pcm { Samples = data, Channels = ch, SampleRate = sr };
            }
            catch { return null; }   // caller logs; the decode itself is Unity-free so it can run on a worker thread
        }

        /// <summary>Straight front-to-back decode (the fast path). <paramref name="expectedPerChannel"/> is what
        /// NLayer's own frame index says the file holds — compare it against what came out to spot dropped frames.</summary>
        private static float[] DecodeSequential(string path, out int ch, out int sr, out int len, out int expectedPerChannel)
        {
            using (var mp3 = new MpegFile(path))
            {
                ch = mp3.Channels > 0 ? mp3.Channels : 2;
                sr = mp3.SampleRate > 0 ? mp3.SampleRate : 44100;
                expectedPerChannel = (int)(mp3.Length / (4L * ch));   // Length is in bytes (float per channel)
                var data = new float[1 << 20];
                len = 0;
                var buf = new float[16384];
                int n;
                while ((n = mp3.ReadSamples(buf, 0, buf.Length)) > 0)
                {
                    if (len + n > data.Length) Array.Resize(ref data, Math.Max(data.Length * 2, len + n));
                    for (int i = 0; i < n; i++)
                    {
                        float s = buf[i];
                        data[len++] = s > 1f ? 1f : (s < -1f ? -1f : s);   // NLayer can overshoot ±1 slightly → clamp
                    }
                }
                return data;
            }
        }

        private static readonly byte[] XingTag = { 0x58, 0x69, 0x6E, 0x67 }; // "Xing" (VBR header — decoders SKIP this frame)
        private static readonly byte[] InfoTag = { 0x49, 0x6E, 0x66, 0x6F }; // "Info" (CBR header — BASS/DWI EMIT it as silence)

        /// <summary>
        /// osu!/BASS gapless positioning: drop the leading <see cref="OsuGaplessTrim"/> priming samples (per channel) so
        /// the music starts where an osu chart's ABSOLUTE hit times expect it. In-place shift; returns the new
        /// interleaved length. Pure w.r.t. Unity (safe off-thread). Verified: trimming this lands SDO Pack9's osu charts
        /// within a few ms of the grid at global-offset 0, where NLayer's un-trimmed output sat ~25 ms late.
        /// </summary>
        private static int ApplyOsuGapless(float[] data, int len, int ch)
        {
            if (ch <= 0 || len <= 0 || data == null) return len;
            int keep = OsuGaplessKeptLength(len, ch);
            int skip = len - keep;
            if (skip <= 0) return len;
            if (keep > 0) Array.Copy(data, skip, data, 0, keep);   // memmove: overlapping is fine
            return keep;
        }

        /// <summary>Interleaved length left after the osu gapless trim removes <see cref="OsuGaplessTrim"/> frames from
        /// the front; 0 if that eats the whole buffer. Pure — unit-tested.</summary>
        public static int OsuGaplessKeptLength(int len, int ch)
        {
            if (ch <= 0 || len <= 0) return len < 0 ? 0 : len;
            int skip = OsuGaplessTrim * ch;
            return skip >= len ? 0 : len - skip;
        }

        /// <summary>
        /// StepMania/MAD positioning: re-insert the leading "Info"-header frame as silence so a decoded mp3 lines up the
        /// way StepMania plays it (global-offset 0). Returns the buffer to use (grown when a frame is prepended, else
        /// <paramref name="data"/>); <paramref name="len"/> is updated. Pure w.r.t. Unity (safe off-thread).
        ///
        /// Why: an mp3 begins with one Xing/Info header frame carrying VBR/CBR metadata, not audio. NLayer — like most
        /// decoders — SKIPS it. StepMania's MAD reader deliberately does NOT: for a CBR "Info" tag it lets the frame
        /// through as ~26 ms of silence to "match DWI sync" (RageSoundReader_MP3.cpp: <c>if(type==INFO) return false</c>).
        /// So StepMania's timeline starts one frame EARLIER than NLayer's; without re-inserting it, SM charts run ~26 ms
        /// early. A "Xing" (VBR) tag IS skipped by MAD too → no prepend. And MAD does NO gapless trim (unlike osu's
        /// modern BASS — hence the separate <see cref="Mp3Sync.Osu"/> path). Verified on Be Crazy For Me (SM, ±3 ms).
        /// </summary>
        private static float[] ApplyStepManiaInfoFrame(string path, float[] data, ref int len, int ch)
        {
            if (ch <= 0 || len <= 0 || data == null) return data;
            byte[] region = ReadTagRegion(path);
            if (region == null || !HasInfoHeaderFrame(region)) return data;   // Xing / no tag → NLayer already matches MAD
            int frame = FrameSamplesPerChannel(region);
            if (frame <= 0) return data;
            int prepend = frame * ch;
            var grown = new float[len + prepend];
            Array.Copy(data, 0, grown, prepend, len);   // front stays 0 = one silent header frame, exactly what MAD emits
            len += prepend;
            return grown;
        }

        /// <summary>True when the file's VBR/CBR header frame is an "Info" (CBR) tag — the kind BASS/DWI emit as a silence
        /// frame, so it must be re-inserted. A "Xing" (VBR) tag, or no tag at all, returns false (those are skipped / do
        /// not exist, matching NLayer). The tag lives inside the first audio frame; only the first ~1 KB is scanned so
        /// the same 4 bytes reappearing in real audio later can't be mistaken for it. Pure — unit-tested.</summary>
        public static bool HasInfoHeaderFrame(byte[] region)
        {
            if (region == null) return false;
            int scan = Math.Min(region.Length, 1100);   // first frame only (≤ ~1044 B even at 128 kbps)
            int xi = IndexOf(region, XingTag, 0);
            if (xi >= 0 && xi < scan) return false;      // Xing (VBR) header → skipped by MAD/BASS too
            int ii = IndexOf(region, InfoTag, 0);
            return ii >= 0 && ii < scan;
        }

        /// <summary>Samples-per-channel in one MPEG audio frame, read from the first frame-sync header in
        /// <paramref name="region"/>: MPEG-1 Layer III = 1152, MPEG-2/2.5 Layer III = 576 (mp3 is always Layer III).
        /// 0 when no frame sync is found. Pure — unit-tested.</summary>
        public static int FrameSamplesPerChannel(byte[] region)
        {
            if (region == null) return 0;
            for (int i = 0; i + 1 < region.Length; i++)
            {
                if (region[i] != 0xFF || (region[i + 1] & 0xE0) != 0xE0) continue;   // 11-bit frame sync
                int version = (region[i + 1] >> 3) & 0x3;   // 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5, 1 = reserved
                if (version == 1) continue;                 // reserved → false sync, keep scanning
                return version == 3 ? 1152 : 576;
            }
            return 0;
        }

        // ---- timeline-exact re-decode (only used when the straight decode came up short) ----

        private const int SliceChunkFrames = 32;    // whole MPEG frames decoded per standalone slice
        private const int SlicePrerollFrames = 2;   // leading frames carried along so the bit reservoir is primed

        private static readonly int[] BitrateV1 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
        private static readonly int[] BitrateV2 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
        private static readonly int[,] SampleRates = { { 11025, 12000, 8000, 0 }, { 0, 0, 0, 0 },
                                                       { 22050, 24000, 16000, 0 }, { 44100, 48000, 32000, 0 } };

        /// <summary>Byte offset of every MPEG audio frame in <paramref name="data"/>, plus a trailing
        /// end-of-last-frame sentinel — frame i spans [table[i], table[i+1]). A leading ID3v2 tag is skipped via its
        /// syncsafe size, and bytes that only look like a frame sync are stepped over. <paramref name="samplesPerFrame"/>
        /// comes from the first real frame (MPEG-1 Layer III = 1152, MPEG-2/2.5 = 576). Pure — unit-tested.</summary>
        public static System.Collections.Generic.List<int> FrameTable(byte[] data, out int samplesPerFrame)
        {
            samplesPerFrame = 0;
            var list = new System.Collections.Generic.List<int>();
            if (data == null) { list.Add(0); return list; }
            int p = 0;
            if (data.Length > 10 && data[0] == 0x49 && data[1] == 0x44 && data[2] == 0x33)   // "ID3"
                p = 10 + (((data[6] & 0x7F) << 21) | ((data[7] & 0x7F) << 14) | ((data[8] & 0x7F) << 7) | (data[9] & 0x7F));
            if (p < 0 || p > data.Length) p = 0;
            while (p + 4 <= data.Length)
            {
                if (data[p] != 0xFF || (data[p + 1] & 0xE0) != 0xE0) { p++; continue; }
                int ver = (data[p + 1] >> 3) & 3, layer = (data[p + 1] >> 1) & 3;
                int bri = (data[p + 2] >> 4) & 0xF, sri = (data[p + 2] >> 2) & 3, pad = (data[p + 2] >> 1) & 1;
                if (ver == 1 || layer != 1 || bri == 0 || bri == 15 || sri == 3) { p++; continue; }   // 1 = reserved, layer 1 = III
                int rate = SampleRates[ver, sri];
                int bps = (ver == 3 ? BitrateV1[bri] : BitrateV2[bri]) * 1000;
                if (rate == 0 || bps == 0) { p++; continue; }
                int flen = 144 * bps / rate + pad;
                if (flen <= 4 || p + flen > data.Length) break;
                if (samplesPerFrame == 0) samplesPerFrame = ver == 3 ? 1152 : 576;
                list.Add(p);
                p += flen;
            }
            list.Add(p);   // sentinel = end of the last complete frame
            return list;
        }

        /// <summary>True when the file's FIRST frame carries a Xing/Info tag instead of audio — the frame every
        /// decoder (NLayer included) skips, so it produces no samples. Distinct from
        /// <see cref="HasInfoHeaderFrame"/>, which asks the narrower "is it the CBR Info kind StepMania's MAD emits
        /// as silence". Pure — unit-tested.</summary>
        public static bool HasVbrTagFrame(byte[] data, System.Collections.Generic.List<int> table)
        {
            if (data == null || table == null || table.Count < 2) return false;
            int from = Math.Max(0, table[0]), to = Math.Min(table[1], data.Length);
            return IndexOfIn(data, XingTag, from, to) >= 0 || IndexOfIn(data, InfoTag, from, to) >= 0;
        }

        /// <summary>
        /// Re-decode the file as standalone chunks of whole MPEG frames, laying each chunk back down at ITS OWN
        /// frame index. NLayer silently DROPS a frame it fails to decode, and in a straight front-to-back decode
        /// everything after that point moves earlier by one frame — the drift accumulates and the song ends up
        /// playing ahead of the chart (擬態ごっこ: 3 dropped frames = 72 ms). Anchoring every chunk on the frame
        /// table makes a bad frame cost at most its own chunk and never shifts the timeline.
        ///
        /// A slice always loses its FIRST frame (its bit reservoir points at bytes that are not in the slice), so
        /// each chunk carries <see cref="SlicePrerollFrames"/> extra leading frames and is copied out ALIGNED FROM
        /// ITS END. Losing more than that means a frame inside the kept range really is undecodable → redo that
        /// chunk one frame at a time and leave the bad frame as silence, so the timeline still holds.
        ///
        /// Verified sample-exact against libsndfile across a whole 2:34 song (max |diff| 2e-6 = float rounding).
        /// </summary>
        private static float[] DecodeSliced(string path, int ch)
        {
            var file = System.IO.File.ReadAllBytes(path);
            int spf;
            var tbl = FrameTable(file, out spf);
            int frames = tbl.Count - 1;                       // last entry is the end sentinel
            if (frames <= 0 || spf <= 0 || ch <= 0) return null;
            int audio0 = HasVbrTagFrame(file, tbl) ? 1 : 0;   // NLayer never emits the Xing/Info header frame
            int audioFrames = frames - audio0;
            if (audioFrames <= 0) return null;
            int fw = spf * ch;                                // interleaved samples in one frame
            long total = (long)audioFrames * fw;
            if (total <= 0 || total > int.MaxValue) return null;
            var outBuf = new float[total];
            var tmp = new float[(SliceChunkFrames + SlicePrerollFrames) * fw];
            for (int a = 0; a < audioFrames; a += SliceChunkFrames)
            {
                int k = Math.Min(SliceChunkFrames, audioFrames - a);
                int first = a == 0 ? 0 : audio0 + a - Math.Min(SlicePrerollFrames, a);
                int last = audio0 + a + k;
                int sliceAudio = (last - first) - (first < audio0 ? 1 : 0);   // frames the decoder should emit
                int got = DecodeSlice(file, tbl, first, last, tmp);
                if (got >= (sliceAudio - 1) * fw && got >= k * fw)
                {
                    Array.Copy(tmp, got - k * fw, outBuf, a * fw, k * fw);    // align from the END
                    continue;
                }
                for (int f = 0; f < k; f++)
                {
                    int i = a + f;
                    int fi = i == 0 ? 0 : audio0 + i - Math.Min(SlicePrerollFrames, i);
                    int g = DecodeSlice(file, tbl, fi, audio0 + i + 1, tmp);
                    if (g >= fw) Array.Copy(tmp, g - fw, outBuf, i * fw, fw);
                    else Array.Clear(outBuf, i * fw, fw);     // undecodable frame → silence, timeline preserved
                }
            }
            return outBuf;
        }

        /// <summary>Decode file frames [<paramref name="from"/>, <paramref name="to"/>) as a stream of their own into
        /// <paramref name="dst"/>; returns how many interleaved samples came out (a slice always loses its first
        /// frame, so this is normally one frame short of what the slice contains).</summary>
        private static int DecodeSlice(byte[] file, System.Collections.Generic.List<int> table, int from, int to, float[] dst)
        {
            int a = table[from], b = table[to];
            if (b <= a) return 0;
            using (var ms = new System.IO.MemoryStream(file, a, b - a, false))
            using (var mp3 = new MpegFile(ms))
            {
                var buf = new float[16384];
                int got = 0, n;
                while ((n = mp3.ReadSamples(buf, 0, buf.Length)) > 0)
                {
                    if (got + n > dst.Length) n = dst.Length - got;
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++)
                    {
                        float s = buf[i];
                        dst[got + i] = s > 1f ? 1f : (s < -1f ? -1f : s);
                    }
                    got += n;
                }
                return got;
            }
        }

        /// <summary>First index of <paramref name="needle"/> in <paramref name="hay"/> within
        /// [<paramref name="start"/>, <paramref name="end"/>), or −1.</summary>
        private static int IndexOfIn(byte[] hay, byte[] needle, int start, int end)
        {
            if (hay == null || needle == null || needle.Length == 0) return -1;
            int last = Math.Min(end, hay.Length) - needle.Length;
            for (int i = Math.Max(0, start); i <= last; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        /// <summary>First index of <paramref name="needle"/> in <paramref name="hay"/> at or after
        /// <paramref name="start"/>, or −1. Small linear scan (needle is 4 bytes; region is a few KB).</summary>
        private static int IndexOf(byte[] hay, byte[] needle, int start)
        {
            if (hay == null || needle == null || needle.Length == 0) return -1;
            int last = hay.Length - needle.Length;
            for (int i = Math.Max(0, start); i <= last; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        /// <summary>Read the bytes covering an mp3's first audio frame (where the Xing/Info tag lives), skipping any
        /// leading ID3v2 tag via its syncsafe size. ~2.6 KB is enough for the header frame. Null on IO failure →
        /// <see cref="ApplyBassInfoFrame"/> then leaves the audio exactly as NLayer decoded it.</summary>
        private static byte[] ReadTagRegion(string path)
        {
            try
            {
                using (var fs = System.IO.File.OpenRead(path))
                {
                    long fileLen = fs.Length;
                    int id3 = 0;
                    var h = new byte[10];
                    if (fs.Read(h, 0, 10) == 10 && h[0] == 0x49 && h[1] == 0x44 && h[2] == 0x33)   // "ID3"
                        id3 = 10 + (((h[6] & 0x7F) << 21) | ((h[7] & 0x7F) << 14) | ((h[8] & 0x7F) << 7) | (h[9] & 0x7F));
                    if (id3 < 0 || id3 >= fileLen) return null;
                    int want = (int)Math.Min(2600L, fileLen - id3);
                    if (want <= 0) return null;
                    var region = new byte[want];
                    fs.Seek(id3, System.IO.SeekOrigin.Begin);
                    int read = 0, r;
                    while (read < want && (r = fs.Read(region, read, want - read)) > 0) read += r;
                    if (read <= 0) return null;
                    if (read < want) Array.Resize(ref region, read);
                    return region;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// Decode ONLY a window of an mp3 (for song-select previews) instead of the whole song — NLayer seeks
        /// accurately in ~15 ms, so this is ~10× faster than a full decode (≈120 ms vs ≈1.4 s) and starts the preview
        /// almost immediately. <paramref name="startSec"/> &lt; 0 (or past the end) → centre the window. Background-thread safe.
        /// </summary>
        public static Mp3Pcm DecodeWindow(string path, float startSec, float lenSec)
        {
            try
            {
                using (var mp3 = new MpegFile(path))
                {
                    int ch = mp3.Channels > 0 ? mp3.Channels : 2;
                    int sr = mp3.SampleRate > 0 ? mp3.SampleRate : 44100;
                    double dur = mp3.Duration.TotalSeconds;
                    float win = lenSec > 0f ? lenSec : 20f;
                    if (dur > 0 && win > dur) win = (float)dur;
                    float start = startSec;
                    if (start < 0f || (dur > 0 && start >= dur)) start = (float)(dur * 0.4);   // osu default preview = 40% of length
                    if (dur > 0) start = (float)Math.Min(start, Math.Max(0.0, dur - win));
                    if (start < 0f) start = 0f;
                    if (start > 0f) mp3.Time = TimeSpan.FromSeconds(start);

                    int need = (int)(win * sr) * ch;
                    if (need <= 0) return null;
                    var data = new float[need];
                    int len = 0;
                    var buf = new float[16384];
                    int n;
                    while (len < need && (n = mp3.ReadSamples(buf, 0, Math.Min(buf.Length, need - len))) > 0)
                        for (int i = 0; i < n; i++)
                        {
                            float s = buf[i];
                            data[len++] = s > 1f ? 1f : (s < -1f ? -1f : s);
                        }
                    if (len == 0) return null;
                    if (len != data.Length) Array.Resize(ref data, len);
                    return new Mp3Pcm { Samples = data, Channels = ch, SampleRate = sr };
                }
            }
            catch { return null; }
        }

        /// <summary>Build an AudioClip from decoded PCM (MAIN THREAD only). Null on empty/invalid input.</summary>
        public static AudioClip ToClip(Mp3Pcm pcm, string name)
        {
            if (pcm == null || pcm.Samples == null || pcm.Samples.Length == 0 || pcm.Channels <= 0) return null;
            int perChannel = pcm.Samples.Length / pcm.Channels;
            if (perChannel <= 0) return null;
            var clip = AudioClip.Create(name, perChannel, pcm.Channels, pcm.SampleRate, false);
            clip.SetData(pcm.Samples, 0);
            return clip;
        }
    }

    /// <summary>
    /// STREAMING mp3 preview clip — plays as fast as ogg. Instead of decoding the whole window up front (~100-470 ms),
    /// it creates a streaming AudioClip whose PCMReaderCallback pulls samples from NLayer on demand on the audio thread,
    /// looping a [startSec, startSec+lenSec] window seamlessly. Opening + seeking is ~20-30 ms so playback starts almost
    /// immediately; NLayer decodes ~130× faster than realtime so the callback never underruns. Create on the MAIN thread
    /// (AudioClip.Create), then assign <see cref="Clip"/> to an AudioSource and Play. Dispose (main thread) when done.
    /// </summary>
    public sealed class Mp3StreamClip : IDisposable
    {
        private NLayer.MpegFile _mp3;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly double _startSec;
        private readonly int _windowFrames;   // per-channel samples in the loop window
        private int _framesRead;              // per-channel frames since window start
        private readonly object _lock = new object();

        public AudioClip Clip { get; private set; }

        /// <summary>Open <paramref name="path"/> and build a looping streaming clip of a [start, start+len] window.
        /// startSec &lt; 0 (or past the end) → centre the window. Null on failure. MAIN THREAD.</summary>
        public static Mp3StreamClip Create(string path, float startSec, float lenSec, string name)
        {
            NLayer.MpegFile mp3 = null;
            try
            {
                mp3 = new NLayer.MpegFile(path);
                int ch = mp3.Channels > 0 ? mp3.Channels : 2;
                int sr = mp3.SampleRate > 0 ? mp3.SampleRate : 44100;
                double dur = mp3.Duration.TotalSeconds;
                float win = lenSec > 0f ? lenSec : 20f;
                if (dur > 0 && win > dur) win = (float)dur;
                float start = startSec;
                if (start < 0f || (dur > 0 && start >= dur)) start = (float)(dur * 0.4);   // osu default preview = 40% of length
                if (dur > 0) start = (float)Math.Min(start, Math.Max(0.0, dur - win));
                if (start < 0f) start = 0f;
                int winFrames = Math.Max(1, (int)(win * sr));

                var self = new Mp3StreamClip(mp3, ch, sr, start, winFrames);
                self.SeekStart();
                // Big clip length + our own internal loop (OnRead) so Unity never wraps the position (avoids a resync
                // seam at the loop). Streaming clips don't allocate the sample buffer, so a long length is free.
                int clipLen = Math.Max(winFrames, sr * 600);
                self.Clip = AudioClip.Create(name, clipLen, ch, sr, true, self.OnRead, self.OnSetPos);
                return self;
            }
            catch { try { mp3?.Dispose(); } catch { } return null; }
        }

        private Mp3StreamClip(NLayer.MpegFile mp3, int ch, int sr, double start, int winFrames)
        { _mp3 = mp3; _channels = ch; _sampleRate = sr; _startSec = start; _windowFrames = winFrames; }

        // Audio thread: fill data with the next interleaved samples, looping the window seamlessly.
        private void OnRead(float[] data)
        {
            lock (_lock)
            {
                if (_mp3 == null) { Array.Clear(data, 0, data.Length); return; }
                int need = data.Length, off = 0, guard = 0;
                while (off < need && guard++ < 256)
                {
                    if (_framesRead >= _windowFrames) SeekStart();
                    int framesLeft = Math.Max(1, _windowFrames - _framesRead);
                    int want = Math.Min(need - off, framesLeft * _channels);
                    int n = _mp3.ReadSamples(data, off, want);
                    if (n <= 0) { SeekStart(); continue; }
                    for (int k = off; k < off + n; k++) { float s = data[k]; if (s > 1f) data[k] = 1f; else if (s < -1f) data[k] = -1f; }
                    off += n; _framesRead += n / _channels;
                }
                if (off < need) Array.Clear(data, off, need - off);
            }
        }

        // Audio thread: Unity seeks (rare with our big length + internal loop) → re-point the decoder.
        private void OnSetPos(int position)
        {
            lock (_lock)
            {
                int p = position % _windowFrames; if (p < 0) p += _windowFrames;
                _framesRead = p;
                SeekTo(p);
            }
        }

        private void SeekStart() { _framesRead = 0; SeekTo(0); }
        private void SeekTo(int frame)
        {
            if (_mp3 == null) return;
            try { _mp3.Time = TimeSpan.FromSeconds(_startSec + frame / (double)_sampleRate); } catch { }
        }

        public void Dispose()
        {
            lock (_lock) { try { _mp3?.Dispose(); } catch { } _mp3 = null; }
            if (Clip != null) { UnityEngine.Object.Destroy(Clip); Clip = null; }
        }
    }
}
