using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Sdo.UI.Util
{
    /// <summary>Center-top transient message (errors, info). Built once into the modal layer.</summary>
    public sealed class Toast : MonoBehaviour
    {
        private static Toast _inst;
        private TextMeshProUGUI _text;
        private CanvasGroup _cg;
        private float _hideAt;

        public static void Init(RectTransform parent)
        {
            if (_inst != null) return;
            var rt = UIKit.NewRect(parent, "Toast");
            UIKit.Anchor(rt, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f));
            rt.sizeDelta = new Vector2(620f, 54f);
            rt.anchoredPosition = new Vector2(0f, -72f);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0.82f);
            img.raycastTarget = false;
            var t = UIKit.AddText(rt, "Text", "", 18, UITheme.Text, TextAlignmentOptions.Center);
            UIKit.Stretch(t, 14, 0, 14, 0);
            var cg = rt.gameObject.AddComponent<CanvasGroup>();
            cg.alpha = 0f; cg.blocksRaycasts = false; cg.interactable = false;
            _inst = rt.gameObject.AddComponent<Toast>();
            _inst._text = t;
            _inst._cg = cg;
        }

        public static void Show(string msg, float seconds = 2.5f)
        {
            if (_inst == null) { Debug.Log($"[Toast] {msg}"); return; }
            _inst._text.text = msg;
            _inst._cg.alpha = 1f;
            _inst._hideAt = Time.unscaledTime + seconds;
        }

        private void Update()
        {
            if (_cg.alpha > 0f && Time.unscaledTime >= _hideAt)
                _cg.alpha = Mathf.MoveTowards(_cg.alpha, 0f, Time.unscaledDeltaTime * 2.5f);
        }
    }
}
