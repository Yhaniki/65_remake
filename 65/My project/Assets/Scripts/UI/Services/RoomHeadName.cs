using Sdo.Net;

namespace Sdo.UI.Services
{
    /// <summary>
    /// 房間 3D 裡「別人頭上那塊名牌」寫什麼字。
    ///
    /// 平常是 <c>名字 + LV:等級</c>;那個人**進去打歌**之後(還沒回房間)改寫 <see cref="PlayingText"/> ——
    /// 與頭貼下緣那條 PLAYING 徽章(<see cref="RoomBadgeChoice"/>)是同一件事的兩個位置,
    /// 所以「算不算在場中」直接共用 <see cref="RoomBadgeChoice.IsInMatch"/>:
    /// 只有一個地方會退回 Idle,不會出現「徽章寫 PLAYING、名牌卻寫名字」這種自相矛盾的畫面。
    ///
    /// 誰會看到:留在房間的人(沒被納入這場、或旁觀)。**打歌的人自己看不到自己這塊** ——
    /// 他按開始之後畫面已經整個轉黑進 loading 了(RoomScreen.FadeToStage),再切到舞台;
    /// 只有從舞台跳出來回房間時,才會看到還在場裡的其他人頂著 Playing。
    ///
    /// 為什麼不進 Localization:官方那張徽章圖就是英文 PLAYING,名牌跟著它走比較一致
    /// (而且名牌只有 160px 寬,翻譯後的長度不好控)。
    /// </summary>
    public static class RoomHeadName
    {
        /// <summary>在場中時名牌上的字。刻意與官方徽章同字(大小寫照名牌的一般字體慣例)。</summary>
        public const string PlayingText = "Playing";

        /// <param name="name">server 給的顯示名(null 當空字串)。</param>
        /// <param name="level">等級;<c>&lt;= 0</c> 就不接那一段(還沒同步到等級的人不要顯示 LV:0)。</param>
        /// <param name="play">那個人的遊玩狀態。</param>
        public static string For(string name, int level, PlayState play)
        {
            if (RoomBadgeChoice.IsInMatch(play)) return PlayingText;
            return (name ?? "") + (level > 0 ? "  LV:" + level : "");
        }
    }
}
