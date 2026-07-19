using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 打拍測試的合成譜：固定 BPM 的等距音符。音符間距一錯，節拍音（assist tick 掛在音符上）就跟著錯，
    /// 校時整個就沒意義了。
    /// </summary>
    public class BeatTestChartTests
    {
        [Test]
        public void QuarterNotes_AreOneBeatApart_OnTheRightLane()
        {
            var map = BeatTestChart.Build(120.0, durationSec: 10.0);   // 120 BPM → 一拍 500ms

            Assert.Greater(map.HitObjects.Count, 10);
            Assert.AreEqual(120.0, map.Bpm, 1e-9);
            Assert.AreEqual(1, map.TimingPoints.Count);
            Assert.AreEqual(500.0, map.TimingPoints[0].BeatLength, 1e-9);

            for (int i = 1; i < map.HitObjects.Count; i++)
            {
                Assert.AreEqual(500, map.HitObjects[i].StartTimeMs - map.HitObjects[i - 1].StartTimeMs,
                    "第 " + i + " 顆音符的間距不是一拍");
                Assert.AreEqual(BeatTestChart.RightLane, map.HitObjects[i].Lane);
                Assert.IsFalse(map.HitObjects[i].IsHold);
            }
        }

        [Test]
        public void FirstNote_IsOnABeat_AfterTheLeadIn()
        {
            var map = BeatTestChart.Build(150.0, durationSec: 20.0);   // 一拍 400ms
            int first = map.HitObjects[0].StartTimeMs;

            Assert.GreaterOrEqual(first, BeatTestChart.LeadInMs, "第一顆音符不能一按播放就在受擊線上");
            Assert.AreEqual(0, first % 400, "第一顆音符要落在整拍上");
            Assert.Less(first, BeatTestChart.LeadInMs + 400, "但也不該多等超過一拍");
        }

        [Test]
        public void Duration_CoversTheRequestedLength()
        {
            var map = BeatTestChart.Build(120.0, durationSec: 60.0);
            int last = map.HitObjects[map.HitObjects.Count - 1].StartTimeMs;
            Assert.GreaterOrEqual(last, 59500);
            Assert.LessOrEqual(last, 60500);
        }

        [Test]
        public void EighthNotes_HalveTheSpacing()
        {
            var map = BeatTestChart.Build(120.0, durationSec: 10.0, beatsPerNote: 0.5);
            Assert.AreEqual(250, map.HitObjects[1].StartTimeMs - map.HitObjects[0].StartTimeMs);
        }

        [Test]
        public void Bpm_IsClampedToSaneRange()
        {
            Assert.AreEqual(20.0, BeatTestChart.Build(1.0, 10.0).Bpm, 1e-9);
            Assert.AreEqual(400.0, BeatTestChart.Build(9999.0, 10.0).Bpm, 1e-9);
        }
    }
}
