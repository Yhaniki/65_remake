namespace Sdo.Game
{
    /// <summary>
    /// 「IME 還沒選字上屏」的那一段字要怎麼標出來 —— **底線**,三個打字的地方共用同一種回饋:
    ///
    ///   • 房間左下 / 大廳左下的輸入框:TMP_InputField 自己會把 <c>compositionString</c> 包成
    ///     <c>&lt;u&gt;…&lt;/u&gt;</c>(richText 開著才有,見 <c>RoomScreen.ConfigureRoomChatInput</c>)。
    ///   • 房間的頭上打字泡:也是 TMP,所以照抄同一個標籤(<see cref="Underline"/>)。
    ///   • 遊戲畫面右下那條:字是 legacy <c>TextMesh</c>(<c>Label3D</c>)畫的,舊版富文字**沒有**
    ///     <c>&lt;u&gt;</c> 標籤 —— 只能自己在字底下擺一條白線,那就要知道「畫出來的字串裡,
    ///     哪幾個字是組字中的」(<see cref="ShownStart"/>)。
    ///
    /// 放在 <c>Sdo.Game</c> 是因為 asmdef 是 <c>Sdo.UI → Sdo.Game</c> 的單向依賴:房間/大廳引用得到這裡,
    /// 反過來不行。這裡只有字串與索引,不碰任何 Unity 型別。
    /// </summary>
    public static class ImeCompose
    {
        public const string UnderlineOpen = "<u>";
        public const string UnderlineClose = "</u>";

        /// <summary>把(已跳脫過的)組字串包上底線標籤;沒有組字就回空字串(不要吐出空的一對標籤)。</summary>
        public static string Underline(string escapedComposition)
        {
            if (string.IsNullOrEmpty(escapedComposition)) return "";
            return UnderlineOpen + escapedComposition + UnderlineClose;
        }

        /// <summary>
        /// 顯示字串裡「組字段」的**起始字元索引**(回傳值 == <paramref name="shownLength"/> 代表這一刻沒有組字要畫線)。
        ///
        /// 組字永遠接在草稿尾端,而輸入框太窄時是**砍掉開頭**留尾巴(<c>GameplayChat.ClipToWidth</c>)
        /// → 看得見的組字段就是最後 min(組字長度, 顯示長度) 個字元;組字比框還長時整段都在畫線範圍內。
        /// </summary>
        public static int ShownStart(int shownLength, int composingLength)
        {
            if (shownLength <= 0) return 0;
            if (composingLength <= 0) return shownLength;
            return composingLength >= shownLength ? 0 : shownLength - composingLength;
        }
    }
}
