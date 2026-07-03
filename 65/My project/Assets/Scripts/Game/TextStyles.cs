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
        public enum Style { HeadName, ListLocal, ListOther, Looker }

        // Colours eyedropped from the official screenshots (20151030 / 20151208):
        public static readonly Color FaceCream  = new Color32(250, 252, 214, 255);       // 頭頂名(粗體) 房間+遊戲共用 = RGB(250,252,214)
        public static readonly Color FaceYellow = new Color(1.000f, 1.000f, 0.580f, 1f); // 清單-本機       = RGB(255,255,148)
        public static readonly Color FaceOrange = new Color(1.000f, 0.682f, 0.063f, 1f); // 清單-其他       = RGB(255,174,16)
        private static readonly Color EdgeRed   = new Color(0.643f, 0.110f, 0.000f, 1f); // 清單陰影         = RGB(164,28,0)
        private static readonly Color EdgeBlack = new Color(0f, 0f, 0f, 1f);             // 頭頂黑邊 / 旁觀陰影
        public static readonly Color FaceLookerBlue = new Color(0.612f, 0.792f, 1.000f, 1f); // 旁觀玩家       = RGB(156,202,255)

        private static Font _cjk;

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

        private static Vector2[] Outline8(float d) => new[]
        {
            new Vector2(d, 0), new Vector2(-d, 0), new Vector2(0, d), new Vector2(0, -d),
            new Vector2(d, d), new Vector2(d, -d), new Vector2(-d, d), new Vector2(-d, -d),
        };

        /// <summary>Create a styled label (face + outline/shadow copies). Caller positions via <c>Position</c>.</summary>
        public static Label3D NewLabel(string name, Style style, int order, float pxSize, TextAnchor anchor, int layer = 0)
        {
            Color face, edge; Vector2[] offsets; bool bold;
            switch (style)
            {
                case Style.ListLocal:  face = FaceYellow;     edge = EdgeRed;   offsets = new[] { new Vector2(1.4f, -1.4f) }; bold = false; break;
                case Style.ListOther:  face = FaceOrange;     edge = EdgeRed;   offsets = new[] { new Vector2(1.4f, -1.4f) }; bold = false; break;
                case Style.Looker:     face = FaceLookerBlue; edge = EdgeBlack; offsets = new[] { new Vector2(1f, -1f) };     bold = false; break;
                default: /* HeadName */ face = FaceCream;     edge = EdgeBlack; offsets = Outline8(1.4f); bold = true; break;
            }
            return new Label3D(name, CjkFont(), face, edge, offsets, order, pxSize, anchor, layer, -3f, bold);
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
    /// outline (8-way) or drop-shadow (1 offset). All children of a root transform; move the label by
    /// setting <see cref="Position"/>. Sizes are in design px (the HUD ortho cam maps 1px = 1 world unit).
    /// </summary>
    public sealed class Label3D
    {
        public readonly GameObject root;
        private readonly TextMesh _main;
        private readonly TextMesh[] _back;

        public Label3D(string name, Font font, Color face, Color edge, Vector2[] offsets,
                       int order, float pxSize, TextAnchor anchor, int layer, float z, bool bold = false)
        {
            root = new GameObject(name);
            if (layer != 0) root.layer = layer;
            root.transform.position = new Vector3(0f, 0f, z);
            var fs = bold ? FontStyle.Bold : FontStyle.Normal;
            _back = new TextMesh[offsets != null ? offsets.Length : 0];
            for (int i = 0; i < _back.Length; i++)
                _back[i] = MakeTm(root.transform, "edge" + i, font, edge, offsets[i], order - 1, pxSize, anchor, layer, fs);
            _main = MakeTm(root.transform, "main", font, face, Vector2.zero, order, pxSize, anchor, layer, fs);
        }

        private static TextMesh MakeTm(Transform parent, string n, Font font, Color col, Vector2 off,
                                       int order, float pxSize, TextAnchor anchor, int layer, FontStyle fontStyle)
        {
            var go = new GameObject(n);
            if (layer != 0) go.layer = layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(off.x, off.y, 0f);
            var tm = go.AddComponent<TextMesh>();
            tm.font = font;
            tm.fontSize = 64;
            tm.fontStyle = fontStyle;
            tm.characterSize = pxSize * 0.11f;   // see PxToCharSize
            tm.anchor = anchor;
            tm.alignment = TextAlignment.Center;
            tm.color = col;
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = font.material;   // dynamic font's texture material (GUI/Text shader)
            mr.sortingOrder = order;
            return tm;
        }

        // design px → TextMesh characterSize. Calibrated against the offscreen capture: at fontSize 64 the
        // dynamic CJK font renders ≈9 px per unit of characterSize, so px × 0.11 ≈ px tall on screen.
        private const float PxToCharSize = 0.11f;

        public string Text { set { _main.text = value; for (int i = 0; i < _back.Length; i++) _back[i].text = value; } }
        public float PxSize { set { float c = value * PxToCharSize; _main.characterSize = c; for (int i = 0; i < _back.Length; i++) _back[i].characterSize = c; } }
        public Vector3 Position { set { root.transform.position = value; } }
        public void SetColors(Color face, Color edge) { _main.color = face; for (int i = 0; i < _back.Length; i++) _back[i].color = edge; }
        public void SetActive(bool on) { if (root != null) root.SetActive(on); }
    }
}
