namespace Sdo.UI.Services
{
    /// <summary>
    /// 「這一場是什麼模式」的純規則(0=自由 1=普通 2=ShowTime,對應 <c>GameSession.GameMode</c> 的編碼)。
    ///
    /// 兩件事本來散在各處、而且散得不一致:
    ///
    /// 1. **來源**:線上一律以 server 的房間設定為準(房主自己也是 —— 它按的模式會 push 上去)。
    ///    非房主的 <c>GameSession.GameMode</c> 還留著自己上次在選歌對話框選的值,拿它當來源的話,
    ///    「自由模式的房間」在別人那台會被當普通模式跑:名次照出、還照記勝負。
    ///    離線沒有 server,session 就是房間設定。
    ///
    /// 2. **自由模式代表什麼**:整場不出名次(遊戲中的 N/M 與右側名單、結算列最左的名次數字)、
    ///    不出 YOU WIN/LOSE、不記勝負(<c>PlayStats.RecordsWinLoss</c>)。
    ///    🔴 **G幣/EXP 照給**(使用者指定):名次仍然照算,只是不畫出來,獎勵就照那個名次發。
    ///    判定/血量/GAME OVER 也都照常。
    ///
    /// 零 Unity 依賴 → 可單元測試,見 GameModeRulesTests。
    /// </summary>
    public static class GameModeRules
    {
        public const int Free = 0;        // 自由模式
        public const int Normal = 1;      // 普通模式
        public const int Showtime = 2;    // ShowTime(氣條/集氣)模式

        /// <summary>自由模式 = 不比名次(但 G幣/EXP 照給)的那個模式。</summary>
        public static bool IsFree(int gameMode) => Clamp(gameMode) == Free;

        /// <summary>ShowTime 模式(氣條/集氣玩法)。</summary>
        public static bool IsShowtime(int gameMode) => Clamp(gameMode) == Showtime;

        /// <summary>
        /// 這一場實際生效的模式。<paramref name="roomGameMode"/> = server 房間設定裡的模式
        /// (<c>null</c> = 離線 / 還沒有房間快照)→ 有就以它為準,沒有才退回本機 session 的值。
        /// </summary>
        public static int Effective(int? roomGameMode, int sessionGameMode)
            => Clamp(roomGameMode ?? sessionGameMode);

        /// <summary>夾回 0..2(協定與 config.ini 都可能塞進超出範圍的值)。</summary>
        public static int Clamp(int gameMode)
            => gameMode < Free ? Free : (gameMode > Showtime ? Showtime : gameMode);
    }
}
