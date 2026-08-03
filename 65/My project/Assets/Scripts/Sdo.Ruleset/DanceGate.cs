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
    /// 舞者「跳舞 / 停舞」的 8 拍結算決策(純函式)—— **本機與遠端共用同一份**。
    ///
    /// 官方規則(見 ScreenGameplay._blockHadBreak 的欄位註解):斷 combo 不會當場停舞,只記旗標,
    /// 到下一個 8 拍(＝計分結算同一節拍)邊界才重新決定:
    ///   1. 這個 block 有 Bad/Miss → combo 還撐在 <see cref="CarryCombo"/> 以上才續跳,否則停。
    ///   2. 沒斷但有判定到音符 → 跳(乾淨的 block 一律跳,即使 combo 很低)。
    ///   3. 沒斷也沒有任何判定(空 block)→ 維持現況(停住的舞者不會因為一段沒音符就自己站起來跳)。
    ///
    /// 🔴 為什麼規則一定要抽成共用的純函式(而不是遠端那邊照抄一份):
    /// 它有一個門檻值(<see cref="CarryCombo"/>)。哪天有人調它,而遠端那份沒跟著改,結果是
    /// 「別人的角色跳的跟他自己看到的不一樣」—— 那是一個沒有任何測試會紅、也沒有人會回報得清楚的
    /// bug(誰會注意到別人畫面上的舞者多站了兩個八拍?)。同一個函式就不可能漂移。
    /// </summary>
    public static class DanceGate
    {
        /// <summary>斷了 combo 仍能續跳的門檻:結算當下 combo 要 &gt; 這個值。</summary>
        public const int CarryCombo = 30;

        /// <summary>結算間隔 = 8 拍 = 2 小節(與計分結算同一個節奏)。
        /// BPM 壞掉(0 或負)時回一個很大但**有限**的間隔(等於「這首歌不結算」)——
        /// 不是 Infinity/NaN,那會讓呼叫端的 <c>while (now &gt;= next)</c> 變成無限迴圈或永遠不跑。</summary>
        public static double SettleMs(double bpm) => 8.0 * (60000.0 / (bpm > 1.0 ? bpm : 1.0));

        /// <param name="dancing">目前是否在跳(空 block 時原樣留著)。</param>
        /// <param name="hadBreak">這個 block 有沒有 Bad/Miss。</param>
        /// <param name="hadNote">這個 block 有沒有判定到音符。</param>
        /// <param name="combo">結算當下的 combo。</param>
        /// <param name="ignoreMiss">true＝掉 miss 也照跳(config.ini <c>opt_danceIgnoreMiss</c>)。
        /// 唯一不豁免的是空 block —— 那條維持現況,不然編輯器/觀察模式那種刻意停住的舞者會被叫起來跳。</param>
        public static bool NextState(bool dancing, bool hadBreak, bool hadNote, int combo, bool ignoreMiss)
        {
            if (!hadBreak && !hadNote) return dancing;   // (3) 空 block:維持現況
            if (ignoreMiss) return true;                 // 掉 miss 不影響跳舞
            if (hadBreak) return combo > CarryCombo;     // (1) 斷了:combo 夠強才續跳
            return true;                                 // (2) 乾淨且有音符:跳
        }

        /// <summary>
        /// 舞者這一幀到底跳不跳(ScreenGameplay 的 <c>DanceEnabled</c> / <c>RecordGate</c> 同用這條)。
        /// 在 <see cref="NextState"/> 的結果之上再加兩道停舞:
        ///   • <paramref name="failed"/>＝一般模式 HP 歸零(遊戲立刻中斷進 GAME OVER);
        ///   • <paramref name="hpDead"/>＝這局死過(完奏模式歌不切斷,但血用完就回待機站到曲末)。
        /// <paramref name="ignoreMiss"/> 開著時**血量完全不管**(優先權最大):死了照跳。
        /// </summary>
        public static bool Enabled(bool dancing, bool failed, bool hpDead, bool ignoreMiss)
        {
            if (!dancing) return false;
            if (failed) return false;                    // 一般模式死亡＝遊戲已中斷,畫面切走,不是「繼續跳舞」的情境
            return !hpDead || ignoreMiss;                // 完奏模式死亡:預設停舞;opt_danceIgnoreMiss 開著則照跳
        }

        // ---- 遠端推導 ----------------------------------------------------------------------------------
        // 遠端只收到分數流的累計數字(每秒約 5 筆),收不到「這個 block 有沒有斷」這種 per-block 的旗標。
        // 但那兩個旗標可以從**相鄰兩筆的差**還原出來 —— 這就是分數流不必傳按鍵記錄的原因(計畫的 D4)。

        /// <summary>兩筆之間有沒有斷過連。</summary>
        public static bool HadBreak(DanceJudgeCounts prev, DanceJudgeCounts cur) => cur.Breaks > prev.Breaks;

        /// <summary>兩筆之間有沒有判定過音符。</summary>
        public static bool HadNote(DanceJudgeCounts prev, DanceJudgeCounts cur) => cur.Total > prev.Total;

        /// <summary>
        /// 遠端版:拿相鄰兩筆推出旗標,再套 <see cref="NextState"/>。
        ///
        /// 🔴 <c>ignoreMiss</c> 一律傳 <b>false</b>(＝官方規則)。那個選項是**對方那台機器**的
        /// config.ini(<c>opt_danceIgnoreMiss</c>),協定沒有帶它,我們也不該猜 —— 它是一個
        /// 「讓自己看得順眼」的本機顯示選項,不是房間規則。代價是:對方把它打開時,他自己畫面上的
        /// 角色會一直跳,而你這邊那尊仍照官方規則停 —— 純視覺差異,判定與分數完全不受影響。
        ///
        /// ⚠️ 另一個**已知且刻意接受**的限制:分數流是取樣的,所以一個 8 拍 block 內若「先斷連、
        /// 之後又打回一大串」,遠端只看得到期間的總差,判斷會與本機一致;但若兩筆取樣剛好橫跨結算點,
        /// 遠端可能把上一個 block 的斷算到這一個 block。誤差最多讓遠端的舞者多跳或多站一個 block(約 2 小節),
        /// 而**判定與分數完全不受影響**(那些是本機權威、直接照抄 server 的數字)。
        /// 要做到逐 block 精確就得傳 per-block 旗標,那是為了視覺上的一格差異多開一條協定欄位 —— 不值得。
        /// </summary>
        public static bool NextFromSamples(bool dancing, DanceJudgeCounts prev, DanceJudgeCounts cur, int combo)
            => NextState(dancing, HadBreak(prev, cur), HadNote(prev, cur), combo, ignoreMiss: false);
    }
}
