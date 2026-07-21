using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    public class OsuBeatmapParserTests
    {
        // mania 4K: x in {64,192,320,448} -> lanes 0,1,2,3. One hold (type 128) ending at 3270.
        private const string Sample =
            "osu file format v14\n" +
            "\n" +
            "[General]\n" +
            "AudioFilename: Bassdrop.mp3\n" +
            "Mode: 3\n" +
            "\n" +
            "[Metadata]\n" +
            "Title:Bassdrop Freaks\n" +
            "Version:eca's 4K HD\n" +
            "\n" +
            "[Difficulty]\n" +
            "HPDrainRate:6\n" +
            "CircleSize:4\n" +
            "OverallDifficulty:5\n" +
            "\n" +
            "[HitObjects]\n" +
            "64,192,2432,5,0,0:0:0:0:\n" +
            "320,192,2432,1,0,0:0:0:0:\n" +
            "192,0,2432,128,0,3270:0:0:0:0:\n" +
            "448,192,3102,1,0,0:0:0:0:\n";

        [Test]
        public void Parses_General_And_Difficulty()
        {
            var map = OsuBeatmapParser.Parse(Sample);
            Assert.AreEqual("Bassdrop.mp3", map.AudioFilename);
            Assert.AreEqual(3, map.Mode);
            Assert.AreEqual(4, map.Keys);
            Assert.AreEqual("eca's 4K HD", map.Version);
        }

        [Test]
        public void Parses_Four_HitObjects()
        {
            var map = OsuBeatmapParser.Parse(Sample);
            Assert.AreEqual(4, map.HitObjects.Count);
        }

        [Test]
        public void Maps_X_To_Lane()
        {
            var map = OsuBeatmapParser.Parse(Sample);
            Assert.AreEqual(0, map.HitObjects[0].Lane); // x=64
            Assert.AreEqual(2, map.HitObjects[1].Lane); // x=320
            Assert.AreEqual(1, map.HitObjects[2].Lane); // x=192
            Assert.AreEqual(3, map.HitObjects[3].Lane); // x=448
        }

        [Test]
        public void Parses_Hold_With_EndTime()
        {
            var map = OsuBeatmapParser.Parse(Sample);
            var hold = map.HitObjects[2];
            Assert.IsTrue(hold.IsHold);
            Assert.AreEqual(2432, hold.StartTimeMs);
            Assert.AreEqual(3270, hold.EndTimeMs);
        }

        [Test]
        public void Tap_Has_No_EndTime()
        {
            var map = OsuBeatmapParser.Parse(Sample);
            Assert.IsFalse(map.HitObjects[0].IsHold);
            Assert.IsNull(map.HitObjects[0].EndTimeMs);
        }

        [Test]
        public void TotalNotes_Counts_Hold_As_Two()
        {
            var map = OsuBeatmapParser.Parse(Sample);
            // 3 taps + 1 hold(=2) = 5
            Assert.AreEqual(5, map.TotalNotes);
        }

        [Test]
        public void TotalNotes_Excludes_Bombs()
        {
            // 炸彈永遠不會被判定(踩到只扣血),算進分母的話滿分就永遠打不到。
            var map = new OsuBeatmap { Keys = 4 };
            map.HitObjects.Add(new OsuHitObject(0, 0));                        // tap        → 1
            map.HitObjects.Add(new OsuHitObject(1, 500, 1500));                // hold       → 2
            map.HitObjects.Add(new OsuHitObject(2, 1000, null, isBomb: true)); // 炸彈       → 0
            Assert.AreEqual(3, map.TotalNotes);
        }

        [Test]
        public void ApplyLeadIn_Keeps_The_Bomb_Flag()
        {
            // 外部 osu/StepMania 譜一定會走 ApplyLeadIn;重建 note 時漏掉 IsBomb 的話,炸彈會變成一般 note。
            var map = new OsuBeatmap { Keys = 4 };
            map.HitObjects.Add(new OsuHitObject(0, 100, null, isBomb: true));
            map.HitObjects.Add(new OsuHitObject(1, 200, 400));
            map.ApplyLeadIn(1000);

            Assert.IsTrue(map.HitObjects[0].IsBomb);
            Assert.AreEqual(1100, map.HitObjects[0].StartTimeMs);
            Assert.IsFalse(map.HitObjects[1].IsBomb);
            Assert.AreEqual(1200, map.HitObjects[1].StartTimeMs);
            Assert.AreEqual(1400, map.HitObjects[1].EndTimeMs);
        }

        [Test]
        public void EarlyVersionTimingOffset_Only_For_FormatBelow5()
        {
            // osu adds +24ms to every time on format < 5 (EARLY_VERSION_TIMING_OFFSET); v5+ gets 0.
            Assert.AreEqual(24, OsuBeatmapParser.EarlyVersionTimingOffset("osu file format v4\n[HitObjects]\n"));
            Assert.AreEqual(24, OsuBeatmapParser.EarlyVersionTimingOffset("osu file format v3"));
            Assert.AreEqual(0, OsuBeatmapParser.EarlyVersionTimingOffset("osu file format v5"));
            Assert.AreEqual(0, OsuBeatmapParser.EarlyVersionTimingOffset("osu file format v14"));
            Assert.AreEqual(0, OsuBeatmapParser.EarlyVersionTimingOffset("no header at all"));
        }

        [Test]
        public void OldFormat_ShiftsAllTimesBy24ms()
        {
            // v14 sample → 2432 verbatim; the same chart as v4 → every time +24 (notes AND hold end).
            var modern = OsuBeatmapParser.Parse(Sample);
            Assert.AreEqual(2432, modern.HitObjects[0].StartTimeMs);

            var old = OsuBeatmapParser.Parse(Sample.Replace("osu file format v14", "osu file format v4"));
            Assert.AreEqual(2432 + 24, old.HitObjects[0].StartTimeMs);
            var hold = old.HitObjects[2];
            Assert.AreEqual(2432 + 24, hold.StartTimeMs);
            Assert.AreEqual(3270 + 24, hold.EndTimeMs);
        }
    }
}
