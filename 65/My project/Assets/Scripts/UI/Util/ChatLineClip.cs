using UnityEngine;
using UnityEngine.UI;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 讓 ScrollRect 以「整行」為單位裁切。
    ///
    /// RectMask2D 是逐像素裁切，不管行界：訊息欄視窗 104px、每行 16px、下 padding 3px，捲到底時視窗上緣
    /// 落在 104-3 = 6 行 + 5px 的地方 → 最上面那行只露出下半截字，而且因為永遠釘在底部，這截殘影會一直留著。
    /// 這裡在排版/捲動之後把「沒有整行落在視窗內」的行 alpha 歸零；用 CanvasGroup 而不是 SetActive，
    /// 才不會把它踢出 VerticalLayoutGroup 而改變內容高度、反過來又動到捲動位置。
    /// </summary>
    [RequireComponent(typeof(ScrollRect))]
    public sealed class ChatLineClip : MonoBehaviour
    {
        private ScrollRect _scroll;
        private float _lastY = float.NaN;
        private float _lastHeight = float.NaN;
        private int _lastCount = -1;

        /// <summary>行是否整行落在視窗內（座標同一空間、Y 軸向上）。</summary>
        /// <remarks>
        /// 比視窗還高的行（長訊息折行）永遠不可能整行進來，那就回 true 交還給 RectMask2D 的像素裁切，
        /// 否則整條訊息會直接消失。
        /// </remarks>
        public static bool IsLineVisible(float lineBottom, float lineTop,
                                         float viewBottom, float viewTop, float tolerance = 0.5f)
        {
            if (lineTop - lineBottom > viewTop - viewBottom + tolerance) return true;
            return lineBottom >= viewBottom - tolerance && lineTop <= viewTop + tolerance;
        }

        private void Awake() => _scroll = GetComponent<ScrollRect>();

        /// <summary>捲到底/重建清單之後直接重算一次（呼叫端已經 ForceUpdateCanvases，rect 是新的）。</summary>
        public void Refresh()
        {
            _lastCount = -1;
            Apply();
        }

        private void LateUpdate() => Apply();

        private void Apply()
        {
            if (_scroll == null) return;
            var vp = _scroll.viewport;
            var content = _scroll.content;
            if (vp == null || content == null) return;

            // 只有捲動位置 / 內容高度 / 行數變了才重算（訊息欄每幀都在，行數可能上百）。
            float y = content.anchoredPosition.y, h = content.rect.height;
            int n = content.childCount;
            if (n == _lastCount && Mathf.Approximately(y, _lastY) && Mathf.Approximately(h, _lastHeight)) return;
            _lastY = y; _lastHeight = h; _lastCount = n;

            float viewBottom = vp.rect.yMin, viewTop = vp.rect.yMax;
            for (int i = 0; i < n; i++)
            {
                var line = content.GetChild(i) as RectTransform;
                if (line == null) continue;
                var r = line.rect;
                float bottom = vp.InverseTransformPoint(line.TransformPoint(new Vector3(0f, r.yMin, 0f))).y;
                float top = vp.InverseTransformPoint(line.TransformPoint(new Vector3(0f, r.yMax, 0f))).y;
                // 不能用 ?? ：GetComponent 找不到時回的是假 null，會繞過 Unity 覆寫的 ==。
                if (!line.TryGetComponent<CanvasGroup>(out var cg)) cg = line.gameObject.AddComponent<CanvasGroup>();
                cg.alpha = IsLineVisible(bottom, top, viewBottom, viewTop) ? 1f : 0f;
            }
        }
    }
}
