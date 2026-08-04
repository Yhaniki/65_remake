namespace Sdo.Game
{
    /// <summary>
    /// 遊戲中聊天「文字」的自動隱藏 —— 純函式(可單元測試)。
    ///
    /// 規則(使用者指定):進遊戲**預設不顯示任何字**;有人講話(或自己開始打字)就整批冒出來;之後
    /// <see cref="IdleHideSec"/> 秒都沒有新訊息就**直接消失**(使用者指定不要淡出)。
    ///
    /// 只管**訊息文字**。底板/当前鈕/輸入框/送出鈕那一條是常駐的(官方畫面也是),不受這裡影響。
    /// </summary>
    public static class GameplayChatFade
    {
        /// <summary>最後一則訊息之後,文字還留在畫面上的秒數。</summary>
        public const float IdleHideSec = 10f;

        /// <summary>
        /// 訊息文字這一刻的不透明度 —— 只有 1(全顯示)或 0(全隱藏),中間不過渡。
        /// </summary>
        /// <param name="typing">正在打字 → 一律全顯示(要看得到自己在跟誰講什麼)。</param>
        /// <param name="lastActivitySec">最後一次「有訊息進來 / 送出」的時間(秒);≤0 = 從來沒有過 → 預設不顯示。</param>
        /// <param name="nowSec">現在時間(秒),與 <paramref name="lastActivitySec"/> 同一支時鐘。</param>
        public static float TextAlpha(bool typing, double lastActivitySec, double nowSec)
        {
            if (typing) return 1f;
            if (lastActivitySec <= 0.0) return 0f;   // 一句話都還沒有 → 預設「字不顯示」
            double idle = nowSec - lastActivitySec;
            return idle <= IdleHideSec ? 1f : 0f;   // 時間一到直接不見,不淡出
        }
    }
}
