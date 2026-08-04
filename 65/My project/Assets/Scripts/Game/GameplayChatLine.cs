using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 遊戲畫面聊天框要畫的**一行**。刻意是「已經排好版的字」而不是 <c>ChatMessage</c>:
    /// <c>Sdo.Game</c> 不能引用 <c>Sdo.UI</c>(asmdef 是 UI → Game 的單向依賴),所以「這句話屬於哪個頻道、
    /// 該不該顯示、要用什麼顏色、名字後面要不要加冒號」全部在前端(<c>FrontendApp</c>)決定好再推進來。
    /// 表情的圖也一樣由前端提供(<c>RoomExpressionArt.SmallFrames</c>),這邊只負責畫。
    /// </summary>
    public struct GameplayChatLine
    {
        /// <summary>名字欄(前端已加好冒號,例如「Eithwa:」)。空 = 這行沒有名字(系統行 / 密語整句)。</summary>
        public string Name;
        /// <summary>表情前面的字(保留輸入時 emoji 的位置:前字〔emoji〕後字)。</summary>
        public string Lead;
        /// <summary>表情動畫的畫格;null/空 = 這行沒有表情。</summary>
        public Sprite[] ExpressionFrames;
        /// <summary>表情後面的字;沒有表情時就是整句內容。</summary>
        public string Body;
        /// <summary>整行顏色的 RGB hex(不含 #),空 = 白。與房間 <c>ChatPalette</c> 同一組值。</summary>
        public string ColorHex;

        /// <summary>沒有表情圖時,把三段字接成一整串(給單一 Label3D 畫)。</summary>
        public string PlainText()
        {
            string s = Name ?? "";
            string lead = Lead ?? "";
            string body = Body ?? "";
            if (lead.Length > 0) s = s.Length > 0 ? s + " " + lead : lead;
            if (body.Length > 0) s = s.Length > 0 ? s + " " + body : body;
            return s;
        }
    }
}
