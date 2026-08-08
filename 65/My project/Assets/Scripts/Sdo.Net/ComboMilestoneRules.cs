namespace Sdo.Net
{
    /// <summary>
    /// combo 里程碑(50/100/150…)這個一次性事件的合法範圍。client 與 server **編譯同一份這個檔**
    /// (server csproj 直接拉 Sdo.Net 的原始碼),所以兩邊的驗證不可能漂移。
    /// 理由與 <see cref="ShowtimeReleaseRules"/> 相同。
    ///
    /// **為什麼這個事件必須獨立傳**:分數流是 5 Hz 的快照,收端看到的 combo 會直接從 49 跳到 53 ——
    /// 「剛好踩到 50」在快照裡是看不到的,所以里程碑要自己送一則可靠事件。
    /// </summary>
    public static class ComboMilestoneRules
    {
        /// <summary>里程碑的間隔:每 50 combo 一次(本機的觸發規則見 ScreenGameplay 的 _lastMilestone)。</summary>
        public const int Step = 50;

        /// <summary>第一個里程碑是 50;上界純粹是防呆(沒有一首歌打得出一百萬 combo)。</summary>
        public const int MinCombo = 50;
        public const int MaxCombo = 1000000;

        /// <summary>
        /// 同一個人兩則之間至少要隔這麼久(ms)。
        ///
        /// 這是**防洪**用的,不是節流:兩則里程碑之間一定隔著 50 次判定,而最密的譜面(200 BPM 16 分音
        /// 四軌齊按)也才 ~50 判定/秒 —— 100 ms 要湊滿 50 個判定得每秒 500 次判定,不可能發生。
        /// 門檻放這麼寬是刻意的:寧可漏擋一個灌包的 client,也不要誤殺一則真的里程碑
        /// (症狀會是「遠端的人 combo 特效有時候沒出來」,而那正是這個檔案要修掉的東西)。
        /// </summary>
        public const double MinIntervalMs = 100.0;

        /// <summary>這是不是一個真的里程碑值。非邊界值一律是壞掉/偽造的封包,不可以拿去放特效。</summary>
        public static bool IsValid(int combo)
            => combo >= MinCombo && combo <= MaxCombo && combo % Step == 0;

        /// <summary>
        /// 同一個人的下一則里程碑可不可以接受(去重)。
        ///
        /// 🔴 **不是「必須比上一則大」。** combo 會斷 —— 打到 250 之後 Bad 一下歸零,再爬回 50 / 100 /
        /// 150 全都是**真的**里程碑,本機那台也確實會各放一次特效(見 ScreenGameplay.Effects 的
        /// _lastMilestone:它只擋「與上一次相同的值」,不要求遞增)。用單調遞增去重的話,這一整段
        /// 在別人畫面上會完全消失,直到他重新超過斷掉前的最高點 —— 也就是使用者回報的
        /// 「遠端的人 combo 特效有時候沒出來」。
        ///
        /// 要擋的只有「同一則被重送」。而重爬時必定先經過 50 才會再到 100,所以連續兩則相同的值
        /// 在正常遊玩中不可能出現,拿它當去重條件是安全的。
        /// </summary>
        public static bool AcceptsCombo(int last, int combo) => combo != last;

        /// <summary>距離上一則放行的時間夠久了嗎(<see cref="MinIntervalMs"/>)。</summary>
        public static bool AcceptsAt(double lastMs, double nowMs) => nowMs - lastMs >= MinIntervalMs;
    }
}
