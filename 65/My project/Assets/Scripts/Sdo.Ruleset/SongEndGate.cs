using System;

namespace Sdo.Ruleset
{
    /// <summary>
    /// 最後一顆音符打完之後,**什麼時候切結算、音樂要不要淡出**。純函式。
    ///
    /// 分兩種歌:
    /// <list type="bullet">
    /// <item><b>音檔跟著譜一起收</b>(尾巴 ≤ <see cref="LongOutroMs"/>)—— 等音樂自己播完,再 +1 秒進結算。
    ///       音樂是自然結束的,不必淡出。</item>
    /// <item><b>音檔遠比譜長</b>(尾巴 &gt; <see cref="LongOutroMs"/>)—— 常見於 osu 的多曲包:一首 4 分半的歌
    ///       只鋪了前 2 分 20 秒的譜,後面還有兩分多鐘沒鋪。這種苦等尾奏沒意義,但也不能在譜末 +1 秒的地方
    ///       把還在大聲播的音樂**一刀切掉**(那一聲「啪」就是玩家說的「怎麼一打完就跳結算」)。
    ///       改成從最後一顆音符起 <see cref="FadeMs"/> 慢慢淡出,淡完才進結算。</item>
    /// </list>
    ///
    /// 血條見底當場出局(<see cref="GameOverGate.EliminatedNow"/>)走的是另一條路,不經過這裡 ——
    /// 死亡是「當場斷」,本來就不該有 4 秒尾巴。
    /// </summary>
    public static class SongEndGate
    {
        /// <summary>音檔在最後一顆音符之後還剩超過這麼久,就算「長尾奏」→ 改走淡出。</summary>
        public const double LongOutroMs = 4000.0;

        /// <summary>長尾奏的淡出長度(從最後一顆音符算起)。淡完的那一刻 = 進結算。</summary>
        public const double FadeMs = 4000.0;

        /// <summary>音樂自然播完之後、進結算之前的緩衝(osu 的 RESULTS_DISPLAY_DELAY 也是 1 秒)。</summary>
        public const double SettleMs = 1000.0;

        /// <summary>
        /// 一首歌的收尾排程。<see cref="EndAtMs"/> 之後就切結算;
        /// <see cref="FadeStartMs"/> 到 <see cref="EndAtMs"/> 之間音樂線性淡出(不淡出時兩者相等)。
        /// </summary>
        public readonly struct Plan
        {
            public readonly double FadeStartMs;
            public readonly double EndAtMs;

            public Plan(double fadeStartMs, double endAtMs)
            {
                FadeStartMs = fadeStartMs;
                EndAtMs = endAtMs;
            }

            /// <summary>這首歌的收尾要不要淡出音樂。</summary>
            public bool FadesOut => EndAtMs > FadeStartMs;

            /// <summary>這一刻該切結算了嗎(<paramref name="nowMs"/> = 譜面時鐘)。</summary>
            public bool EndedAt(double nowMs) => nowMs > EndAtMs;

            /// <summary>
            /// 這一刻的音樂音量倍率(1 = 原音量、0 = 靜音)。乘在使用者的音樂音量上,不是取代它。
            ///
            /// 用**平方**而非線性:振幅線性下降在聽感上是「前 3 秒幾乎沒變、最後半秒唰地消失」。
            /// 平方 = 在感知刻度上等速下降,跟音量滑桿用的同一條曲線(AudioMix.Gain),聽起來才是平順的
            /// 「慢慢淡出」。
            /// </summary>
            public double VolumeAt(double nowMs)
            {
                if (!FadesOut || nowMs <= FadeStartMs) return 1.0;
                if (nowMs >= EndAtMs) return 0.0;
                double remain = (EndAtMs - nowMs) / (EndAtMs - FadeStartMs);   // 1 → 0
                return remain * remain;
            }
        }

        /// <summary>
        /// 排這首歌的收尾。
        /// </summary>
        /// <param name="notesEndMs">最後一顆音符(長條算尾巴)的譜面時間。</param>
        /// <param name="audibleEndMs">
        /// 最後還聽得到聲音的譜面時間 —— 背景音檔播完的時刻,虛擬 keysound 譜則是最後一顆自動樣本。
        /// 沒有音檔(觀察/爆發模式)或音檔比譜還短,就傳 ≤ <paramref name="notesEndMs"/> 的值。
        /// </param>
        public static Plan For(double notesEndMs, double audibleEndMs)
        {
            double tail = audibleEndMs - notesEndMs;
            // 音檔比譜短 / 沒有音檔 → 沒有尾巴可等,譜末 +1 秒。
            if (!(tail > 0.0) || double.IsNaN(tail)) return new Plan(notesEndMs + SettleMs, notesEndMs + SettleMs);
            // 音檔跟著譜收 → 等它播完 +1 秒,不淡出(它自己就會靜下來)。
            if (tail <= LongOutroMs) return new Plan(audibleEndMs + SettleMs, audibleEndMs + SettleMs);
            // 長尾奏 → 從譜末起淡出,淡完進結算。
            return new Plan(notesEndMs, notesEndMs + FadeMs);
        }
    }
}
