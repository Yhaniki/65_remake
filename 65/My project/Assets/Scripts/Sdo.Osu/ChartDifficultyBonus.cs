using System;
using System.Collections.Generic;

namespace Sdo.Osu
{
    /// <summary>
    /// 「同一張譜，只是多灑了炸彈、或多了變速，就該比原版難一點點」——把這件事變成一個乘在難度上的小加成，
    /// osu 星數（<see cref="ManiaStarRating.CalculateAdjusted"/>）和 MinaCalc MSD
    /// （<see cref="ManiaMsd.OverallAdjusted"/>）共用同一份，兩套計算器才不會給出相反的排序。
    ///
    /// 為什麼兩個計算器自己算不出來：它們都只看「哪個 lane 在哪個時刻有一顆**要打**的音符」。炸彈永遠不判定
    /// （<see cref="ManiaStarRating"/> / <see cref="ManiaMsd.ToNoteInfo"/> 都刻意跳過它——算進去會雙重灌水，
    /// 灑滿雷的慢譜曾經從 LV6 虛高到 LV44），而變速（BPM 換段、osu 綠線 SV、SDO 捲動速度、StepMania 停拍）
    /// 只改音符怎麼捲過畫面。所以「原譜」和「原譜＋雷＋變速」在它們眼裡是同一張，算出來一模一樣；實際打起來
    /// 卻是炸彈要閃、變速要重新抓落點，就是比較難。
    ///
    /// 做法是**不動兩個計算器的核心**（各自對照過 oracle，是逐位相符的移植，見它們的類別註解），只在外面包一層：
    /// <see cref="Multiplier"/> ＝ 1 + 炸彈項 + 變速項，兩項各自飽和在 <see cref="BombMax"/> / <see cref="SpeedMax"/>。
    /// 上限刻意壓得很小（合計最多 +8%）——它是「同一張譜之間」的排序修正，不是難度來源，灑滿雷的慢譜不該
    /// 因此爬到快譜上面去。沒有炸彈也沒有變速的譜拿到 1.0，等級與加這層之前**完全相同**。
    ///
    /// 純函式、無引擎相依、可單元測試（見 ChartDifficultyBonusTests）。
    /// </summary>
    public static class ChartDifficultyBonus
    {
        /// <summary>炸彈項的上限（+4%）：炸彈再多也只加這麼多。</summary>
        public const double BombMax = 0.04;

        /// <summary>變速項的上限（+4%）：變速再兇也只加這麼多。</summary>
        public const double SpeedMax = 0.04;

        /// <summary>
        /// 炸彈 ÷ 可打音符 到這個比例時，炸彈項拿到上限的一半。0.1 ＝ 每 10 顆音符夾 1 顆雷。
        /// 挑這個數是為了對**真實**譜面有解析度：外部 .sm 譜的雷通常只佔千分之幾到百分之幾
        /// （實測 666.sm 1745 音符 8 顆雷、灯火 824 音符 4 顆），飽和點放在 0.5 的話這些譜全部擠在 +0.05% 以內，
        /// 等於沒分。放 0.1 之後「零星幾顆雷」還是幾乎不加分（該有的樣子），「整首灑雷」才吃得到上限。
        /// </summary>
        private const double BombHalf = 0.1;

        /// <summary>變速分數到這個值時，變速項拿到上限的一半。</summary>
        private const double SpeedHalf = 0.5;

        /// <summary>
        /// 單一段速度對「整首平均速度」的偏離取 log2 之後的上限。這條是給 StepMania warp 用的：
        /// <see cref="SmChart"/> 把負 BPM 壓成 1 ms 的超高速捲動窗，倍率可以到上千（log2 ≈ 10），
        /// 沒有這條的話一個 warp 就能把整首的變速分數炸滿。它的時間權重本來就趨近 0，再夾一次當保險。
        /// </summary>
        private const double LogClamp = 3.0;

        /// <summary>停拍佔譜面時長的比例換算成變速分數的係數：停拍佔 6.25% → 0.5 分（＝變速項的一半）。</summary>
        private const double StopWeight = 8.0;

