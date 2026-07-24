using System.Text.RegularExpressions;
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
        private Vector2[] _dirs = Dirs16;    // edge-copy directions (Create=16-ring; CreateRich picks its own count)

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
            float glyphScaleX = 1f, float glyphScaleY = 1f, float charSpacing = 0f)
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
            ol._dirs = Dirs16;
            ol._edges = new TextMeshProUGUI[Dirs16.Length];
            for (int i = 0; i < Dirs16.Length; i++)           // edges first → they sit BEHIND the face (UGUI sibling order)
                ol._edges[i] = Make(holder, "Edge" + i, size, edge, bold, align, glyphScale, charSpacing);
            ol._face = Make(holder, "Face", size, face, bold, align, glyphScale, charSpacing);
            ol.ApplyEdgeOffsets(true);
            return ol;
        }

        private static TextMeshProUGUI Make(Transform parent, string name, float size, Color32 color, bool bold, TextAlignmentOptions align, Vector3 glyphScale, float charSpacing)
        {
            var t = UIKit.AddText(parent, name, "", size, color, align);
            if (bold) t.fontStyle = FontStyles.Bold;
            t.characterSpacing = charSpacing;   // TMP letter-spacing: adds charSpacing×fontSize/100 px per gap (negative = tighter)
            var rt = t.rectTransform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;   // stretch to the holder; edges get shifted by ApplyEdgeOffsets
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = glyphScale;                               // squash glyphs around their centre (pivot 0.5,0.5)
            return t;
        }

        // <color=..>/</color> and the <#rrggbb[aa]> shorthand — the ONLY colour markup our chat lines emit. Stripping
        // it (and nothing else: <noparse>, <link> stay) lets the edge copies render the exact same glyphs in a single
        // solid colour, so a green/cyan/white rich line still gets a truly BLACK ring instead of tinted offset ghosts.
        private static readonly Regex ColorTagRx =
            new Regex(@"</?color[^>]*>|<#[0-9a-fA-F]{3,8}>", RegexOptions.IgnoreCase);

        /// <summary>Remove TMP colour markup from <paramref name="s"/>, leaving every other tag intact. Pure — unit-tested.</summary>
        public static string StripColorTags(string s)
            => string.IsNullOrEmpty(s) ? (s ?? "") : ColorTagRx.Replace(s, "");

        /// <summary>Outline a RICH-TEXT line (colour tags, <c>&lt;link&gt;</c>, <c>&lt;noparse&gt;</c> preserved): the face keeps
        /// the markup, the <paramref name="dirs"/> edge copies get the same string with colour tags stripped so they draw a
        /// solid <paramref name="edge"/>-coloured ring. Used for the room's bottom-left chat log, whose transparent backing
        /// lets the small coloured text blend into the busy 3D room. The holder carries no LayoutElement — the caller sets
        /// its layout (VLG preferredHeight / HLG preferredWidth) exactly as it did for the bare TMP it replaces. Returns the
        /// component; <see cref="Face"/> is the front TMP (measure it, or wire name-link clicks onto it).</summary>
        public static OutlinedLabel CreateRich(Transform parent, string name, string rich, float size,
            Color32 edge, float edgePx, int dirs, bool wrap = true,
            TextAlignmentOptions align = TextAlignmentOptions.TopLeft)
        {
            var holder = UIKit.NewRect(parent, name);
            var ol = holder.gameObject.AddComponent<OutlinedLabel>();
            ol._edgePx = edgePx;
            ol._dirs = NameplateMetrics.Ring(1f, Mathf.Max(1, dirs));
            ol._edges = new TextMeshProUGUI[ol._dirs.Length];
            string edgeText = StripColorTags(rich);
            for (int i = 0; i < ol._dirs.Length; i++)         // edges first → behind the face (UGUI sibling order)
                ol._edges[i] = MakeRich(holder, "Edge" + i, edgeText, size, edge, wrap, align);
            ol._face = MakeRich(holder, "Face", rich, size, Color.white, wrap, align);   // <color> tags override white where present
            ol.ApplyEdgeOffsets(true);
            return ol;
        }

        private static TextMeshProUGUI MakeRich(Transform parent, string name, string text, float size, Color32 color, bool wrap, TextAlignmentOptions align)
        {
            var t = UIKit.AddText(parent, name, text, size, color, align, wrap);
            t.richText = true;
            var rt = t.rectTransform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;   // stretch to the holder; edges get shifted by ApplyEdgeOffsets
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
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
                Vector2 o = NameplateMetrics.Compensate(_dirs[i] * _edgePx, ax);
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
