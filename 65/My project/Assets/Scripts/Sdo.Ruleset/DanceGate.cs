namespace Sdo.Ruleset
{
    /// <summary>一位舞者在某個結算點的判定計數(只有 gate 需要的那幾個欄位)。</summary>
    public struct DanceJudgeCounts
    {
        public int Perfect, Cool, Bad, Miss;

        /// <summary>判定過的音符總數。</summary>
        public int Total => Perfect + Cool + Bad + Miss;

        /// <summary>斷連的次數(Bad + Miss)。</summary>
        public int Breaks => Bad + Miss;

        public DanceJudgeCounts(int perfect, int cool, int bad, int miss)
        {
            Perfect = perfect; Cool = cool; Bad = bad; Miss = miss;
        }
    }

    /// <summary>
    /// 「舞者現在該跳舞還是站著待機」的規則 —— **本機與遠端共用同一份**。
    ///
    /// 🔴 為什麼一定要抽成共用的純函式(而不是遠端那邊照抄一份):
    /// 這條規則有一個門檻值(<see cref="MinComboAfterBreak"/>)。哪天有人調它,而遠端那份沒跟著改,
    /// 結果是「別人的角色跳的跟他自己看到的不一樣」—— 那是一個沒有任何測試會紅、也沒有人會回報得清楚的
    /// bug(誰會注意到別人畫面上的舞者多站了兩個八拍?)。同一個函式就不可能漂移。
    ///
    /// 規則本身逐字重製自 EXE 的行為(見 <c>ScreenGameplay.UpdateDanceGate</c> 的註解):
    /// 決定**只在 8 拍結算點**做,斷連不會當場讓舞者停下來,而是記著、等下一個結算點一起判。
    /// </summary>
    public static class DanceGate
    {
        /// <summary>斷過連之後還想繼續跳,combo 至少要**大於**這個值。</summary>
        public const int MinComboAfterBreak = 30;

        /// <summary>結算間隔 = 8 拍 = 2 小節(與計分結算同一個節奏)。</summary>
        public static double SettleMs(double bpm) => 8.0 * (60000.0 / (bpm > 1.0 ? bpm : 1.0));

        /// <summary>
        /// 一個結算點的決定。
        /// </summary>
        /// <param name="dancing">目前在跳嗎(空 block 會維持它)。</param>
        /// <param name="hadBreak">這個 block 裡有沒有 Bad/Miss。</param>
        /// <param name="hadNote">這個 block 裡有沒有任何音符被判定。</param>
        /// <param name="combo">結算這一刻的 combo。</param>
        public static bool Next(bool dancing, bool hadBreak, bool hadNote, int combo)
        {
            if (hadBreak) return combo > MinComboAfterBreak;   // (1) 斷過 → 只有 combo 還夠強才繼續跳
            if (hadNote) return true;                          // (2) 這個 block 有音符且沒斷 → 跳/恢復跳
            return dancing;                                    // (3) 空 block(沒斷也沒音符)→ 維持現狀
        }

        // ---- 遠端推導 ----------------------------------------------------------------------------------
        // 遠端只收到分數流的累計數字(每秒約 5 筆),收不到「這個 block 有沒有斷」這種 per-block 的旗標。
        // 但那兩個旗標可以從**相鄰兩筆的差**還原出來 —— 這就是分數流不必傳按鍵記錄的原因(計畫的 D4)。

        /// <summary>兩筆之間有沒有斷過連。</summary>
        public static bool HadBreak(DanceJudgeCounts prev, DanceJudgeCounts cur) => cur.Breaks > prev.Breaks;

        /// <summary>兩筆之間有沒有判定過音符。</summary>
        public static bool HadNote(DanceJudgeCounts prev, DanceJudgeCounts cur) => cur.Total > prev.Total;

        /// <summary>
        /// 遠端版:拿相鄰兩筆推出旗標,再套 <see cref="Next"/>。
        ///
        /// ⚠️ 這裡有一個**已知且刻意接受**的限制:分數流是取樣的,所以一個 8 拍 block 內若「先斷連、
        /// 之後又打回一大串」,遠端只看得到期間的總差,判斷會與本機一致;但若兩筆取樣剛好橫跨結算點,
        /// 遠端可能把上一個 block 的斷算到這一個 block。誤差最多讓遠端的舞者多跳或多站一個 block(約 2 小節),
        /// 而**判定與分數完全不受影響**(那些是本機權威、直接照抄 server 的數字)。
        /// 要做到逐 block 精確就得傳 per-block 旗標,那是為了視覺上的一格差異多開一條協定欄位 —— 不值得。
        /// </summary>
        public static bool NextFromSamples(bool dancing, DanceJudgeCounts prev, DanceJudgeCounts cur, int combo)
            => Next(dancing, HadBreak(prev, cur), HadNote(prev, cur), combo);
    }
}
