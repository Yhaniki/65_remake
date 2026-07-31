using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// Mp3Decoder timing — making imported osu!/StepMania (mp3) charts line up at global-offset 0 like they do in their
    /// home game. The two games place the SAME mp3 differently, so each gets its own path:
    ///
    ///   • StepMania (MAD) keeps the encoder-delay priming and EMITS a CBR "Info" header frame as ~26 ms of silence
    ///     (RageSoundReader_MP3.cpp: <c>if(type==INFO) return false</c>). That quirk is StepMania's alone.
    ///   • osu (BASS) SKIPS the header frame — "Xing" and "Info" alike — and gapless-trims the priming. How much it
    ///     trims is per-FILE: <c>(header frame ? its samples : 0) + LAME encoder delay + 529</c>.
    ///
    /// The osu side was ground-truthed against osu!stable's own bass.dll (2.4.15.2): decode through it, cross-correlate
    /// with our libmad output, and read off where BASS's sample 0 lands. 12/12 real charts matched the formula, with
    /// three different answers (529 / 1681 / 2257) — which is why a single constant could not work. A fixed 1105 left
    /// every Info-tagged song 1152 samples (26 ms) late, and that is exactly the group the player had to hand-correct.
    /// </summary>
    public class Mp3GaplessTests
    {
        private static void Put(byte[] b, int at, string s) { for (int i = 0; i < s.Length; i++) b[at + i] = (byte)s[i]; }

        // First frame header + a Xing/Info tag at the usual offset (after MPEG1-stereo side info).
        private static byte[] Frame(byte hdr1, string vbrTag, int size = 128)
        {
            var b = new byte[size];
            b[0] = 0xFF; b[1] = hdr1;         // frame sync + version/layer
            b[2] = 0x90;                      // bitrate idx 9, samplerate idx 0, no padding — a VALID header.
                                              // 幀頭必須合法:FrameSamplesPerChannel 現在會驗 layer/bitrate/
                                              // samplerate(跟 FrameTable 同一組條件),byte 2 留 0 等於 free-form
                                              // bitrate,會被正確地當成假同步丟掉。
            b[3] = 0x44;
            if (vbrTag != null) Put(b, 36, vbrTag);
            return b;
        }

        [Test]
        public void FrameSamples_ReadsMpegVersion()
        {
            // 0xFB = MPEG-1 Layer III → 1152; 0xF3 = MPEG-2 Layer III → 576; 0xE3 = MPEG-2.5 → 576.
            Assert.AreEqual(1152, Mp3Decoder.FrameSamplesPerChannel(Frame(0xFB, null)));
            Assert.AreEqual(576, Mp3Decoder.FrameSamplesPerChannel(Frame(0xF3, null)));
            Assert.AreEqual(576, Mp3Decoder.FrameSamplesPerChannel(Frame(0xE3, null)));
        }

        [Test]
        public void FrameSamples_ZeroWhenNoSync()
        {
            Assert.AreEqual(0, Mp3Decoder.FrameSamplesPerChannel(new byte[64]));   // all zero → no 0xFFEx sync
            Assert.AreEqual(0, Mp3Decoder.FrameSamplesPerChannel(null));
        }

        [Test]
        public void FrameSamples_RejectsASyncWithAnInvalidHeader()
        {
            // 11 個 1 在非 MPEG 的資料裡到處都是,所以光比對 sync 不夠 —— 這個值會餵進 OsuGaplessTrimFor,
            // 信了假同步就是整整一幀(26 ms)。實測 Songs/ 底下 324 個 .mp3 有 4 個(sdom0158/0225/0439/1186)
            // 其實是副檔名寫成 .mp3 的 Ogg Vorbis:舊碼在 offset 44 「找到」幀頭並回報 1152,但整個檔案裡
            // 根本沒有 MPEG 幀。條件跟 FrameTable 用同一組。
            var freeform = new byte[64];
            freeform[0] = 0xFF; freeform[1] = 0xFB; freeform[2] = 0x04;   // bitrate idx 0 = free-form
            Assert.AreEqual(0, Mp3Decoder.FrameSamplesPerChannel(freeform));

            var badRate = new byte[64];
            badRate[0] = 0xFF; badRate[1] = 0xFB; badRate[2] = 0xF4;      // bitrate idx 15 = "bad"
            Assert.AreEqual(0, Mp3Decoder.FrameSamplesPerChannel(badRate));

            var reservedSr = new byte[64];
            reservedSr[0] = 0xFF; reservedSr[1] = 0xFB; reservedSr[2] = 0x9C;   // samplerate idx 3 = reserved
            Assert.AreEqual(0, Mp3Decoder.FrameSamplesPerChannel(reservedSr));

            var layerII = new byte[64];
            layerII[0] = 0xFF; layerII[1] = 0xFD; layerII[2] = 0x90;      // layer 2 = Layer II, mp3 一定是 Layer III
            Assert.AreEqual(0, Mp3Decoder.FrameSamplesPerChannel(layerII));

            // 假同步不能贏過後面「真的」幀頭 —— 要繼續往下掃,不是就地放棄。
            var decoy = new byte[128];
            decoy[0] = 0xFF; decoy[1] = 0xFB; decoy[2] = 0xF4;            // 假的(bad bitrate)
            decoy[40] = 0xFF; decoy[41] = 0xF3; decoy[42] = 0x90; decoy[43] = 0x44;   // 真的 MPEG-2 幀
            Assert.AreEqual(576, Mp3Decoder.FrameSamplesPerChannel(decoy));
        }

        [Test]
        public void InfoHeaderFrame_TrueForInfo_FalseForXing()
        {
            // "Info" (CBR) → BASS/DWI emit it as a silence frame → must be re-inserted. This is Be Crazy For Me's case.
            Assert.IsTrue(Mp3Decoder.HasInfoHeaderFrame(Frame(0xFB, "Info")));
            // "Xing" (VBR) → skipped by MAD/BASS too → nothing to re-insert.
            Assert.IsFalse(Mp3Decoder.HasInfoHeaderFrame(Frame(0xFB, "Xing")));
            // No VBR/CBR tag at all → no header frame.
            Assert.IsFalse(Mp3Decoder.HasInfoHeaderFrame(Frame(0xFB, null)));
            Assert.IsFalse(Mp3Decoder.HasInfoHeaderFrame(null));
        }

        [Test]
        public void StepManiaLeadFrame_PrependsForInfoAndNoHeader_NotForXing()
        {
            // Info (CBR) → MAD keeps the frame as silence → prepend, like before.
            Assert.IsTrue(Mp3Decoder.ShouldPrependStepManiaLeadFrame(Frame(0xFB, "Info")));
            // No header at all (BlythE / ALBIDA) → the YHANIKI editor realigns these to the same +1-frame position,
            // so they must ALSO get the lead frame or they sit one frame (~26 ms) early vs every headered song.
            Assert.IsTrue(Mp3Decoder.ShouldPrependStepManiaLeadFrame(Frame(0xFB, null)));
            // Xing (VBR) → MAD/BASS skip it → content already at 0 → NO lead frame.
            Assert.IsFalse(Mp3Decoder.ShouldPrependStepManiaLeadFrame(Frame(0xFB, "Xing")));
            // Couldn't read the tag region → leave the decode untouched.
            Assert.IsFalse(Mp3Decoder.ShouldPrependStepManiaLeadFrame(null));
        }

        [Test]
        public void OsuGapless_TrimsPrimingFromTheFront()
        {
            // stereo: 1105 frames = 2210 interleaved samples removed.
            Assert.AreEqual(100000 - 2210, Mp3Decoder.OsuGaplessKeptLength(100000, 2, 1105));
            Assert.AreEqual(100000 - 1105, Mp3Decoder.OsuGaplessKeptLength(100000, 1, 1105));   // mono
            // a buffer shorter than the priming is emptied, never negative.
            Assert.AreEqual(0, Mp3Decoder.OsuGaplessKeptLength(1000, 2, 1105));
            Assert.AreEqual(0, Mp3Decoder.OsuGaplessKeptLength(0, 2, 1105));
            // trim 0 (or nonsense) → leave the buffer exactly as decoded.
            Assert.AreEqual(100000, Mp3Decoder.OsuGaplessKeptLength(100000, 2, 0));
            Assert.AreEqual(100000, Mp3Decoder.OsuGaplessKeptLength(100000, 2, -5));
        }

        // ---- how much osu/BASS actually trims (per file, not a constant) ----

        // Build the first frame the way a real encoder does: header + Xing/Info marker at 36, then the encoder
        // version string, whose byte +21 starts the 12-bit encoder delay.
        private static byte[] TaggedFrame(string vbrTag, string encoder, int delay, byte hdr1 = 0xFB)
        {
            var b = Frame(hdr1, vbrTag, 256);
            if (encoder != null)
            {
                const int at = 120;                       // anywhere after the marker; real files vary
                Put(b, at, encoder);
                b[at + 21] = (byte)(delay >> 4);
                b[at + 22] = (byte)((delay & 0xF) << 4);  // low nibble of delay, then padding's top 4 bits (0)
            }
            return b;
        }

        [Test]
        public void OsuTrim_IsHeaderFramePlusEncoderDelayPlus529()
        {
            // The three answers seen across 12 real charts, each reproduced by bass.dll:
            // (a) no header frame at all → BASS still drops its own 529 decoder delay.
            Assert.AreEqual(529, Mp3Decoder.OsuGaplessTrimFor(Frame(0xFB, null), true));
            // (b) Info header + LAME delay 576 → 1152 + 576 + 529. Kamui / Bassdrop / BLUE ARMY / Yes my master…
            Assert.AreEqual(2257, Mp3Decoder.OsuGaplessTrimFor(TaggedFrame("Info", "LAME3.99r", 576), true));
            // (c) Info header but no encoder tag → delay reads 0. Violet Soul.
            Assert.AreEqual(1681, Mp3Decoder.OsuGaplessTrimFor(TaggedFrame("Info", null, 0), true));
            // "Xing" is the same header, and BASS makes no distinction (unlike StepMania's MAD).
            Assert.AreEqual(2257, Mp3Decoder.OsuGaplessTrimFor(TaggedFrame("Xing", "LAME3.99r", 576), true));
            // ffmpeg writes the identical layout under a different name — real files: Lavf54.31, Lavf55.19.
            Assert.AreEqual(2257, Mp3Decoder.OsuGaplessTrimFor(TaggedFrame("Info", "Lavf55.19", 576), true));
            Assert.AreEqual(2257, Mp3Decoder.OsuGaplessTrimFor(TaggedFrame("Info", "Lavc58.13", 576), true));
        }

        [Test]
        public void OsuTrim_HeaderFrameOnlyCountsWhenTheDecoderKeptIt()
        {
            // libmad decodes the header frame like any other → it's still in front of the audio → subtract it.
            // NLayer (the fallback) skips it exactly like BASS → subtracting again would eat a real 26 ms of music.
            var f = TaggedFrame("Info", "LAME3.99r", 576);
            Assert.AreEqual(1152 + 576 + 529, Mp3Decoder.OsuGaplessTrimFor(f, true));
            Assert.AreEqual(576 + 529, Mp3Decoder.OsuGaplessTrimFor(f, false));
            // No header frame → nothing to skip either way, so the flag changes nothing.
            var plain = Frame(0xFB, null);
            Assert.AreEqual(529, Mp3Decoder.OsuGaplessTrimFor(plain, true));
            Assert.AreEqual(529, Mp3Decoder.OsuGaplessTrimFor(plain, false));
        }

        [Test]
        public void OsuTrim_UsesTheRealFrameSizeForMpeg2()
        {
            // MPEG-2/2.5 Layer III frames are 576 samples, not 1152 — the header frame is that much shorter.
            Assert.AreEqual(576 + 576 + 529, Mp3Decoder.OsuGaplessTrimFor(TaggedFrame("Info", "LAME3.99r", 576, 0xF3), true));
        }

        [Test]
        public void OsuTrim_FallsBackToTheDecoderDelayWhenTheHeaderCantBeRead()
        {
            // Couldn't read the file → do what BASS does with a file carrying no delay info, not nothing at all.
            Assert.AreEqual(529, Mp3Decoder.OsuGaplessTrimFor(null, true));
            Assert.AreEqual(Mp3Decoder.BassDecoderDelay, Mp3Decoder.OsuGaplessTrimFor(new byte[64], true));
        }

        [Test]
        public void EncoderDelay_IsIgnoredOutsideAHeaderFrame()
        {
            // Untagged files can still carry a bare "LAME" string in ancillary data followed by 0x55 filler, which
            // reads as delay 1365. Sayonara Trip and Isetsu Higanbana both do — honouring it would shove those songs
            // 31 ms off for no reason, and bass.dll trims only 529 from them.
            var b = Frame(0xFB, null, 256);
            Put(b, 120, "LAME");
            for (int i = 124; i < 150; i++) b[i] = 0x55;
            Assert.AreEqual(0, Mp3Decoder.LameEncoderDelay(b));
            Assert.AreEqual(529, Mp3Decoder.OsuGaplessTrimFor(b, true));
            // The same string INSIDE a header frame is the real tag and is read.
            Assert.AreEqual(576, Mp3Decoder.LameEncoderDelay(TaggedFrame("Info", "LAME3.99r", 576)));
        }

        // 表頭幀常常用很低的位元率 —— Dreamin 的只有 182 B(56 kbps)、Violet Soul 417 B(128 kbps)。
        // 用固定 ~1 KB 的窗去找 encoder 標籤會跨進第 2 幀的**真音訊**,那裡的位元組剛好拼成 "LAME",
        // 後面接 0x55 填充 → 讀成 delay 1365,兩首各被推掉 ~18 ms。窗一定要收在真正的幀邊界。
        //
        // 這裡直接照真檔的樣子組:56 kbps@44.1kHz(144×56000/44100 = 182 B 一幀)。
        private static byte[] ShortHeaderFrameStream(string encoderInFrame1, int delay, bool fakeLameInFrame2)
        {
            const int Len = 182;
            var b = new byte[Len * 3];
            for (int f = 0; f < 3; f++)
            {
                int at = f * Len;
                b[at] = 0xFF; b[at + 1] = 0xFB;      // MPEG-1 Layer III
                b[at + 2] = 0x40;                    // bitrate idx 4 = 56 kbps, samplerate idx 0 = 44.1 kHz, no padding
                b[at + 3] = 0x44;
            }
            Put(b, 36, "Info");                      // header frame marker
            if (encoderInFrame1 != null)
            {
                const int at = 152;                  // real files put it right after the tag; +24 still fits in 182
                Put(b, at, encoderInFrame1);
                b[at + 21] = (byte)(delay >> 4);
                b[at + 22] = (byte)((delay & 0xF) << 4);
            }
            if (fakeLameInFrame2)
            {
                Put(b, Len + 36, "LAME");            // coincidence inside real audio
                for (int i = Len + 40; i < Len + 70; i++) b[i] = 0x55;   // → reads as delay 1365
            }
            return b;
        }

        [Test]
        public void EncoderDelay_StopsAtTheRealFrameBoundary_NotAFixedWindow()
        {
            // Dreamin: 182 B header frame carrying Lavf54.31/576, with a chance "LAME" in frame 2.
            var dreamin = ShortHeaderFrameStream("Lavf54.31", 576, fakeLameInFrame2: true);
            Assert.AreEqual(576, Mp3Decoder.LameEncoderDelay(dreamin), "第 2 幀那個假 LAME 不能蓋掉幀內真的 Lavf");
            Assert.AreEqual(1152 + 576 + 529, Mp3Decoder.OsuGaplessTrimFor(dreamin, true));

            // Violet Soul: header frame with NO encoder tag at all → delay 0, even though frame 2 has a fake "LAME".
            var violet = ShortHeaderFrameStream(null, 0, fakeLameInFrame2: true);
            Assert.AreEqual(0, Mp3Decoder.LameEncoderDelay(violet));
            Assert.AreEqual(1152 + 0 + 529, Mp3Decoder.OsuGaplessTrimFor(violet, true));   // 1681, matches bass.dll
        }

        [Test]
        public void VbrTagOffset_FindsEitherSpellingInTheFirstFrameOnly()
        {
            Assert.AreEqual(36, Mp3Decoder.VbrTagOffset(Frame(0xFB, "Info")));
            Assert.AreEqual(36, Mp3Decoder.VbrTagOffset(Frame(0xFB, "Xing")));
            Assert.AreEqual(-1, Mp3Decoder.VbrTagOffset(Frame(0xFB, null)));
            Assert.AreEqual(-1, Mp3Decoder.VbrTagOffset(null));
            // "Info" as real audio data far past the first frame is not a header tag.
            var late = new byte[2000];
            late[0] = 0xFF; late[1] = 0xFB;
            Put(late, 1200, "Info");
            Assert.AreEqual(-1, Mp3Decoder.VbrTagOffset(late));
        }

        // ---- frame table (drives the timeline-exact re-decode) ----

        // MPEG-1 Layer III, 320 kbps, 48 kHz, no padding → 144 × 320000 / 48000 = 960 B per frame.
        private const int FrameLen = 960;

        private static byte[] Stream(int frames, int id3 = 0, string tagInFirst = null)
        {
            var b = new byte[id3 + frames * FrameLen];
            if (id3 >= 10)
            {
                Put(b, 0, "ID3");
                int n = id3 - 10;                                  // syncsafe size (7 bits per byte)
                b[6] = (byte)((n >> 21) & 0x7F); b[7] = (byte)((n >> 14) & 0x7F);
                b[8] = (byte)((n >> 7) & 0x7F);  b[9] = (byte)(n & 0x7F);
            }
            for (int f = 0; f < frames; f++)
            {
                int at = id3 + f * FrameLen;
                b[at] = 0xFF; b[at + 1] = 0xFB; b[at + 2] = 0xE4; b[at + 3] = 0x44;
            }
            if (tagInFirst != null) Put(b, id3 + 36, tagInFirst);
            return b;
        }

        [Test]
        public void FrameTable_WalksEveryFrameAndEndsWithASentinel()
        {
            int spf;
            var t = Mp3Decoder.FrameTable(Stream(4), out spf);
            Assert.AreEqual(1152, spf);                            // MPEG-1 Layer III
            Assert.AreEqual(5, t.Count);                           // 4 frames + end-of-last-frame sentinel
            for (int i = 0; i < 5; i++) Assert.AreEqual(i * FrameLen, t[i], "frame " + i);
        }

        [Test]
        public void FrameTable_SkipsAnId3Tag()
        {
            int spf;
            var t = Mp3Decoder.FrameTable(Stream(3, id3: 4193), out spf);
            Assert.AreEqual(4, t.Count);
            Assert.AreEqual(4193, t[0]);                           // audio starts after the tag, not at byte 0
            Assert.AreEqual(4193 + 3 * FrameLen, t[3]);
        }

        [Test]
        public void FrameTable_UsesTheLsfCoefficientForMpeg2()
        {
            // 幀長係數跟著「一幀幾個 sample」走:MPEG-1 Layer III 一幀 1152 sample → 144;MPEG-2/2.5(LSF)
            // 一幀只有 576 → 72。這裡照真檔組:MPEG-2、80 kbps、22.05 kHz → 72×80000/22050 = 261 B。
            // 用 MPEG-1 的 144 會算成 522,幀表整個錯位,而且 FirstFrameEnd 的搜尋窗會開成兩倍 —— 第 2 幀裡
            // 巧合的 "LAME" 就被當成真的 gapless 標籤讀進來(整首歌位移 13 ms)。
            const int Len = 261;
            var b = new byte[Len * 3];
            for (int f = 0; f < 3; f++)
            {
                int at = f * Len;
                b[at] = 0xFF; b[at + 1] = 0xF3;   // sync + MPEG-2 + Layer III
                b[at + 2] = 0x90;                 // bitrate idx 9 = 80 kbps, samplerate idx 0 = 22050, no padding
                b[at + 3] = 0x44;
            }
            int spf;
            var t = Mp3Decoder.FrameTable(b, out spf);
            Assert.AreEqual(576, spf);                             // MPEG-2 Layer III
            Assert.AreEqual(4, t.Count, "3 幀 + 哨兵;用 144 只會走到 1 幀就撞底");
            Assert.AreEqual(0, t[0]);
            Assert.AreEqual(Len, t[1], "LSF 幀長係數是 72,不是 144(用 144 會算成 522)");
            Assert.AreEqual(2 * Len, t[2]);
            Assert.AreEqual(3 * Len, t[3]);
        }

        [Test]
        public void FrameTable_StepsOverAFakeSync()
        {
            // 0xFF 0xEA = frame sync bits but version 1 (reserved) → not a frame; the real one after it still lands.
            var b = new byte[8 + 2 * FrameLen];
            b[0] = 0xFF; b[1] = 0xEA;
            for (int f = 0; f < 2; f++)
            {
                int at = 8 + f * FrameLen;
                b[at] = 0xFF; b[at + 1] = 0xFB; b[at + 2] = 0xE4; b[at + 3] = 0x44;
            }
            int spf;
            var t = Mp3Decoder.FrameTable(b, out spf);
            Assert.AreEqual(3, t.Count);
            Assert.AreEqual(8, t[0]);
        }

        [Test]
        public void FrameTable_EmptyInputStillReturnsASentinel()
        {
            int spf;
            Assert.AreEqual(1, Mp3Decoder.FrameTable(null, out spf).Count);
            Assert.AreEqual(0, spf);
            Assert.AreEqual(1, Mp3Decoder.FrameTable(new byte[0], out spf).Count);
        }

        [Test]
        public void VbrTagFrame_TrueForXingOrInfoInTheFirstFrameOnly()
        {
            // Either tag means frame 0 is a header, not audio — NLayer emits no samples for it, so the frame→sample
            // accounting of the re-decode has to skip it. (HasInfoHeaderFrame is the narrower "Info only" question.)
            int spf;
            foreach (var tag in new[] { "Xing", "Info" })
            {
                var d = Stream(3, tagInFirst: tag);
                Assert.IsTrue(Mp3Decoder.HasVbrTagFrame(d, Mp3Decoder.FrameTable(d, out spf)), tag);
            }
            var plain = Stream(3);
            Assert.IsFalse(Mp3Decoder.HasVbrTagFrame(plain, Mp3Decoder.FrameTable(plain, out spf)));
            Assert.IsFalse(Mp3Decoder.HasVbrTagFrame(null, null));
        }

        [Test]
        public void VbrTagFrame_IgnoresATagInALaterFrame()
        {
            // "Xing" appearing as audio data inside frame 1 must not make frame 0 look like a header frame —
            // that would drop a real frame's worth of samples (26 ms) off the front of every such file.
            var d = Stream(3);
            Put(d, FrameLen + 36, "Xing");
            int spf;
            Assert.IsFalse(Mp3Decoder.HasVbrTagFrame(d, Mp3Decoder.FrameTable(d, out spf)));
        }

        [Test]
        public void InfoHeaderFrame_OnlyLooksInsideTheFirstFrame()
        {
            // "Info" as 4 bytes of real audio data 1200 B in is NOT the header tag → must not be treated as one.
            var b = new byte[2000];
            b[0] = 0xFF; b[1] = 0xFB;
            Put(b, 1200, "Info");
            Assert.IsFalse(Mp3Decoder.HasInfoHeaderFrame(b));
            // The genuine tag near the frame start is still found.
            Put(b, 36, "Info");
            Assert.IsTrue(Mp3Decoder.HasInfoHeaderFrame(b));
        }

        // ---- 漏幀對帳 ----
        //
        // 「解出來的樣本比 mp3.Length 少」**不能**當判準:mp3.Length 會多報(它把從不輸出成 PCM 的
        // Xing/Info 表頭幀和尾端 LAME padding 也算進去),所以一次完美的解碼本來就會少約 1 幀。
        // 而 NLayer 真的漏解一幀時,樣本數上長得一模一樣 —— 唯一能分辨的是去數檔案裡實際有幾個 MPEG 幀。

        [Test]
        public void ShortOfDeclaredLength_IsOnlyACheapPreScreen()
        {
            // 少了 → 值得再掃一次幀表(但還不代表漏幀)
            Assert.IsTrue(Mp3Decoder.IsShortOfDeclaredLength(5_340_672, 5_339_520));
            // 沒少 → 一定沒漏,連掃都不必掃
            Assert.IsFalse(Mp3Decoder.IsShortOfDeclaredLength(1_000_000, 1_000_000));
            Assert.IsFalse(Mp3Decoder.IsShortOfDeclaredLength(1_000_000, 1_000_001));
            // 長度不明 → 不做事
            Assert.IsFalse(Mp3Decoder.IsShortOfDeclaredLength(0, 0));
            Assert.IsFalse(Mp3Decoder.IsShortOfDeclaredLength(-1, 100));
        }

        [Test]
        public void DroppedFrames_IsZeroWhenTheLengthMerelyOverReports()
        {
            // Amanojaku.mp3:mp3.Length 說 5,340,672/ch,乾淨的直解吐 5,339,520(剛好少一個 1152 幀),
            // 但檔案裡的音訊幀數就是 4635 —— 一幀都沒漏。重解它會讓 DecodeSliced 打出 70 個靜音洞(漏封包 爆)。
            Assert.AreEqual(0, Mp3Decoder.DroppedFrames(4635, 5_339_520, 1152));
            Assert.AreEqual(4635, 5_339_520 / 1152);
        }

        [Test]
        public void DroppedFrames_CountsAGenuineDrop()
        {
            // engine[Blue]:檔案有 4859 個音訊幀,NLayer 直解只吐 4858 —— 漏掉的第 1434 幀讓 37.5 秒之後
            // 整首音樂提前 26 ms。樣本數的短少量和上面的 Amanojaku 一模一樣,只有幀表分得出來。
            Assert.AreEqual(1, Mp3Decoder.DroppedFrames(4859, 4858 * 1152, 1152));
            // 累積型的漏幀(擬態ごっこ 3 幀 = 72 ms)
            Assert.AreEqual(3, Mp3Decoder.DroppedFrames(1000, 997 * 1152, 1152));
            // MPEG-2/2.5 一幀 576 取樣
            Assert.AreEqual(2, Mp3Decoder.DroppedFrames(500, 498 * 576, 576));
        }

        [Test]
        public void DroppedFrames_IsZeroWhenTheFrameTableIsUnreadable()
        {
            Assert.AreEqual(0, Mp3Decoder.DroppedFrames(0, 1_000_000, 1152));
            Assert.AreEqual(0, Mp3Decoder.DroppedFrames(100, 1_000_000, 0));
            Assert.AreEqual(0, Mp3Decoder.DroppedFrames(100, -1, 1152));
        }

        [Test]
        public void FileAudioFrameCount_SkipsTheVbrTagFrame()
        {
            Assert.AreEqual(0, Mp3Decoder.FileAudioFrameCount(null, out int spf));
            Assert.AreEqual(0, spf);
            Assert.AreEqual(0, Mp3Decoder.FileAudioFrameCount(new byte[0], out _));
        }

        // 這一題是「歌會不會越播越提早」的總驗收:**解出來的幀數不准比檔案裡的音訊幀數少**。
        // 少一幀,那之後整首就提前 26 ms 並一路帶到歌尾(engine[Blue] 37.5 秒起、Amanojaku 24 秒起
        // 就是這樣漂掉的 —— NLayer 的高階 MpegFile 會靜默跳過 bit reservoir 指不到資料的幀)。
        // libmad 對那種幀是 `ret = 0; /* pretend success */`(RageSoundReader_MP3.cpp:429),照樣送一幀。
        //
        // 上限則是「總幀數」(含 Xing/Info 表頭幀):表頭幀要不要輸出,StepMania 兩種都有 ——
        // Xing 會被 handle_first_frame 跳過,Info 則刻意留著當一幀靜音送出去(為了對上 DWI/BASS 的 sync,
        // 見 handle_first_frame 裡 `if( type == INFO ) return false;` 的註解)。所以兩端都要容許。
        [Test]
        public void Decode_EmitsOneFrameForEveryFrameInTheFile()
        {
            string path = System.IO.Path.Combine(Application.streamingAssetsPath, "Step1", "Bassdrop.mp3");
            if (!System.IO.File.Exists(path)) Assert.Ignore("找不到 StreamingAssets/Step1/Bassdrop.mp3");
            var bytes = System.IO.File.ReadAllBytes(path);
            int audioFrames = Mp3Decoder.FileAudioFrameCount(bytes, out int spf);   // 扣掉 Xing/Info 表頭幀
            int totalFrames = Mp3Decoder.FrameTable(bytes, out _).Count - 1;        // 含表頭幀(最後一筆是哨兵)
            Assert.Greater(audioFrames, 0, "測試檔要讀得出幀表");

            var pcm = Mp3Decoder.Decode(path, Mp3Decoder.Mp3Sync.Osu);   // Osu = 不補前導幀,幀數才好直接比
            Assert.IsNotNull(pcm);
            // 把 gapless 剪掉的加回來 —— 剪多少是看檔案算的(這首是 Info + Lavf delay 576 → 2257)
            int perChannel = pcm.Samples.Length / pcm.Channels + Mp3Decoder.OsuGaplessTrimForFile(path, true);
            int decoded = perChannel / spf;
            Assert.GreaterOrEqual(decoded, audioFrames,
                $"解出 {decoded} 幀但檔案有 {audioFrames} 個音訊幀 —— 少一幀,那之後整首就提前一幀(26 ms)");
            Assert.LessOrEqual(decoded, totalFrames,
                $"解出 {decoded} 幀但檔案總共才 {totalFrames} 幀(含表頭幀)—— 憑空多出來的幀會讓整首延後");
        }

        // ---- 真實檔案的 ground truth(回歸用)----
        //
        // StreamingAssets/Step1/Bassdrop.mp3 就是玩家實測那首 Camellia - Bassdrop Freaks(同一份 4,808,920 B)。
        // 它的檔頭:Info 表頭幀 + Lavf55.19(ffmpeg)+ encoder delay 576 → BASS 的第 0 個樣本落在 libmad 的第
        // 1152+576+529 = 2257 個樣本。這個 2257 不是算出來的期望值,是拿 osu!stable 自己的 bass.dll(2.4.15.2)
        // 解同一個檔、跟 libmad 逐樣本互相關量出來的(相對誤差 ~1e-13)。
        //
        // 這一組是「音樂會不會整首偏掉」的總驗收:錯一個表頭幀就是 26 ms,玩家得逐首手調 offset 才蓋得掉。

        private static string TestSongPath()
            => System.IO.Path.Combine(Application.streamingAssetsPath, "Step1", "Bassdrop.mp3");

        [Test]
        public void OsuTrim_MatchesBassOnTheShippedSong()
        {
            string path = TestSongPath();
            if (!System.IO.File.Exists(path)) Assert.Ignore("找不到 StreamingAssets/Step1/Bassdrop.mp3");
            // libmad 留著表頭幀 → 連它一起剪。
            Assert.AreEqual(2257, Mp3Decoder.OsuGaplessTrimForFile(path, true));
            // NLayer 已經跳掉表頭幀 → 只剪 priming,不然會多吃掉一幀真音樂。
            Assert.AreEqual(1105, Mp3Decoder.OsuGaplessTrimForFile(path, false));
            // 檔案讀不到也不能回 0(那等於完全不做 gapless);至少是 BASS 的解碼延遲。
            Assert.AreEqual(Mp3Decoder.BassDecoderDelay,
                            Mp3Decoder.OsuGaplessTrimForFile(path + ".nope", true));
        }

        [Test]
        public void Decode_OsuStartsExactlyTheBassTrimAheadOfStepMania()
        {
            string path = TestSongPath();
            if (!System.IO.File.Exists(path)) Assert.Ignore("找不到 StreamingAssets/Step1/Bassdrop.mp3");
            if (!MadDecoder.Available) Assert.Ignore("sdomad.dll 載不到 → 走 NLayer fallback,前導幀規則不同");

            var sm = Mp3Decoder.Decode(path, Mp3Decoder.Mp3Sync.StepMania);   // libmad 原樣(不剪也不補)
            var osu = Mp3Decoder.Decode(path, Mp3Decoder.Mp3Sync.Osu);
            Assert.IsNotNull(sm); Assert.IsNotNull(osu);
            Assert.AreEqual(sm.Channels, osu.Channels);

            int trim = 2257 * sm.Channels;
            Assert.AreEqual(sm.Samples.Length - trim, osu.Samples.Length, "osu 版必須正好短了 BASS 那一段");
            // 而且要是「從前面剪掉」,不是從尾巴 —— 剪錯邊長度一樣但整首都對不上。
            for (int i = 0; i < 200000 && i < osu.Samples.Length; i += 977)
                Assert.AreEqual(sm.Samples[i + trim], osu.Samples[i], 1e-6f, $"sample {i}");
        }

        // ---- overload protection (hot masters like Amanojaku.mp3 decode to > ±1) ----

        [Test]
        public void NormalizeIfHot_ScalesAnOverloadedBufferDownToTarget_PreservingShape()
        {
            // Amanojaku.mp3 decodes to a sample peak of ~1.29 (+2.2 dBFS). The old per-sample clamp flattened those
            // peaks into a square wave (harsh 爆音); a plain gain fixes it with ZERO waveform distortion.
            var d = new[] { 1.29f, -0.645f, 0f, 0.129f };   // peak 1.29
            Mp3Decoder.NormalizeIfHot(d, d.Length);          // default target 0.98
            Assert.AreEqual(0.98f, d[0], 1e-5f);             // loudest sample lands exactly on target
            float g = 0.98f / 1.29f;
            Assert.AreEqual(-0.645f * g, d[1], 1e-5f);       // everything scaled by the SAME gain → shape intact
            Assert.AreEqual(0f, d[2], 1e-6f);
            Assert.AreEqual(0.129f * g, d[3], 1e-6f);
        }

        [Test]
        public void NormalizeIfHot_LeavesAlreadySafeAudioUntouched()
        {
            // Peak ≤ target → bit-transparent, no gain applied (don't quietly turn down normal songs).
            var d = new[] { 0.5f, -0.98f, 0.1f };
            var copy = (float[])d.Clone();
            Mp3Decoder.NormalizeIfHot(d, d.Length);
            CollectionAssert.AreEqual(copy, d);
        }

        [Test]
        public void NormalizeIfHot_OnlyLooksAtTheUsedPrefix_AndHandlesEmptyOrNull()
        {
            // len < array length: the tail is scratch space and must not count toward the peak.
            var d = new[] { 1.2f, -0.6f, 9f /* garbage past len */ };
            Mp3Decoder.NormalizeIfHot(d, 2);
            Assert.AreEqual(0.98f, d[0], 1e-5f);             // scaled to the 1.2 peak, not the 9
            Assert.AreEqual(9f, d[2], 0f);                   // untouched
            Assert.DoesNotThrow(() => Mp3Decoder.NormalizeIfHot(null, 4));
            Assert.DoesNotThrow(() => Mp3Decoder.NormalizeIfHot(new float[0], 0));
        }
    }
}
