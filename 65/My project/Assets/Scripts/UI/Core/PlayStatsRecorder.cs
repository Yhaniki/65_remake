using Sdo.Settings;

namespace Sdo.UI.Core
{
    /// <summary>
    /// 把一場遊玩的成績寫進這個角色的 <c>profile.json</c>(<see cref="UserProfile.stats"/>)。
    ///
    /// 分層照 <c>WardrobeStore</c> 的做法:政策是**純函式**(<see cref="ShouldRecordPlay"/> /
    /// <see cref="ShouldRecordWinLoss"/>,不碰 Unity 也不碰磁碟,所以測得動),只有 <see cref="Record"/>
    /// 這一個入口會真的存檔。
    ///
    /// 政策(使用者指定):
    /// * **判定數(P/C/B/M)**:只要真的打完一場就累計,離線也算 —— 那些是玩家真的按出來的。
    /// * **勝負**:只有**連線對局**、而且是**普通模式或 ShowTime 模式**才記。
    ///   - 自由模式沒有名次可言(結算也不發獎勵)。
    ///   - 旁觀不是參賽者。
    ///   - 單機的對手是假資料 bot、本機幾乎必勝 —— 記了只會讓個人頁的勝率失去意義。
    /// * 中途離開(ESC)不算一場:那不是一份完整的成績,判定數也只有半首歌。
    /// </summary>
    public static class PlayStatsRecorder
    {
        /// <summary>這場的判定數要不要累計?打完 + 不是旁觀 就算。</summary>
        public static bool ShouldRecordPlay(bool finished, bool spectator)
            => finished && !spectator;

        /// <summary>這場的勝負要不要記?連線 + 非旁觀 + 普通/ShowTime 模式,三個都成立才記。</summary>
        public static bool ShouldRecordWinLoss(bool finished, bool spectator, bool online, int gameMode)
            => finished && !spectator && online && PlayStats.RecordsWinLoss(gameMode);

        /// <summary>
        /// 記一場。<paramref name="finished"/> = 打到曲末並看完結算(中途 ESC 走的是 false)。
        /// 回 true = 真的寫了檔(呼叫端可用來決定要不要 log)。
        /// </summary>
        public static bool Record(UserProfile who, int perfect, int cool, int bad, int miss,
                                  bool finished, bool spectator, bool online, int gameMode, bool localWon)
        {
            if (who == null) return false;
            if (!ShouldRecordPlay(finished, spectator)) return false;

            if (who.stats == null) who.stats = new PlayStats();
            who.stats.AddPlay(perfect, cool, bad, miss);
            if (ShouldRecordWinLoss(finished, spectator, online, gameMode))
                who.stats.AddResult(localWon);

            ProfileManager.Save();
            return true;
        }
    }
}
