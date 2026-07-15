using System;

namespace Sdo.Ruleset
{
    /// <summary>
    /// 「整體遊戲流速」——StepMania 的 <b>music rate</b>(<c>SongOptions::m_fMusicRate</c>,譜面選項字串裡的
    /// <c>"1.50xMusic"</c>)。StepMania 的作法是:**只讓音樂本身變速**
    /// (<c>RageSoundParams::SetPlaybackRate</c> → 重取樣,所以音高也跟著變),其它一切(音符、判定、跳舞的人、
    /// 背景動畫)都是掛在音樂時鐘上跑的,自然就一起變慢/變快;assist tick 則是把「距離下一個 tick 的秒數」
    /// 除以 rate(<c>fSecondsUntil /= m_fMusicRate</c> — 註解原文:「2x music rate means the time until the
    /// tick is halved」)。判定窗口本身**不變**(仍是譜面時間),所以速度越快真的越難 —— 這也是照抄的。
    ///
    /// 重製版對應:<c>Time.timeScale = rate</c>(音符/舞者/特效/HUD 全都吃 scaled time)+
    /// <c>AudioSource.pitch = rate</c>(音樂變速變調,等同 SetPlaybackRate)。麻煩的只有一件事:音訊排程用的
    /// <c>AudioSettings.dspTime</c> 是**真實時間**,不受 timeScale 影響,所以 dsp ↔ 譜面時間的換算必須自己帶上 rate,
    /// 而且**中途改速度時要重新錨定**,否則譜面時間會瞬間跳掉(音符/判定當場錯位)。那組換算就是這裡的純函式。
    ///
    /// 不變式:<c>clipPos(dsp) = rate × (dsp − anchorDsp)</c>,亦即 anchorDsp = 音樂第 0 秒對應的 dsp 時刻
    /// (可以是過去、也可以是還沒到的未來 = 排程起播點)。譜面時間 = clipPos + count-in(type-10 音樂起點前導)。
    /// </summary>
    public static class GameRate
    {
        /// <summary>正常速度。</summary>
        public const double Normal = 1.0;

        /// <summary>可用範圍。下限留給觀察特效用的極慢速(Unity 的 pitch 撐得住);上限 2× 同 StepMania 常見設定。</summary>
        public const double Min = 0.05, Max = 2.0;

        /// <summary>微調步進 = 0.05(StepMania 的 rate 字串是兩位小數:1.05xMusic)。</summary>
        public const double StepSize = 0.05;

        /// <summary>面板快捷鍵的預設檔位。</summary>
        public static readonly double[] Presets = { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };

        public static double Clamp(double rate)
        {
            if (double.IsNaN(rate)) return Normal;
            return rate < Min ? Min : (rate > Max ? Max : rate);
        }

        /// <summary>±<see cref="StepSize"/> 微調,並對齊到 0.05 的格線(從 0.97 按 + 會落在 1.00 而不是 1.02)。</summary>
        public static double Step(double rate, int dir)
        {
            double snapped = Math.Round(Clamp(rate) / StepSize) * StepSize;
            if (Math.Abs(snapped - rate) < 1e-9) snapped = rate;                   // 已經在格線上 → 純步進
            else if (dir > 0 && snapped > rate) return Clamp(Round2(snapped));     // 先吸到上一格
            else if (dir < 0 && snapped < rate) return Clamp(Round2(snapped));     // 先吸到下一格
            return Clamp(Round2(snapped + dir * StepSize));
        }

        private static double Round2(double v) => Math.Round(v, 2);

        /// <summary>dsp 時刻 → 譜面時間(秒)。<paramref name="countInSec"/> = 音符領先音樂的無聲前導(type-10 marker)。</summary>
        public static double ChartSecondsFromDsp(double dsp, double anchorDsp, double rate, double countInSec)
            => rate * (dsp - anchorDsp) + countInSec;

        /// <summary>譜面時間(秒) → 該在哪個 dsp 時刻發聲(打拍音排程用;StepMania 的 fSecondsUntil /= rate 就是這條)。</summary>
        public static double DspFromChartSeconds(double chartSec, double anchorDsp, double rate, double countInSec)
            => anchorDsp + (chartSec - countInSec) / rate;

        /// <summary>重新錨定:要「此刻(<paramref name="dspNow"/>)的譜面時間剛好等於 <paramref name="chartSecNow"/>」時,
        /// 新的 anchorDsp 是多少。改速度、暫停後恢復都用它 —— 譜面時間因此**連續**,不會跳拍。</summary>
        public static double AnchorForChartSeconds(double dspNow, double chartSecNow, double rate, double countInSec)
            => dspNow - (chartSecNow - countInSec) / rate;

        /// <summary>開場排程:此刻 dsp、譜面時間還差 <paramref name="chartSecUntilZero"/> 秒才到 0(READY/GO 的 lead-in),
        /// 音樂又要比譜面第 0 拍晚 <paramref name="countInSec"/> 秒進來 → 音樂該排在哪個 dsp 起播。
        /// 兩段前導都是**譜面時間**,所以都要除以 rate 換回真實秒數。</summary>
        public static double StartDspFor(double dspNow, double chartSecUntilZero, double countInSec, double rate)
            => dspNow + (chartSecUntilZero + countInSec) / rate;

        /// <summary>音訊排程的最小提前量(真實秒)。PlayScheduled 排到「已經過去」的時刻會當場開聲,
        /// 起播點就跟錨點對不上了 —— 永遠留這點餘裕給音訊執行緒。</summary>
        public const double MinScheduleLeadSec = 0.02;

        /// <summary>
        /// 開場排程,允許 <paramref name="countInSec"/> 為**負**(每首歌的 offset 把音樂往前挪,見
        /// song_name_overrides.json 的 offsetMs)。負得夠多時錨點(clip 第 0 秒)會落在現在之前 —— 那段音樂
        /// 已經來不及播,只能**從中途切入**:排在最早可排的時刻起播,並把 clip 讀取頭先推到
        /// <paramref name="clipSkipSec"/>。不變式 clipPos(dsp) = rate×(dsp − anchorDsp) 因此照樣成立,
        /// 上層的 dsp↔譜面時間換算(<see cref="ChartSecondsFromDsp"/>)完全不用改。
        /// </summary>
        /// <param name="anchorDsp">clip 第 0 秒對應的 dsp(可能已是過去式 → 搭配 clipSkipSec)。</param>
        /// <param name="playAtDsp">實際要餵給 PlayScheduled 的時刻。</param>
        /// <param name="clipSkipSec">起播時 clip 要跳過的秒數(0 = 從頭播)。</param>
        public static void ScheduleMusic(double dspNow, double chartSecUntilZero, double countInSec, double rate,
                                         out double anchorDsp, out double playAtDsp, out double clipSkipSec)
        {
            rate = Clamp(rate);
            anchorDsp = StartDspFor(dspNow, chartSecUntilZero, countInSec, rate);
            double earliest = dspNow + MinScheduleLeadSec;
            if (anchorDsp >= earliest) { playAtDsp = anchorDsp; clipSkipSec = 0.0; return; }
            playAtDsp = earliest;
            clipSkipSec = rate * (playAtDsp - anchorDsp);   // 錨點在過去 → clip 已經該播到這裡了
        }
    }
}
