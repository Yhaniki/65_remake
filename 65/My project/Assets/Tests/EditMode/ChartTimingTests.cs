using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// GN multi-BPM timing: type-1 BPM-change frames must shift note times (integrate beat→ms across BPM
    /// segments) AND emit one scroll timing point per segment. Single-BPM charts must time exactly as before.
    /// </summary>
    public class GnChartBpmTests
    {
        // ---- minimal plaintext StepFile builder (no ddrm/sdom wrapper → GnChart's "plain" path) ----
        private static byte[] Tap() => new byte[] { 1, 0, 0, 0 };                 // u0=1, nt=0
        private static byte[] HoldStart() => new byte[] { 1, 0, 0, 2 };           // nt=2
        private static byte[] HoldEnd() => new byte[] { 1, 0, 0, 3 };             // nt=3
        private static byte[] BpmSlot(float bpm) => BitConverter.GetBytes(bpm);   // 4 bytes ARE the float

        private static byte[] Frame(int meas, short ft, params byte[][] slots)
        {
            var b = new List<byte>();
            b.AddRange(BitConverter.GetBytes((uint)meas));
            b.AddRange(BitConverter.GetBytes(ft));
            b.AddRange(BitConverter.GetBytes((ushort)slots.Length));
            foreach (var s in slots) b.AddRange(s);
            return b.ToArray();
        }

        private static byte[] BuildStepFile(float headerBpm, params byte[][] frames)
        {
            var fb = new List<byte>();
            foreach (var f in frames) fb.AddRange(f);
            byte[] frameBytes = fb.ToArray();
            var file = new byte[300 + frameBytes.Length];
            file[4] = (byte)'g'; file[5] = (byte)'n';                 // file_type "gn\0\0"
            BitConverter.GetBytes(headerBpm).CopyTo(file, 16);        // bpm @16
            BitConverter.GetBytes((short)5).CopyTo(file, 20);         // level_easy
            BitConverter.GetBytes((uint)300).CopyTo(file, 284);       // address_easy == 300 (required by plain path)
            uint end = (uint)(300 + frameBytes.Length);
            BitConverter.GetBytes(end).CopyTo(file, 288);             // address_normal/hard/end (easy = all frames)
            BitConverter.GetBytes(end).CopyTo(file, 292);
            BitConverter.GetBytes(end).CopyTo(file, 296);
            frameBytes.CopyTo(file, 300);
            return file;
        }

        [Test]
        public void SingleBpm_Timing_Is_Unchanged()
        {
            // taps at beat 0 (meas0) and beat 8 (meas2), 100 bpm → 600 ms/beat → 0 and 4800 ms.
            byte[] file = BuildStepFile(100f,
                Frame(0, 2, Tap()),
                Frame(2, 2, Tap()));
            var map = GnChart.Load(file, 0);

            Assert.AreEqual(100.0, map.Bpm, 1e-3);
            Assert.AreEqual(2, map.HitObjects.Count);
            Assert.AreEqual(0, map.HitObjects[0].StartTimeMs);
            Assert.AreEqual(4800, map.HitObjects[1].StartTimeMs);
            Assert.AreEqual(1, map.TimingPoints.Count);
            Assert.AreEqual(0.0, map.TimingPoints[0].TimeMs, 1e-6);
            Assert.AreEqual(600.0, map.TimingPoints[0].BeatLength, 1e-6);
        }

        [Test]
        public void MultiBpm_ShiftsNoteTimes_And_EmitsSegments()
        {
            // 100 bpm, then a type-1 BPM frame sets 200 bpm at beat 4. A tap at beat 8 must land at
            // 2400 + 4×300 = 3600 ms (NOT the old single-BPM 8×600 = 4800 ms).
            byte[] file = BuildStepFile(100f,
                Frame(0, 2, Tap()),            // beat 0  → 0 ms
                Frame(1, 1, BpmSlot(200f)),    // beat 4  → BPM becomes 200
                Frame(2, 2, Tap()));           // beat 8  → 3600 ms
            var map = GnChart.Load(file, 0);

            Assert.AreEqual(2, map.HitObjects.Count, "type-1 frame must not be counted as a note");
            Assert.AreEqual(0, map.HitObjects[0].StartTimeMs);
            Assert.AreEqual(3600, map.HitObjects[1].StartTimeMs);

            Assert.AreEqual(2, map.TimingPoints.Count);
            Assert.AreEqual(0.0, map.TimingPoints[0].TimeMs, 1e-6);
            Assert.AreEqual(600.0, map.TimingPoints[0].BeatLength, 1e-6);   // 100 bpm
            Assert.AreEqual(2400.0, map.TimingPoints[1].TimeMs, 1e-6);
            Assert.AreEqual(300.0, map.TimingPoints[1].BeatLength, 1e-6);   // 200 bpm
        }

        [Test]
        public void Bpm340_Gimmick_Slot_Is_Read_As_Float_Not_Skipped_On_Zero_U0()
        {
            // 340.0f = 00 00 AA 43 → its u0 (first int16) is 0. A naive "u0==0 → skip" would drop it.
            byte[] file = BuildStepFile(170f,
                Frame(0, 2, Tap()),            // beat 0 → 0 ms (at 170)
                Frame(1, 1, BpmSlot(340f)),    // beat 4 → 340 bpm
                Frame(2, 2, Tap()));           // beat 8
            var map = GnChart.Load(file, 0);

            // beat0→4 at 170: 4×(60000/170)=1411.76ms; beat4→8 at 340: 4×(60000/340)=705.88 → 2117.6 → 2118
            Assert.AreEqual(2, map.HitObjects.Count);
            Assert.AreEqual(2118, map.HitObjects[1].StartTimeMs);
            Assert.AreEqual(2, map.TimingPoints.Count, "the 340 BPM segment must be picked up");
        }

        [Test]
        public void Hold_Pairs_StartAndEnd_Across_The_Timeline()
        {
            // hold on lane 1 (ft=3=Down): start beat 0, end beat 4 @100bpm → end 2400 ms.
            byte[] file = BuildStepFile(100f,
                Frame(0, 3, HoldStart()),
                Frame(1, 3, HoldEnd()));
            var map = GnChart.Load(file, 0);

            Assert.AreEqual(1, map.HitObjects.Count);
            Assert.IsTrue(map.HitObjects[0].IsHold);
            Assert.AreEqual(1, map.HitObjects[0].Lane);
            Assert.AreEqual(0, map.HitObjects[0].StartTimeMs);
            Assert.AreEqual(2400, map.HitObjects[0].EndTimeMs);
        }
    }

    /// <summary>osu! parser must keep ALL timing points (uninherited tempo + inherited SV) for the scroll.</summary>
    public class OsuTimingPointsTests
    {
        [Test]
        public void Parses_Uninherited_And_Inherited_Points()
        {
            string osu =
                "[General]\nMode: 3\n[Difficulty]\nCircleSize:4\n" +
                "[TimingPoints]\n" +
                "0,500,4,2,0,100,1,0\n" +       // uninherited: 120 bpm
                "1000,-50,4,2,0,100,0,0\n" +    // inherited (green): SV ×2
                "2000,250,4,2,0,100,1,0\n" +    // uninherited: 240 bpm
                "[HitObjects]\n256,0,0,1,0\n";
            var map = OsuBeatmapParser.Parse(osu);

            Assert.AreEqual(3, map.TimingPoints.Count);
            Assert.AreEqual(120.0, map.Bpm, 1e-6);                       // first uninherited
            Assert.IsTrue(map.TimingPoints[0].Uninherited);
            Assert.IsFalse(map.TimingPoints[1].Uninherited);            // green line
            Assert.AreEqual(2.0, map.TimingPoints[1].SpeedMultiplier, 1e-6);   // -100/-50
            Assert.AreEqual(1.0, map.TimingPoints[0].SpeedMultiplier, 1e-6);   // tempo point → 1.0
        }
    }
}