        /// <summary>
        /// 這張譜的難度加成倍率，1.0（沒炸彈也沒變速）到 1 + <see cref="BombMax"/> + <see cref="SpeedMax"/>。
        /// 直接乘在星數 / MSD 上。null 或空譜 → 1.0。
        /// </summary>
        public static double Multiplier(OsuBeatmap bm)
        {
            if (bm == null) return 1.0;
            return 1.0 + BombBonus(bm) + SpeedBonus(bm);
        }

        /// <summary>
        /// 炸彈項（0..<see cref="BombMax"/>）＝ 飽和(炸彈數 ÷ 可打音符數)。用**比例**不用絕對顆數：一首歌灑 200 顆雷，
        /// 在 3000 音符的譜上幾乎感覺不到，在 200 音符的譜上就是每顆音符旁邊都有雷。warp 掃掉的裝飾音兩邊都不算
        /// （那段譜玩家連一幀都碰不到）。整張只有炸彈 → 直接給滿，反正那種譜的星數/MSD 本來就是 0，乘什麼都是 0。
        /// </summary>
        public static double BombBonus(OsuBeatmap bm)
        {
            if (bm == null) return 0.0;
            int bombs = 0, playable = 0;
            foreach (var h in bm.HitObjects)
            {
                if (h.IsFake) continue;
                if (h.IsBomb) bombs++; else playable++;
            }
            if (bombs <= 0) return 0.0;
            if (playable <= 0) return BombMax;
            return Saturate((double)bombs / playable, BombHalf) * BombMax;
        }

        /// <summary>
        /// 變速項（0..<see cref="SpeedMax"/>）＝ 飽和(BPM 換段 ＋ osu 綠線 SV ＋ SDO 捲動速度 ＋ 停拍 四項分數相加)。
        /// 四個來源分開算再相加：它們的切點各自獨立（一個 BPM 段裡可以有好幾條綠線），硬合成單一條速度曲線
        /// 只會讓「一次變速被算兩次」的邊界更難講清楚，相加的近似對「有沒有變速、變多兇」這個量級足夠。
        /// </summary>
        public static double SpeedBonus(OsuBeatmap bm)
        {
            if (bm == null) return 0.0;
            double startMs = bm.FirstNoteMs, endMs = bm.LastNoteMs;
            double span = endMs - startMs;
            if (span <= 0.0) return 0.0;

            double score = TempoVariation(bm, startMs, endMs)
                         + SvVariation(bm, startMs, endMs)
                         + ScrollVariation(bm, startMs, endMs)
                         + StopScore(bm, span);
            return Saturate(score, SpeedHalf) * SpeedMax;
        }

        // ---- 四個變速來源 ----

        /// <summary>BPM 換段（uninherited timing point）。速度取 1/beatLength——比值才有意義，單位可以省。</summary>
        private static double TempoVariation(OsuBeatmap bm, double startMs, double endMs)
        {
            var times = new List<double>();
            var vals = new List<double>();
            foreach (var tp in bm.TimingPoints)
            {
                if (!tp.Uninherited || tp.BeatLength <= 0.0) continue;
                Push(times, vals, tp.TimeMs, 1.0 / tp.BeatLength);
            }
            return Variation(times, vals, startMs, endMs);
        }

        /// <summary>
        /// osu 綠線（inherited timing point）的 SV。照 osu 的語意重建：綠線把倍率設成
        /// <see cref="OsuTimingPoint.SpeedMultiplier"/> 並一直有效到下一條綠線，而**紅線（BPM 點）會把 SV 重設回 1.0**
        /// ——所以不能只挑綠線出來看，得照時間順序走完整條 timing point 清單。
        /// </summary>
        private static double SvVariation(OsuBeatmap bm, double startMs, double endMs)
        {
            var times = new List<double>();
            var vals = new List<double>();
            foreach (var tp in bm.TimingPoints)
            {
                double sv = tp.Uninherited ? 1.0 : tp.SpeedMultiplier;   // 紅線 = SV 重設回 1.0
                if (sv <= 0.0) continue;
                Push(times, vals, tp.TimeMs, sv);   // 值沒變的點會被 Push 自己吃掉，不會多出一段
            }
            return Variation(times, vals, startMs, endMs);
        }

