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

        /// <summary>StepMania 的打拍音是一顆**手拍(clap)**(theme 的 "assist tick" 音檔,DDR 系的 clap);
        /// 那顆 wav 不在這份 source 樹裡(SM-YHANIKI-master 只有 src/ 和 Program/,沒有 Themes/),SDO 的 SE 音色庫
        /// 也沒有 clap,所以照 clap 的物理模型**合成**一顆(單聲道 PCM,[-1,1]):
        ///
        ///   • 三顆極短的噪音爆點(間隔 ~9.5ms、各自 ~2ms 衰減)—— 手掌拍擊的多次接觸,就是 clap 那個「啪」的顆粒感;
        ///   • 一條較長的噪音尾巴(~45ms 衰減)—— 拍完的殘響,clap 聽起來比單純 click「有身體」的原因;
        ///   • 帶通(HP ~600Hz + LP ~4kHz)—— 去掉低頻轟隆與過亮的嘶聲,落在人耳最容易對拍的頻段。
        ///
        /// 亂數是固定 seed 的 LCG(不用 System.Random),所以這是**純函式**:同樣輸入永遠同樣波形,可單元測試。
        /// 播放時不隨遊戲流速變速(StepMania 也是:m_soundAssistTick.Play 沒有 SetPlaybackRate)。
        /// </summary>
        public static float[] RenderClap(int sampleRate, double lengthSec = 0.15)
        {
            if (sampleRate <= 0) return new float[0];
            int n = Math.Max(1, (int)Math.Round(sampleRate * Math.Max(0.001, lengthSec)));
            var buf = new float[n];

            uint seed = 0x9E3779B9;                                 // 固定 seed → 波形完全可重現
            double lp = 0.0, hpPrev = 0.0, hpOut = 0.0;
            double aLp = 1.0 - Math.Exp(-2 * Math.PI * 4000.0 / sampleRate);   // 低通 4kHz
            double aHp = Math.Exp(-2 * Math.PI * 600.0 / sampleRate);          // 高通 600Hz(一階)

            for (int i = 0; i < n; i++)
            {
                double t = (double)i / sampleRate;

                double env = 0.0;
                for (int k = 0; k < 3; k++)                          // 三連爆點 = 手掌的多次接觸
                {
                    double tk = t - k * 0.0095;
                    if (tk >= 0.0) env = Math.Max(env, Math.Exp(-tk / 0.0022));
                }
                if (t >= 0.019) env = Math.Max(env, 0.5 * Math.Exp(-(t - 0.019) / 0.045));   // 殘響尾巴
                env *= Math.Min(1.0, t / 0.0003);                   // 0.3ms 起音斜坡:噪音直接從滿振幅切入 = 一個 DC 階躍(多一聲爆音)

                seed = seed * 1664525u + 1013904223u;               // LCG 白噪音
                double noise = (int)(seed >> 8) / 8388608.0 - 1.0;  // [-1,1)

                double x = noise * env;
                lp += (x - lp) * aLp;                               // 低通
                hpOut = aHp * (hpOut + lp - hpPrev); hpPrev = lp;   // 高通
                buf[i] = (float)hpOut;
            }

            // 尾端 5ms 淡出(切斷噪音會有爆音)+ 正規化到 0.9(合成音量不該隨參數飄)
            int fade = Math.Max(1, (int)(sampleRate * 0.005));
            for (int i = Math.Max(0, n - fade); i < n; i++) buf[i] *= (float)(n - i) / fade;
            float peak = 0f;
            for (int i = 0; i < n; i++) { float a = Math.Abs(buf[i]); if (a > peak) peak = a; }
            if (peak > 1e-6f) { float g = 0.9f / peak; for (int i = 0; i < n; i++) buf[i] *= g; }
            return buf;
        }
    }
}
