using System;
using System.Reflection;
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
        ///   • <see cref="StepMania"/> — MAD keeps the LAME encoder-delay priming (NO trim) and prepends ONE leading
        ///     silence frame (~26 ms) for every file EXCEPT a "Xing" (VBR) header. It does this literally for a CBR
        ///     "Info" tag (RageSoundReader_MP3.cpp: <c>if(type==INFO) return false</c> → the frame is emitted as
        ///     silence); a file with NO Xing/Info header at all is realigned to that same +1-frame position by the
        ///     YHANIKI editor's WaveformDisplay (<c>DetectMp3FrameAlignSeconds</c>). Without it a header-less file such
        ///     as BlythE sits exactly one frame (~26 ms) EARLY relative to every headered song in the pack, so its
        ///     waveform/audio lands a frame before the notes. Only "Xing" (VBR) is skipped (content already at 0).
        ///   • <see cref="Osu"/> — BASS SKIPS the Xing/Info header frame entirely and gapless-trims the priming.
        ///     How much depends on the FILE, not a constant — see <see cref="OsuGaplessTrimFor"/>.
        /// Picking the wrong one shifts the song ~50 ms. Measured on Be Crazy For Me (SM Info, ±3 ms) and SDO Pack9's
        /// six osu charts (clean-kick ones within ±5 ms of the grid).
        /// </summary>
        public enum Mp3Sync { StepMania, Osu }

        /// <summary>
        /// Samples-per-channel BASS strips even from an mp3 that carries NO delay info at all — its own decoder
        /// delay. Official: BASS 2.4.12 upgrade notes, <c>BASS_StreamCreateFile</c> — "529 samples of silence are
        /// removed from the start of MP3 files that do not include delay info (eg. in a LAME header)". Gapless is
        /// unconditional in BASS 2.4.15 (osu!stable's build): there is no BASS_CONFIG_MP3_DISABLEGAPLESS in that
        /// version and no config id changes the output, so this is never off.
        /// </summary>
        public const int BassDecoderDelay = 529;

        /// <summary>
        /// Samples-per-channel to drop off the FRONT so the audio sits where osu! puts it — i.e. so the chart's
        /// ABSOLUTE hit times land on the right sound. Verified against osu!stable's own bass.dll (2.4.15.2) by
        /// decoding through it and cross-correlating with our libmad output: this formula reproduced the true offset
        /// for 12/12 real charts (values 529 / 1681 / 2257 — it is NOT one constant).
        ///
        /// WHERE THAT VERIFICATION LIVES (it is NOT in this repo, so don't assume it was never done — it has already
        /// been mistaken for a fabricated claim once): <c>H:/offset_check/bassgt/</c> — <c>bassdump.cs</c> P/Invokes
        /// osu!stable's own <c>bass.dll</c> (<c>%LOCALAPPDATA%/osu!/bass.dll</c>, BASS_GetVersion = 0x02040F02 =
        /// 2.4.15.2) with BASS_STREAM_DECODE|BASS_SAMPLE_FLOAT and dumps raw PCM; <c>bass_vs_mad.json</c> holds the
        /// per-file result. Re-confirmed independently on 2026-07-29 for the two extremes: Kamui.mp3 (Info + LAME
        /// delay 576) → BASS sample 0 sits at libmad sample 2257; Sayonara Trip.mp3 (no Xing/Info frame at all) →
        /// 529. Both matched to max|Δ| ≈ 1.3e-6 over 200 000 samples, i.e. bit-exact modulo the float32 WAV round-trip.
        /// So the 529 branch is MEASURED, not inferred — mpg123/libsndfile trim 0 there and are simply not BASS.
        ///
        ///     trim = (header frame still in our output ? that frame's samples : 0) + encoder delay + 529
        ///
        /// <paramref name="headerFrameInOutput"/> is the decoder's business, not the file's: libmad decodes the
        /// Xing/Info header frame like any other frame and emits it as ~26 ms of silence, so that frame is still
        /// sitting in front of the audio and has to come off here. NLayer (the fallback) skips it like BASS does,
        /// so for that path it's already gone. Getting this backwards costs exactly one frame (26 ms @ 44.1 kHz).
        ///
        /// Null/unreadable region → <see cref="BassDecoderDelay"/>, which is what BASS does with a file it can read
        /// no delay info from. Pure — unit-tested.
        /// </summary>
        public static int OsuGaplessTrimFor(byte[] region, bool headerFrameInOutput)
        {
            int header = headerFrameInOutput && VbrTagOffset(region) >= 0 ? FrameSamplesPerChannel(region) : 0;
            return header + LameEncoderDelay(region) + BassDecoderDelay;
        }

        /// <summary>Same, for an actual file — reads its first frame off disk. Unreadable → the bare
        /// <see cref="BassDecoderDelay"/>, matching BASS on a file it finds no delay info in.</summary>
        public static int OsuGaplessTrimForFile(string path, bool headerFrameInOutput)
            => OsuGaplessTrimFor(ReadTagRegion(path), headerFrameInOutput);

        /// <summary>Decode an .mp3 file to interleaved PCM, positioned to match the chart's home game
        /// (<paramref name="sync"/>). Returns null on failure. Background-thread safe.</summary>
        public static Mp3Pcm Decode(string path, Mp3Sync sync = Mp3Sync.StepMania)
        {
            try
            {
                // (1) 首選 libmad —— **StepMania 用的同一個解碼器**(Assets/Plugins/x86_64/sdomad.dll)。
                // 只有它能同時做到「壞幀整幀丟棄、不佔時間軸」和「reservoir 指不到的幀照樣輸出一幀」,
                // 而那兩件事正是外部歌對不上音樂的兩個根因 —— 詳見 MadDecoder 的註解。
                var mad = MadDecoder.Decode(path, out int droppedFrames, out int pretendFrames);
                if (mad != null)
                {
                    if (droppedFrames > 0 || pretendFrames > 0)
                        Debug.Log($"[mp3] {System.IO.Path.GetFileName(path)}: libmad 丟棄 {droppedFrames} 個壞幀"
                                + $"、{pretendFrames} 幀 pretend-success(與 StepMania 逐位相同)");
                    // osu 譜的音訊要照 BASS 的 gapless 修掉 priming。StepMania 譜**不再補前導幀** ——
                    // 那個補償當初是為了讓 NLayer 的輸出對上 MAD;現在解碼器就是 MAD,補了反而多 26ms。
                    // libmad 會把 Xing/Info 表頭幀當一般幀解成 ~26ms 靜音(BASS 是整幀跳過),所以這條路徑
                    // 要連那一幀一起剪掉 → headerFrameInOutput: true。
                    if (sync == Mp3Sync.Osu)
                    {
                        var d = mad.Samples;
                        int keep = ApplyOsuGapless(d, d.Length, mad.Channels,
                                                   OsuGaplessTrimForFile(path, true));
                        if (keep == 0) return null;
                        if (keep != d.Length) Array.Resize(ref d, keep);
                        mad.Samples = d;
                    }
                    NormalizeIfHot(mad.Samples, mad.Samples.Length);
                    return mad;
                }

                // (2) 退回 NLayer(純 C#):DLL 載不到時才走這裡,行為與加入 libmad 之前相同。
                int ch, sr, len;
                // 照搬 StepMania/MAD:單次循序解碼,檔案有幾個音訊幀就輸出幾幀,解不出來的送靜音、時間軸不動。
                var data = DecodeFramewise(path, out ch, out sr, out len, out int silencedFrames);
                if (data != null && silencedFrames > 0)
                    Debug.LogWarning($"[mp3] {System.IO.Path.GetFileName(path)}: {silencedFrames} 個幀解不出來 → "
                                   + "當靜音送出(和 MAD 一樣),時間軸不受影響");
                if (data == null)
                {
                    // 反射拿不到 NLayer 的內部 reader(例如 IL2CPP 把它剝了)→ 退回高階 API。它會把解不出來的幀
                    // **靜默跳過**,那之後整首每漏一幀就提前 26 ms,只能記個警告讓人知道。
                    data = DecodeSequential(path, out ch, out sr, out len, out int expectedPerChannel);
                    if (data == null || len == 0) return null;
                    if (IsShortOfDeclaredLength(expectedPerChannel, len / ch))
                    {
                        int dropped = DroppedFrames(FileAudioFrameCount(System.IO.File.ReadAllBytes(path), out int spf),
                                                    len / ch, spf);
                        if (dropped > 0)
                            Debug.LogWarning($"[mp3] {System.IO.Path.GetFileName(path)}: 退回高階解碼且漏了 {dropped} 幀"
                                           + $" → 歌會提前 {(sr > 0 ? dropped * spf * 1000 / sr : 0)} ms");
                    }
                }
                if (len == 0) return null;
                // Position the PCM to match the chart's home game (see Mp3Sync). osu → drop BASS's gapless priming;
                // StepMania → prepend the one leading silence frame it keeps for everything but a Xing (VBR) header.
                // Both verified against real charts. NLayer already skips the Xing/Info header frame (like BASS),
                // so this path must NOT subtract it again → headerFrameInOutput: false.
                if (sync == Mp3Sync.Osu)
                    len = ApplyOsuGapless(data, len, ch, OsuGaplessTrimForFile(path, false));
                else
                    data = ApplyStepManiaLeadFrame(path, data, ref len, ch);
                if (len == 0) return null;
                if (len != data.Length) Array.Resize(ref data, len);
                // 過載母帶(峰值 > 1,如 Amanojaku.mp3 做到 +2.2 dBFS)靠**整段透明降增益**處理,而不是靠上面
                // 那個每取樣硬 clamp 成 ±1 —— clamp 把峰削平成方波,會在最響的地方冒出刺耳諧波。乘一個常數增益
                // 是純線性、零失真。
                NormalizeIfHot(data, len);
                return new Mp3Pcm { Samples = data, Channels = ch, SampleRate = sr };
            }
            catch { return null; }   // caller logs; the decode itself is Unity-free so it can run on a worker thread
        }

        // ---- 單次循序解碼(照搬 StepMania/MAD)----
        //
        // MAD 解 mp3 是「從頭到尾一次過」:bit reservoir 跨幀保留,而**每一個檔案幀都會產生一幀輸出** ——
        // 連 bit reservoir 指不到資料的壞幀也是,它 `ret = 0; /* pretend success */` 讓那一幀當靜音送出去
        // (RageSoundReader_MP3.cpp:429,註解寫著 "BASS pretends the bad frames are silent")。所以 SM 既不爆音
        // (不切塊 → 每幀都 prime 得起來)也不會跑掉(壞幀不吃掉時間)。
        //
        // NLayer 的高階 MpegFile.ReadSamples 差別就在最後一步:它把解不出來的幀**靜默跳過**,一跳整首就提前
        // 26 ms —— engine[Blue](37.5 秒起)和 Amanojaku(24 秒起)漂掉就是這樣來的,而且外部歌有一半以上
        // 中招。低階的 MpegStreamReader.NextFrame() + MpegFrameDecoder 才能逐幀對帳,可惜前者是 internal,
        // 只好用反射拿(拿不到就退回高階 API,見 DecodeSequential)。
        private static readonly ConstructorInfo _readerCtor;
        private static readonly MethodInfo _nextFrame;

        static Mp3Decoder()
        {
            try
            {
                var t = typeof(MpegFrameDecoder).Assembly.GetType("NLayer.Decoder.MpegStreamReader");
                if (t == null) return;
                const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                foreach (var c in t.GetConstructors(Any))
                {
                    var ps = c.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(System.IO.Stream)) { _readerCtor = c; break; }
                }
                _nextFrame = t.GetMethod("NextFrame", Any);
            }
            catch { _readerCtor = null; _nextFrame = null; }   // 反射不通(IL2CPP 剝掉了)→ 退回高階 API
        }

        /// <summary>
        /// 逐幀循序解碼 —— 和 MAD 一樣,檔案裡有幾個音訊幀就輸出幾幀,解不出來的那一幀送靜音(時間軸不動)。
        /// 回 null 表示這條路走不通(反射拿不到 NLayer 的內部 reader),呼叫端要退回高階 API。
        /// </summary>
        private static float[] DecodeFramewise(string path, out int ch, out int sr, out int len, out int silencedFrames)
        {
            ch = 0; sr = 0; len = 0; silencedFrames = 0;
            if (_readerCtor == null || _nextFrame == null) return null;
            using (var fs = System.IO.File.OpenRead(path))
            {
                var reader = _readerCtor.Invoke(new object[] { fs });
                var dec = new MpegFrameDecoder();
                var data = new float[1 << 20];
                float[] buf = null;
                while (true)
                {
                    var frame = _nextFrame.Invoke(reader, null) as IMpegFrame;
                    if (frame == null) break;
                    if (ch == 0)
                    {
                        ch = frame.ChannelMode == MpegChannelMode.Mono ? 1 : 2;
                        sr = frame.SampleRate > 0 ? frame.SampleRate : 44100;
                    }
                    int fw = frame.SampleCount * ch;                       // 這一幀該有幾個交錯樣本
                    if (buf == null || buf.Length < fw) buf = new float[fw * 2];
                    if (len + fw > data.Length) Array.Resize(ref data, Math.Max(data.Length * 2, len + fw));
                    int n = 0;
                    try { n = dec.DecodeFrame(frame, buf, 0); } catch { n = 0; }
                    if (n > 0)
                        for (int i = 0; i < n && i < fw; i++)
                        {
                            float s = buf[i];
                            data[len + i] = s > 1f ? 1f : (s < -1f ? -1f : s);   // NLayer can overshoot ±1 → clamp
                        }
                    if (n < fw)
                    {
                        Array.Clear(data, len + Math.Max(0, n), fw - Math.Max(0, n));
                        if (n <= 0) silencedFrames++;      // MAD 的 pretend success:壞幀當靜音,時間軸照走
                    }
                    len += fw;                             // ← 關鍵:不管解不解得出來,時間軸一定前進一幀
                }
                if (ch == 0 || len == 0) return null;
                return data;
            }
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

        /// <summary>
        /// 直解出來的樣本數比 NLayer 自報的 <c>mp3.Length</c> 少嗎?這**只是初篩** —— 少了不代表漏幀
        /// (mp3.Length 會多報:它把從不輸出成 PCM 的 Xing/Info 表頭幀和尾端 LAME padding 也算進去),
        /// 但沒少就一定沒漏。少了才值得去掃一次檔案的實際幀表(<see cref="FileAudioFrameCount"/>)。純函式。
        /// </summary>
        public static bool IsShortOfDeclaredLength(int expectedPerChannel, int decodedPerChannel)
            => expectedPerChannel > 0 && decodedPerChannel < expectedPerChannel;

        /// <summary>
        /// 檔案裡實際有幾個**音訊** MPEG 幀(掃幀表,扣掉 Xing/Info 表頭幀 —— 解碼器不會把它輸出成 PCM)。
        /// 這是唯一能分辨「mp3.Length 多報」和「NLayer 真的漏解」的東西。掃不出來回 0。
        /// </summary>
        public static int FileAudioFrameCount(byte[] file, out int samplesPerFrame)
        {
            samplesPerFrame = 0;
            if (file == null || file.Length == 0) return 0;
            var tbl = FrameTable(file, out samplesPerFrame);
            if (samplesPerFrame <= 0 || tbl == null || tbl.Count < 2) return 0;
            int frames = tbl.Count - 1;                                  // 最後一筆是結尾哨兵
            return frames - (HasVbrTagFrame(file, tbl) ? 1 : 0);
        }

        /// <summary>
        /// 直解漏了幾個 MPEG 幀(0 = 一個都沒漏)。漏一幀 = 那之後整首提前一幀的時間(44.1kHz 是 26 ms),
        /// 而且會一路帶到歌尾 —— engine[Blue] 就是漏了第 1434 幀,37.5 秒之後音樂全部早 26 ms。
        ///
        /// StepMania/MAD 從來不會漏:遇到 bit reservoir 指不到資料(MAD_ERROR_BADDATAPTR)時它
        /// <c>ret = 0; /* pretend success */</c>,那一幀照樣輸出(內容當靜音)、時間軸一格都不動
        /// (RageSoundReader_MP3.cpp:429)。NLayer 則是直接跳過那一幀,所以才需要這個對帳。純函式。
        /// </summary>
        public static int DroppedFrames(int fileAudioFrames, int decodedPerChannel, int samplesPerFrame)
        {
            if (fileAudioFrames <= 0 || samplesPerFrame <= 0 || decodedPerChannel < 0) return 0;
            int decodedFrames = decodedPerChannel / samplesPerFrame;
            return fileAudioFrames > decodedFrames ? fileAudioFrames - decodedFrames : 0;
        }

        /// <summary>過載保護:整段峰值 &gt; 1 時,乘一個增益把峰壓到 <paramref name="target"/>(預設 0.98)。這是純線性
        /// 縮放,<b>完全不改波形形狀(零失真)</b>,用來取代「硬 clamp 成平頂」對過載母帶造成的方波削波爆音。峰值
        /// 已在 <paramref name="target"/> 以下就原封不動。就地處理。純函式 —— 有單元測試。</summary>
        public static void NormalizeIfHot(float[] data, int len, float target = 0.98f)
        {
            if (data == null || len <= 0) return;
            int n = Math.Min(len, data.Length);
            float peak = 0f;
            for (int i = 0; i < n; i++) { float a = data[i] < 0f ? -data[i] : data[i]; if (a > peak) peak = a; }
            if (peak <= target || peak <= 0f) return;
            float g = target / peak;
            for (int i = 0; i < n; i++) data[i] *= g;
        }

        private static readonly byte[] XingTag = { 0x58, 0x69, 0x6E, 0x67 }; // "Xing" (VBR header — decoders SKIP this frame)
        // "Info" = the CBR spelling of the same header. StepMania's MAD reader deliberately emits it as a silence frame
        // (RageSoundReader_MP3.cpp: if(type==INFO) return false) — that is a StepMania quirk, NOT a BASS one. Verified
        // against osu!stable's bass.dll by rewriting Kamui.mp3's marker: "Info" and "Xing" give byte-identical output
        // (BASS skips both); only corrupting it to "Junk" changes anything (+1 frame). libmad, having no special case
        // at all, decodes either one into ~26 ms of silence.
        private static readonly byte[] InfoTag = { 0x49, 0x6E, 0x66, 0x6F };
        // Encoder-version strings that carry a LAME-style gapless tag. Lavf/Lavc = ffmpeg (it writes the same layout).
        private static readonly byte[][] EncoderTags =
        {
            new byte[] { 0x4C, 0x41, 0x4D, 0x45 },   // "LAME"
            new byte[] { 0x4C, 0x61, 0x76, 0x66 },   // "Lavf"
            new byte[] { 0x4C, 0x61, 0x76, 0x63 },   // "Lavc"
        };

        /// <summary>
        /// osu!/BASS gapless positioning: drop <paramref name="trim"/> leading samples (per channel) so the music
        /// starts where an osu chart's ABSOLUTE hit times expect it. The amount comes from the file — see
        /// <see cref="OsuGaplessTrimFor"/>. In-place shift; returns the new interleaved length. Pure w.r.t. Unity
        /// (safe off-thread).
        /// </summary>
        private static int ApplyOsuGapless(float[] data, int len, int ch, int trim)
        {
            if (ch <= 0 || len <= 0 || data == null) return len;
            int keep = OsuGaplessKeptLength(len, ch, trim);
            int skip = len - keep;
            if (skip <= 0) return len;
            if (keep > 0) Array.Copy(data, skip, data, 0, keep);   // memmove: overlapping is fine
            return keep;
        }

        /// <summary>Interleaved length left after the osu gapless trim removes <paramref name="trim"/> frames from
        /// the front; 0 if that eats the whole buffer. Pure — unit-tested.</summary>
        public static int OsuGaplessKeptLength(int len, int ch, int trim)
        {
            if (ch <= 0 || len <= 0) return len < 0 ? 0 : len;
            if (trim <= 0) return len;
            int skip = trim * ch;
            return skip >= len ? 0 : len - skip;
        }

        /// <summary>
        /// StepMania/MAD positioning: prepend the one leading silence frame so a decoded mp3 lines up the way StepMania
        /// plays it (global-offset 0). Returns the buffer to use (grown when a frame is prepended, else
        /// <paramref name="data"/>); <paramref name="len"/> is updated. Pure w.r.t. Unity (safe off-thread).
        ///
        /// Why: an mp3 that carries a Xing/Info header frame starts with metadata, not audio; NLayer — like most
        /// decoders — SKIPS it. StepMania's MAD reader deliberately does NOT skip a CBR "Info" tag: it lets the frame
        /// through as ~26 ms of silence to "match DWI sync" (RageSoundReader_MP3.cpp: <c>if(type==INFO) return false</c>),
        /// so its timeline starts one frame EARLIER than NLayer's → without re-inserting it, SM charts run ~26 ms early.
        /// A "Xing" (VBR) tag IS skipped by MAD too → no prepend. A file with NO header frame at all gets the SAME
        /// leading frame: the YHANIKI editor aligns header-less MP3s to that +1-frame position (see
        /// <see cref="ShouldPrependStepManiaLeadFrame"/>), so BlythE-style files don't sit one frame ahead of the rest
        /// of the pack. MAD does NO gapless trim (unlike osu's modern BASS — hence the separate <see cref="Mp3Sync.Osu"/>
        /// path). Verified on Be Crazy For Me (SM Info, ±3 ms).
        /// </summary>
        private static float[] ApplyStepManiaLeadFrame(string path, float[] data, ref int len, int ch)
        {
            if (ch <= 0 || len <= 0 || data == null) return data;
            byte[] region = ReadTagRegion(path);
            if (!ShouldPrependStepManiaLeadFrame(region)) return data;   // Xing / read failed → NLayer already matches MAD
            int frame = FrameSamplesPerChannel(region);
            if (frame <= 0) return data;
            int prepend = frame * ch;
            var grown = new float[len + prepend];
            Array.Copy(data, 0, grown, prepend, len);   // front stays 0 = one silent lead frame, exactly what MAD emits
            len += prepend;
            return grown;
        }

        /// <summary>
        /// StepMania-sync: should a leading silence frame be prepended so this file sits at the same "+1 frame" position
        /// as the rest of a pack. TRUE for a CBR "Info" header (MAD keeps it as silence) AND for a file with NO
        /// Xing/Info header at all — the YHANIKI editor's <c>WaveformDisplay.DetectMp3FrameAlignSeconds</c> realigns
        /// those header-less MP3s to the same +1-frame position, otherwise a file like BlythE plays/draws exactly one
        /// frame (~26 ms) EARLY relative to every headered song. FALSE only for a "Xing" (VBR) header, which MAD/BASS
        /// skip → content is already at 0; also FALSE when the tag region couldn't be read (leave the audio untouched).
        /// The tag lives inside the first audio frame; only the first ~1 KB is scanned so the same 4 bytes reappearing
        /// in real audio later can't be mistaken for it. Pure — unit-tested.
        /// </summary>
        public static bool ShouldPrependStepManiaLeadFrame(byte[] region)
        {
            if (region == null) return false;               // read failed → don't touch the decode
            int scan = Math.Min(region.Length, 1100);       // first frame only (≤ ~1044 B even at 128 kbps)
            int xi = IndexOf(region, XingTag, 0);
            if (xi >= 0 && xi < scan) return false;         // Xing (VBR) → skipped by MAD/BASS, no lead frame
            return true;                                    // Info (CBR) or no header → align to the +1-frame position
        }

        /// <summary>True when the file's VBR/CBR header frame is an "Info" (CBR) tag — the kind StepMania's MAD reader
        /// emits as a silence frame, so it must be re-inserted on the StepMania path. A "Xing" (VBR) tag, or no tag at
        /// all, returns false (those are skipped / do not exist, matching NLayer). The tag lives inside the first audio
        /// frame; only the first ~1 KB is scanned so the same 4 bytes reappearing in real audio later can't be mistaken
        /// for it. Pure — unit-tested.</summary>
        public static bool HasInfoHeaderFrame(byte[] region)
        {
            if (region == null) return false;
            int scan = Math.Min(region.Length, 1100);   // first frame only (≤ ~1044 B even at 128 kbps)
            int xi = IndexOf(region, XingTag, 0);
            if (xi >= 0 && xi < scan) return false;      // Xing (VBR) header → skipped by MAD/BASS too
            int ii = IndexOf(region, InfoTag, 0);
            return ii >= 0 && ii < scan;
        }

        /// <summary>Offset of the Xing/Info marker inside the first audio frame, or −1 when this file has no header
        /// frame at all. Either spelling counts — BASS treats them identically (see <see cref="InfoTag"/>), and for
        /// the gapless maths all that matters is "is frame 0 a header instead of audio". Only the first ~1 KB is
        /// scanned so the same 4 bytes appearing in real audio later can't be mistaken for it. Pure — unit-tested.</summary>
        public static int VbrTagOffset(byte[] region)
        {
            int end = FirstFrameEnd(region);
            if (end <= 0) return -1;
            int xi = IndexOfIn(region, XingTag, 0, end);
            if (xi >= 0) return xi;
            return IndexOfIn(region, InfoTag, 0, end);
        }

        /// <summary>
        /// Where the first MPEG frame ends inside <paramref name="region"/> (exclusive) — the only bytes a
        /// Xing/Info/LAME tag can legally live in.
        ///
        /// This has to be the REAL frame length, not a constant: encoders write the header frame at whatever bitrate
        /// they like, and low ones are tiny — Dreamin's is 182 B (56 kbps), Violet Soul's 417 B (128 kbps). A fixed
        /// ~1 KB window runs straight past them into actual audio, where the bytes "LAME" turn up by chance and read
        /// as a 1365-sample delay (0x555 = the 0x55 filler). That mis-set both songs by ~18 ms.
        /// Falls back to the old conservative window when no frame table can be read. Pure — unit-tested.
        /// </summary>
        private static int FirstFrameEnd(byte[] region)
        {
            if (region == null || region.Length == 0) return 0;
            var tbl = FrameTable(region, out _);
            if (tbl != null && tbl.Count >= 2 && tbl[1] > tbl[0]) return Math.Min(tbl[1], region.Length);
            return Math.Min(region.Length, 1100);
        }

        /// <summary>
        /// The encoder delay (samples per channel) recorded in the file's LAME-style gapless tag, or 0 when there
        /// isn't one. Layout: 21 bytes past the encoder-version string sit 3 bytes holding a 12-bit delay followed by
        /// a 12-bit padding; ffmpeg ("Lavf"/"Lavc") writes the same layout, verified on real files (Lavf54.31 and
        /// Lavf55.19 both read 576, matching what bass.dll actually trimmed).
        ///
        /// Only looked for INSIDE the Xing/Info header frame, and only after the marker — see
        /// <see cref="FirstFrameEnd"/> for why the boundary has to be the real frame length. Without that guard the
        /// bytes "LAME" occurring by chance in the audio that follows get read as a 1365-sample delay: untagged files
        /// (Sayonara Trip, Isetsu Higanbana) carry a bare "LAME" + 0x55 filler, and short header frames (Dreamin at
        /// 182 B, Violet Soul at 417 B) let the window spill into frame 2 where the same thing happens.
        /// The EARLIEST match wins, not the first name tried — the version string sits right after the tag, so
        /// anything later is coincidence. Pure — unit-tested.
        /// </summary>
        public static int LameEncoderDelay(byte[] region)
        {
            int tag = VbrTagOffset(region);
            if (tag < 0) return 0;                        // no header frame → nothing to read (BASS trims 529 only)
            int end = FirstFrameEnd(region);
            int at = -1;
            foreach (var enc in EncoderTags)
            {
                int i = IndexOfIn(region, enc, tag, end);
                if (i >= 0 && (at < 0 || i < at)) at = i;
            }
            if (at < 0 || at + 24 > region.Length) return 0;
            return (region[at + 21] << 4) | (region[at + 22] >> 4);   // 12-bit delay (padding is the next 12)
        }

        /// <summary>Samples-per-channel in one MPEG audio frame, read from the first frame-sync header in
        /// <paramref name="region"/>: MPEG-1 Layer III = 1152, MPEG-2/2.5 Layer III = 576 (mp3 is always Layer III).
        /// 0 when no frame sync is found.
        ///
        /// The header has to be VALIDATED, not just sync-matched: 11 bits of 1s turn up constantly in non-MPEG data,
        /// and this value feeds <see cref="OsuGaplessTrimFor"/> — believing a false sync costs a whole frame (26 ms).
        /// Measured on the 324 mp3s under Songs/: four of them (sdom0158/0225/0439/1186) are Ogg Vorbis wearing an
        /// .mp3 extension, and the sync-only test "found" a frame at offset 44 and answered 1152 for a file with no
        /// MPEG frame in it at all. Same reject list as <see cref="FrameTable"/>, which already got this right.
        /// Pure — unit-tested.</summary>
        public static int FrameSamplesPerChannel(byte[] region)
        {
            if (region == null) return 0;
            for (int i = 0; i + 2 < region.Length; i++)
            {
                if (region[i] != 0xFF || (region[i + 1] & 0xE0) != 0xE0) continue;   // 11-bit frame sync
                int version = (region[i + 1] >> 3) & 0x3;   // 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5, 1 = reserved
                int layer = (region[i + 1] >> 1) & 0x3;     // 1 = Layer III (mp3 is always Layer III)
                if (version == 1 || layer != 1) continue;   // reserved version / not Layer III → false sync
                int bri = (region[i + 2] >> 4) & 0xF, sri = (region[i + 2] >> 2) & 3;
                if (bri == 0 || bri == 15 || sri == 3) continue;   // free-form / bad bitrate, reserved sample rate
                return version == 3 ? 1152 : 576;
            }
            return 0;
        }

        // ---- 檔案幀表(給 FileAudioFrameCount 對帳、以及 Xing/Info 判定用)----

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
                // 幀長係數跟著「一幀幾個 sample」走:MPEG-1 Layer III 一幀 1152 → 144;MPEG-2/2.5(LSF)一幀
                // 只有 576 → 72。用 144 去算 LSF 會把每一幀都算成兩倍長,FirstFrameEnd 的搜尋窗跟著開兩倍,
                // 第 2 幀裡巧合的 "LAME" 就會被當成真的 gapless 標籤讀進來(= 整首歌位移 13 ms)。
                int flen = (ver == 3 ? 144 : 72) * bps / rate + pad;
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
        /// <see cref="ApplyStepManiaLeadFrame"/> then leaves the audio exactly as NLayer decoded it, and
        /// <see cref="OsuGaplessTrimFor"/> falls back to the bare <see cref="BassDecoderDelay"/>.</summary>
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
