using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 把 uGUI 的指標點擊（含右鍵/中鍵 —— 這些是 UGUI Button 會忽略的）轉發給一個委派。掛在 Button 旁邊就能
    /// 加上「右鍵」處理而不干擾原本的左鍵 onClick（ExecuteEvents 會把事件送到同物件上所有 IPointerClickHandler）。
    /// 選歌清單用它做「右鍵歌曲 → 加入/移出收藏」的彈出選單。
    /// </summary>
    public sealed class PointerClickProxy : MonoBehaviour, IPointerClickHandler
    {
        /// <summary>收到點擊時呼叫；用 <c>eventData.button</c> 判左/右鍵，<c>eventData.position</c> 是螢幕座標。</summary>
        public Action<PointerEventData> Clicked;

        public void OnPointerClick(PointerEventData eventData) => Clicked?.Invoke(eventData);
    }
}
