using TMPro;
using UnityEngine;

namespace Sdo.UI.Util
{
    /// <summary>
    /// A TMP label with a SOLID coloured edge, drawn the bulletproof way: eight offset copies of the glyphs in the
    /// edge colour stacked BEHIND the face copy. Unlike TMP's SDF material outline
    /// (<c>outlineWidth</c>/<c>outlineColor</c>), this does NOT depend on the font being SDF or on the Distance-Field
    /// shader's outline feature — which is exactly why the room header uses it: the runtime-built dynamic CJK atlas
    /// would not show the SDF outline at any width. Call <see cref="SetText"/> to update face + all edge copies at once.
    /// </summary>
    public sealed class OutlinedLabel : MonoBehaviour
    {
        private TextMeshProUGUI _face;
        private TextMeshProUGUI[] _edges;

        /// <summary>The front-face text (read-only access for callers that need the TMP itself).</summary>
        public TextMeshProUGUI Face => _face;

        /// <summary>The holder RectTransform (this component lives on it) — for callers that re-position the label.</summary>
        public RectTransform Rect => (RectTransform)transform;

        /// <summary>Rendered width (canvas px) of the current face text — measure it to pack labels left-to-right.</summary>
        public float PreferredWidth => _face != null ? _face.GetPreferredValues(_face.text).x : 0f;

        /// <summary>Move the label's LEFT edge to canvas-x (keeps its y). Use with <see cref="PreferredWidth"/> to flow.</summary>
        public void SetX(float x)
        {
            var rt = Rect;
            rt.anchoredPosition = new Vector2(x, rt.anchoredPosition.y);
        }

        // eight compass directions → a smooth ring of edge copies around the face glyphs
        private static readonly Vector2[] Dirs8 =
        {
            new Vector2( 1f,  0f), new Vector2(-1f,  0f), new Vector2( 0f,  1f), new Vector2( 0f, -1f),
            new Vector2( 1f,  1f), new Vector2( 1f, -1f), new Vector2(-1f,  1f), new Vector2(-1f, -1f),
        };

        /// <summary>Build the label under <paramref name="parent"/> at top-left (<paramref name="x"/>,<paramref name="y"/>)
        /// of size (<paramref name="w"/>,<paramref name="h"/>): a <paramref name="face"/>-coloured face over a
        /// <paramref name="edge"/>-coloured edge <paramref name="edgePx"/> pixels thick.</summary>
        public static OutlinedLabel Create(Transform parent, string name, float x, float y, float w, float h,
            float size, Color32 face, Color32 edge, float edgePx, bool bold,
            TextAlignmentOptions align = TextAlignmentOptions.Center)
        {
            var holder = UIKit.NewRect(parent, name);
            holder.anchorMin = holder.anchorMax = new Vector2(0f, 1f);
            holder.pivot = new Vector2(0f, 1f);
            holder.anchoredPosition = new Vector2(x, -y);
            holder.sizeDelta = new Vector2(w, h);

            var ol = holder.gameObject.AddComponent<OutlinedLabel>();
            ol._edges = new TextMeshProUGUI[Dirs8.Length];
            for (int i = 0; i < Dirs8.Length; i++)           // edges first → they sit BEHIND the face (UGUI sibling order)
                ol._edges[i] = Make(holder, "Edge" + i, size, edge, bold, Dirs8[i] * edgePx, align);
            ol._face = Make(holder, "Face", size, face, bold, Vector2.zero, align);
            return ol;
        }

        private static TextMeshProUGUI Make(Transform parent, string name, float size, Color32 color, bool bold, Vector2 offset, TextAlignmentOptions align)
        {
            var t = UIKit.AddText(parent, name, "", size, color, align);
            if (bold) t.fontStyle = FontStyles.Bold;
            var rt = t.rectTransform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;   // stretch to the holder, then shift by the offset
            rt.offsetMin = offset;
            rt.offsetMax = offset;
            return t;
        }

        /// <summary>Set the text on the face and every edge copy together.</summary>
        public void SetText(string s)
        {
            if (_face != null) _face.text = s;
            if (_edges != null)
                for (int i = 0; i < _edges.Length; i++)
                    if (_edges[i] != null) _edges[i].text = s;
        }
    }
}
