using System.Globalization;

namespace Sdo.Settings
{
    /// <summary>
    /// 角色等級曲線：把每局結算拿到的經驗值（<c>Sdo.Ruleset.Reward.Experience</c>）換成「等級 ＋ 該等級內的累積經驗」。
    /// profile.json 存的就是這兩個數（<see cref="UserProfile.level"/> / <see cref="UserProfile.exp"/>），
    /// 累加與升等都走這裡 —— 純函式、不碰檔案，可單元測試（落地是 <see cref="ProfileManager.AddExperience"/> 的事）。
    ///
    /// 升一級要的經驗 = <see cref="PerLevel"/> × 目前等級（LV1→2 要 100、LV10→11 要 1000）。一局乾淨過關在 4 人房
    /// 拿第一約 120 exp，所以低等級一兩局一級、越後面越慢 —— 線性累進曲線。**這是本 remake 自訂的**（官方伺服器的
    /// 經驗表沒有留下來，跟 Reward 那幾條公式一樣是重建值），要調鬆緊就改 <see cref="PerLevel"/> / <see cref="MaxLevel"/>。
    /// </summary>
    public static class PlayerLevel
    {
        public const int MinLevel = 1;
        public const int MaxLevel = 99;
        public const int PerLevel = 100;   // 升一級所需經驗 = PerLevel × 目前等級

        /// <summary>從 <paramref name="level"/> 升到下一級需要的經驗值；已滿級 → 0（＝不再升）。純函式。</summary>
        public static int ExpToNext(int level)
        {
            level = Clamp(level);
            return level >= MaxLevel ? 0 : PerLevel * level;
        }

        /// <summary>等級夾進 1..<see cref="MaxLevel"/>。純函式。</summary>
        public static int Clamp(int level)
            => level < MinLevel ? MinLevel : (level > MaxLevel ? MaxLevel : level);

        /// <summary>舊 config.ini <c>[Profile] playerLevel</c> 那個字串 → 等級，**空/非數字 → 0 ＝「沒設定」**
        /// （不是 LV1；0 讓「留空＝不顯示等級」搬進 profile.json 後還是同一個意思）。只有搬遷會用到。純函式。</summary>
        public static int ParseOptional(string level)
        {
            level = (level ?? "").Trim();
            if (level.Length == 0) return 0;
            return int.TryParse(level, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? Clamp(v) : 0;
        }

        /// <summary>頭上名字牌的等級標籤：&gt;0 → 「LV:N」（LV 兩字都大寫）；0/負 → 空字串（＝不顯示等級）。純函式。</summary>
        public static string Label(int level)
            => level <= 0 ? "" : "LV:" + level.ToString(CultureInfo.InvariantCulture);

        /// <summary>把 <paramref name="gained"/> 點經驗加進 (level, exp)，跨過門檻就連續升等（一局爆量也能升好幾級）。
        /// 滿級後經驗不再累積（exp 固定 0）。負的 gained 當 0 —— 結算只會加、不會扣。純函式。</summary>
        public static (int level, int exp) Grant(int level, int exp, int gained)
        {
            level = Clamp(level);
            if (exp < 0) exp = 0;
            if (gained > 0) exp += gained;
            while (level < MaxLevel)
            {
                int need = ExpToNext(level);
                if (exp < need) break;
                exp -= need;
                level++;
            }
            if (level >= MaxLevel) { level = MaxLevel; exp = 0; }   // 滿級 → 經驗條收掉
            return (level, exp);
        }
    }
}
