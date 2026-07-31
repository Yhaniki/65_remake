using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// OPTION 進階「停用炸彈」的譜面修整（<see cref="OsuBeatmap.RemoveBombs"/>）。炸彈踩到只扣血（不斷 combo）、
    /// 永遠不計分也不計 miss，所以拿掉炸彈**不可以**動到滿分／TotalNotes／其他音符的時間。
    /// </summary>
    public class BombRemovalTests
    {
        private static OsuBeatmap Map(params OsuHitObject[] notes)
        {
            var m = new OsuBeatmap { Keys = 4, Bpm = 150 };
            m.HitObjects.AddRange(notes);
            return m;
        }

        [Test]
        public void RemoveBombs_DropsOnlyBombs_AndKeepsOrder()
        {
            var m = Map(
                new OsuHitObject(0, 1000),
                new OsuHitObject(1, 1500, null, isBomb: true),
                new OsuHitObject(2, 2000, 2500),
                new OsuHitObject(3, 2600, null, isBomb: true),
                new OsuHitObject(0, 3000));

            Assert.AreEqual(2, m.RemoveBombs());
            Assert.AreEqual(3, m.HitObjects.Count);
            Assert.AreEqual(new[] { 1000.0, 2000.0, 3000.0 },
                new[] { m.HitObjects[0].StartTimeMs, m.HitObjects[1].StartTimeMs, m.HitObjects[2].StartTimeMs },
                "剩下的音符要保持原順序/原時間");
            Assert.IsTrue(m.HitObjects[1].IsHold, "長條不能被弄成 tap");
        }

        [Test]
        public void RemoveBombs_DoesNotChangeTotalNotes()   // 炸彈本來就不計分 → 滿分不能因為這個開關而變
        {
            var m = Map(
                new OsuHitObject(0, 1000),                          // 1
                new OsuHitObject(1, 1200, null, isBomb: true),      // 0
                new OsuHitObject(2, 2000, 2500));                   // 2 (頭+尾)
            int before = m.TotalNotes;
            m.RemoveBombs();
            Assert.AreEqual(3, before);
            Assert.AreEqual(before, m.TotalNotes);
        }

        [Test]
        public void RemoveBombs_AllBombs()
        {
            var m = Map(
                new OsuHitObject(0, 1000, null, isBomb: true),
                new OsuHitObject(1, 1100, null, isBomb: true));
            Assert.AreEqual(2, m.RemoveBombs());
            Assert.AreEqual(0, m.HitObjects.Count);
            Assert.AreEqual(0, m.TotalNotes);
        }

        [Test]
        public void RemoveBombs_NoBombs_IsANoOp()
        {
            var m = Map(new OsuHitObject(0, 1000), new OsuHitObject(1, 2000, 2400));
            Assert.AreEqual(0, m.RemoveBombs());
            Assert.AreEqual(2, m.HitObjects.Count);
            Assert.AreEqual(0, m.RemoveBombs(), "再跑一次仍是 0（冪等）");
        }

        [Test]
        public void RemoveBombs_LeavesTimingUntouched()
        {
            var m = Map(new OsuHitObject(0, 1000, null, isBomb: true), new OsuHitObject(1, 5000));
            m.TimingPoints.Add(new OsuTimingPoint(0, 400));
            m.MusicStartOffsetMs = 1234.0;
            m.RemoveBombs();
            Assert.AreEqual(1, m.TimingPoints.Count);
            Assert.AreEqual(0, m.TimingPoints[0].TimeMs);
            Assert.AreEqual(1234.0, m.MusicStartOffsetMs, 1e-9);
            Assert.AreEqual(5000.0, m.LastNoteMs, 1e-9);
        }
    }
}
