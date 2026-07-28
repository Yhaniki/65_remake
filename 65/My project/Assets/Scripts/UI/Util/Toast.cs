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

        /// <summary>
        /// Toast 已經建好了嗎(<see cref="Init"/> 跑過了)。
        ///
        /// 🔴 **開機途中要說的話必須先問這個。** <see cref="Show"/> 在還沒 Init 時只會寫一行 log,
        /// 訊息就這樣消失了。實際踩過:「連不上伺服器,改用單機模式」那句是在開機 Phase 3 設的,
        /// 而 Toast 到 Phase 5 才建 —— Update 在下一幀就把它讀走並丟掉,**玩家永遠看不到那句話**,
        /// 只會覺得「怎麼變單機了」。
        /// </summary>
        public static bool Ready => _inst != null;

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
