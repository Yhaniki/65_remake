using System.IO;
using System.Text;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 生成編舞前要量「這首歌每一個難度」的頭尾（<see cref="ExternalChartIO.Windows"/>）——
    /// 空格子/讀不到的譜跳過，.sm 那種一個檔裝多個難度的也要各自量對，不能互相串。
    /// </summary>
    public class ExternalChartIOTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sdo_chartio_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* 暫存目錄，清不掉就算了 */ }
        }

        [Test]
        public void Osu_Measures_Every_Difficulty_And_Unions_Their_Windows()
        {
            Osu("easy.osu", 12_000, 108_000);
            Osu("normal.osu", 8_400, 121_500);
            Osu("hard.osu", 5_200, 118_900);

            var windows = ExternalChartIO.Windows(1, new[] { P("easy.osu"), P("normal.osu"), P("hard.osu") },
                                                  new[] { 0, 0, 0 }, 0);

            Assert.AreEqual(3, windows.Count);
            Assert.AreEqual((121_500 - 5_200) / 1000.0, DanceInputs.UnionSeconds(windows), 1e-6,
                            "最早的第一顆（hard）→ 最晚的最後一顆（normal）");
        }

        [Test]
        public void Empty_Slots_Missing_Files_And_Junk_Are_Skipped()
        {
            Osu("only.osu", 1_000, 60_000);
            File.WriteAllText(P("junk.osu"), "這不是譜面");

            var windows = ExternalChartIO.Windows(1,
                new[] { P("only.osu"), "", null, P("gone.osu"), P("junk.osu") },
                new[] { 0, 0, 0, 0, 0 }, 0);

            Assert.AreEqual(1, windows.Count, "只有真的量得到的那張算數");
            Assert.AreEqual(59.0, DanceInputs.UnionSeconds(windows), 1e-6);
        }

        [Test]
        public void Sm_Blocks_In_One_File_Are_Measured_Per_Index()
        {
            // 一個 .sm 兩個 #NOTES：block 0 在第一小節、block 1 在第二小節（BPM 120 → 一拍 0.5s）
            Sm("song.sm",
               new[] { "1000", "0000", "0000", "0001" },                                     // 0.0s ~ 1.5s
               new[] { "0000", "0000", "0000", "0000", ",", "0100", "0000", "0000", "0010" }); // 2.0s ~ 3.5s

            var windows = ExternalChartIO.Windows(2, new[] { P("song.sm"), P("song.sm") }, new[] { 0, 1 }, 0);

            Assert.AreEqual(2, windows.Count);
            Assert.AreEqual(0.0, windows[0].FirstMs, 1.0);
            Assert.AreEqual(1_500.0, windows[0].LastMs, 1.0);
            Assert.AreEqual(2_000.0, windows[1].FirstMs, 1.0, "同一個檔讀第二次（快取）不能把 block 0 的答案給 block 1");
            Assert.AreEqual(3_500.0, windows[1].LastMs, 1.0);
            Assert.AreEqual(3.5, DanceInputs.UnionSeconds(windows), 1e-3);
        }

        [Test]
        public void Nothing_Measurable_Gives_No_Windows()
        {
            Assert.AreEqual(0, ExternalChartIO.Windows(1, new[] { P("nope.osu") }, new[] { 0 }, 0).Count);
            Assert.AreEqual(0, ExternalChartIO.Windows(1, null, null, 0).Count);
            Assert.AreEqual(0, ExternalChartIO.Windows(0, new[] { P("nope.osu") }, new[] { 0 }, 0).Count);
        }

        private string P(string file) => Path.Combine(_dir, file);

        /// <summary>一張 4K osu!mania 譜，音符落在指定的毫秒上。</summary>
        private void Osu(string file, params int[] timesMs)
        {
            var sb = new StringBuilder();
            sb.Append("osu file format v14\n\n[General]\nAudioFilename: a.mp3\nPreviewTime: 1000\nMode: 3\n\n");
            sb.Append("[Metadata]\nTitle:T\nArtist:A\nVersion:V\n\n[Difficulty]\nCircleSize:4\n\n");
            sb.Append("[TimingPoints]\n0,500,4,2,0,100,1,0\n\n[HitObjects]\n");
            for (int i = 0; i < timesMs.Length; i++)
                sb.Append(64 + (i % 4) * 128).Append(",192,").Append(timesMs[i]).Append(",1,0,0:0:0:0:\n");
            File.WriteAllText(P(file), sb.ToString());
        }

        /// <summary>一個 .sm，每個參數是一個 #NOTES 區塊的 row（"," = 小節分隔）。</summary>
        private void Sm(string file, params string[][] blocks)
        {
            var sb = new StringBuilder();
            sb.Append("#TITLE:T;\n#ARTIST:A;\n#MUSIC:a.mp3;\n#OFFSET:0.000;\n#BPMS:0.000=120.000;\n");
            foreach (var rows in blocks)
            {
                sb.Append("#NOTES:\n     dance-single:\n     :\n     Hard:\n     8:\n     0,0,0,0,0:\n");
                sb.Append(string.Join("\n", rows)).Append("\n;\n");
            }
            File.WriteAllText(P(file), sb.ToString());
        }
    }
}
