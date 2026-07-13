using System;
using System.Collections.Generic;

namespace Sdo.Ruleset
{
    /// <summary>
    /// 「打拍音」(assist tick) 的純邏輯 —— 移植自 StepMania 的 <c>ScreenGameplay::PlayTicks()</c>
    /// (assets/SM-YHANIKI-master/src/ScreenGameplay.cpp:1215)。打的是**音符**不是節拍器:譜面上每一個
    /// 有 tap / hold-head 的 row 響一聲 click,同一時間點的多個音符只響一聲(StepMania 是「每個 row 最多一次」)。
    ///
    /// StepMania 不是「到了就播」——音效卡有輸出延遲,所以它提前 <c>latency + TickEarlySeconds + 0.25s</c>
    /// 掃出即將到來的 tick,再用精準的起播時間排程(<c>RageSoundParams::StartTime</c>)。這裡照抄:
    /// <see cref="TryDequeue"/> 交出「地平線(now + lookahead)之前、還沒排過」的 tick 時間,呼叫端把它換算成
    /// dspTime 用 <c>AudioSource.PlayScheduled</c> 排進去 —— 於是 tick 的時間精度來自音訊時鐘,不受 frame rate 影響。
    ///
    /// 狀態只有一個游標;<see cref="Rewind"/> 把游標移到某個時間點(關閉期間每幀 rewind,
    /// 中途才打開才不會把過去累積的 tick 一次全倒出來)。
    /// </summary>
    public sealed class AssistTick
    {
        /// <summary>提前排程的視窗(ms)。StepMania = 音效卡延遲 + TickEarlySeconds + 0.25s。</summary>
        public const double DefaultLookaheadMs = 250.0;

        /// <summary>同一 row 的判定寬度(ms):相隔在這之內的音符視為同時,只響一聲。</summary>
        public const double DefaultRowEpsilonMs = 1.0;

        private static readonly double[] Empty = new double[0];

        private double[] _times = Empty;
        private int _next;

        /// <summary>時間軸上的 tick 數。</summary>
        public int Count { get { return _times.Length; } }

        /// <summary>還沒被交出去的 tick 數。</summary>
        public int Remaining { get { return _times.Length - _next; } }

        /// <summary>音符起始時間(ms,任意順序、可重複) → 排序去重後的 tick 時間軸。</summary>
        public static double[] BuildTimeline(IEnumerable<double> noteStartMs, double rowEpsilonMs = DefaultRowEpsilonMs)
        {
            if (noteStartMs == null) return Empty;
            var list = new List<double>();
            foreach (var t in noteStartMs) list.Add(t);
            if (list.Count == 0) return Empty;
            list.Sort();
            var outList = new List<double>(list.Count);
            foreach (var t in list)
                if (outList.Count == 0 || t - outList[outList.Count - 1] > rowEpsilonMs) outList.Add(t);   // 同 row = 一聲
            return outList.ToArray();
        }

        /// <summary>載入譜面的音符起始時間並把游標歸零。</summary>
        public void Load(IEnumerable<double> noteStartMs, double rowEpsilonMs = DefaultRowEpsilonMs)
        {
            _times = BuildTimeline(noteStartMs, rowEpsilonMs);
            _next = 0;
        }

        /// <summary>把游標移到 <paramref name="nowMs"/>:早於它的 tick 全部視為已播(不會補播)。</summary>
        public void Rewind(double nowMs)
        {
            int lo = 0, hi = _times.Length;
            while (lo < hi)   // 第一個 >= nowMs 的位置
            {
                int mid = (lo + hi) / 2;
                if (_times[mid] < nowMs) lo = mid + 1; else hi = mid;
            }
            _next = lo;
        }

        /// <summary>交出下一個「不晚於 <paramref name="horizonMs"/>」的 tick(通常 horizon = now + lookahead)。
        /// 每個 tick 只會交出一次;沒有就回 false。呼叫端用 while 迴圈把這一幀該排的全部取完。</summary>
        public bool TryDequeue(double horizonMs, out double tickMs)
        {
            if (_next < _times.Length && _times[_next] <= horizonMs) { tickMs = _times[_next++]; return true; }
            tickMs = 0.0;
            return false;
        }

        /// <summary>合成一聲 click(單聲道 PCM,[-1,1])。遊戲的 SE 音色庫裡沒有 StepMania 那顆 clap,
        /// 而打拍音只要「短、脆、對得準」,所以直接合成:1.5ms 起音 + 指數衰減的 1.8kHz 基音(疊一個八度泛音)。
        /// 純函式(不吃亂數),可單元測試。</summary>
        public static float[] RenderClick(int sampleRate, double lengthSec = 0.04, double freqHz = 1800.0, double decay = 90.0)
        {
            if (sampleRate <= 0) return new float[0];
            int n = Math.Max(1, (int)Math.Round(sampleRate * Math.Max(0.001, lengthSec)));
            var buf = new float[n];
            double attack = 0.0015;   // 起音斜坡:直接從 1 開始會爆一聲 DC click
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / sampleRate;
                double env = Math.Exp(-decay * t) * Math.Min(1.0, t / attack);
                double tail = 1.0 - (double)i / n;                      // 尾端拉到 0,不留突然切斷的爆音
                double s = 0.75 * Math.Sin(2 * Math.PI * freqHz * t)
                         + 0.25 * Math.Sin(4 * Math.PI * freqHz * t);   // + 一個八度
                buf[i] = (float)Math.Max(-1.0, Math.Min(1.0, s * env * tail));
            }
            return buf;
        }
    }
}
