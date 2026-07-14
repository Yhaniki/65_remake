using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 譜面編輯器的格線：拍 ↔ 毫秒互轉、小節線位置。.gn 是 4/4（一小節 4 拍），變速歌的第二段之後
    /// 必須接在前一段的累積時間上，不然編輯器畫的小節線會跟音符（GnChart 算出來的 ms）對不起來。
    /// </summary>
    public class BeatGridTests
    {
        private static List<OsuTimingPoint> Pts(params (double ms, double bpm)[] segs)
        {
            var l = new List<OsuTimingPoint>();
            foreach (var s in segs) l.Add(new OsuTimingPoint(s.ms, 60000.0 / s.bpm));
            return l;
        }

        [Test]
        public void SingleBpm_BeatAndMs_RoundTrip()
        {
            var g = new BeatGrid(Pts((0, 120)));           // 120 BPM → 一拍 500ms
            Assert.AreEqual(0.0, g.BeatToMs(0), 1e-9);
            Assert.AreEqual(500.0, g.BeatToMs(1), 1e-9);
            Assert.AreEqual(2000.0, g.BeatToMs(4), 1e-9);  // 第 1 小節線
            Assert.AreEqual(4.0, g.MsToBeat(2000.0), 1e-9);
            Assert.AreEqual(120.0, g.BpmAt(1234.0), 1e-9);
        }

        [Test]
        public void NoTimingPoints_FallsBackToHeaderBpm()
        {
            var g = new BeatGrid(null, 150.0);             // 150 BPM → 一拍 400ms
            Assert.AreEqual(400.0, g.BeatToMs(1), 1e-9);
            Assert.AreEqual(1, g.MeasureAt(1700.0));       // 1600ms = 小節 1 的起點
        }

        [Test]
        public void BpmChange_SecondSegment_StacksOnFirst()
        {
            // 120 BPM 走 4 拍（0..2000ms），之後變 240 BPM（一拍 250ms）
            var g = new BeatGrid(Pts((0, 120), (2000, 240)));
            Assert.AreEqual(2000.0, g.BeatToMs(4), 1e-9);
            Assert.AreEqual(2250.0, g.BeatToMs(5), 1e-9);   // 變速後的第一拍只花 250ms
            Assert.AreEqual(3000.0, g.BeatToMs(8), 1e-9);   // 第 2 小節線
            Assert.AreEqual(8.0, g.MsToBeat(3000.0), 1e-9);
            Assert.AreEqual(240.0, g.BpmAt(2500.0), 1e-9);
            Assert.AreEqual(2, g.MeasureAt(3000.0));
        }

        [Test]
        public void MeasureStartMs_MatchesGnBeatSpace()
        {
            // .gn: beat = measurement*4 → 小節 3 = 第 12 拍
            var g = new BeatGrid(Pts((0, 100)));            // 一拍 600ms
            Assert.AreEqual(g.BeatToMs(12), g.MeasureStartMs(3), 1e-9);
            Assert.AreEqual(7200.0, g.MeasureStartMs(3), 1e-9);
        }

        [Test]
        public void LinesInWindow_ClassifiesMeasureBeatAndSub()
        {
            var g = new BeatGrid(Pts((0, 120)));            // 一拍 500ms、一小節 2000ms
            var lines = new List<BeatGrid.Line>();
            g.LinesInWindow(0, 2000, 2, lines);             // 每拍細分 2 → 8 分音

            Assert.AreEqual(9, lines.Count);                                  // 0, 250, 500 … 2000
            Assert.AreEqual(BeatGrid.LineKind.Measure, lines[0].Kind);        // beat 0
            Assert.AreEqual(BeatGrid.LineKind.Sub, lines[1].Kind);            // beat 0.5
            Assert.AreEqual(BeatGrid.LineKind.Beat, lines[2].Kind);           // beat 1
            Assert.AreEqual(BeatGrid.LineKind.Measure, lines[8].Kind);        // beat 4 = 下一條小節線
            Assert.AreEqual(2000.0, lines[8].Ms, 1e-9);
        }

        [Test]
        public void LinesInWindow_SkipsNegativeBeatsAndRespectsCap()
        {
            var g = new BeatGrid(Pts((0, 120)));
            var lines = new List<BeatGrid.Line>();

            g.LinesInWindow(-5000, 1000, 1, lines);         // 譜面沒有負拍 → 只從 beat 0 起
            Assert.AreEqual(0.0, lines[0].Ms, 1e-9);
            Assert.AreEqual(3, lines.Count);               // beat 0/1/2 (0, 500, 1000ms)

            g.LinesInWindow(0, 600000, 4, lines, maxLines: 32);   // 超長視窗 → 上限收工，不吃光 CPU
            Assert.LessOrEqual(lines.Count, 33);
        }
    }
}
