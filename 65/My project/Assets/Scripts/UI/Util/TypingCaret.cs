using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 自畫的閃爍游標,掛在 <see cref="TMP_InputField"/> 上。
    ///
    /// 🔴 **為什麼不用 TMP 內建的 caret**:這個專案的字型是執行期建的 CJK 動態圖集,而前端畫布是
    ///    world-space + 自己的 UI 相機 —— 這兩件事湊在一起時 TMP 算不出可見的 caret 寬高,結果就是
    ///    「打得出字但看不到游標」。房間/大廳的聊天輸入早就踩過這個坑(見 <c>LobbyScreen.UpdateChatCaret</c>
    ///    與 <c>RoomScreen</c> 的 _chatCaret),做法是自己畫一條 Image、每幀擺到文字尾端。
    ///    這裡把那段邏輯抽成可以掛的元件,個人資料視窗那四個可填欄位直接沿用同一套手感。
    ///    (聊天那兩處**沒有**改成用這個 —— 那是已經校好、每天在用的畫面,不動它。)
    ///
    /// 游標位置吃的是「已上屏的字(到 <c>stringPosition</c> 為止)+ IME 組字串」的寬度,所以往回移、
    /// 中間刪、注音/拼音組字到一半都跟得上。順便把 <c>Input.compositionCursorPos</c> 餵回系統,
    /// 輸入法的候選視窗才會跟著游標跑,而不是黏在畫面左下角。
    /// </summary>
    public sealed class TypingCaret : MonoBehaviour
    {
        private TMP_InputField _field;
        private Image _caret;
        private float _x0;
        private readonly Vector3[] _corners = new Vector3[4];

        /// <summary>閃爍節拍:亮 0.55 秒、暗 0.45 秒(與房間聊天同拍)。</summary>
        private const float OnFrac = 0.55f;

        /// <param name="x0">游標離文字區左緣的起點(= 第一個字的左邊)。</param>
        public static TypingCaret Attach(TMP_InputField field, Color color, float w = 2f, float h = 15f, float x0 = 0f)
        {
            if (field == null || field.textViewport == null) return null;
            var c = field.gameObject.AddComponent<TypingCaret>();
            c._field = field;
            c._x0 = x0;
            c._caret = UIKit.AddImage(field.textViewport, "TypingCaret", color, raycast: false);
            var rt = c._caret.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x0, 0f);
            c._caret.gameObject.SetActive(false);
            return c;
        }

        private void LateUpdate()
        {
            if (_caret == null || _field == null) return;
            if (!_field.isFocused)
            {
                if (_caret.gameObject.activeSelf) _caret.gameObject.SetActive(false);
                return;
            }

            string committed = _field.text ?? "";
            int caretPos = Mathf.Clamp(_field.stringPosition, 0, committed.Length);
            string upTo = committed.Substring(0, caretPos) + (Input.compositionString ?? "");
            float w = (_field.textComponent != null && upTo.Length > 0)
                ? _field.textComponent.GetPreferredValues(upTo).x : 0f;
            _caret.rectTransform.anchoredPosition = new Vector2(_x0 + w, 0f);
            if (!_caret.gameObject.activeSelf) _caret.gameObject.SetActive(true);

            bool on = Mathf.Repeat(Time.unscaledTime, 1f) < OnFrac;
            var col = _caret.color; col.a = on ? 1f : 0f; _caret.color = col;

            // 自製輸入框要自己告訴系統「文字游標在螢幕哪裡」,注音/拼音的候選視窗才會跟著游標跑。
            var canvas = _caret.rectTransform.GetComponentInParent<Canvas>();
            Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;
            _caret.rectTransform.GetWorldCorners(_corners);
            Input.compositionCursorPos = RectTransformUtility.WorldToScreenPoint(cam, _corners[0]);
        }
    }
}
