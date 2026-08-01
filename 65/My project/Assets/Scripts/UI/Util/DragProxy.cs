using UnityEngine;
using UnityEngine.EventSystems;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 把一張圖變成「拖得動」的東西 —— 專門給自畫的捲軸握把用(那些握把是 <c>Image</c>,不是 Unity 的
    /// <c>Scrollbar</c>,沒有人會幫它們接拖曳)。
    ///
    /// 🔴 **一定要同時實作 <see cref="IBeginDragHandler"/>**,這是這個元件存在的唯一理由:
    ///    EventSystem 是在 PointerDown 的當下用 <c>ExecuteEvents.GetEventHandler&lt;IBeginDragHandler&gt;</c>
    ///    往上找、把找到的物件記成 <c>eventData.pointerDrag</c>,之後的 Drag 才會送給它。
    ///    只掛一個「有 IDragHandler」的東西(例如只註冊 Drag 的 <c>EventTrigger</c>)**永遠收不到拖曳事件** ——
    ///    症狀是「滾輪捲得動、滑鼠左鍵拉不動」,而且完全不會報錯。踩過一次,不要再用 EventTrigger 做這件事。
    ///
    /// 掛之前記得把該 Graphic 的 <c>raycastTarget</c> 打開,否則射線根本打不到它。
    /// </summary>
    public sealed class DragProxy : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        /// <summary>每一幀的垂直位移(Unity 慣例:往上為正)。</summary>
        public System.Action<float> Dragged;

        public void OnBeginDrag(PointerEventData e) { }

        public void OnDrag(PointerEventData e)
        {
            if (Dragged != null) Dragged(e.delta.y);
        }
    }
}
