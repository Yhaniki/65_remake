namespace Sdo.Ruleset
{
    /// <summary>
    /// 舞者「跳舞 / 停舞」的 8 拍結算決策（純函式；呼叫端是 ScreenGameplay.UpdateDanceGate）。
    ///
    /// 官方規則（見 ScreenGameplay._blockHadBreak 的欄位註解）：斷 combo 不會當場停舞，只記旗標，
    /// 到下一個 8 拍（＝計分結算同一節拍）邊界才重新決定：
    ///   1. 這個 block 有 Bad/Miss → combo 還撐在 <see cref="CarryCombo"/> 以上才續跳，否則停。
    ///   2. 沒斷但有判定到音符 → 跳（乾淨的 block 一律跳，即使 combo 很低）。
    ///   3. 沒斷也沒有任何判定（空 block）→ 維持現況（停住的舞者不會因為一段沒音符就自己站起來跳）。
    ///
    /// <paramref name="ignoreMiss"/>＝config.ini 的 <c>opt_danceIgnoreMiss</c>：跳舞完全不受 combo/miss/血量影響，
    /// 打得再爛、血用完都照跳（優先權最大，見 <see cref="Enabled"/>）。唯一不豁免的是空 block —— 那條維持現況，
    /// 不然編輯器/觀察模式那種刻意停住的舞者會被叫起來跳。
    /// </summary>
    public static class DanceGate
    {
        /// <summary>斷了 combo 仍能續跳的門檻：結算當下 combo 要 &gt; 這個值。</summary>
        public const int CarryCombo = 30;

        /// <param name="dancing">目前是否在跳（空 block 時原樣留著）。</param>
        /// <param name="hadBreak">這個 block 有沒有 Bad/Miss。</param>
        /// <param name="hadNote">這個 block 有沒有判定到音符。</param>
        /// <param name="combo">結算當下的 combo。</param>
        /// <param name="ignoreMiss">true＝掉 miss 也照跳（config.ini opt_danceIgnoreMiss）。</param>
        public static bool NextState(bool dancing, bool hadBreak, bool hadNote, int combo, bool ignoreMiss)
        {
            if (!hadBreak && !hadNote) return dancing;   // (3) 空 block：維持現況
            if (ignoreMiss) return true;                 // 掉 miss 不影響跳舞
            if (hadBreak) return combo > CarryCombo;     // (1) 斷了：combo 夠強才續跳
            return true;                                 // (2) 乾淨且有音符：跳
        }

        /// <summary>
        /// 舞者這一幀到底跳不跳（ScreenGameplay 的 <c>DanceEnabled</c> / <c>RecordGate</c> 同用這條）。
        /// 在 <see cref="NextState"/> 的結果之上再加兩道停舞：
        ///   • <paramref name="failed"/>＝一般模式 HP 歸零（遊戲立刻中斷進 GAME OVER）；
        ///   • <paramref name="hpDead"/>＝這局死過（完奏模式歌不切斷，但血用完就回待機站到曲末）。
        /// <paramref name="ignoreMiss"/> 開著時**血量完全不管**（優先權最大）：死了照跳。
        /// </summary>
        public static bool Enabled(bool dancing, bool failed, bool hpDead, bool ignoreMiss)
        {
            if (!dancing) return false;
            if (failed) return false;                    // 一般模式死亡＝遊戲已中斷，畫面切走，不是「繼續跳舞」的情境
            return !hpDead || ignoreMiss;                // 完奏模式死亡：預設停舞；opt_danceIgnoreMiss 開著則照跳
        }
    }
}
