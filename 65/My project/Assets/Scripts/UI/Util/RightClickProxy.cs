using UnityEngine;
using UnityEngine.EventSystems;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 把一個 UI 物件變成「右鍵按得動」。
    ///
    /// 🔴 <c>Button.onClick</c> **只在左鍵觸發**(UGUI 的 Button.OnPointerClick 第一行就把非左鍵擋掉),
    ///    所以右鍵選單一定要另外接。掛在同一個 GameObject 上不會打架:EventSystem 會把 PointerClick
    ///    送給該物件上的**每一個** IPointerClickHandler,Button 自己過濾左鍵、這個只收右鍵。
    ///
    /// 掛之前記得該物件(或它的子物件)有吃得到射線的 Graphic,否則點不到。
    /// </summary>
    public sealed class RightClickProxy : MonoBehaviour, IPointerClickHandler
    {
        public System.Action Clicked;

        /// <summary>false 時整個忽略(給「空位不能右鍵」那種狀態用,不必拆元件)。</summary>
        public bool Enabled = true;

        public void OnPointerClick(PointerEventData e)
        {
            if (!Enabled || e.button != PointerEventData.InputButton.Right) return;
            if (Clicked != null) Clicked();
        }
    }
}
