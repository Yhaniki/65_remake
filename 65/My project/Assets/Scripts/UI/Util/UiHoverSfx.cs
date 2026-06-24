using UnityEngine;
using UnityEngine.EventSystems;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Plays the front-end menu hover SE (default <see cref="UiSfx.Menufloat"/>) when the pointer slides onto this
    /// UI element — the original SDO sound for moving over dropdown rows / menu items (模式 / 隊形 / 旁觀人數).
    /// Add to any raycast-target Graphic (it needs an EventSystem + GraphicRaycaster, which uGUI buttons already use).
    /// The pointer-enter caused by the element appearing under a stationary cursor (e.g. a dropdown opening) is
    /// skipped, so opening a list doesn't fire an instant hover on the same frame.
    /// </summary>
    public sealed class UiHoverSfx : MonoBehaviour, IPointerEnterHandler
    {
        public string sound = UiSfx.Menufloat;
        private int _spawnFrame;

        private void OnEnable() => _spawnFrame = Time.frameCount;

        public void OnPointerEnter(PointerEventData _)
        {
            if (Time.frameCount == _spawnFrame) return;   // the enter from the element appearing under the cursor, not a real hover
            UiSfx.Play(sound);
        }

        /// <summary>Attach a hover-SE to a UI component's GameObject. Pass <paramref name="sound"/> to override the clip.</summary>
        public static UiHoverSfx Attach(Component target, string sound = null)
        {
            if (target == null) return null;
            var h = target.gameObject.AddComponent<UiHoverSfx>();
            if (sound != null) h.sound = sound;
            return h;
        }
    }
}
