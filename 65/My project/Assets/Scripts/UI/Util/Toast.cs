using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 畫面上方置中的浮動訊息。整個 app 只建一個(在 modal 層)。
    ///
    /// 🔴 **什麼該彈、什麼只寫 log** —— 這條線劃過一次了,不要讓它慢慢長回來:
    ///
    ///   彈:玩家**看不出來**發生了什麼,而且他能據此做點什麼 ——
    ///       購買/刪除成功、設定已套用、連不上伺服器改用單機、傳歌失敗、
    ///       「再按一次開始可以強制開始」這種操作指引。
    ///
    ///   只寫 log:「按了但條件不符」的例行拒絕 —— 沒選歌、還有人沒準備、不是房主、
    ///       房間滿了、正在局裡不能旁觀…… 這些**畫面本身已經表達了**(歌名欄是空的、
    ///       頭上沒有準備標記、不是房主就沒有那顆按鈕、房間列表寫著人數與「遊戲中」)。
    ///       在那上面再蓋一條浮動訊息只是把畫面弄髒,而玩家早就知道了。
    ///
    /// 追原因看 log:那些行印的是同一句本地化文字(不是 error code),所以 log 一樣讀得懂。
    /// </summary>
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
