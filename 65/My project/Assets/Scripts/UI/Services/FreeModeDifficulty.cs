namespace Sdo.UI.Services
{
    /// <summary>
    /// 自由模式的「每個人自己挑難度」規則。
    ///
    /// 官方版面(DDRROOM.XML)把這件事講得很清楚:<c>FMGameLevel</c> 這個小視窗與房主的
    /// <c>songselect</c>(房主設置)鈕**擺在同一個位置**(win2 的 (5,292) 對 (14,296))——
    /// 兩者互斥。房主用那格選歌,其他人在自由模式下就把那格換成「難度設置 ◄ EASY ►」,
    /// 各自挑自己要打的難度;右側面板上方的難度數字與 CD 光碟跟著自己選的走,
    /// 所以同一首歌可以每個人打不同難度的譜。
    ///
    /// 為什麼難度不進 <c>NetRoomSettings</c>(不同步給別人):它跟速度 / note 皮 / 掉落方向
    /// 是同一類東西 —— **個人偏好**。房主決定的是「哪一首歌」,不是「你要打多難」。
    ///
    /// 這裡是純規則(零 Unity 依賴)→ 可單元測試,見 FreeModeDifficultyTests。
    /// </summary>
    public static class FreeModeDifficulty
    {
        /// <summary>難度槽數(EASY / NORMAL / HARD)。對映 <c>Sdo.UI.Core.Difficulty</c>。</summary>
        public const int SlotCount = 3;

        /// <summary>自由模式的模式代號(<c>GameSession.GameMode</c>:0=自由 1=普通 2=ShowTime)。</summary>
        public const int FreeGameMode = 0;

        /// <summary>
        /// 這台要不要顯示「難度設置」那格 —— 自由模式**而且不是房主**。
        ///
        /// 房主看到的仍是「房主設置」(選歌)鈕:它挑的難度就是它自己要打的那個,
        /// 不需要再多一個選擇器(而且官方那兩格本來就疊在一起,只能二選一)。
        /// </summary>
        public static bool PlayerPicksOwn(int gameMode, bool isHost)
            => gameMode == FreeGameMode && !isHost;

        /// <summary>
        /// 按下 ◄ / ► 之後的難度。<paramref name="dir"/> = -1 / +1。
        ///
        /// **會跳過沒有譜的難度**,而且**到底就停住不繞回去**(箭頭鈕的語意):外部歌很常只有一兩張譜
        /// (osu 圖包的難度數不固定),繞回去會讓玩家以為按錯了。整首歌都沒有可打的譜時原地不動。
        /// <paramref name="playable"/> 傳 null = 不知道(缺歌 / 查不到目錄)→ 三個都當作可選。
        /// </summary>
        public static int Step(int current, int dir, bool[] playable)
        {
            int cur = Clamp(current);
            if (dir == 0) return cur;
            int d = dir > 0 ? 1 : -1;
            for (int i = cur + d; i >= 0 && i < SlotCount; i += d)
                if (IsPlayable(playable, i)) return i;
            return cur;
        }

        /// <summary>
        /// 把一個「想要的難度」貼到最近的**可打**難度上(換歌之後用:上一首選 HARD,新的那首只有 EASY)。
        ///
        /// 距離一樣時取**比較簡單**的那個 —— 自動幫人跳到更難的譜不是好的預設。
        /// 完全沒有可打的譜(或 <paramref name="playable"/> 為 null)就原樣夾回 0..2。
        /// </summary>
        public static int Snap(int want, bool[] playable)
        {
            int w = Clamp(want);
            if (IsPlayable(playable, w)) return w;
            for (int dist = 1; dist < SlotCount; dist++)
            {
                int lo = w - dist, hi = w + dist;
                if (lo >= 0 && IsPlayable(playable, lo)) return lo;
                if (hi < SlotCount && IsPlayable(playable, hi)) return hi;
            }
            return w;
        }

        /// <summary>「難度設置」框裡那個值的字。官方就是英文大寫(FMLvlChoose 的 labeltext="EASY")。</summary>
        public static string Name(int slot)
        {
            switch (Clamp(slot))
            {
                case 1: return "NORMAL";
                case 2: return "HARD";
                default: return "EASY";
            }
        }

        /// <summary>0..2 夾值。</summary>
        public static int Clamp(int slot) => slot < 0 ? 0 : (slot >= SlotCount ? SlotCount - 1 : slot);

        private static bool IsPlayable(bool[] playable, int slot)
        {
            if (slot < 0 || slot >= SlotCount) return false;
            if (playable == null || playable.Length <= slot) return true;   // 不知道 → 當作可選
            return playable[slot];
        }
    }
}
