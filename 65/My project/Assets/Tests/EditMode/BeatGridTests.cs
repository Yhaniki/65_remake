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

        // ---------------------------------------------------------------------------------------------------
        // StepMania 的 #STOPS / 負 BPM(warp)。格線一定要走**真實**時間軸(OsuBeatmap.GridSegments):
        // TimingPoints 是捲動用的顯示時間軸 —— warp 在上面被壓成 1ms 的超高速窗、停拍根本不在裡面,
        // 拿它反推 beat 編號的話每個停拍都會多算「停多久 ÷ 一拍」,warp 後面更是把定格的 200 多 ms
        // 當成一拍 0.125ms 的超高速段換算,一次多算上千拍(engine[Blue] 實測第 48 小節起就開始歪)。
        // ---------------------------------------------------------------------------------------------------

        private const string SmHead = "#NOTES:\n     dance-single:\n     :\n     Easy:\n     1:\n     0,0,0,0,0:\n";
        private const string SmBody =    // 每拍一顆,4 小節(beats 0..15)
            "1000\n1000\n1000\n1000\n,\n1000\n1000\n1000\n1000\n,\n" +
            "1000\n1000\n1000\n1000\n,\n1000\n1000\n1000\n1000\n;\n";

        private static BeatGrid GridOf(string header, out OsuBeatmap map)
        {
            map = SmChart.ToBeatmap(SmChart.Parse(header + SmHead + SmBody), 0);
            return BeatGrid.From(map);
        }

        [Test]
        public void Stop_PushesEveryLaterLine_ByTheFreezeDuration()
        {
            // 120 BPM(一拍 500ms),beat 4 停 1 秒。
            var g = GridOf("#TITLE:S;\n#OFFSET:0;\n#BPMS:0=120;\n#STOPS:4.000=1.000;\n", out var map);
            Assert.AreEqual(2000.0, g.BeatToMs(4), 1e-6, "停拍那一拍 = 定格的起點");
            Assert.AreEqual(3500.0, g.BeatToMs(5), 1e-6, "停完 1 秒才走下一拍");
            Assert.AreEqual(5000.0, g.BeatToMs(8), 1e-6, "第 2 小節線 = 4000 + 停的 1000");
            Assert.AreEqual(4.0, g.MsToBeat(2500.0), 1e-9, "定格中:拍子不動");
            Assert.AreEqual(4.0, g.MsToBeat(3000.0), 1e-9);
            Assert.AreEqual(5.0, g.MsToBeat(3500.0), 1e-9);

            // 每一顆音符都要正好落在格線上 —— 這就是「後面的按鍵沒在小節線上」那個 bug 的判準。
            foreach (var h in map.HitObjects)
            {
                double b = g.MsToBeat(h.StartTimeMs);
                Assert.AreEqual(System.Math.Round(b), b, 1e-6, $"{h.StartTimeMs}ms 的音符沒落在整拍上");
            }
        }

        [Test]
        public void Warp_HasNoThicknessOnTheGrid()
        {
            // beats 4..12 被負 BPM 瞬間跳過:那段拍子的格線全部落在起跳的那一刻。
            var g = GridOf("#TITLE:W;\n#OFFSET:0;\n#BPMS:0=120,4=-120,8=120;\n", out _);
            Assert.AreEqual(2000.0, g.BeatToMs(4), 1e-6, "起跳");
            Assert.AreEqual(2000.0, g.BeatToMs(8), 1e-6, "warp 內");
            Assert.AreEqual(2000.0, g.BeatToMs(12), 1e-6, "落地");
            Assert.AreEqual(2500.0, g.BeatToMs(13), 1e-6, "之後照原時間接上");
            Assert.AreEqual(1500.0, g.BeatToMs(3), 1e-6);

            double prev = -1.0;
            for (double t = 0.0; t <= 4000.0; t += 25.0)
            {
                double b = g.MsToBeat(t);
                Assert.GreaterOrEqual(b, prev, $"{t}ms:拍數不能倒退");
                prev = b;
            }
        }

        [Test]
        public void NegativeBpmWithStops_KeepsNotesOnTheLines()
        {
            // engine[Blue] 的結構:負 4 拍 / 正 4 拍,接縫上放停拍 —— 兩種毛病(停拍 + warp)一起來。
            var g = GridOf("#TITLE:WS;\n#OFFSET:0;\n#BPMS:0=120,4=-120,8=120,12=-120,16=120;\n" +
                           "#STOPS:12.000=0.500;\n", out var map);
            Assert.AreEqual(2000.0, g.BeatToMs(12), 1e-6, "warp 落地 = 定格起點");
            Assert.AreEqual(2500.0, g.BeatToMs(20), 1e-6, "定格 0.5 秒之後第二段 warp 落地");
            Assert.AreEqual(3000.0, g.BeatToMs(21), 1e-6, "之後照原時間接上");
            Assert.AreEqual(3.998, g.MsToBeat(1999.0), 1e-9, "warp 之前照常走");
            Assert.AreEqual(12.0, g.MsToBeat(2250.0), 1e-9, "定格中停在 beat 12");
            foreach (var h in map.HitObjects)
            {
                if (h.IsFake) continue;              // warp 內的裝飾音在時間軸上疊在一點
                double b = g.MsToBeat(h.StartTimeMs);
                Assert.AreEqual(System.Math.Round(b), b, 1e-6, $"{h.StartTimeMs}ms 的音符沒落在整拍上");
            }
        }

        [Test]
        public void ChartsWithoutGridSegments_StillUseTheTimingPoints()
        {
            var map = new OsuBeatmap { Bpm = 120.0 };
            map.TimingPoints.Add(new OsuTimingPoint(0.0, 500.0));
            Assert.AreEqual(0, map.GridSegments.Count, ".gn/.osu 不帶真實時間軸");
            var g = BeatGrid.From(map);
            Assert.AreEqual(2000.0, g.BeatToMs(4), 1e-9);
            Assert.AreEqual(4.0, g.MsToBeat(2000.0), 1e-9);
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
        public void FirstTimingPointAtPositiveMs_AnchorsBeatZeroThere_NotAtMsZero()
        {
            // Hibana 迴歸：SM #OFFSET 為負(−0.041) → SmChart 把 beat 0 的 timing point 放在 +41ms。格線的 beat 0 一定要
            // 跟音符同錨在那 41ms，否則(舊 bug 在 ms 0 硬插 beat 0)整條線位移，音符落不到線上。200 BPM → 一拍 300ms。
            var g = new BeatGrid(Pts((41, 200)));
            Assert.AreEqual(0.0, g.MsToBeat(41.0), 1e-9);       // beat 0 就在 timing point 上，不是 ms 0
            Assert.AreEqual(41.0, g.BeatToMs(0), 1e-9);
            // 音符 beat 4（SmChart 給的 chart 時間 = 4×300 − (−41) = 1241ms）必須正好落在 beat 4 的格線上。
            Assert.AreEqual(4.0, g.MsToBeat(1241.0), 1e-9);
            Assert.AreEqual(1241.0, g.BeatToMs(4), 1e-9);       // beat 4 = 第 1 小節線，音符在線上
            Assert.AreEqual(1, g.MeasureAt(1241.0));
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
        public void SnapOf_ClassifiesTheStepManiaQuantizations()
        {
            // 一小節 192 row、一拍 48 row。4分每 48、8分每 24、12分每 16、16分每 12、24分每 8、32分每 6。
            Assert.AreEqual(4, BeatGrid.SnapOf(0.0));       // 正拍
            Assert.AreEqual(4, BeatGrid.SnapOf(3.0));
            Assert.AreEqual(8, BeatGrid.SnapOf(0.5));       // 反拍
            Assert.AreEqual(12, BeatGrid.SnapOf(1.0 / 3));  // 三連音
            Assert.AreEqual(16, BeatGrid.SnapOf(0.25));
            Assert.AreEqual(24, BeatGrid.SnapOf(1.0 / 6));
            Assert.AreEqual(32, BeatGrid.SnapOf(0.125));
            Assert.AreEqual(0, BeatGrid.SnapOf(1.0 / 12));  // 48分 → 更細的一律 0（畫成白色）
            Assert.AreEqual(0, BeatGrid.SnapOf(1.0 / 16));  // 64分
        }

        [Test]
        public void LinesInWindow_TagsEachLineWithItsSnap()
        {
            var g = new BeatGrid(Pts((0, 120)));
            var lines = new List<BeatGrid.Line>();
            g.LinesInWindow(0, 2000, 4, lines);             // 每拍 4 格 = 16 分音

            Assert.AreEqual(4, lines[0].Snap);              // beat 0   → 4分
            Assert.AreEqual(16, lines[1].Snap);             // beat .25 → 16分
            Assert.AreEqual(8, lines[2].Snap);              // beat .5  → 8分
            Assert.AreEqual(16, lines[3].Snap);             // beat .75 → 16分
            Assert.AreEqual(4, lines[4].Snap);              // beat 1   → 4分
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
