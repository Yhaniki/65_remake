using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    /// <summary>
    /// 整體遊戲流速（StepMania music rate）。重點是 dsp（真實時間）↔ 譜面時間的換算：
    /// 中途改速度時必須重新錨定，否則譜面時間會瞬間跳掉 → 音符/判定當場錯位。
    /// </summary>
    public class GameRateTests
    {
        [Test]
        public void Clamp_And_Defaults()
        {
            Assert.AreEqual(GameRate.Max, GameRate.Clamp(99.0), 1e-9);
            Assert.AreEqual(GameRate.Min, GameRate.Clamp(0.0), 1e-9);
            Assert.AreEqual(1.0, GameRate.Clamp(double.NaN), 1e-9);
            Assert.AreEqual(1.25, GameRate.Clamp(1.25), 1e-9);
        }

        [Test]
        public void Step_MovesByFiveHundredths_AndSnapsToTheGrid()
        {
            Assert.AreEqual(1.05, GameRate.Step(1.0, +1), 1e-9);
            Assert.AreEqual(0.95, GameRate.Step(1.0, -1), 1e-9);
            Assert.AreEqual(1.00, GameRate.Step(0.97, +1), 1e-9);   // 不在格線上 → 先吸到上一格
            Assert.AreEqual(0.95, GameRate.Step(0.97, -1), 1e-9);   // 再往下就是下一格
        }

        [Test]
        public void Step_ClampsAtBothEnds()
        {
            Assert.AreEqual(GameRate.Max, GameRate.Step(GameRate.Max, +1), 1e-9);
            Assert.AreEqual(GameRate.Min, GameRate.Step(GameRate.Min, -1), 1e-9);
        }

        [Test]
        public void ChartSecondsFromDsp_RoundTrips()
        {
            const double anchor = 100.0, countIn = 1.2;
            foreach (var rate in new[] { 0.5, 1.0, 1.5 })
            {
                double chart = GameRate.ChartSecondsFromDsp(anchor + 4.0, anchor, rate, countIn);
                double dsp = GameRate.DspFromChartSeconds(chart, anchor, rate, countIn);
                Assert.AreEqual(anchor + 4.0, dsp, 1e-9, "rate " + rate);
            }
        }

        [Test]
        public void MusicStartsAtTheCountIn_AndChartTimeRunsAtTheRate()
        {
            const double anchor = 50.0, countIn = 2.0;
            // 起播那一刻（dsp = anchor）＝ 譜面上的「音樂起點」＝ count-in
            Assert.AreEqual(countIn, GameRate.ChartSecondsFromDsp(anchor, anchor, 1.5, countIn), 1e-9);
            // 1.5× 時，真實 2 秒 = 譜面 3 秒
            Assert.AreEqual(countIn + 3.0, GameRate.ChartSecondsFromDsp(anchor + 2.0, anchor, 1.5, countIn), 1e-9);
        }

        [Test]
        public void TimeUntilTick_IsHalvedAtDoubleRate()   // StepMania: "2x music rate means the time until the tick is halved"
        {
            const double anchor = 10.0, countIn = 0.0;
            double at1x = GameRate.DspFromChartSeconds(4.0, anchor, 1.0, countIn) - anchor;
            double at2x = GameRate.DspFromChartSeconds(4.0, anchor, 2.0, countIn) - anchor;
            Assert.AreEqual(4.0, at1x, 1e-9);
            Assert.AreEqual(at1x / 2.0, at2x, 1e-9);
        }

        [Test]
        public void Reanchor_KeepsChartTimeContinuousAcrossARateChange()
        {
            const double countIn = 1.0;
            double anchor = 20.0, rate = 1.0;
            double dspNow = 25.0;                                               // 播了 5 秒真實時間

            double before = GameRate.ChartSecondsFromDsp(dspNow, anchor, rate, countIn);
            Assert.AreEqual(6.0, before, 1e-9);                                 // 5 秒 + count-in

            double newRate = 0.5;                                               // 當場切成半速
            double newAnchor = GameRate.AnchorForChartSeconds(dspNow, before, newRate, countIn);
            double after = GameRate.ChartSecondsFromDsp(dspNow, newAnchor, newRate, countIn);
            Assert.AreEqual(before, after, 1e-9, "改速度不能讓譜面時間跳掉");

            // 而且往後真的走一半速度：再 2 秒真實時間 = 譜面 1 秒
            Assert.AreEqual(before + 1.0, GameRate.ChartSecondsFromDsp(dspNow + 2.0, newAnchor, newRate, countIn), 1e-9);
        }

        [Test]
        public void Reanchor_AfterAPause_ResumesAtTheSameChartTime()
        {
            const double countIn = 0.5, rate = 1.25;
            double anchor = 3.0;
            double pausedAtChart = GameRate.ChartSecondsFromDsp(8.0, anchor, rate, countIn);

            double dspResume = 30.0;   // 暫停了 22 秒真實時間（dsp 一直在跑）
            double newAnchor = GameRate.AnchorForChartSeconds(dspResume, pausedAtChart, rate, countIn);
            Assert.AreEqual(pausedAtChart, GameRate.ChartSecondsFromDsp(dspResume, newAnchor, rate, countIn), 1e-9);
        }

        [Test]
        public void StartDspFor_ScalesBothLeadInsByTheRate()
        {
            const double dspNow = 100.0, lead = 0.1, countIn = 2.0;
            Assert.AreEqual(dspNow + lead + countIn, GameRate.StartDspFor(dspNow, lead, countIn, 1.0), 1e-9);
            // 兩段前導都是譜面時間 → 2× 時真實只等一半
            Assert.AreEqual(dspNow + (lead + countIn) / 2.0, GameRate.StartDspFor(dspNow, lead, countIn, 2.0), 1e-9);
            // 起播那刻的譜面時間仍然剛好是 count-in（前導剛好燒完）
            double s = GameRate.StartDspFor(dspNow, lead, countIn, 0.5);
            Assert.AreEqual(countIn, GameRate.ChartSecondsFromDsp(s, s, 0.5, countIn), 1e-9);
            Assert.AreEqual(-lead, GameRate.ChartSecondsFromDsp(dspNow, s, 0.5, countIn), 1e-9);   // 此刻 = 譜面 −lead
        }
    }
}
