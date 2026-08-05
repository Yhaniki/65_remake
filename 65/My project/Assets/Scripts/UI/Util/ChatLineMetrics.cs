namespace Sdo.UI.Util
{
    /// <summary>
    /// 聊天訊息欄裡「一則訊息要向排版要多高」的算式 —— 純數字,可單元測試。
    ///
    /// 房間左下與大廳左下的訊息欄都是 <c>VerticalLayoutGroup</c> + 每則訊息一個 <c>LayoutElement</c>,
    /// 而那個 <c>preferredHeight</c> 以前是**寫死一行的高度**。訊息一長就折到第二行:排版仍然只留一行的位置,
    /// 於是第二行壓在下一則訊息上、捲到底也只捲得到第一行(使用者回報的兩個症狀)。
    /// </summary>
    public static class ChatLineMetrics
    {
        /// <summary>
        /// 一則訊息的排版高度。
        ///
        /// 刻意**不去數折了幾行**再乘行高 —— 那要假設「TMP 的單行高 == 我們設定的行距」,字型換一支就不成立。
        /// 這裡直接拿 TMP 限寬量到的實際文字高,再補上原本設計在行距裡的那點空隙
        /// (<paramref name="rowHeight"/> − 單行字身高)。單行訊息因此**剛好還是** <paramref name="rowHeight"/>
        /// (既有版面一格不動,大廳「一次 8 行」的算式仍然成立),折行的訊息則長出它真正需要的高度。
        /// </summary>
        /// <param name="wrappedHeight">限寬(欄寬)量到的文字總高。</param>
        /// <param name="singleRowHeight">同一串字不限寬(＝保證不折)量到的高。</param>
        /// <param name="rowHeight">設計上的單行行距(房間 16 / 大廳 12.5)。</param>
        public static float BlockHeight(float wrappedHeight, float singleRowHeight, float rowHeight)
        {
            if (wrappedHeight <= 0f || singleRowHeight <= 0f) return rowHeight;
            float h = wrappedHeight + (rowHeight - singleRowHeight);   // 行距裡多留的那點空隙照舊
            return h < rowHeight ? rowHeight : h;                      // 永遠不會比一行還矮
        }
    }
}
