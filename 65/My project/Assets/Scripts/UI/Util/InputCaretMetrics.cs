namespace Sdo.UI.Util
{
    /// <summary>
    /// 自畫游標(房間 / 大廳的聊天輸入框、個人資料視窗的欄位)擺在文字區裡的位置 —— 純幾何,可單元測試。
    ///
    /// 為什麼要一支專門的函式:輸入的字比框還寬時,<c>TMP_InputField</c> 會把**整段文字**往左推
    /// (<c>textComponent.rectTransform.anchoredPosition.x</c> 變負)讓游標留在框內。自畫的游標若只看
    /// 「游標前面那串字有多寬」就會一路往右跑出框外,和真正顯示出來的字對不上 —— 所以要把那個位移加回去。
    /// </summary>
    public static class InputCaretMetrics
    {
        /// <summary>游標左緣相對文字區(textViewport)左緣的 x。</summary>
        /// <param name="x0">第一個字的左緣(文字區的內縮)。</param>
        /// <param name="widthUpToCaret">游標前面那串字(含 IME 組字)的顯示寬。</param>
        /// <param name="textShiftX">TMP 把文字整段推掉的量(往左為負;沒推是 0)。</param>
        /// <param name="viewportW">文字區可見寬;&le; 0 表示不知道 → 不夾。</param>
        /// <param name="caretW">游標自己的寬(夾在右緣時要留給它)。</param>
        public static float CaretX(float x0, float widthUpToCaret, float textShiftX,
                                   float viewportW = 0f, float caretW = 0f)
        {
            float x = x0 + widthUpToCaret + textShiftX;
            if (viewportW <= 0f) return x;
            float max = viewportW - caretW;
            if (max < 0f) max = 0f;
            if (x < 0f) return 0f;
            return x > max ? max : x;
        }
    }
}
