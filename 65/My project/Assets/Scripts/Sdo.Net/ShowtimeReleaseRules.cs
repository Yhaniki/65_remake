namespace Sdo.Net
{
    /// <summary>
    /// ShowTime「按 SPACE 釋放」這個一次性事件的合法範圍。client 與 server **編譯同一份這個檔**
    /// (server csproj 直接拉 Sdo.Net 的原始碼),所以兩邊的驗證不可能漂移。
    ///
    /// **為什麼這個事件必須獨立傳、而不是從分數流推導**:遠端舞者的「跳/停」可以從 5 Hz 的判定計數
    /// 差推出來(<see cref="Sdo.Ruleset.DanceGate"/>),但 ShowTime 釋放推不出來 ——
    /// 釋放期間他每一擊都是 PERFECT,而「連續全 PERFECT」與「打得好」在分數流裡長得一模一樣。
    /// 而且要重現他的畫面還需要兩個本機根本猜不到的值:
    ///   • <c>level</c>   釋放檔位(0/1/2)→ 決定 breaking 的 E/N/H
    ///   • <c>variant</c> 該檔位的變體編號 → 官方在**歌曲載入時**每個檔位各骰一次(FUN_0092d280),
    ///                    是那一台自己的亂數,別台不可能算出同一個
    /// 少了它們,遠端只能跳到別支舞或整段不動 —— 也就是使用者回報的「看不到他的發光和特殊舞蹈動作」。
    /// </summary>
    public static class ShowtimeReleaseRules
    {
        /// <summary>釋放檔位 0..2(綠/黃/紅 → breaking_E / _N / _H)。</summary>
        public const int MaxLevel = 2;

        /// <summary>
        /// breaking 變體編號。官方在歌曲載入時每個檔位各骰一次:**E 骰 1..6、N/H 骰 1..8**
        /// (FUN_0092d280 的 rand%6 / rand&amp;7),資產也真的只有 BREAKING_E_1..6 與 BREAKING_N|H_1..8。
        /// 這裡的常數是**聯集**的上下界;逐檔位的正確上界見 <see cref="MaxVariantFor"/>。
        /// </summary>
        public const int MinVariant = 1;
        public const int MaxVariant = 8;

        /// <summary>這個檔位真的存在幾支 breaking(E 只有 6 支)。</summary>
        public static int MaxVariantFor(int level) => level == 0 ? 6 : MaxVariant;

        /// <summary>
        /// 視窗長度的合法區間(ms)。
        ///
        /// 上界不是憑感覺:視窗 = 檔位預算(8/12/18 秒)往上進位到整段 pas,而 pas = 8 拍 —— 慢歌
        /// (BPM 30~40)一段就十幾秒,進位後可以到 30 秒出頭;再加上「視窗至少要蓋過選中的 breaking
        /// 全長 + idle 尾巴」那條保底。45 秒留了餘裕,同時擋掉「一則封包讓對方的舞者卡住整首歌」。
        /// </summary>
        public const double MinWindowMs = 1000.0;
        public const double MaxWindowMs = 45000.0;

        /// <summary>
        /// 同一個人兩次釋放之間至少要隔這麼久(ms)。最短的視窗本身就有 8 秒以上,而且釋放完要重新
        /// 集滿 130 個好判定才可能再按 —— 3 秒是很寬鬆的門檻,純粹擋「壞掉/惡意的 client 每幀送一則」。
        /// </summary>
        public const double MinIntervalMs = 3000.0;

        public static bool IsValidLevel(int level) => level >= 0 && level <= MaxLevel;

        public static bool IsValidVariant(int variant) => variant >= MinVariant && variant <= MaxVariant;

        /// <summary>
        /// 這個(檔位, 變體)組合真的對得到一支 DPS 嗎。
        /// 🔴 <see cref="IsValidVariant"/> 只看聯集,`level=0(E) + variant=7` 會通過它但那支檔案不存在 ——
        /// 收端會落回「只有光環、沒有街舞」。server 用這一條就能在轉發前把它擋掉。
        /// </summary>
        public static bool IsValidPair(int level, int variant)
            => IsValidLevel(level) && variant >= MinVariant && variant <= MaxVariantFor(level);

        public static bool IsValidWindowMs(double windowMs)
            => windowMs >= MinWindowMs && windowMs <= MaxWindowMs;

        /// <summary>三個欄位全部合法才算數(server 收到不合法的就整則丟掉,不修正也不回錯)。</summary>
        public static bool IsValid(int level, int variant, double windowMs)
            => IsValidPair(level, variant) && IsValidWindowMs(windowMs);

        /// <summary>
        /// 收端用的夾值。**驗證與夾值分開**是刻意的:server 的角色是「不合法就不轉發」,
        /// client 的角色是「就算收到怪值也不能讓場上那隻舞者壞掉」——
        /// 兩者的正確反應不同,共用一個函式一定會有一邊被將就。
        /// </summary>
        public static double ClampWindowMs(double windowMs)
            => windowMs < MinWindowMs ? MinWindowMs : (windowMs > MaxWindowMs ? MaxWindowMs : windowMs);

        public static int ClampLevel(int level) => level < 0 ? 0 : (level > MaxLevel ? MaxLevel : level);

        /// <summary>夾進**這個檔位**真的有的變體範圍(E 只有 6 支)。</summary>
        public static int ClampVariant(int level, int variant)
        {
            int max = MaxVariantFor(ClampLevel(level));
            return variant < MinVariant ? MinVariant : (variant > max ? max : variant);
        }

        /// <summary>
        /// 這一則要不要放行(防洪)。<paramref name="lastAcceptedMs"/> ≤ 0 = 這一場還沒放行過他的任何一則。
        /// </summary>
        public static bool AcceptsAt(double lastAcceptedMs, double nowMs)
            => lastAcceptedMs <= 0.0 || nowMs - lastAcceptedMs >= MinIntervalMs;
    }
}
