using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// ManiaScroll = osu!mania-style scroll at a FIXED base tempo (the user's "base 140, 不依 BPM").
    /// Verifies: base velocity calibration, constant (single-BPM) linearity, the osu "Constant Speed"
    /// toggle, and that mid-song BPM changes / SV still vary the scroll locally (relative-scale multiplier).
    /// </summary>
    public class ManiaScrollTests
    {
        private const double Eps = 1e-6;

        // lastObjMs controls how MostCommonBeatLength weights segments (osu weights each tempo segment by
        // its duration up to the last note) — set it so the intended segment is the most common (base).
        private static OsuBeatmap MapWith(double bpm, double lastObjMs, params OsuTimingPoint[] pts)
        {
            var m = new OsuBeatmap { Bpm = bpm };
            foreach (var p in pts) m.TimingPoints.Add(p);
            m.HitObjects.Add(new OsuHitObject(0, (int)lastObjMs));
            return m;
        }

        [Test]
        public void BaseVelocity_Is_ReferenceBpm_Times_Speed_Times_1point6()
        {
            Assert.AreEqual(560.0, ManiaScroll.BaseVelocityFor(2.5), Eps);          // 140 × 2.5 × 1.6
            Assert.AreEqual(224.0, ManiaScroll.BaseVelocityFor(1.0), Eps);          // 140 × 1.0 × 1.6
            Assert.AreEqual(1792.0, ManiaScroll.BaseVelocityFor(8.0), Eps);         // 140 × 8.0 × 1.6
            Assert.AreEqual(320.0, ManiaScroll.BaseVelocityFor(2.5, 80.0), Eps);    // 80bpm → matches old 320px/s
        }

        [Test]
        public void NoTimingPoints_Is_Constant_Linear_AtBaseVelocity()
        {
            var scroll = ManiaScroll.Build(new OsuBeatmap { Bpm = 123 }, 2.5);      // no TimingPoints
            double v = ManiaScroll.BaseVelocityFor(2.5);                            // 560 px/s
            Assert.AreEqual(v * 1.0, scroll.PixelDistance(0, 1000), 1e-4);          // 1s → 560px
            Assert.AreEqual(v * 2.0, scroll.PixelDistance(0, 2000), 1e-4);          // 2s → 1120px
            Assert.AreEqual(0.0, scroll.PixelDistance(500, 500), Eps);
        }

        [Test]
        public void Distance_Is_Additive()
        {
            var map = MapWith(120, 10000, new OsuTimingPoint(0, 500), new OsuTimingPoint(2000, 250));
            var scroll = ManiaScroll.Build(map, 3.0);
            double ac = scroll.PixelDistance(500, 3500);
            double ab = scroll.PixelDistance(500, 2000);
            double bc = scroll.PixelDistance(2000, 3500);
            Assert.AreEqual(ac, ab + bc, 1e-4);
        }

        [Test]
        public void BpmChange_Doubles_The_Scroll_Speed_In_The_Faster_Segment()
        {
            // base (most common) = 500ms beat (120bpm) over [0,8000); brief 250ms beat (240bpm) over [8000,9000).
            // lastObj=9000 makes the 120bpm segment dominate → it is the base (like a 170-bpm song's ×2 gimmick).
            var map = MapWith(120, 9000, new OsuTimingPoint(0, 500), new OsuTimingPoint(8000, 250));
            var scroll = ManiaScroll.Build(map, 2.5);
            double v = ManiaScroll.BaseVelocityFor(2.5);
            Assert.AreEqual(v * 1.0, scroll.PixelDistance(0, 1000), 1e-3);     // base segment
            Assert.AreEqual(v * 2.0, scroll.PixelDistance(8000, 9000), 1e-3);  // ×2 (240/120) gimmick segment
        }

        [Test]
        public void GreenLine_SV_Scales_The_Scroll()
        {
            // one tempo point (500ms/120bpm) + an inherited SV point at 1000ms with beatLength -50 → SV 2.0
            var map = MapWith(120, 5000, new OsuTimingPoint(0, 500), new OsuTimingPoint(1000, -50));
            var scroll = ManiaScroll.Build(map, 2.5);
            double v = ManiaScroll.BaseVelocityFor(2.5);
            Assert.AreEqual(v * 1.0, scroll.PixelDistance(0, 1000), 1e-3);   // before SV: base
            Assert.AreEqual(v * 2.0, scroll.PixelDistance(1000, 2000), 1e-3); // after SV ×2
        }

        [Test]
        public void ConstantScroll_Toggle_Ignores_All_Variation()
        {
            var map = MapWith(120, 9000, new OsuTimingPoint(0, 500), new OsuTimingPoint(8000, 250), new OsuTimingPoint(1000, -50));
            var scroll = ManiaScroll.Build(map, 2.5, constantScroll: true);
            double v = ManiaScroll.BaseVelocityFor(2.5);
            // perfectly linear at base speed everywhere despite the BPM change + SV
            Assert.AreEqual(v * 1.0, scroll.PixelDistance(0, 1000), 1e-4);
            Assert.AreEqual(v * 1.0, scroll.PixelDistance(8000, 9000), 1e-4);
            Assert.AreEqual(v * 1.0, scroll.PixelDistance(1000, 2000), 1e-4);
        }

        [Test]
        public void MostCommonBeatLength_Forces_First_Segment_From_Time0()
        {
            // first tempo (500ms/120bpm) starts at 5000ms and lasts only 100ms before a 250ms/240bpm tempo.
            // osu forces the opening segment to start at 0, so 120bpm dominates → base; multiplier 1.0 in [5000,5100).
            var map = new OsuBeatmap { Bpm = 120 };
            map.TimingPoints.Add(new OsuTimingPoint(5000, 500));
            map.TimingPoints.Add(new OsuTimingPoint(5100, 250));
            map.HitObjects.Add(new OsuHitObject(0, 5300));
            var scroll = ManiaScroll.Build(map, 2.5);
            double v = ManiaScroll.BaseVelocityFor(2.5);
            Assert.AreEqual(v * 0.05, scroll.PixelDistance(5000, 5050), 1e-3);   // base segment (would be ×0.5 without the fix)
        }

        [Test]
        public void ReferenceBpm_Anchor_Is_Independent_Of_Song_Bpm()
        {
            // two different songs (60 vs 240 bpm) at the same speed step → SAME base velocity (constant base).
            var slow = ManiaScroll.Build(MapWith(60, 5000, new OsuTimingPoint(0, 1000)), 2.5);
            var fast = ManiaScroll.Build(MapWith(240, 5000, new OsuTimingPoint(0, 250)), 2.5);
            Assert.AreEqual(slow.PixelDistance(0, 1000), fast.PixelDistance(0, 1000), 1e-4);
        }
    }
}
