namespace Sdo.Ruleset
{
    /// <summary>
    /// 血條見底之後,**什麼時候才算真的出局**(＝停止遊玩、切 GAME OVER 結算)。純函式。
    ///
    /// 官方多人的語意是「這一場還沒結束」:自己血空不等於全場結束。房裡還有人在打的話,
    /// 血空的人**照樣繼續打**(音符照捲、照判定、只是分數凍結、HP 鎖在地板、舞者停舞),
    /// 直到**全員陣亡**(或歌自然播完)才一起收。GAME OVER 字幕只有前者才打 —— 歌被別人撐到播完的話,
    /// 那一場是正常打完的,只是自己輸了(見 <see cref="GameOverGate.GameOverEnding"/>)。
    ///
    /// 舊版單人重製把「本人血空」直接當成「出局」,於是連線時一個人先死,他的畫面立刻切走:
    /// 別人還在跳的那段他完全看不到,而且他的 playFinished 也提早送上去。
    ///
    /// ⚠️ 與**完奏模式無關**:完奏模式(<c>playFullSong</c>)是「血空也整首打到曲末,永不中途出局」,
    /// 而這條規則講的是**沒開完奏**的人 —— 他一樣要打到房裡的人都倒下為止。兩者是不同的兩件事。
    /// </summary>
    public static class GameOverGate
    {
        /// <summary>
        /// 這位遠端玩家還在場上打嗎(死了 / 中離都不算)。
        ///
        /// 判準刻意與遠端舞者停舞(<see cref="DanceGate.RemoteEnabled"/>)用同一組旗標:
        /// 畫面上站著不動的人,就是這裡不必再等的人 —— 兩邊各用一套判準的話,會出現
        /// 「場上所有人都站著了,自己卻還在打」這種沒有人查得出原因的畫面。
        /// </summary>
        public static bool RemoteStillPlaying(bool dead, bool left) => !dead && !left;

        /// <summary>
        /// 這一幀出局了嗎(＝停止遊玩,當場切 GAME OVER 結算)。每幀求值 —— 條件成立的那一刻
        /// 可能離血空已經過了整整一段歌。
        ///
        /// <list type="bullet">
        /// <item>血還沒空 → 永遠 false(活著的人不會出局;別人死光也不會讓自己提早結算)。</item>
        /// <item>完奏模式 → 永遠 false:整首打到曲末,由曲末那條出口收(那時是不是 GAME OVER
        ///       由 <see cref="GameOverEnding"/> 決定)。</item>
        /// <item>一般模式 → 房裡**沒有人還在打**才出局。<paramref name="anyRemoteStillPlaying"/>
        ///       在離線 / 單人 / 旁觀時恆為 false → 血空當場出局,行為與加這條規則之前一模一樣。</item>
        /// </list>
        ///
        /// 歌自然播完是**另一條獨立的出口**(呼叫端的曲末判定),不走這裡。
        /// </summary>
        public static bool EliminatedNow(bool hpDead, bool playFullSong, bool anyRemoteStillPlaying)
            => hpDead && !playFullSong && !anyRemoteStillPlaying;

        /// <summary>
        /// 這一局要用「GAME OVER 死亡演出」收尾嗎(false = 走一般的輸贏定格 + 短曲 + FINISHED)。
        /// 在**切結算的那一幀**求值。
        ///
        /// 🔴 GAME OVER 大字是「我被淘汰了、這一場對我結束了」的字幕,不是「我這局輸了」的字幕。
        /// 多人時自己血空但**別人還在打**,歌一路播到曲末 —— 那一場是正常打完的,只是自己成績最差:
        /// 走一般輸掉的流程(cat4 輸姿勢 + SE_0015 + 輸的旗),不出 GAME OVER
        /// (使用者指定,2026-08-08)。評分照樣是 F —— 死了就是死了,那是另一回事。
        ///
        /// 反過來,出局那條路(<see cref="EliminatedNow"/>)必然 <c>anyRemoteStillPlaying == false</c>,
        /// 所以離線 / 單人 / 全員陣亡一律照舊出 GAME OVER;完奏模式單人死透打完整首也還是 GAME OVER。
        /// </summary>
        public static bool GameOverEnding(bool hpDead, bool anyRemoteStillPlaying)
            => hpDead && !anyRemoteStillPlaying;
    }
}
