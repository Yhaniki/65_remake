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
    }
}
