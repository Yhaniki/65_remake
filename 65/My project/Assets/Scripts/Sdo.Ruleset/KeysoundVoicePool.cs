using System.Collections.Generic;

namespace Sdo.Ruleset
{
    /// <summary>
    /// keysound(osu! 每顆音符自帶的取樣)音源池的純邏輯:一顆取樣該用哪個音源、音源該掛多高的發聲優先度。
    /// 遊玩畫面(ScreenGameplay 的 keysound 排程)與選歌試聽(OsuKeysoundPreviewPlayer)共用同一套判斷。
    ///
    /// **為什麼池會滿**:BMS 移植的 keysound 譜(.osu 的 <c>AudioFilename: virtual</c>)沒有背景音樂檔 ——
    /// 整首曲子就是幾百顆鋼琴取樣疊出來的,而長取樣(那套 piano set 的 <c>l_*.ogg</c>)一顆要響 5~6.5 秒,
    /// 音源就被佔住那麼久。實測 "Piano Beatmap Set - Collection" 14 個難度的同時發聲數峰值是 46~170
    /// (william tell 170 @130s、rachmaninov 144 @102s、la campanella 104 @232s),整首有一半以上的時間
    /// 都在 32 聲以上 —— 一般譜「音樂只佔 1 個音源」的假設在這裡完全不成立。
    ///
    /// **滿了要偷,不能放棄**:舊版滿了就回 -1,排程迴圈 break 而且游標不前進;等有空位時那些取樣早就
    /// 超過遲到門檻(100ms)被整批當「來不及」丟掉 —— 聽起來就是「玩到後面整段沒音樂」。滿了要偷,而且
    /// 偷**最舊的那顆**:鋼琴音是衰減的,最舊那顆的尾巴早就快聽不見,截掉它遠比讓後面整段消失便宜
    /// (這就是取樣器的 voice stealing)。已排程但還沒響的不能偷 —— 那是未來的音,偷了等於它從沒響過。
    /// </summary>
    public static class KeysoundVoicePool
    {
        /// <summary>音源池上限。Unity 的 Real Voices 上限是 255(專案設定 AudioManager.m_RealVoiceCount),
        /// 留一些給打拍音與遊戲音效,所以池本身停在 240。實測最吃的譜峰值 170,還有餘裕。</summary>
        public const int MaxVoices = 240;

        /// <summary>「剛起音」的長度(秒):這段時間內的取樣是曲子當下正在響的音,不能被虛擬化掉。</summary>
        public const double FreshSec = 0.4;

        /// <summary>超過這個長度(秒)就算「衰減尾巴」——鋼琴音到這裡已經小聲到可以犧牲。</summary>
        public const double DecaySec = 1.2;

        // Unity 的 AudioSource.priority:0 最高、255 最低,發聲數吃滿時**數字大的先被虛擬化**(靜音但時間照走)。
        // keysound 譜的取樣就是音樂本身,所以剛起音的掛最高;隨衰減往下降,真的爆掉時被犧牲的是快聽不見的尾巴。
        /// <summary>剛起音的取樣(&lt; <see cref="FreshSec"/>)。</summary>
        public const int PriorityFresh = 0;
        /// <summary>衰減中的取樣(<see cref="FreshSec"/> ~ <see cref="DecaySec"/>)。</summary>
        public const int PriorityDecaying = 96;
        /// <summary>衰減尾巴(&gt; <see cref="DecaySec"/>)——發聲數不夠時第一個該讓位的。</summary>
        public const int PriorityTail = 160;

        /// <summary>F7 打拍音的優先度:比琴音尾巴重要(漏掉整段就沒有對拍的參考),比剛起音的琴音次要。</summary>
        public const int PriorityAssistTick = 128;

        /// <summary>第一個已經播完的音源(busyUntil &lt;= 現在);沒有就回 -1。純函式。</summary>
        public static int FindFree(IReadOnlyList<double> busyUntil, double dspNow)
        {
            if (busyUntil == null) return -1;
            for (int i = 0; i < busyUntil.Count; i++)
                if (busyUntil[i] <= dspNow) return i;
            return -1;
        }

        /// <summary>
        /// 池滿時該偷哪個音源:**已經開始響、而且開始得最早**的那個(它的衰減尾巴最接近聽不見)。
        /// 跳過還沒響的排程(未來的音,偷了等於它從沒響過)以及暫停中的音源
        /// (<paramref name="pausedSamples"/> 該格 &gt;= 0;傳 null 表示這個池沒有暫停狀態)。
        /// 沒有可偷的(全都是未來排程或暫停中)就回 -1。純函式。
        /// </summary>
        public static int FindStealable(
            IReadOnlyList<double> startsAt, IReadOnlyList<int> pausedSamples, double dspNow)
        {
            if (startsAt == null) return -1;
            int best = -1;
            double oldest = 0.0;
            for (int i = 0; i < startsAt.Count; i++)
            {
                if (pausedSamples != null && i < pausedSamples.Count && pausedSamples[i] >= 0) continue;
                double at = startsAt[i];
                if (at > dspNow) continue;                  // 還沒響 → 不能偷
                if (best >= 0 && at >= oldest) continue;    // 已經有更舊的
                best = i;
                oldest = at;
            }
            return best;
        }

        /// <summary>這顆音已經響了 <paramref name="ageSec"/> 秒時該掛的發聲優先度。還沒響的(負數)
        /// 視同剛起音。純函式。</summary>
        public static int PriorityForAge(double ageSec)
        {
            if (ageSec < FreshSec) return PriorityFresh;
            if (ageSec < DecaySec) return PriorityDecaying;
            return PriorityTail;
        }
    }
}
