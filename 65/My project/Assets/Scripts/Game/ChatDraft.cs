namespace Sdo.Game
{
    /// <summary>
    /// 聊天輸入框的純字串規則 —— **房間左下角那個輸入框與遊戲畫面右下角那個共用同一套**,
    /// 玩家在哪一邊點表情 / 點名字,打出來的東西才會一模一樣。
    ///
    /// 為什麼放在 <c>Sdo.Game</c>:asmdef 是 <c>Sdo.UI → Sdo.Game</c> 的單向依賴,房間那邊引用得到這裡,
    /// 反過來不行(<c>RoomChatCommand</c> 在 Sdo.UI,遊戲畫面拿不到)。這裡只放不碰任何 Unity 型別的字串規則。
    /// </summary>
    public static class ChatDraft
    {
        /// <summary>
        /// 換上新的密語對象:<c>[名字] </c> + 使用者已經打的內容。已經有 <c>[舊名字]</c> 前綴就**換掉**
        /// (不是疊上去),名字前後的空白吃掉;名字是空的就原樣不動。
        /// </summary>
        public static string WithWhisperTarget(string draft, string name)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0) return draft ?? "";
            string body = draft ?? "";
            int close = body.StartsWith("[", System.StringComparison.Ordinal) ? body.IndexOf(']') : -1;
            if (close >= 0) body = body.Substring(close + 1).TrimStart();
            return "[" + n + "] " + body;
        }

        /// <summary>
        /// 把表情指令(<c>/GO</c>)接到輸入框後面:前面有字就補一個空白隔開,結尾也留一個空白讓人接著打。
        /// <paramref name="limit"/> &gt; 0 時截到那個長度(輸入框的 characterLimit)。
        /// </summary>
        public static string WithExpression(string draft, string command, int limit = 0)
        {
            string cmd = command ?? "";
            if (cmd.Length == 0) return draft ?? "";
            string s = draft ?? "";
            if (s.Length > 0 && !s.EndsWith(" ")) s += " ";
            s += cmd + " ";
            if (limit > 0 && s.Length > limit) s = s.Substring(0, limit);
            return s;
        }
    }
}
