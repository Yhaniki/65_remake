using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    public class OsuMetaTests
    {
        private const string Sample =
            "osu file format v14\n" +
            "[General]\n" +
            "AudioFilename: audio.mp3\n" +
            "Mode: 3\n" +
            "PreviewTime: 45000\n" +
            "[Metadata]\n" +
            "Title:My Title\n" +
            "Artist:My Artist\n" +
            "Creator:Mapper\n" +
            "Version:4K Insane\n" +
            "[Difficulty]\n" +
            "CircleSize:4\n" +
            "[Events]\n" +
            "//Background and Video events\n" +
            "0,0,\"back ground.jpg\",0,0\n" +
            "[TimingPoints]\n" +
            "0,300,4,2,0,100,1,0\n" +
            "[HitObjects]\n" +
            "64,192,100,1,0,0:0:0:0:\n" +
            "192,192,200,1,0,0:0:0:0:\n" +
            "320,0,300,128,0,800:0:0:0:0:\n";

        [Test]
        public void Reads_General_Metadata_Difficulty()
        {
            var m = OsuBeatmapParser.ReadMeta(Sample);
            Assert.AreEqual(3, m.Mode);
            Assert.AreEqual(4, m.Keys);
            Assert.AreEqual("My Title", m.Title);
            Assert.AreEqual("My Artist", m.Artist);
            Assert.AreEqual("Mapper", m.Creator);
            Assert.AreEqual("4K Insane", m.Version);
            Assert.AreEqual("audio.mp3", m.AudioFilename);
        }

        [Test]
        public void Reads_Background_From_Events()
        {
            var m = OsuBeatmapParser.ReadMeta(Sample);
            Assert.AreEqual("back ground.jpg", m.BackgroundFilename);   // quotes stripped
        }

        [Test]
        public void Reads_Bpm_From_First_Uninherited_Timing_Point()
        {
            var m = OsuBeatmapParser.ReadMeta(Sample);
            Assert.AreEqual(200.0, m.Bpm, 1e-6);   // 60000 / 300
        }

        [Test]
        public void Counts_HitObjects()
        {
            var m = OsuBeatmapParser.ReadMeta(Sample);
            Assert.AreEqual(3, m.NoteCount);
        }

        [Test]
        public void Reads_PreviewTime()
        {
            var m = OsuBeatmapParser.ReadMeta(Sample);
            Assert.AreEqual(45000, m.PreviewTime);
        }
    }
}
