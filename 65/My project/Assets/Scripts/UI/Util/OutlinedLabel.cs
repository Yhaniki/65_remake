using Sdo.Game;
using TMPro;
using UnityEngine;

namespace Sdo.UI.Util
{
    /// <summary>
    /// A TMP label with a SOLID coloured edge, drawn the bulletproof way: offset copies of the glyphs in the
    /// edge colour stacked BEHIND the face copy. Unlike TMP's SDF material outline
    /// (<c>outlineWidth</c>/<c>outlineColor</c>), this does NOT depend on the font being SDF or on the Distance-Field
    /// shader's outline feature — which is exactly why the room header uses it: the runtime-built dynamic CJK atlas
    /// would not show the SDF outline at any width. Call <see cref="SetText"/> to update face + all edge copies at once.
    ///
    /// The edge is a 16-direction ring (8 showed scalloped notches once fullscreen magnified the offsets to
    /// 2–3 physical px), and each offset's x is divided by the 4:3→screen stretch anisotropy so the ring
    /// thickness looks uniform under the non-uniform Stretch mode (re-applied on resolution/mode change).
    /// </summary>
    public sealed class OutlinedLabel : MonoBehaviour
    {
        private TextMeshProUGUI _face;
        private TextMeshProUGUI[] _edges;
        private float _edgePx;
        private int _lastW, _lastH;
        private AspectMode _lastMode;

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

        // 16 evenly-spaced unit directions → a closed ring of edge copies around the face glyphs
        private static readonly Vector2[] Dirs16 = NameplateMetrics.Ring(1f, 16);

        /// <summary>Build the label under <paramref name="parent"/> at top-left (<paramref name="x"/>,<paramref name="y"/>)
        /// of size (<paramref name="w"/>,<paramref name="h"/>): a <paramref name="face"/>-coloured face over a
        /// <paramref name="edge"/>-coloured edge <paramref name="edgePx"/> pixels thick.</summary>
        /// <param name="glyphScaleX">Horizontal squash of the glyphs (1 = none). &lt;1 makes the text narrower/taller-looking
        /// — e.g. 0.75 cancels the 4:3→16:9 stretch. Applied as the holder's localScale, so face + all edges squash together.</param>
        /// <param name="glyphScaleY">Vertical stretch of the glyphs (1 = none). &gt;1 makes the text taller.</param>
        public static OutlinedLabel Create(Transform parent, string name, float x, float y, float w, float h,
            float size, Color32 face, Color32 edge, float edgePx, bool bold,
            TextAlignmentOptions align = TextAlignmentOptions.Center,
            float glyphScaleX = 1f, float glyphScaleY = 1f)
        {
            var holder = UIKit.NewRect(parent, name);
            holder.anchorMin = holder.anchorMax = new Vector2(0f, 1f);
            holder.pivot = new Vector2(0f, 1f);
            holder.anchoredPosition = new Vector2(x, -y);
            holder.sizeDelta = new Vector2(w, h);

            // Scale the GLYPHS (each face/edge child), NOT the holder: the children stretch-fill the holder with a
            // centred pivot (0.5,0.5), so localScale squashes around the box CENTRE → a centred label stays centred.
            // Scaling the holder instead would pivot on its top-left corner and shove the text sideways.
            var glyphScale = new Vector3(glyphScaleX, glyphScaleY, 1f);

            var ol = holder.gameObject.AddComponent<OutlinedLabel>();
            ol._edgePx = edgePx;
            ol._edges = new TextMeshProUGUI[Dirs16.Length];
            for (int i = 0; i < Dirs16.Length; i++)           // edges first → they sit BEHIND the face (UGUI sibling order)
                ol._edges[i] = Make(holder, "Edge" + i, size, edge, bold, align, glyphScale);
            ol._face = Make(holder, "Face", size, face, bold, align, glyphScale);
            ol.ApplyEdgeOffsets(true);
            return ol;
        }

        private static TextMeshProUGUI Make(Transform parent, string name, float size, Color32 color, bool bold, TextAlignmentOptions align, Vector3 glyphScale)
        {
            var t = UIKit.AddText(parent, name, "", size, color, align);
            if (bold) t.fontStyle = FontStyles.Bold;
            var rt = t.rectTransform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;   // stretch to the holder; edges get shifted by ApplyEdgeOffsets
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = glyphScale;                               // squash glyphs around their centre (pivot 0.5,0.5)
            return t;
        }

        private void LateUpdate() => ApplyEdgeOffsets(false);         // labels live across fullscreen toggles

        /// <summary>(Re)apply the edge-copy offsets, compressing x by the current stretch anisotropy so the
        /// ring stays visually uniform. Runs once per resolution/mode change (cheap int check per frame).</summary>
        private void ApplyEdgeOffsets(bool force)
        {
            if (_edges == null) return;
            if (!force && Screen.width == _lastW && Screen.height == _lastH && AspectController.Mode == _lastMode) return;
            _lastW = Screen.width; _lastH = Screen.height; _lastMode = AspectController.Mode;

            float ax = NameplateMetrics.AnisotropyX(Screen.width, Screen.height, AspectController.ContentRect);
            for (int i = 0; i < _edges.Length; i++)
            {
                if (_edges[i] == null) continue;
                var rt = _edges[i].rectTransform;
                Vector2 o = NameplateMetrics.Compensate(Dirs16[i] * _edgePx, ax);
                rt.offsetMin = o;                                     // shift the whole stretched rect by the offset
                rt.offsetMax = o;
            }
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
