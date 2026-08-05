using System;
using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>
    /// 把一句話切成「一列裝得下」的幾段 —— 純邏輯(寬度由呼叫端量),可單元測試。
    ///
    /// 遊戲畫面右下那條聊天列跟房間/大廳不一樣:它不是 TMP,是一列一顆 <c>Label3D</c>(TextMesh),
    /// **完全沒有自動折行** —— 訊息一長就直接畫出欄寬外面。這裡負責在推進聊天框之前先把它切成幾列,
    /// 每一列各自成為一行訊息,原本的排版/淡出/上限 14 列全部沿用。
    ///
    /// 切法是「裝得下就多收一個字」的貪婪法(中文本來就逐字折,一串數字也一樣照欄寬折,不會整串跳下一列),
    /// 但切點後面若不是空白,會往回找最近的空白 —— 英文單字不要無謂地被切成兩半。回退超過半列就不回退
    /// (不然一個超長單字會把整列讓出去,又變回「這一列空著」的老問題)。
    /// </summary>
    public static class ChatTextWrap
    {
        /// <summary>回退找空白時,最多讓出這一列的多少比例(超過就照原切點硬切)。</summary>
        private const float MaxRetreatFrac = 0.5f;

        /// <summary>
        /// 切成幾段。<paramref name="prefixWidth"/>(n) = <paramref name="text"/> 前 n 個字元畫出來的寬度
        /// (必須可相加:一段 [a,b) 的寬 = prefixWidth(b) − prefixWidth(a))。
        /// </summary>
        /// <param name="firstWidth">第一段可用的寬(有表情圖的那一列要先扣掉名字與小圖)。</param>
        /// <param name="restWidth">之後每一段可用的寬(整個欄寬)。</param>
        public static List<string> Wrap(string text, Func<int, float> prefixWidth, float firstWidth, float restWidth)
        {
            var parts = new List<string>();
            if (string.IsNullOrEmpty(text) || prefixWidth == null) { parts.Add(text ?? ""); return parts; }

            int a = 0;
            bool first = true;
            while (a < text.Length)
            {
                float budget = first ? firstWidth : restWidth;
                float w0 = prefixWidth(a);
                int b = a + 1;   // 至少收一個字:再窄也要往前走,否則會空轉
                while (b < text.Length && prefixWidth(b + 1) - w0 <= budget) b++;

                if (b < text.Length && !char.IsWhiteSpace(text[b]))
                {
                    int sp = LastSpace(text, a, b);
                    if (sp > a && (b - sp) <= (b - a) * MaxRetreatFrac) b = sp;
                }

                parts.Add(text.Substring(a, b - a).TrimEnd());
                a = b;
                while (a < text.Length && text[a] == ' ') a++;   // 折在空白處 → 那個空白吃掉,下一列不要縮排
                first = false;
            }
            if (parts.Count == 0) parts.Add("");
            return parts;
        }

        /// <summary>[a, b) 裡最後一個空白的位置;沒有就回 −1。</summary>
        private static int LastSpace(string s, int a, int b)
        {
            for (int i = b - 1; i > a; i--)
                if (s[i] == ' ') return i;
            return -1;
        }
    }
}
