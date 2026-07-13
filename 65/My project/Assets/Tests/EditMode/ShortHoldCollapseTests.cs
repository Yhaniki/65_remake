using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 無理短長條 → 一般 note（<see cref="OsuBeatmap.CollapseShortHolds"/>）。門檻＝180 BPM 的 16 分音符
    /// (60000/180/4 ≈ 83.3 ms)，「16 分以下、不含 16 分」＝短於這個長度的 long note 才收掉。
    /// </summary>
    public class ShortHoldCollapseTests
    {
        private static OsuBeatmap Map(params OsuHitObject[] notes)
        {
            var m = new OsuBeatmap { Keys = 4, Bpm = 180 };
            m.HitObjects.AddRange(notes);
            return m;
        }

        [Test]
        public void Threshold_Is_16th_At_180Bpm()
        {
            Assert.AreEqual(60000.0 / 180.0 / 4.0, OsuBeatmap.ShortHoldMaxMs, 1e-9);
            Assert.AreEqual(83.33, OsuBeatmap.ShortHoldMaxMs, 0.01);
        }

        [Test]
        public void Short_Hold_Becomes_Tap()
        {
            var m = Map(new OsuHitObject(2, 1000, 1060));   // 60 ms — 按不出來的裝飾 hold
            Assert.AreEqual(1, m.CollapseShortHolds());
            Assert.IsFalse(m.HitObjects[0].IsHold);
            Assert.AreEqual(2, m.HitObjects[0].Lane, "lane 不變");
            Assert.AreEqual(1000, m.HitObjects[0].StartTimeMs, "判定時間不變");
        }

        [Test]
        public void Hold_Of_Exactly_A_16th_Is_Kept()
        {
            // 180 BPM 的 16 分 = 83.33 ms，整數 ms 譜會存成 83（或 84）→ 取整容差讓它留著（規格：不含 16 分）
            var m = Map(new OsuHitObject(0, 0, 83), new OsuHitObject(1, 500, 584));
            Assert.AreEqual(0, m.CollapseShortHolds());
            Assert.IsTrue(m.HitObjects[0].IsHold);
            Assert.IsTrue(m.HitObjects[1].IsHold);
        }

        [Test]
        public void Long_Holds_Are_Kept()
        {
            var m = Map(new OsuHitObject(3, 2000, 2500));   // 半秒長條
            Assert.AreEqual(0, m.CollapseShortHolds());
            Assert.IsTrue(m.HitObjects[0].IsHold);
            Assert.AreEqual(2500, m.HitObjects[0].EndTimeMs.Value);
        }

        [Test]
        public void Cutoff_Is_Absolute_Time_Not_Song_Bpm()
        {
            // 220 BPM 的 16 分 (68.2→68 ms) 比 180 BPM 的 16 分短 → 收掉；
            // 150 BPM 的 16 分 (100 ms) 比它長 → 留著。歌曲自己的 BPM 不影響門檻。
            var m = Map(new OsuHitObject(0, 0, 68), new OsuHitObject(1, 1000, 1100));
            m.Bpm = 220;
            Assert.AreEqual(1, m.CollapseShortHolds());
            Assert.IsFalse(m.HitObjects[0].IsHold);
            Assert.IsTrue(m.HitObjects[1].IsHold);
        }

        [Test]
        public void Taps_Untouched_And_TotalNotes_Drops_Per_Collapsed_Hold()
        {
            var m = Map(
                new OsuHitObject(0, 0),               // tap
                new OsuHitObject(1, 100, 130),        // 30 ms  → tap
                new OsuHitObject(2, 200, 400),        // 200 ms → 留
                new OsuHitObject(3, 500, 550));       // 50 ms  → tap
            Assert.AreEqual(1 + 2 + 2 + 2, m.TotalNotes, "收之前：tap 1 顆 + 3 個長條各 2 次判定");
            Assert.AreEqual(2, m.CollapseShortHolds());
            Assert.AreEqual(1 + 1 + 2 + 1, m.TotalNotes, "收之後：只剩下那個長條算 2 次判定");
            Assert.IsFalse(m.HitObjects[0].IsHold);
            Assert.IsTrue(m.HitObjects[2].IsHold);
        }

        [Test]
        public void Custom_Cutoff_Is_Honoured()
        {
            var m = Map(new OsuHitObject(0, 0, 200));
            Assert.AreEqual(0, m.CollapseShortHolds(), "預設門檻下 200 ms 是正常長條");
            Assert.AreEqual(1, m.CollapseShortHolds(250.0), "自訂門檻 250 ms → 收掉");
            Assert.IsFalse(m.HitObjects[0].IsHold);
        }

        [Test]
        public void Empty_Chart_Is_A_Noop()
        {
            Assert.AreEqual(0, new OsuBeatmap().CollapseShortHolds());
        }

        [Test]
        public void Timing_Points_And_Stops_Are_Not_Touched()
        {
            var m = Map(new OsuHitObject(0, 0, 40));
            m.TimingPoints.Add(new OsuTimingPoint(0, 461.5));
            m.Stops.Add(new ScrollStop(1000, 250));
            m.MusicStartOffsetMs = 1234;
            m.CollapseShortHolds();
            Assert.AreEqual(461.5, m.TimingPoints[0].BeatLength, 1e-9);
            Assert.AreEqual(250.0, m.Stops[0].DurationMs, 1e-9);
            Assert.AreEqual(1234.0, m.MusicStartOffsetMs, 1e-9);
        }
    }
}
