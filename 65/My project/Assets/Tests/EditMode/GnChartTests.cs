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
