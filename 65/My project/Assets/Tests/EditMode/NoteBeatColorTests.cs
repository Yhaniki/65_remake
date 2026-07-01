using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// NoteBeatColor = the official SDO "3D note" beat-quantization colour (exe (tickInBar/12)%4):
    /// on-beat=magenta(0), off-8th=blue(1), 16ths=green(2). Verifies the grid math, negative offsets,
    /// rounding to the nearest 16th, the tempo-map resolver (active point / before-first / BPM fallback).
    /// </summary>
    public class NoteBeatColorTests
    {
        // 120 BPM → 500 ms/beat, 125 ms/16th. phase 0.
        const double Beat = 500.0, Phase = 0.0;

        [Test]
        public void OnBeat_Is_Magenta()
        {
            Assert.AreEqual(NoteBeatColor.Magenta, NoteBeatColor.Family(0, Beat, Phase));      // beat 1
            Assert.AreEqual(NoteBeatColor.Magenta, NoteBeatColor.Family(500, Beat, Phase));    // beat 2
            Assert.AreEqual(NoteBeatColor.Magenta, NoteBeatColor.Family(1000, Beat, Phase));   // beat 3
            Assert.AreEqual(NoteBeatColor.Magenta, NoteBeatColor.Family(2000, Beat, Phase));   // bar 2 beat 1
        }

        [Test]
        public void Off8th_Is_Blue()
        {
            Assert.AreEqual(NoteBeatColor.Blue, NoteBeatColor.Family(250, Beat, Phase));       // the "and" of beat 1
            Assert.AreEqual(NoteBeatColor.Blue, NoteBeatColor.Family(750, Beat, Phase));       // the "and" of beat 2
        }

        [Test]
        public void Sixteenths_Are_Green()
        {
            Assert.AreEqual(NoteBeatColor.Green, NoteBeatColor.Family(125, Beat, Phase));       // e
            Assert.AreEqual(NoteBeatColor.Green, NoteBeatColor.Family(375, Beat, Phase));       // a
            Assert.AreEqual(NoteBeatColor.Green, NoteBeatColor.Family(625, Beat, Phase));       // e of beat 2
            Assert.AreEqual(NoteBeatColor.Green, NoteBeatColor.Family(875, Beat, Phase));       // a of beat 2
        }

        [Test]
        public void Rounds_To_Nearest_Sixteenth()
        {
            // slightly-off timings snap to the nearest 16th grid slot
            Assert.AreEqual(NoteBeatColor.Green, NoteBeatColor.Family(130, Beat, Phase));       // ~125 → grid 1
            Assert.AreEqual(NoteBeatColor.Blue, NoteBeatColor.Family(240, Beat, Phase));        // ~250 → grid 2
            Assert.AreEqual(NoteBeatColor.Magenta, NoteBeatColor.Family(1010, Beat, Phase));    // ~1000 → grid 8
        }

        [Test]
        public void Negative_Offsets_Wrap_Correctly()
        {
            Assert.AreEqual(NoteBeatColor.Magenta, NoteBeatColor.Family(-500, Beat, Phase));    // grid -4 → q0
            Assert.AreEqual(NoteBeatColor.Blue, NoteBeatColor.Family(-250, Beat, Phase));       // grid -2 → q2
            Assert.AreEqual(NoteBeatColor.Green, NoteBeatColor.Family(-125, Beat, Phase));      // grid -1 → q3
        }

        [Test]
        public void NoTempo_Defaults_To_Magenta()
        {
            Assert.AreEqual(NoteBeatColor.Magenta, NoteBeatColor.Family(333, 0.0, Phase));
            Assert.AreEqual(NoteBeatColor.Magenta, NoteBeatColor.Family(333, -1.0, Phase));
        }

        [Test]
        public void PhaseOffset_Shifts_The_Grid()
        {
            // tempo point at 1000ms → that instant is a beat (magenta); +250 from it is the off-8th (blue).
            Assert.AreEqual(NoteBeatColor.Magenta, NoteBeatColor.Family(1000, Beat, 1000));
            Assert.AreEqual(NoteBeatColor.Blue, NoteBeatColor.Family(1250, Beat, 1000));
            Assert.AreEqual(NoteBeatColor.Green, NoteBeatColor.Family(1125, Beat, 1000));
        }

        [Test]
        public void Family_From_Map_Uses_Active_TempoPoint()
        {
            var map = new OsuBeatmap { Bpm = 120 };
            map.TimingPoints.Add(new OsuTimingPoint(0.0, 500.0));        // 120 BPM from t=0
            map.TimingPoints.Add(new OsuTimingPoint(4000.0, 250.0));     // 240 BPM (125 ms/beat) from t=4000

            // in the first segment: 500 ms/beat
            Assert.AreEqual(NoteBeatColor.Magenta, NoteBeatColor.Family(2000, map));   // on-beat
            Assert.AreEqual(NoteBeatColor.Blue, NoteBeatColor.Family(2250, map));      // off-8th

            // in the second segment: 250 ms/beat (62.5 ms/16th), phase 4000
            Assert.AreEqual(NoteBeatColor.Magenta, NoteBeatColor.Family(4000, map));   // on-beat
            Assert.AreEqual(NoteBeatColor.Blue, NoteBeatColor.Family(4125, map));      // +125 = off-8th at 250ms/beat
        }

        [Test]
        public void Family_From_Map_Before_First_Point_Uses_First()
        {
            var map = new OsuBeatmap { Bpm = 120 };
            map.TimingPoints.Add(new OsuTimingPoint(1000.0, 500.0));
            // note at 500 (before the first point) phases off the first point (1000): 500 is one beat before → on-beat.
            Assert.AreEqual(NoteBeatColor.Magenta, NoteBeatColor.Family(500, map));
            Assert.AreEqual(NoteBeatColor.Blue, NoteBeatColor.Family(750, map));
        }

        [Test]
        public void Family_From_Map_No_TimingPoints_Uses_Bpm()
        {
            var map = new OsuBeatmap { Bpm = 120 };                       // 500 ms/beat, phase 0
            Assert.AreEqual(NoteBeatColor.Magenta, NoteBeatColor.Family(1000, map));
            Assert.AreEqual(NoteBeatColor.Blue, NoteBeatColor.Family(1250, map));
            Assert.AreEqual(NoteBeatColor.Green, NoteBeatColor.Family(1125, map));
        }

        [Test]
        public void Inherited_SV_Points_Are_Ignored_For_Tempo()
        {
            var map = new OsuBeatmap { Bpm = 120 };
            map.TimingPoints.Add(new OsuTimingPoint(0.0, 500.0));         // tempo
            map.TimingPoints.Add(new OsuTimingPoint(1000.0, -50.0));      // SV green line (inherited) — must NOT reset phase
            Assert.AreEqual(NoteBeatColor.Magenta, NoteBeatColor.Family(2000, map));
            Assert.AreEqual(NoteBeatColor.Blue, NoteBeatColor.Family(2250, map));
        }
    }
}
