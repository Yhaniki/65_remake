using System;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// Locks the .gn stepFrameType -> lane mapping. The original exe computes lane = frameType - 2
    /// (decompiled 024_note_0048ba80.c), i.e. 2=Left(0) 3=Down(1) 4=Up(2) 5=Right(3). A wrong doc table
    /// once had Down/Up swapped (3=Up,4=Down), so Down/Up notes rendered reversed — this guards against that.
    ///
    /// The chart is built in plaintext: GnChart's LCG decrypt is a no-op when the seed is 0, and both seeds
    /// (header @0x0c, and block1[4] @0x24) are left 0, so the body is parsed verbatim — no encryption needed.
    /// </summary>
    public class GnChartTests
    {
        private const int BodyBase = 0x54;   // body starts at raw offset 0x54

        [Test]
        public void StepFrameType_MapsTo_LaneSequentially_DownAndUpNotSwapped()
        {
            // body: 300-byte header region + four single-note StepFrames (Left/Down/Up/Right at measures 0..3).
            int bodyLen = 348;
            var raw = new byte[BodyBase + bodyLen];

            // ddrm container magic @0 (0x6D726464) so Decrypt takes the DDRM branch and the body at 0x54 is parsed;
            // seeds (header @0x0c, block1[4] @0x24) stay 0 -> the LCG decrypt is a no-op and the body is verbatim.
            raw[0] = (byte)'d'; raw[1] = (byte)'d'; raw[2] = (byte)'r'; raw[3] = (byte)'m';
            PutFloat(raw, 16, 120f);   // bpm @ body+16 -> beat*500 ms

            int o = 300;
            o = WriteTap(raw, o, meas: 0, frameType: 2);   // Left  -> lane 0 @ 0ms
            o = WriteTap(raw, o, meas: 1, frameType: 3);   // Down  -> lane 1 @ 2000ms
            o = WriteTap(raw, o, meas: 2, frameType: 4);   // Up    -> lane 2 @ 4000ms
            o = WriteTap(raw, o, meas: 3, frameType: 5);   // Right -> lane 3 @ 6000ms
            Assert.AreEqual(bodyLen, o, "frames must exactly fill the body");

            var map = GnChart.Load(raw, difficulty: 0);

            Assert.AreEqual(4, map.HitObjects.Count);
            // sorted by time: Left, Down, Up, Right
            Assert.AreEqual(0, map.HitObjects[0].Lane, "frameType 2 -> Left(0)");
            Assert.AreEqual(1, map.HitObjects[1].Lane, "frameType 3 -> Down(1)");   // not Up
            Assert.AreEqual(2, map.HitObjects[2].Lane, "frameType 4 -> Up(2)");     // not Down
            Assert.AreEqual(3, map.HitObjects[3].Lane, "frameType 5 -> Right(3)");
            // times confirm each assertion is tied to the intended frame (measure*4 beats * 500ms)
            Assert.AreEqual(2000, map.HitObjects[1].StartTimeMs);
            Assert.AreEqual(4000, map.HitObjects[2].StartTimeMs);
        }

        /// <summary>
        /// Locks the rewu (熱舞 Online) whole-file-LCG decrypt branch: the ENTIRE .gn is LCG-encrypted
        /// with a seed that lives only in the key table (not the file). GnChart must, when it finds no
        /// SDOM inner offset, try the supplied seed pool at offset 0, recognise the decrypted StepFile
        /// ('gn'\0\0 @4, address_easy==300 @284) and parse it. Guards the branch added for the online set.
        /// </summary>
        [Test]
        public void RewuWholeFileLcg_DecryptsViaSeedPool()
        {
            // plaintext StepFile at offset 0: 300-byte header + one Left tap frame.
            int bodyLen = 312;
            var body = new byte[bodyLen];
            body[4] = (byte)'g'; body[5] = (byte)'n';        // fileType 'gn'\0\0 @4
            PutFloatAbs(body, 16, 120f);                      // bpm @16 -> beat*500ms
            PutU32Abs(body, 284, 300);                        // address_easy == 300
            PutU32Abs(body, 288, (uint)bodyLen);              // address_normal (= easy region end)
            PutU32Abs(body, 292, (uint)bodyLen);              // address_hard
            PutU32Abs(body, 296, (uint)bodyLen);              // address_end
            int o = 300;
            PutU32Abs(body, o, 0);                            // measure 0
            PutU16Abs(body, o + 4, 2);                        // frameType 2 = Left(0)
            PutU16Abs(body, o + 6, 1);                        // interval (1 slot)
            PutU16Abs(body, o + 8, 1);                        // slot u0 != 0 -> a note
            body[o + 8 + 3] = 0;                              // note_type 0 = tap

            const uint seed = 0x00ABCDEFu;                    // non-zero so LCG actually runs
            var enc = (byte[])body.Clone();
            LcgEncrypt(seed, enc);
            Assert.AreNotEqual((byte)'g', enc[4], "encryption must scramble the fileType so it isn't mistaken for plain/SDOM");

            var map = GnChart.Load(enc, difficulty: 0, sdomSeeds: new[] { seed });

            Assert.AreEqual(1, map.HitObjects.Count, "rewu chart should decrypt+parse to the single tap");
            Assert.AreEqual(0, map.HitObjects[0].Lane, "frameType 2 -> Left(0)");
            Assert.AreEqual(0, map.HitObjects[0].StartTimeMs);
        }

        /// <summary>
        /// Locks the music-start anchor: it is the FIRST type-10 (音樂起止) slot carrying value 1000 — the marker
        /// the exe watches for (NewNote_TriggerNoteSound_0048e9c0 sets its music-start flag when the play head hits
        /// (slot &amp; 0xfff)==1000). Here bpm=120 (beat = 500ms) and the marker sits at measure 1 (beat 4), so the
        /// audio+dancer should be delayed 2000ms. Guards against the old behaviour, which used the type-9 小節線.
        /// </summary>
        [Test]
        public void MusicStart_AnchorsToType10Marker1000()
        {
            var body = PlainStep(120f,
                (meas: 1, ft: 10, u0: 1000),   // 音樂起止 start marker @ beat 4 -> 2000ms
                (meas: 2, ft: 2, u0: 1));       // a Left note after it
            var map = GnChart.Load(body, difficulty: 0);
            Assert.AreEqual(2000.0, map.MusicStartOffsetMs, 1e-6, "music starts at the type-10 value-1000 marker (beat 4)");
        }

        /// <summary>Charts with no type-10 marker keep the previous behaviour: anchor on the first type-9 小節線.</summary>
        [Test]
        public void MusicStart_FallsBackToType9_WhenNoType10()
        {
            var body = PlainStep(120f,
                (meas: 1, ft: 9, u0: 2),        // bar line @ beat 4
                (meas: 2, ft: 2, u0: 1));
            var map = GnChart.Load(body, difficulty: 0);
            Assert.AreEqual(2000.0, map.MusicStartOffsetMs, 1e-6, "no type-10 -> fall back to the type-9 bar line");
        }

        /// <summary>
        /// type-10 (音樂起止) doubles as the music-END marker; some charts carry only an end marker sitting AFTER
        /// the first note. That must NOT delay the music to mid-song — it is rejected and the anchor falls back
        /// (here no type-9 either -> 0, i.e. music from beat 0), matching the old timing for those charts.
        /// </summary>
        [Test]
        public void MusicStart_IgnoresType10EndMarkerAfterFirstNote()
        {
            var body = PlainStep(120f,
                (meas: 1, ft: 2, u0: 1),        // first note @ beat 4
                (meas: 60, ft: 10, u0: 1000));  // lone end marker far past it
            var map = GnChart.Load(body, difficulty: 0);
            Assert.AreEqual(0.0, map.MusicStartOffsetMs, 1e-6, "an end-only type-10 marker after the first note is ignored");
        }

        /// <summary>When both are present the type-10 marker wins over the type-9 bar line.</summary>
        [Test]
        public void MusicStart_Type10WinsOverType9()
        {
            var body = PlainStep(120f,
                (meas: 1, ft: 9, u0: 2),
                (meas: 1, ft: 10, u0: 1000),
                (meas: 2, ft: 2, u0: 1));
            var map = GnChart.Load(body, difficulty: 0);
            Assert.AreEqual(2000.0, map.MusicStartOffsetMs, 1e-6);
            Assert.AreEqual(1, map.HitObjects.Count, "marker frames must not be counted as notes");
        }

        /// <summary>
        /// Build a PLAINTEXT StepFile at offset 0 ('gn'@4, address_easy==300 -> GnChart's plain branch, no
        /// decrypt). Each frame is one slot (interval 1): (measurement, stepFrameType, u0). u1/nt are 0.
        /// </summary>
        private static byte[] PlainStep(float bpm, params (uint meas, ushort ft, ushort u0)[] frames)
        {
            int bodyLen = 300 + frames.Length * 12;
            var b = new byte[bodyLen];
            b[4] = (byte)'g'; b[5] = (byte)'n';                 // fileType 'gn' @4
            PutFloatAbs(b, 16, bpm);                            // bpm @16
            PutU32Abs(b, 284, 300);                             // address_easy == 300 (plain-branch trigger)
            PutU32Abs(b, 288, (uint)bodyLen);                   // address_normal / hard / end = region end
            PutU32Abs(b, 292, (uint)bodyLen);
            PutU32Abs(b, 296, (uint)bodyLen);
            int o = 300;
            foreach (var (meas, ft, u0) in frames)
            {
                PutU32Abs(b, o, meas); PutU16Abs(b, o + 4, ft); PutU16Abs(b, o + 6, 1);
                PutU16Abs(b, o + 8, u0); b[o + 10] = 0; b[o + 11] = 0;
                o += 12;
            }
            return b;
        }

        /// <summary>Whole-file LCG encrypt (inverse of GnChart's decrypt): st*=0x3D09; out = in + (st>>16).</summary>
        private static void LcgEncrypt(uint seed, byte[] buf)
        {
            uint st = seed;
            for (int i = 0; i < buf.Length; i++) { st *= 0x3D09u; buf[i] = (byte)(buf[i] + (byte)(st >> 16)); }
        }

        private static void PutU32Abs(byte[] b, int o, uint v)
        { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        private static void PutU16Abs(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        private static void PutFloatAbs(byte[] b, int o, float v) => Array.Copy(BitConverter.GetBytes(v), 0, b, o, 4);

        // one StepFrame carrying a single tap: meas(u32) ft(i16) interval=1(u16) + one 4-byte slot (u0=1, note_type=0).
        private static int WriteTap(byte[] raw, int bodyOff, uint meas, ushort frameType)
        {
            PutU32(raw, bodyOff, meas);
            PutU16(raw, bodyOff + 4, frameType);
            PutU16(raw, bodyOff + 6, 1);          // interval (slot count)
            PutU16(raw, bodyOff + 8, 1);          // slot u0 != 0 -> a note exists
            raw[BodyBase + bodyOff + 8 + 3] = 0;  // note_type 0 = tap
            return bodyOff + 8 + 4;
        }

        private static void PutU32(byte[] raw, int bodyOff, uint v)
        {
            int o = BodyBase + bodyOff;
            raw[o] = (byte)v; raw[o + 1] = (byte)(v >> 8); raw[o + 2] = (byte)(v >> 16); raw[o + 3] = (byte)(v >> 24);
        }

        private static void PutU16(byte[] raw, int bodyOff, ushort v)
        {
            int o = BodyBase + bodyOff;
            raw[o] = (byte)v; raw[o + 1] = (byte)(v >> 8);
        }

        private static void PutFloat(byte[] raw, int bodyOff, float v)
            => Array.Copy(BitConverter.GetBytes(v), 0, raw, BodyBase + bodyOff, 4);
    }
}
