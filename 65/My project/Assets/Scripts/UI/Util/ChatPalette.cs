namespace Sdo.UI.Util
{
    /// <summary>
    /// 聊天訊息欄的配色 —— **房間與大廳共用同一份**。
    ///
    /// 這組值原本只寫在 <c>RoomScreen</c> 裡,大廳那邊一度是「整區白字」(那是使用者當時的要求),
    /// 後來使用者要求兩邊一致:家族綠、密語青、系統金 —— 也就是照房間這組。抽出來是為了不讓同一個
    /// 顏色在兩個檔案裡各寫一份:改色只改這裡,兩邊一起變。
    ///
    /// 🔴 這些是 **TMP rich-text 用的 hex 字串**(不帶 #),所以是 <c>const string</c> 而不是 Color ——
    ///    聊天行是用 <c>&lt;color=#XXXXXX&gt;</c> 包出來的,不是設 Graphic 的 color。
    /// </summary>
    public static class ChatPalette
    {
        /// <summary>系統訊息(金黃)。</summary>
        public const string SystemHex = "F0C24A";
        /// <summary>密語(青)—— 「你對X說 / X對你說」整行。好友頻道看到的就是這個顏色。</summary>
        public const string WhisperHex = "1EFEFE";
        /// <summary>進出舞台廣播(淺藍)。只有房間會畫,大廳不顯示這一類。</summary>
        public const string StageHex = "72C1FE";
        /// <summary>家族頻道(綠):「&lt;家族&gt;名字: 內容」與「你沒有家族」同色。</summary>
        public const string GuildHex = "3CE63C";
        /// <summary>一般行與「你說」(白)。</summary>
        public const string PlainHex = "FFFFFF";
    }
}
