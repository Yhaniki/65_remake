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
    ///
    /// The same trick thickens the FACE: pass <c>facePx</c> to <see cref="Create"/> and a second ring is stacked
    /// in the face colour in front of the edge ring, growing the coloured core without thinning the edge —
    /// with <c>faceCopies</c> as the fine knob for how much of that extra weight actually shows.
    /// </summary>
    public sealed class OutlinedLabel : MonoBehaviour
    {
        private TextMeshProUGUI _face;
        /// <summary>Every offset copy behind the face: the edge-coloured ring, plus (when <c>facePx</c> &gt; 0) a
        /// face-coloured ring drawn in front of it that thickens the glyph core. Same text, same tracking, same
        /// overflow — only the colour and the offset differ, so they are kept in one array.</summary>
        private TextMeshProUGUI[] _copies;
        /// <summary>Per-copy offset in design px (direction × that ring's radius). Re-compensated for the
        /// horizontal stretch every time the resolution/aspect mode changes.</summary>
        private Vector2[] _offsets;
        private float _trackEm;              // 想收緊的字距(em);實際收多少每次 SetText 重算 → ApplyTracking
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
        /// <param name="trackEm">Letter-spacing tightening ASKED FOR, as a fraction of an em (0 = natural spacing,
        /// glyphs never distort). How much actually gets applied is re-derived from the string on every
        /// <see cref="SetText"/> so no two glyphs' ink touches — see <see cref="TextTracking"/>.</param>
        /// <param name="facePx">Thicken the FACE by this many px (0 = the glyph's natural weight), by stacking a
        /// second ring of copies IN the face colour in front of the edge ring. The edge ring is pushed out by the
        /// same amount, so the visible edge stays exactly <paramref name="edgePx"/> thick and only the coloured
        /// core grows. This is the knob for "the outline is thick enough but the dark letters inside are too thin":
        /// TMP's faux-bold is the only weight a runtime-built dynamic CJK atlas has, and against a 2px+ ring it is
        /// not enough — the official client's letters have a visibly heavier core than plain bold.</param>
        /// <param name="faceCopies">HOW MANY copies that core ring is made of (fractional: the outermost one gets a
        /// part-alpha), 16 = the full ring. This is the FINE knob for weight, and it is the count — not the radius,
        /// not a global alpha — because of how the stack composites: at a radius near 0 all the copies land on the
        /// same pixels, and each opaque one only darkens the glyph's ANTI-ALIASED EDGE (the core is already opaque),
        /// which saturates after 2–3 layers. So 16 copies is always "fully solid" — <paramref name="facePx"/> 0 vs
        /// 0.01 jumps a whole weight — while fading all 16 does nothing until they are nearly opaque. Fractional
        /// counts around 1 are the useful range: 1 = one solid copy (edge pixels ~half filled), 0.5 = half of one.</param>
        public static OutlinedLabel Create(Transform parent, string name, float x, float y, float w, float h,
            float size, Color32 face, Color32 edge, float edgePx, bool bold,
            TextAlignmentOptions align = TextAlignmentOptions.Center,
            float glyphScaleX = 1f, float glyphScaleY = 1f, float trackEm = 0f, float facePx = 0f,
            float faceCopies = 16f)
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
            ol._trackEm = trackEm;

            if (facePx < 0f) facePx = 0f;
            if (faceCopies < 0f) faceCopies = 0f;
            int core = facePx > 0f ? Mathf.CeilToInt(faceCopies) : 0;
            if (core == 0) facePx = 0f;                           // no core ring → don't push the edge out for one
            // The copies are spread evenly over their own circle (NOT the first N of Dirs16) so a 2- or 3-copy ring
            // still grows the glyph symmetrically. The LAST one carries the fractional part of faceCopies.
            var coreDirs = core > 0 ? NameplateMetrics.Ring(1f, core) : null;
            byte lastAlpha = (byte)Mathf.RoundToInt(face.a * Mathf.Clamp01(faceCopies - (core - 1)));
            ol._copies = new TextMeshProUGUI[Dirs16.Length + core];
            ol._offsets = new Vector2[ol._copies.Length];
            // Build order IS draw order (UGUI sibling order): edge ring → face-coloured core ring → face on top.
            for (int i = 0; i < Dirs16.Length; i++)
            {
                ol._copies[i] = Make(holder, "Edge" + i, size, edge, bold, align, glyphScale);
                ol._offsets[i] = Dirs16[i] * (edgePx + facePx);   // pushed out so the visible ring stays edgePx thick
            }
            for (int i = 0; i < core; i++)
            {
                var c = face;
                if (i == core - 1) c.a = lastAlpha;
                ol._copies[Dirs16.Length + i] = Make(holder, "Core" + i, size, c, bold, align, glyphScale);
                ol._offsets[Dirs16.Length + i] = coreDirs[i] * facePx;
            }
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
            var ring = NameplateMetrics.Ring(1f, Mathf.Max(1, dirs));
            ol._copies = new TextMeshProUGUI[ring.Length];
            ol._offsets = new Vector2[ring.Length];
            string edgeText = StripColorTags(rich);
            for (int i = 0; i < ring.Length; i++)             // edges first → behind the face (UGUI sibling order)
            {
                ol._copies[i] = MakeRich(holder, "Edge" + i, edgeText, size, edge, wrap, align);
                ol._offsets[i] = ring[i] * edgePx;
            }
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
            if (_copies == null) return;
            if (!force && Screen.width == _lastW && Screen.height == _lastH && AspectController.Mode == _lastMode) return;
            _lastW = Screen.width; _lastH = Screen.height; _lastMode = AspectController.Mode;

            float ax = NameplateMetrics.AnisotropyX(Screen.width, Screen.height, AspectController.ContentRect);
            for (int i = 0; i < _copies.Length; i++)
            {
                if (_copies[i] == null) continue;
                var rt = _copies[i].rectTransform;
                Vector2 o = NameplateMetrics.Compensate(_offsets[i], ax);
                rt.offsetMin = o;                                     // shift the whole stretched rect by the offset
                rt.offsetMax = o;
            }
        }

        /// <summary>Set the overflow mode on the face AND every edge copy. They must match: ellipsising only the
        /// face leaves the edge copies still drawing the glyphs the face has already cut — a ring with no face
        /// inside it, spilling past the label's box.</summary>
        public void SetOverflow(TextOverflowModes mode)
        {
            if (_face != null) _face.overflowMode = mode;
            if (_copies != null)
                for (int i = 0; i < _copies.Length; i++)
                    if (_copies[i] != null) _copies[i].overflowMode = mode;
        }

        /// <summary>Set the text on the face and every edge copy together.</summary>
        public void SetText(string s)
        {
            if (_face != null) _face.text = s;
            if (_copies != null)
                for (int i = 0; i < _copies.Length; i++)
                    if (_copies[i] != null) _copies[i].text = s;
            ApplyTracking();
        }

        /// <summary>Re-derive the letter-spacing for the CURRENT string and apply it to the face + every edge copy
        /// (they must match or the ring drifts off the glyphs). A flat tightening welds SimSun's half-width Latin
        /// together — "TA" only has 0.043em of air — so the amount is clamped per string (<see cref="TextTracking"/>).</summary>
        private void ApplyTracking()
        {
            if (_trackEm <= 0f || _face == null) return;
            float cs = -TextTracking.SafeTrackEm(_face.font, _face.text, _trackEm, TextStyles.MinInkGapEm) * 100f;
            _face.characterSpacing = cs;
            if (_copies != null)
                for (int i = 0; i < _copies.Length; i++)
                    if (_copies[i] != null) _copies[i].characterSpacing = cs;
        }
    }
}
