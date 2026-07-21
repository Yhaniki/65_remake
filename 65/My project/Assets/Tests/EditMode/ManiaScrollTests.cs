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
            double r = ManiaScroll.DefaultReferenceBpm;                             // 基準 BPM 可調 → 斷言綁常數，不寫死數字
            Assert.AreEqual(r * 2.5 * 1.6, ManiaScroll.BaseVelocityFor(2.5), Eps);
            Assert.AreEqual(r * 1.0 * 1.6, ManiaScroll.BaseVelocityFor(1.0), Eps);
            Assert.AreEqual(r * 8.0 * 1.6, ManiaScroll.BaseVelocityFor(8.0), Eps);
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

        // ---- 基準速度 (base tempo) 的挑選：osu most-common + ±3% 併群，見 docs/architecture/scroll-base-bpm.md ----

        /// <summary>在 [t0,t1) 之間每 gapMs 放一顆 note（讓某段的 note 佔比夠高、能通過基準速度的 note 門檻）。</summary>
        private static void FillNotes(OsuBeatmap map, double t0, double t1, double gapMs)
        {
            for (double t = t0; t < t1; t += gapMs) map.HitObjects.Add(new OsuHitObject(0, (int)t));
        }

        [Test]
        public void ContestedChart_Uses_HeaderBpm_But_Caps_The_Fast_Section()
        {
            // 爭議譜：長時間慢段(120bpm 60s) + 長時間快段(240bpm 40s) 都站得住腳。
            // 使用者定奪：基準用選歌畫面顯示的 BPM（表頭 120），但表頭就是慢群 → 快段會變 2.0×，
            // 所以往上抬到「快段剛好 1.5×」＝ 240/1.5 = 160bpm。慢段因此是 0.75×。
            var map = new OsuBeatmap { Bpm = 120 };
            map.TimingPoints.Add(new OsuTimingPoint(0, 500));        // 120bpm  0-60s
            map.TimingPoints.Add(new OsuTimingPoint(60000, 250));    // 240bpm 60-100s
            FillNotes(map, 0, 60000, 1000);
            FillNotes(map, 60000, 100000, 500);
            var scroll = ManiaScroll.Build(map, 2.5);
            double v = ManiaScroll.BaseVelocityFor(2.5);
            Assert.AreEqual(v * ManiaScroll.FastSectionMaxMultiplier,
                scroll.PixelDistance(70000, 71000), v * 0.01);                       // 快段 = 1.5×（上限）
            Assert.AreEqual(v * 0.75, scroll.PixelDistance(1000, 2000), v * 0.01);    // 慢段 = 0.75×
        }

        [Test]
        public void ContestedChart_HeaderBpm_In_The_Middle_Wins()
        {
            // Still Alive 的形狀：慢群 110、快群 220，但表頭寫 165（正好在中間）→ 基準就用 165，
            // 因為快段 220/165 = 1.33× 已經在上限 1.5× 以內，不需要再往上抬。
            var map = new OsuBeatmap { Bpm = 165 };
            map.TimingPoints.Add(new OsuTimingPoint(0, 60000.0 / 110.0));       // 110bpm 0-60s
            map.TimingPoints.Add(new OsuTimingPoint(60000, 60000.0 / 220.0));   // 220bpm 60-100s
            FillNotes(map, 0, 60000, 1000);
            FillNotes(map, 60000, 100000, 500);
            var scroll = ManiaScroll.Build(map, 2.5);
            double v = ManiaScroll.BaseVelocityFor(2.5);
            Assert.AreEqual(v * (220.0 / 165.0), scroll.PixelDistance(70000, 71000), v * 0.01);
            Assert.AreEqual(v * (110.0 / 165.0), scroll.PixelDistance(1000, 2000), v * 0.01);
        }

        [Test]
        public void ShortFastBurst_Does_Not_Become_The_Base()
        {
            // 反面：細碎的短爆發（2 秒 ×2）還在可接受範圍 → 基準留在主速度，爆發段自己變 2.0×。
            var map = new OsuBeatmap { Bpm = 120 };
            map.TimingPoints.Add(new OsuTimingPoint(0, 500));
            map.TimingPoints.Add(new OsuTimingPoint(60000, 250));    // 只有 2 秒
            map.TimingPoints.Add(new OsuTimingPoint(62000, 500));
            FillNotes(map, 0, 90000, 500);
            var scroll = ManiaScroll.Build(map, 2.5);
            double v = ManiaScroll.BaseVelocityFor(2.5);
            Assert.AreEqual(v * 1.0, scroll.PixelDistance(1000, 2000), v * 0.01);     // 主速度 = 基準
            Assert.AreEqual(v * 2.0, scroll.PixelDistance(60000, 61000), v * 0.01);   // 短爆發 = ×2
        }

        [Test]
        public void FastInterlude_Without_Notes_Is_Ignored()
        {
            // 長但沒有 note 的快速間奏不用讀 → 不當基準（note 佔比門檻）。
            var map = new OsuBeatmap { Bpm = 120 };
            map.TimingPoints.Add(new OsuTimingPoint(0, 500));
            map.TimingPoints.Add(new OsuTimingPoint(60000, 250));    // 20 秒的快速間奏，一顆 note 都沒有
            map.TimingPoints.Add(new OsuTimingPoint(80000, 500));
            FillNotes(map, 0, 60000, 500);
            FillNotes(map, 80000, 120000, 500);
            var scroll = ManiaScroll.Build(map, 2.5);
            double v = ManiaScroll.BaseVelocityFor(2.5);
            Assert.AreEqual(v * 1.0, scroll.PixelDistance(1000, 2000), v * 0.01);
        }

        [Test]
        public void ExtremeGimmick_Is_Capped_So_The_Song_Does_Not_Crawl()
        {
            // ×4 的 gimmick 就算撐得久也不當基準（上限 MaxBaseTempoRatio），否則整首慢到爬。
            var map = new OsuBeatmap { Bpm = 120 };
            map.TimingPoints.Add(new OsuTimingPoint(0, 500));         // 120bpm
            map.TimingPoints.Add(new OsuTimingPoint(60000, 125));     // 480bpm，20 秒
            map.TimingPoints.Add(new OsuTimingPoint(80000, 500));
            FillNotes(map, 0, 120000, 500);
            var scroll = ManiaScroll.Build(map, 2.5);
            double v = ManiaScroll.BaseVelocityFor(2.5);
            Assert.AreEqual(v * 1.0, scroll.PixelDistance(1000, 2000), v * 0.01);      // 基準仍是 120
            Assert.AreEqual(v * 4.0, scroll.PixelDistance(60000, 61000), v * 0.02);
        }

        [Test]
        public void JitteredMainTempo_Beats_A_Single_Uniform_Slow_Section()
        {
            // sdom5028 (My Dearest) 的形狀：主速度被寫成 169/167.9/168.2 三個略有出入的值(合計 41.3s)，
            // 中間一整段 84bpm(34.3s)。逐一 exact match 的話 84 會以單一最大塊勝出，整首快段就變 ×2 飛出去。
            var map = new OsuBeatmap { Bpm = 169 };
            map.TimingPoints.Add(new OsuTimingPoint(0, 60000.0 / 169.0));      // 0-10.3s
            map.TimingPoints.Add(new OsuTimingPoint(10300, 60000.0 / 167.9));  // 10.3-22.5s
            map.TimingPoints.Add(new OsuTimingPoint(22500, 60000.0 / 83.95));  // 22.5-56.8s ← 單塊最長
            map.TimingPoints.Add(new OsuTimingPoint(56800, 60000.0 / 168.2));  // 56.8-75.6s
            map.HitObjects.Add(new OsuHitObject(0, 75600));
            var scroll = ManiaScroll.Build(map, 2.5);
            double v = ManiaScroll.BaseVelocityFor(2.5);

            Assert.AreEqual(v * 1.0, scroll.PixelDistance(0, 1000), v * 0.02);          // 快段 = 基準速度
            Assert.AreEqual(v * 0.5, scroll.PixelDistance(23000, 24000), v * 0.02);     // 半速段 = ×0.5
        }

        [Test]
        public void PreferredHeaderBpm_Is_The_Group_Representative()
        {
            // 表頭 169 落在勝出的那一群裡 → 基準就用 169（選歌畫面顯示的數字），而不是群內最長的 167.9。
            var tps = new[]
            {
                new OsuTimingPoint(0, 60000.0 / 169.0),
                new OsuTimingPoint(1000, 60000.0 / 167.9),   // 群內最長塊
            };
            Assert.AreEqual(60000.0 / 169.0, ManiaScroll.MostCommonBeatLength(tps, 20000, 169.0), 1e-6);
            Assert.AreEqual(60000.0 / 167.9, ManiaScroll.MostCommonBeatLength(tps, 20000), 1e-6);  // 沒給表頭 → 最長的
        }

        [Test]
        public void HalfTempo_Gimmick_Is_Not_Folded_Into_The_Main_Tempo()
        {
            // 併群只併「差 3% 以內」，不含 ÷2/×2：真的以 130 跑掉大半首的譜(sdom1186 形狀)基準仍是 130，
            // 表頭寫 65 也不會把它拉下去（否則 130 段就會變 ×2）。
            var map = MapWith(65, 20000,
                new OsuTimingPoint(0, 60000.0 / 65.0),        // 0-5s
                new OsuTimingPoint(5000, 60000.0 / 130.0));   // 5-20s ← 主體
            var scroll = ManiaScroll.Build(map, 2.5);
            double v = ManiaScroll.BaseVelocityFor(2.5);
            Assert.AreEqual(v * 1.0, scroll.PixelDistance(6000, 7000), v * 0.01);   // 130 段 = 基準
            Assert.AreEqual(v * 0.5, scroll.PixelDistance(1000, 2000), v * 0.01);   // 65 段 = ×0.5
        }

        [Test]
        public void ReferenceBpm_Anchor_Is_Independent_Of_Song_Bpm()
        {
            // two different songs (60 vs 240 bpm) at the same speed step → SAME base velocity (constant base).
            var slow = ManiaScroll.Build(MapWith(60, 5000, new OsuTimingPoint(0, 1000)), 2.5);
            var fast = ManiaScroll.Build(MapWith(240, 5000, new OsuTimingPoint(0, 250)), 2.5);
            Assert.AreEqual(slow.PixelDistance(0, 1000), fast.PixelDistance(0, 1000), 1e-4);
        }

        [Test]
        public void FollowSongBpm_Base_Follows_The_Song_Bpm_And_Gimmick_Scales_Officially()
        {
            // followSongBpm: base speed = songBpm × speed × 1.6 (official), and a ×2 mid-song gimmick
            // scales to currentBpm × speed × 1.6. base (most-common) = 170bpm over [0,8000); 340bpm over [8000,9000).
            var map = MapWith(170, 9000,
                new OsuTimingPoint(0, 60000.0 / 170.0), new OsuTimingPoint(8000, 60000.0 / 340.0));
            var scroll = ManiaScroll.Build(map, 2.5, constantScroll: false, referenceBpm: 140.0, followSongBpm: true);
            Assert.AreEqual(170.0 * 2.5 * 1.6, scroll.PixelDistance(0, 1000), 1e-2);      // base 170 (NOT the 140 anchor)
            Assert.AreEqual(340.0 * 2.5 * 1.6, scroll.PixelDistance(8000, 9000), 1e-2);   // ×2 gimmick = 340 × speed × 1.6
        }
    }
}
