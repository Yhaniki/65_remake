using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// Mp3Decoder timing — making imported osu!/StepMania (mp3) charts line up at global-offset 0 like they do in their
    /// home game. Both games decode through MAD (StepMania) / BASS (osu, DWI); those keep the LAME encoder-delay priming
    /// (NO gapless trim) and, for a CBR "Info" header frame they can't recognise, EMIT it as ~26 ms of silence rather
    /// than skipping it (RageSoundReader_MP3.cpp: <c>if(type==INFO) return false</c>). NLayer instead skips that frame,
    /// so our decode starts one frame early — <see cref="Mp3Decoder.ApplyBassInfoFrame"/> re-inserts it. These cover the
    /// two pure pieces that decide the fix: is the header an Info tag, and how big is one frame.
    /// Measured on "Be Crazy For Me.mp3" (MPEG-1 L3, Info tag): NLayer-raw onset 0.504 s + 1 frame (1152/44100 ≈ 26 ms)
    /// = 0.530 s ≈ chart beat 4 at 0.537 s; the old gapless trim instead put it at 0.479 s (≈ 51 ms early).
    /// </summary>
    public class Mp3GaplessTests
    {
        private static void Put(byte[] b, int at, string s) { for (int i = 0; i < s.Length; i++) b[at + i] = (byte)s[i]; }

        // First frame header + a Xing/Info tag at the usual offset (after MPEG1-stereo side info).
        private static byte[] Frame(byte hdr1, string vbrTag, int size = 128)
        {
            var b = new byte[size];
            b[0] = 0xFF; b[1] = hdr1;         // frame sync + version/layer
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
            // osu/BASS drops 576+529 = 1105 frames of priming; verified to align SDO Pack9's osu charts at offset 0.
            Assert.AreEqual(1105, Mp3Decoder.OsuGaplessTrim);
            // stereo: 1105 frames = 2210 interleaved samples removed.
            Assert.AreEqual(100000 - 2210, Mp3Decoder.OsuGaplessKeptLength(100000, 2));
            Assert.AreEqual(100000 - 1105, Mp3Decoder.OsuGaplessKeptLength(100000, 1));   // mono
            // a buffer shorter than the priming is emptied, never negative.
            Assert.AreEqual(0, Mp3Decoder.OsuGaplessKeptLength(1000, 2));
            Assert.AreEqual(0, Mp3Decoder.OsuGaplessKeptLength(0, 2));
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
            int perChannel = pcm.Samples.Length / pcm.Channels + Mp3Decoder.OsuGaplessTrim;   // 把 gapless 剪掉的加回來
            int decoded = perChannel / spf;
            Assert.GreaterOrEqual(decoded, audioFrames,
                $"解出 {decoded} 幀但檔案有 {audioFrames} 個音訊幀 —— 少一幀,那之後整首就提前一幀(26 ms)");
            Assert.LessOrEqual(decoded, totalFrames,
                $"解出 {decoded} 幀但檔案總共才 {totalFrames} 幀(含表頭幀)—— 憑空多出來的幀會讓整首延後");
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
