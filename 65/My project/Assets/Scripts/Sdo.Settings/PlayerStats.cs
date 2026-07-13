using System;
using System.Globalization;

namespace Sdo.Settings
{
    /// <summary>
    /// 一個帳號的累計戰績，就是官方 PlayerInformationDlg「技术统计 / SkillStat」分頁上那六列的資料來源
    /// （勝率 / 命中率 / Perfect率 / Cool率 / Bad率 / Miss率）。
    ///
    /// 官方這些數字是**伺服器下發**的（離線 EXE 完全沒有這個對話框的程式碼，跟商城道具名一樣），所以離線重製沒有現成
    /// 來源 —— 這裡改成本地累計：每打完一首非自由模式的歌，<see cref="AddGame"/> 把該場的四種判定數、名次、分數併進來，
    /// 存在 profile.json 的 <c>stats</c> 欄位。將來要接線上，只要換成用伺服器欄位填同一組 rate 就好。
    ///
    /// 判定等級用重製版 ruleset 的四級 (Perfect/Cool/Bad/Miss)，剛好等於官方面板那四列的名字。
    /// </summary>
    [Serializable]
    public class PlayerStats
    {
        public int games;        // 總場次（自由模式不計）
        public int wins;         // 第一名的場次
        public long perfect;     // 以下四項是所有場次的累計判定數
        public long cool;
        public long bad;
        public long miss;
        public long bestScore;   // 單場最高分
        public long totalScore;  // 分數總和（給平均分用）

        /// <summary>累計總 note 數（四種判定的和）。</summary>
        public long TotalNotes => perfect + cool + bad + miss;

        /// <summary>勝率 %：第一名場次 / 總場次。沒打過 → 0。</summary>
        public double WinRate => games > 0 ? 100.0 * wins / games : 0.0;

        /// <summary>命中率 %：有打到的 note（Perfect+Cool+Bad，即非 MISS）/ 總 note。</summary>
        public double HitRate => Rate(perfect + cool + bad);

        public double PerfectRate => Rate(perfect);
        public double CoolRate => Rate(cool);
        public double BadRate => Rate(bad);
        public double MissRate => Rate(miss);

        /// <summary>平均分（四捨五入）。沒打過 → 0。</summary>
        public long AverageScore => games > 0 ? (long)Math.Round((double)totalScore / games) : 0L;

        private double Rate(long n)
        {
            long t = TotalNotes;
            return t > 0 ? 100.0 * n / t : 0.0;
        }

        /// <summary>併入一場的成績。<paramref name="won"/> = 這場拿第一。自由模式請不要呼叫（官方戰績不計自由模式）。</summary>
        public void AddGame(int perfect, int cool, int bad, int miss, bool won, long score)
        {
            if (perfect < 0) perfect = 0;
            if (cool < 0) cool = 0;
            if (bad < 0) bad = 0;
            if (miss < 0) miss = 0;
            if (score < 0) score = 0;

            games++;
            if (won) wins++;
            this.perfect += perfect;
            this.cool += cool;
            this.bad += bad;
            this.miss += miss;
            totalScore += score;
            if (score > bestScore) bestScore = score;
        }

        /// <summary>官方 Label 的格式：兩位小數 + %（XML 裡 labeltext 預設就寫 "0.00%"）。</summary>
        public static string FormatPercent(double pct)
        {
            if (double.IsNaN(pct) || double.IsInfinity(pct)) pct = 0.0;
            return pct.ToString("0.00", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>官方 ProgressBar 是 minrange=0 maxrange=99，所以填充比例用 0..1 的 clamp 值。</summary>
        public static float BarFill(double pct)
        {
            if (double.IsNaN(pct)) return 0f;
            if (pct <= 0.0) return 0f;
            if (pct >= 100.0) return 1f;
            return (float)(pct / 100.0);
        }
    }
}
