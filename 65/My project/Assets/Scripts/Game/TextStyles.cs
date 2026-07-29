using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Runtime text plumbing for the ranking UI (head nameplate + right-side roster list), built on
    /// legacy <see cref="TextMesh"/> — NOT TextMeshPro. TMP's 3D text would not render in this project's
    /// single-camera ortho HUD (it draws through a manual <c>camera.Render()</c> with no UGUI canvas, and
    /// TMP's SDF mesh never reached the renderer there), whereas legacy TextMesh renders reliably (it is
    /// what the bottom song-info labels already use, and it shows in the offscreen capture too).
    ///
    /// CJK names work because we feed TextMesh a DYNAMIC OS font (微軟正黑體 …): dynamic fonts rasterise
    /// any glyph on demand. Outline / drop-shadow are faked the classic way — extra offset copies drawn
    /// behind the face in the edge colour (TextMesh has no native outline).
    /// </summary>
    public static class TextStyles
    {
        public const string DepthTextShaderName = "Sdo/DepthText";

        public enum Style { HeadName, ListLocal, ListOther, Looker }

        /// <summary>Letter-spacing tightening for the head-top nameplate — 房間+遊戲共用, drives the gameplay
        /// <see cref="Label3D"/> (per-char TextMesh layout) AND the room <c>OutlinedLabel</c> (TMP characterSpacing)
        /// so the two match. Value = fraction of an em REMOVED from every inter-character gap — TRUE tracking, glyphs
        /// keep full width (NOT a squash/condense). 0 = natural spacing; raise toward ~0.15 for tighter.</summary>
        public const float HeadNameTrackEm = 0.1f;

        /// <summary>Same true-tracking as <see cref="HeadNameTrackEm"/> but for the right-side ranking roster
        /// (name + score rows, <see cref="Style.ListLocal"/>/<see cref="Style.ListOther"/>). Independently tunable.
        /// 0 = natural spacing.</summary>
        public const float RosterTrackEm = 0.1f;

        /// <summary>True-tracking (em-fraction removed per inter-char gap) for the song-select list-row song names
        /// (TMP characterSpacing). 0 = natural spacing. This is the amount ASKED FOR — the song list clamps it per
        /// string so glyphs never touch (see <c>Sdo.UI.Util.TextTracking</c>).</summary>
        public const float SongTitleTrackEm = 0.05f;

        /// <summary>Ink gap (em) that must survive any tightening — the shared floor for EVERY tracked label here
        /// (song list 0.05, head nameplate + ranking roster 0.1). SimSun's Latin is HALF-WIDTH MONOSPACED (every
        /// letter advances 0.5em) with almost no sidebearing, so "TA" is born with only 0.043em of air and a flat
        /// 0.05em tightening welds the two letters together (SOLID ST[A]TE SQUAD). Full-width CJK has ≥0.08em, which
        /// is why only capital A/T/V/W combos showed it. Keep a little slack for the faux-bold dilate on top.</summary>
        public const float MinInkGapEm = 0.03f;

        /// <summary>Same, but for the gameplay HUD bottom song title (<see cref="TrackedTextMesh"/>) — kept separate
        /// because that label reads tighter at the same value, so it wants a gentler amount.</summary>
        public const float GameSongTitleTrackEm = 0.0f;

        // Colours eyedropped from the official screenshots (20151030 / 20151208):
        public static readonly Color FaceCream  = new Color32(250, 252, 214, 255);       // 頭頂名(粗體) 房間+遊戲共用 = RGB(250,252,214)
        public static readonly Color FaceYellow = new Color(1.000f, 1.000f, 0.580f, 1f); // 清單-本機       = RGB(255,255,148)
        public static readonly Color FaceOrange = new Color(1.000f, 0.682f, 0.063f, 1f); // 清單-其他       = RGB(255,174,16)
        private static readonly Color EdgeRed   = new Color(0.643f, 0.110f, 0.000f, 1f); // 清單陰影         = RGB(164,28,0)
        private static readonly Color EdgeBlack = new Color(0f, 0f, 0f, 1f);             // 頭頂黑邊 / 旁觀陰影
        public static readonly Color FaceLookerBlue = new Color(0.612f, 0.792f, 1.000f, 1f); // 旁觀玩家       = RGB(156,202,255)

        private static Font _cjk;
        private static readonly Dictionary<Font, Material> DepthTextMaterials =
            new Dictionary<Font, Material>();

        private static Material _depthSpriteMaterial;
        /// <summary>The bundled CJK font under Assets/Resources. SINGLE SOURCE shared with the front-end UI
        /// (<c>UIFont.BundledFontResource</c>) so the room (TMP) and the in-game HUD (legacy TextMesh) render the
        /// SAME typeface — the head-name font must match on both sides.</summary>
        public const string BundledFontResource = "Fonts/SourceHanSansTC-Regular";

        /// <summary>Lazily resolve the shared CJK font (cached). PRIMARY = OS SimSun (宋体) — the face the official
        /// exe hardcodes for all text (FontD3DWin.cpp, GDI CreateFontA "SimSun"); loaded from the player's Windows at
        /// runtime exactly like the official client (simsun.ttc is not redistributable, so never bundled). Legacy
        /// TextMesh rasterises OS dynamic fonts on demand, so no tofu probe is needed here (unlike TMP/UIFont).
        /// FALLBACK = the bundled SourceHanSans OTF (imported Dynamic), then any OS CJK face.</summary>
        public static Font CjkFont()
        {
            if (_cjk != null) return _cjk;
            if (OsHasSimSun())
                _cjk = Font.CreateDynamicFontFromOSFont(new[] { "SimSun", "NSimSun" }, 40);
            if (_cjk == null)
                _cjk = Resources.Load<Font>(BundledFontResource);   // 跟房間同一個後備 SourceHanSans → 兩邊字型一致
            if (_cjk == null)
                _cjk = Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft JhengHei", "微軟正黑體", "Microsoft YaHei", "SimHei", "Arial Unicode MS", "PMingLiU" }, 40);
            if (_cjk == null) _cjk = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Debug.Log("[TextStyles] font: " + (_cjk != null ? _cjk.name : "NULL"));
            return _cjk;
        }

        /// <summary>CreateDynamicFontFromOSFont silently substitutes a default face when the family is missing,
        /// so gate on the OS-installed list instead of trusting the returned Font.</summary>
        private static bool OsHasSimSun()
        {
            try
            {
                foreach (var n in Font.GetOSInstalledFontNames())
                    if (n == "SimSun" || n == "NSimSun" || n == "宋体") return true;
            }
            catch { }
            return false;
        }

        /// <summary>Create a styled label (face + outline/shadow copies). Caller positions via <c>Position</c>.
        /// <paramref name="trackEmOverride"/> forces a specific letter-spacing (null = pick by style): the ranking
        /// roster passes 0 for the SCORE column so the numbers show at natural spacing (數字不縮), while the NAME
        /// column keeps the style's <see cref="RosterTrackEm"/> tightening (名字縮緊).</summary>
        public static Label3D NewLabel(string name, Style style, int order, float pxSize, TextAnchor anchor,
                                       int layer = 0, float? trackEmOverride = null, bool depthTested = false)
        {
            Color face, edge; Vector2[] offsets; bool bold;
            // Letter-spacing tightening (字靠緊一點): head nameplate + ranking roster; spectator (Looker) stays natural.
            float trackEm = trackEmOverride ?? (style == Style.HeadName ? HeadNameTrackEm
                          : (style == Style.ListLocal || style == Style.ListOther) ? RosterTrackEm
                          : 0f);
            switch (style)
            {
                case Style.ListLocal:  face = FaceYellow;     edge = EdgeRed;   offsets = new[] { new Vector2(1.4f, -1.4f) }; bold = false; break;
                case Style.ListOther:  face = FaceOrange;     edge = EdgeRed;   offsets = new[] { new Vector2(1.4f, -1.4f) }; bold = false; break;
                case Style.Looker:     face = FaceLookerBlue; edge = EdgeBlack; offsets = new[] { new Vector2(1f, -1f) };     bold = false; break;
                // 16-direction ring: 8 left scalloped notches in the black edge once fullscreen magnified the offsets.
                default: /* HeadName */ face = FaceCream;     edge = EdgeBlack; offsets = NameplateMetrics.Ring(1.4f, 16); bold = true; break;
            }
            return new Label3D(name, CjkFont(), face, edge, offsets, order, pxSize, anchor, layer, -3f,
                               bold, trackEm, depthTested);
        }

        internal static Material DepthTextMaterial(Font font)
        {
            if (font == null) return null;
            if (!DepthTextMaterials.TryGetValue(font, out var material) || material == null)
            {
                var shader = Shader.Find(DepthTextShaderName);
                if (shader == null) return font.material;
                material = new Material(shader);
                material.SetFloat("_UseTextureRgb", 0f);   // dynamic font atlases carry coverage in alpha only
                material.hideFlags = HideFlags.HideAndDontSave;
                DepthTextMaterials[font] = material;
            }
            SyncDepthTextAtlas(font, material);
            return material;
        }
        internal static Material DepthSpriteMaterial()
        {
            if (_depthSpriteMaterial == null)
            {
                var shader = Shader.Find(DepthTextShaderName);
                if (shader == null) return null;
                _depthSpriteMaterial = new Material(shader);
                _depthSpriteMaterial.SetFloat("_UseTextureRgb", 1f);
                _depthSpriteMaterial.hideFlags = HideFlags.HideAndDontSave;
            }
            return _depthSpriteMaterial;
        }


        internal static void SyncDepthTextAtlas(Font font, Material material)
        {
            if (font == null || material == null || font.material == null) return;
            material.mainTexture = font.material.mainTexture;
        }

        public static (Color face, Color edge) Colors(Style style)
        {
            switch (style)
            {
                case Style.ListOther: return (FaceOrange, EdgeRed);   // roster other
                default: return (FaceYellow, EdgeRed);                // roster local
            }
        }
    }

    /// <summary>
    /// A world-space text label = one face <see cref="TextMesh"/> plus N offset copies behind it for an
    /// outline (16-direction ring) or drop-shadow (1 offset). All children of a root transform; move the
    /// label by setting <see cref="Position"/>. Sizes are in design px (the HUD ortho cam maps 1px = 1 world
    /// unit); the glyph bitmap is rasterized at the PHYSICAL px size and the offsets stretch-compensated so
    /// the text stays crisp and the ring uniform at fullscreen (see <see cref="NameplateMetrics"/>).
    /// </summary>
    public sealed class Label3D
    {
        public readonly GameObject root;
        private readonly Font _font;
        private readonly Vector2[] _offsets;    // design-px outline/shadow offsets (x is stretch-compensated on apply)
        private readonly int _order;
        private int _layer;
        private readonly TextAnchor _anchor;
        private readonly FontStyle _fontStyle;
        private readonly float _trackEm;        // em-fraction pulled OUT of each inter-char gap; 0 = legacy single-mesh path
        private Material _materialOverride;
        private Color _faceCol, _edgeCol;
        private float _pxSize;
        private int _lastFontPx = -1;
        private float _lastPxSize = -1f, _lastAx = -1f;
        private string _text = "";

        // legacy path (_trackEm == 0): one face TextMesh + N outline copies, each rendering the whole string.
        private readonly TextMesh _main;
        private readonly TextMesh[] _back;

        // per-char path (_trackEm != 0): one MiddleLeft cell (face + outline copies) per character, laid out by hand so
        // the inter-char advance can be tightened — legacy TextMesh has NO letter-spacing, and this keeps each glyph at
        // its normal fontSize/characterSize (so glyphs are NEVER distorted — only their x positions change).
        private TextMesh[] _cellFace;
        private TextMesh[][] _cellEdge;

        public Label3D(string name, Font font, Color face, Color edge, Vector2[] offsets,
                       int order, float pxSize, TextAnchor anchor, int layer, float z, bool bold = false,
                       float trackEm = 0f, bool depthTested = false)
        {
            root = new GameObject(name);
            if (layer != 0) root.layer = layer;
            root.transform.position = new Vector3(0f, 0f, z);
            _font = font; _faceCol = face; _edgeCol = edge;
            _fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            _anchor = anchor; _order = order; _layer = layer;
            _offsets = offsets ?? System.Array.Empty<Vector2>();
            _trackEm = trackEm;
            _materialOverride = depthTested ? TextStyles.DepthTextMaterial(font) : null;
            _pxSize = pxSize;
            if (trackEm == 0f)
            {
                _back = new TextMesh[_offsets.Length];
                for (int i = 0; i < _back.Length; i++)
                    _back[i] = MakeTm(root.transform, "edge" + i, font, edge, order - 1, anchor, layer,
                                      _fontStyle, _materialOverride);
                _main = MakeTm(root.transform, "main", font, face, order, anchor, layer,
                               _fontStyle, _materialOverride);
                Refresh(true);
            }
            // per-char path defers to the first Text set (needs the string to build one cell per character).
        }

        private static TextMesh MakeTm(Transform parent, string n, Font font, Color col,
                                       int order, TextAnchor anchor, int layer, FontStyle fontStyle,
                                       Material materialOverride)
        {
            var go = new GameObject(n);
            if (layer != 0) go.layer = layer;
            go.transform.SetParent(parent, false);
            var tm = go.AddComponent<TextMesh>();
            tm.font = font;
            tm.fontStyle = fontStyle;           // fontSize/characterSize/offset come from Refresh()
            tm.anchor = anchor;
            tm.alignment = TextAlignment.Center;
            tm.color = col;
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = materialOverride != null
                ? materialOverride
                : font.material;                 // HUD path keeps the dynamic font's GUI/Text material
            mr.sortingOrder = order;
            return tm;
        }

        /// <summary>Re-rasterize at the CURRENT physical on-screen glyph size and re-space the outline copies
        /// for the current 4:3→screen stretch. fontSize == physical px ⇒ the dynamic-font bitmap draws ≈1:1
        /// (crisp); a fixed fontSize gets bilinear-resampled (font atlas has no mips) and the edges go ragged
        /// at fullscreen. No-op when nothing changed — HeadMarker sets PxSize every frame, so a resolution
        /// change mid-game re-applies live.</summary>
        private void Refresh(bool force)
        {
            if (_materialOverride != null) TextStyles.SyncDepthTextAtlas(_font, _materialOverride);
            float sy = NameplateMetrics.ScaleY(Screen.height, AspectController.ContentRect);
            float ax = NameplateMetrics.AnisotropyX(Screen.width, Screen.height, AspectController.ContentRect);
            int fontPx = NameplateMetrics.FontPxFor(_pxSize, sy);
            if (!force && fontPx == _lastFontPx && Mathf.Approximately(_pxSize, _lastPxSize) && Mathf.Approximately(ax, _lastAx))
                return;
            _lastFontPx = fontPx; _lastPxSize = _pxSize; _lastAx = ax;

            float c = NameplateMetrics.CharacterSizeFor(_pxSize, fontPx);
            if (_trackEm == 0f)
            {
                _main.fontSize = fontPx; _main.characterSize = c;
                for (int i = 0; i < _back.Length; i++)
                {
                    _back[i].fontSize = fontPx; _back[i].characterSize = c;
                    var o = NameplateMetrics.Compensate(_offsets[i], ax);
                    _back[i].transform.localPosition = new Vector3(o.x, o.y, 0f);
                }
            }
            else if (_cellFace != null)
            {
                ReflowCells(fontPx, c, ax);
            }
        }

        /// <summary>Lay out the per-char cells left-to-right, tightening each inter-char gap by <see cref="_trackEm"/>
        /// of an em. World units per raster pixel = characterSize×0.1 (Unity's fixed TextMesh scale), so a glyph's
        /// world advance = its pixel advance × that — placing cell k+1 at cell k's advance reproduces the native
        /// string exactly at trackEm 0, then a constant reduction pulls the characters closer. The reduction is
        /// clamped per string so no two glyphs' ink touches — SimSun's Latin is half-width monospaced with almost no
        /// sidebearing ("TA" has only 0.043em of air), see <see cref="TextTracking"/>.</summary>
        private void ReflowCells(int fontPx, float c, float ax)
        {
            string s = _text;
            int n = s.Length;
            if (n == 0) return;
            _font.RequestCharactersInTexture(s, fontPx, _fontStyle);   // ensure advances are available at this raster size
            float worldPerPx = 0.1f * c;
            float trackEm = TextTracking.SafeTrackEm(_font, s, fontPx, _fontStyle, _trackEm, TextStyles.MinInkGapEm);
            float reduce = trackEm * fontPx * worldPerPx;              // constant world gap removed between every pair

            var adv = new float[n];
            float total = 0f;
            for (int k = 0; k < n; k++)
            {
                float a = _font.GetCharacterInfo(s[k], out CharacterInfo info, fontPx, _fontStyle) ? info.advance : fontPx;
                adv[k] = a * worldPerPx;
                total += adv[k];
            }
            if (n > 1) total -= reduce * (n - 1);

            float cursor = HorizStart(_anchor, total);
            for (int k = 0; k < n; k++)
            {
                float x = cursor;
                var f = _cellFace[k]; f.fontSize = fontPx; f.characterSize = c;
                f.transform.localPosition = new Vector3(x, 0f, 0f);
                var es = _cellEdge[k];
                for (int i = 0; i < es.Length; i++)
                {
                    es[i].fontSize = fontPx; es[i].characterSize = c;
                    var o = NameplateMetrics.Compensate(_offsets[i], ax);
                    es[i].transform.localPosition = new Vector3(x + o.x, o.y, 0f);
                }
                cursor += adv[k] - reduce;
            }
        }

        // MiddleLeft cells own their x, so map the label's horizontal anchor to the run's left-edge start.
        private static float HorizStart(TextAnchor a, float total)
        {
            switch (a)
            {
                case TextAnchor.UpperLeft:  case TextAnchor.MiddleLeft:  case TextAnchor.LowerLeft:  return 0f;
                case TextAnchor.UpperRight: case TextAnchor.MiddleRight: case TextAnchor.LowerRight: return -total;
                default: return -total * 0.5f;   // centred
            }
        }

        private void BuildCells(string s)
        {
            DestroyCells();
            int n = s.Length;
            _cellFace = new TextMesh[n];
            _cellEdge = new TextMesh[n][];
            for (int k = 0; k < n; k++)
            {
                string ch = s[k].ToString();
                var es = new TextMesh[_offsets.Length];
                for (int i = 0; i < es.Length; i++)
                {
                    es[i] = MakeTm(root.transform, "c" + k + "e" + i, _font, _edgeCol, _order - 1,
                                   TextAnchor.MiddleLeft, _layer, _fontStyle, _materialOverride);
                    es[i].text = ch;
                }
                var f = MakeTm(root.transform, "c" + k, _font, _faceCol, _order, TextAnchor.MiddleLeft,
                               _layer, _fontStyle, _materialOverride);
                f.text = ch;
                _cellEdge[k] = es; _cellFace[k] = f;
            }
        }

        private void DestroyCells()
        {
            if (_cellFace != null)
                foreach (var f in _cellFace) if (f != null) UnityEngine.Object.Destroy(f.gameObject);
            if (_cellEdge != null)
                foreach (var es in _cellEdge) if (es != null) foreach (var e in es) if (e != null) UnityEngine.Object.Destroy(e.gameObject);
            _cellFace = null; _cellEdge = null;
        }

        public string Text
        {
            set
            {
                string v = value ?? "";
                bool built = _trackEm == 0f ? _main != null : _cellFace != null;
                if (built && v == _text) return;   // unchanged → skip (the live roster score sets this every frame)
                _text = v;
                if (_trackEm == 0f)
                {
                    _main.text = v;
                    for (int i = 0; i < _back.Length; i++) _back[i].text = v;
                }
                else
                {
                    // Reuse cells when the length is unchanged (score digits usually keep their count) — only rebuild
                    // GameObjects when the character count changes, so a per-frame score update just re-letters + reflows.
                    if (_cellFace == null || _cellFace.Length != v.Length) BuildCells(v);
                    else for (int k = 0; k < v.Length; k++) SetCellChar(k, v[k]);
                    _lastFontPx = -1;   // force a reflow of the (re)built cells
                    Refresh(true);
                }
            }
        }

        private void SetCellChar(int k, char ch)
        {
            string s = ch.ToString();
            if (_cellFace[k] != null) _cellFace[k].text = s;
            var es = _cellEdge[k];
            if (es != null) for (int i = 0; i < es.Length; i++) if (es[i] != null) es[i].text = s;
        }

        public float PxSize { set { _pxSize = value; Refresh(false); } }
        public Vector3 Position { set { root.transform.position = value; } }

        public void SetColors(Color face, Color edge)
        {
            _faceCol = face; _edgeCol = edge;
            if (_trackEm == 0f)
            {
                if (_main != null) _main.color = face;
                if (_back != null) foreach (var b in _back) if (b != null) b.color = edge;
            }
            else
            {
                if (_cellFace != null) foreach (var f in _cellFace) if (f != null) f.color = face;
                if (_cellEdge != null) foreach (var es in _cellEdge) if (es != null) foreach (var e in es) if (e != null) e.color = edge;
            }
        }

        /// <summary>
        /// Move an already-built HUD label into a scene camera's world layer and replace GUI/Text's
        /// depth-always material. New per-character cells built after this call inherit the same layer/material.
        /// </summary>
        public void EnableDepthTestedWorld(int layer)
        {
            _layer = layer;
            root.layer = layer;
            _materialOverride = TextStyles.DepthTextMaterial(_font);
            foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderer.gameObject.layer = layer;
                renderer.sharedMaterial = _materialOverride;
            }
        }

        public void SetActive(bool on) { if (root != null) root.SetActive(on); }
    }

    /// <summary>
    /// A plain (no-outline) legacy <see cref="TextMesh"/> label laid out ONE glyph at a time so its letter-spacing can
    /// be tightened — legacy TextMesh has no character-spacing. Each cell keeps the SAME fontSize/characterSize as the
    /// single-mesh label it replaces, so glyphs are never distorted; only their x positions change. World units per
    /// raster pixel = characterSize×0.1 (Unity's fixed TextMesh scale), so placing cell k+1 at cell k's advance
    /// reproduces the native string at trackEm 0, then a constant reduction pulls the characters closer. Used for the
    /// gameplay HUD bottom song title.
    ///
    /// 光柵尺寸跟著螢幕走(同 <see cref="Label3D.Refresh"/>)：舊版寫死 fontSize 64 再用 characterSize 縮到 ~13px 顯示，
    /// 等於把字圖縮小 5 倍取樣，而動態字型 atlas 沒有 mipmap → 邊緣糊出鬼影(「歌名有殘影」)。改成依 PHYSICAL 螢幕
    /// px 光柵、characterSize 反向補回，字圖就近 1:1 畫出來，顯示大小完全不變。<see cref="Tick"/> 每幀檢查一次，
    /// 解析度/視窗大小一改就重新光柵化。
    /// </summary>
    public sealed class TrackedTextMesh
    {
        public readonly GameObject root;
        private readonly Font _font;
        private readonly float _designPx;      // 想在螢幕上站多高(設計 px) — 光柵尺寸由它 × 螢幕縮放算出
        private readonly int _order;
        private readonly TextAnchor _anchor;
        private readonly float _trackEm;
        private int _fontPx;                   // 目前的光柵尺寸(實體 px)
        private float _charSize;               // 對應的 characterSize，維持顯示高度 = _designPx
        private Color _color;
        private TextMesh[] _cells;
        private string _text = "";

        public TrackedTextMesh(string name, Font font, float designPx, Color color, int order, TextAnchor anchor, float trackEm)
        {
            root = new GameObject(name);
            _font = font; _designPx = designPx; _color = color; _order = order; _anchor = anchor; _trackEm = trackEm;
            PickRaster();
        }

        public Vector3 Position { set { root.transform.position = value; } }
        public void SetActive(bool on) { if (root != null) root.SetActive(on); }
        public Color Color { set { _color = value; if (_cells != null) foreach (var c in _cells) if (c != null) c.color = value; } }

        /// <summary>目前螢幕下該用的光柵尺寸；有變才回 true。</summary>
        private bool PickRaster()
        {
            float sy = NameplateMetrics.ScaleY(Screen.height, AspectController.ContentRect);
            int fp = NameplateMetrics.FontPxFor(_designPx, sy);
            if (fp == _fontPx) return false;
            _fontPx = fp;
            _charSize = NameplateMetrics.CharacterSizeFor(_designPx, fp, NameplateMetrics.PxToCharSizeLegacyAt64);
            return true;
        }

        /// <summary>每幀呼叫：視窗大小/全螢幕切換後重新以實體 px 光柵化(沒變就是 no-op)。</summary>
        public void Tick()
        {
            if (!PickRaster() || _cells == null) return;
            foreach (var c in _cells) if (c != null) { c.fontSize = _fontPx; c.characterSize = _charSize; }
            Reflow();
        }

        public string Text
        {
            set
            {
                string v = value ?? "";
                if (_cells != null && v == _text) return;
                if (_cells == null || _cells.Length != v.Length) Build(v);
                else for (int k = 0; k < v.Length; k++) _cells[k].text = v[k].ToString();
                _text = v;
                Reflow();
            }
        }

        private void Build(string s)
        {
            if (_cells != null) foreach (var c in _cells) if (c != null) UnityEngine.Object.Destroy(c.gameObject);
            _cells = new TextMesh[s.Length];
            for (int k = 0; k < s.Length; k++)
            {
                var go = new GameObject("c" + k);
                go.transform.SetParent(root.transform, false);
                var tm = go.AddComponent<TextMesh>();
                tm.font = _font; tm.fontSize = _fontPx; tm.characterSize = _charSize;
                tm.anchor = TextAnchor.MiddleLeft; tm.alignment = TextAlignment.Left; tm.color = _color;
                tm.text = s[k].ToString();
                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterial = _font.material; mr.sortingOrder = _order;
                _cells[k] = tm;
            }
        }

        private void Reflow()
        {
            int n = _cells.Length;
            if (n == 0) return;
            _font.RequestCharactersInTexture(_text, _fontPx, FontStyle.Normal);
            float worldPerPx = 0.1f * _charSize;
            // 收緊量逐字串 clamp，字母不會黏在一起（SimSun 半形西文的 "TA" 天生只有 0.043em）→ 見 TextTracking。
            float trackEm = TextTracking.SafeTrackEm(_font, _text, _fontPx, FontStyle.Normal, _trackEm, TextStyles.MinInkGapEm);
            float reduce = trackEm * _fontPx * worldPerPx;
            var adv = new float[n];
            float total = 0f;
            for (int k = 0; k < n; k++)
            {
                float a = _font.GetCharacterInfo(_text[k], out CharacterInfo info, _fontPx, FontStyle.Normal) ? info.advance : _fontPx;
                adv[k] = a * worldPerPx; total += adv[k];
            }
            if (n > 1) total -= reduce * (n - 1);
            float cursor =
                (_anchor == TextAnchor.MiddleLeft  || _anchor == TextAnchor.UpperLeft  || _anchor == TextAnchor.LowerLeft)  ? 0f :
                (_anchor == TextAnchor.MiddleRight || _anchor == TextAnchor.UpperRight || _anchor == TextAnchor.LowerRight) ? -total :
                -total * 0.5f;
            for (int k = 0; k < n; k++)
            {
                _cells[k].transform.localPosition = new Vector3(cursor, 0f, 0f);
                cursor += adv[k] - reduce;
            }
        }
    }
}
