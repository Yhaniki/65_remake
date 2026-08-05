using System.Text;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 讓「一長串英數」也能在欄寬中途折行 —— 純字串處理,可單元測試。
    ///
    /// TMP 的折行是**詞邊界**折行:中文每個字都是一個可折點,所以中文訊息會乖乖填滿一行才換行;
    /// 但 <c>888888…</c> / 一長串英文字母對它來說是**一個單字**,擠不進這一行的剩餘寬度時就整串搬到下一行
    /// —— 使用者回報的「全部輸入數字時,字直接跳到下一排去」。
    ///
    /// 做法是在超過 <c>minRun</c> 個字元的英數串裡,每個字元後面塞一個 U+200B(零寬空格)。TMP 認得它是可折點
    /// (<c>TMP_Text</c> 的斷行判斷把 0x200B 與空白同等對待),而且字型資產把它合成成**零寬度**的字元,
    /// 所以顯示寬度、量出來的高度都不受影響。短單字不動 —— 正常的英文句子不該被從中間切開。
    /// </summary>
    public static class ChatSoftWrap
    {
        /// <summary>零寬空格:TMP 的可折點,顯示寬度 0。</summary>
        public const char Zwsp = '\u200B';

        /// <summary>連續幾個英數字元以上才允許中途折(短單字不切)。</summary>
        public const int DefaultMinRun = 12;

        /// <summary>
        /// 在 <paramref name="rich"/>(可含 TMP 標籤)裡,把長度 &ge; <paramref name="minRun"/> 的
        /// 「不可折字元串」拆成可折的。標籤 <c>&lt;…&gt;</c> 與跳脫實體 <c>&amp;lt;</c> 一律原樣保留、不會被切開。
        /// 沒有東西要處理時回傳原字串本身(不配置)。
        /// </summary>
        public static string Apply(string rich, int minRun = DefaultMinRun)
            => Apply(rich, minRun, -1, out _);

        /// <summary>
        /// 同上,另外把一個**顯示字元索引**換算成塞了可折點之後的索引。頭上打字泡的游標是用這個索引去問
        /// <c>textInfo.characterInfo</c> 要座標的,而零寬空格自己也佔一格 —— 不換算的話字愈長游標偏得愈多。
        /// </summary>
        /// <param name="caretCharIndex">換算前的顯示字元索引;&lt; 0 = 不需要換算。</param>
        public static string Apply(string rich, int minRun, int caretCharIndex, out int mappedCaretCharIndex)
        {
            mappedCaretCharIndex = caretCharIndex;
            if (string.IsNullOrEmpty(rich)) return rich ?? "";
            if (minRun < 2) minRun = 2;

            StringBuilder sb = null;
            int i = 0;
            int chars = 0;        // 已輸出的顯示字元數(標籤不算,塞進去的零寬空格也不算)
            int zwspBefore = 0;   // 落在游標**前面**的零寬空格數
            while (i < rich.Length)
            {
                char c = rich[i];
                if (c == '<')   // TMP 標籤:整段原樣抄過去(裡面的 link id / 色碼絕不能被塞東西,也不算顯示字元)
                {
                    int end = rich.IndexOf('>', i);
                    if (end < 0) { sb?.Append(rich, i, rich.Length - i); break; }
                    sb?.Append(rich, i, end - i + 1);
                    i = end + 1;
                    continue;
                }
                if (!IsRunChar(c)) { sb?.Append(c); i++; chars++; continue; }

                // 一段連續的「TMP 折不開」的字元(英數/半形標點),以「顯示單位」計數:跳脫實體算一個。
                int start = i, units = 0;
                while (i < rich.Length && rich[i] != '<' && IsRunChar(rich[i]))
                {
                    i += UnitLength(rich, i);
                    units++;
                }
                if (units < minRun) { sb?.Append(rich, start, i - start); chars += i - start; continue; }

                if (sb == null) sb = new StringBuilder(rich.Length + units).Append(rich, 0, start);
                for (int k = start; k < i; )
                {
                    int len = UnitLength(rich, k);
                    sb.Append(rich, k, len);
                    k += len;
                    chars += len;
                    if (k >= i) break;
                    sb.Append(Zwsp);                                        // 每個字元之間都給一個可折點
                    if (caretCharIndex >= 0 && chars <= caretCharIndex) zwspBefore++;
                }
            }
            if (caretCharIndex >= 0) mappedCaretCharIndex = caretCharIndex + zwspBefore;
            return sb == null ? rich : sb.ToString();
        }

        /// <summary>這個字元是不是「TMP 自己折不開」的:非空白、而且不是 CJK/全形(那些每個字都可折)。</summary>
        private static bool IsRunChar(char c)
            => !char.IsWhiteSpace(c) && c < '\u2E80' && c != Zwsp;

        /// <summary>一個顯示單位的長度:<c>&amp;lt;</c> 之類的跳脫實體整組算一個,其餘 1。</summary>
        private static int UnitLength(string s, int i)
        {
            if (s[i] != '&') return 1;
            int max = i + 9 < s.Length ? i + 9 : s.Length;
            for (int j = i + 1; j < max; j++)
            {
                if (s[j] == ';') return j - i + 1;
                if (!char.IsLetterOrDigit(s[j]) && s[j] != '#') break;
            }
            return 1;
        }
    }
}