        /// <summary>SDO frame_type 33 捲動速度（<see cref="OsuBeatmap.ScrollSpeeds"/>）；外部 osu/sm/mc 譜恆空 → 0。</summary>
        private static double ScrollVariation(OsuBeatmap bm, double startMs, double endMs)
        {
            var times = new List<double>();
            var vals = new List<double>();
            foreach (var ss in bm.ScrollSpeeds)
            {
                if (ss.Mult <= 0.0) continue;
                Push(times, vals, ss.TimeMs, ss.Mult);
            }
            return Variation(times, vals, startMs, endMs);
        }

        /// <summary>StepMania 停拍（<see cref="OsuBeatmap.Stops"/>）：定格總時長佔譜長的比例 × <see cref="StopWeight"/>。</summary>
        private static double StopScore(OsuBeatmap bm, double span)
        {
            double stopped = 0.0;
            foreach (var s in bm.Stops) if (s.DurationMs > 0.0) stopped += s.DurationMs;
            if (stopped <= 0.0) return 0.0;
            return Math.Min(1.0, stopped / span) * StopWeight;
        }

        // ---- 共用工具 ----

        /// <summary>
        /// 一條階梯速度曲線的「變化度」＝ 時間加權的 |log2(v ÷ 時間加權幾何平均)| 平均。
        ///
        /// 用**幾何平均當基準**而不是「第一個值」或「1.0」，是為了讓「整首都同一個速度」拿 0 分：整首 SV 0.7 的譜
        /// 跟整首 SV 1.0 的譜打起來一樣，不該有人多拿分；真正該加分的是**段與段之間差多少**。
        /// 時間加權則讓「只有一小節慢下來」不會跟「整首一半的時間都在慢速」拿一樣的分。
        /// </summary>
        private static double Variation(List<double> times, List<double> vals, double startMs, double endMs)
        {
            if (times.Count <= 1) return 0.0;   // 一段到底 = 沒有變速

            double totalW = 0.0, sumLog = 0.0;
            for (int i = 0; i < times.Count; i++)
            {
                double w = SegmentWeight(times, i, startMs, endMs);
                if (w <= 0.0) continue;
                totalW += w;
                sumLog += w * Log2(vals[i]);
            }
            if (totalW <= 0.0) return 0.0;

            double meanLog = sumLog / totalW;
            double dev = 0.0;
            for (int i = 0; i < times.Count; i++)
            {
                double w = SegmentWeight(times, i, startMs, endMs);
                if (w <= 0.0) continue;
                dev += w * Math.Min(LogClamp, Math.Abs(Log2(vals[i]) - meanLog));
            }
            return dev / totalW;
        }

        /// <summary>第 i 段在 [startMs, endMs] 裡佔多少 ms。第一段往前延伸到 startMs（第一個點之前就是它的值）。</summary>
        private static double SegmentWeight(List<double> times, int i, double startMs, double endMs)
        {
            double t0 = i == 0 ? startMs : Math.Max(times[i], startMs);
            double t1 = Math.Min(i + 1 < times.Count ? times[i + 1] : endMs, endMs);
            return t1 - t0;
        }

        /// <summary>同一時刻的重複點只留最後一個（後面那個才是生效的），並且值沒變就不新增一段。</summary>
        private static void Push(List<double> times, List<double> vals, double timeMs, double value)
        {
            int n = times.Count;
            if (n > 0 && Math.Abs(times[n - 1] - timeMs) <= 1e-9) { vals[n - 1] = value; return; }
            if (n > 0 && Math.Abs(vals[n - 1] - value) <= 1e-12) return;
            times.Add(timeMs);
            vals.Add(value);
        }

        /// <summary>x/(x+half)：0 → 0，x == half → 0.5，x → ∞ 時逼近 1。永遠不會超過 1，所以上限是硬的。</summary>
        private static double Saturate(double x, double half)
            => x <= 0.0 ? 0.0 : x / (x + half);

        private static double Log2(double v) => Math.Log(v <= 0.0 ? 1e-9 : v) / Math.Log(2.0);
    }
}
