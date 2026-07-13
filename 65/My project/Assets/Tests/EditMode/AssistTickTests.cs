using System;
using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    /// <summary>
    /// 打拍音 (F7 assist tick) 的純邏輯 — StepMania PlayTicks() 的移植。
    /// 重點:同一 row 只響一聲、每個 tick 只交出一次、關閉期間 rewind 不會補播過去的 tick。
    /// </summary>
    public class AssistTickTests
    {
        [Test]
        public void BuildTimeline_SortsAndCollapsesSimultaneousNotes()
        {
            // 三軌同時 + 一個晚 0.5ms 的浮點誤差 → 一個 row 一聲
            var t = AssistTick.BuildTimeline(new[] { 500.0, 1000.0, 0.0, 1000.0, 1000.5, 250.0 });
            Assert.AreEqual(new[] { 0.0, 250.0, 500.0, 1000.0 }, t);
        }

        [Test]
        public void BuildTimeline_EmptyOrNull()
        {
            Assert.AreEqual(0, AssistTick.BuildTimeline(null).Length);
            Assert.AreEqual(0, AssistTick.BuildTimeline(new double[0]).Length);
        }

        [Test]
        public void TryDequeue_OnlyWithinHorizon_AndOncePerTick()
        {
            var a = new AssistTick();
            a.Load(new[] { 0.0, 100.0, 400.0 });

            // now = 0, lookahead 250 → 0 與 100 該排,400 還不到
            Assert.IsTrue(a.TryDequeue(250.0, out double t1)); Assert.AreEqual(0.0, t1);
            Assert.IsTrue(a.TryDequeue(250.0, out double t2)); Assert.AreEqual(100.0, t2);
            Assert.IsFalse(a.TryDequeue(250.0, out _));        // 同一幀不會重複交出已排的
            Assert.AreEqual(1, a.Remaining);

            // 下一幀 now = 200 → 400 進入視窗
            Assert.IsTrue(a.TryDequeue(450.0, out double t3)); Assert.AreEqual(400.0, t3);
            Assert.IsFalse(a.TryDequeue(1e9, out _));          // 排完了
            Assert.AreEqual(0, a.Remaining);
        }

        [Test]
        public void Rewind_SkipsPastTicks_SoTurningItOnMidSongDoesNotDumpThemAll()
        {
            var a = new AssistTick();
            a.Load(new[] { 0.0, 100.0, 200.0, 300.0, 400.0 });

            a.Rewind(250.0);                 // 關閉期間游標一直被推到「現在」;中途才打開
            Assert.AreEqual(2, a.Remaining); // 只剩 300 / 400

            Assert.IsTrue(a.TryDequeue(1e9, out double t)); Assert.AreEqual(300.0, t);
        }

        [Test]
        public void Rewind_Boundaries()
        {
            var a = new AssistTick();
            a.Load(new[] { 100.0, 200.0 });

            a.Rewind(100.0);                 // 剛好落在 tick 上 → 這一聲還沒播,要留著
            Assert.AreEqual(2, a.Remaining);

            a.Rewind(-5000.0);               // 開場前導(負的譜面時間)→ 全部都還在
            Assert.AreEqual(2, a.Remaining);

            a.Rewind(1e9);                   // 曲末之後 → 沒有東西可排
            Assert.AreEqual(0, a.Remaining);
            Assert.IsFalse(a.TryDequeue(1e9, out _));
        }

        [Test]
        public void Load_ResetsCursor()
        {
            var a = new AssistTick();
            a.Load(new[] { 0.0, 100.0 });
            a.TryDequeue(1e9, out _);
            a.Load(new[] { 50.0, 150.0, 250.0 });   // 換一首歌
            Assert.AreEqual(3, a.Count);
            Assert.AreEqual(3, a.Remaining);
            Assert.IsTrue(a.TryDequeue(1e9, out double t)); Assert.AreEqual(50.0, t);
        }

        [Test]
        public void RenderClick_IsAShortDecayingClickInRange()
        {
            const int rate = 48000;
            var pcm = AssistTick.RenderClick(rate, lengthSec: 0.04);
            Assert.AreEqual((int)Math.Round(rate * 0.04), pcm.Length);

            float peak = 0f, last = 0f;
            int peakAt = 0;
            for (int i = 0; i < pcm.Length; i++)
            {
                Assert.LessOrEqual(Math.Abs(pcm[i]), 1f, "sample " + i + " clips");
                if (Math.Abs(pcm[i]) > peak) { peak = Math.Abs(pcm[i]); peakAt = i; }
                last = Math.Abs(pcm[i]);
            }
            Assert.Greater(peak, 0.3f, "click 太小聲");
            Assert.AreEqual(0f, pcm[0], 1e-6f, "起音要從 0 開始(否則爆一聲 DC click)");
            Assert.Less(peakAt, pcm.Length / 4, "能量該集中在起音,是 click 不是持續音");
            Assert.Less(last, 0.01f, "尾端要收到 0(不留切斷的爆音)");
        }

        [Test]
        public void RenderClick_BadSampleRate()
        {
            Assert.AreEqual(0, AssistTick.RenderClick(0).Length);
        }
    }
}
