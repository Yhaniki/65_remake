using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Loads original SDO art straight from the extracted game tree at runtime
    /// (H:\...\sdox_offline\Extracted). Understands the ".an" container format:
    /// plain text, one line = one animation frame = a .png filename relative to the
    /// .an's own folder, with an optional trailing " (x,y,w,h)" sub-rectangle crop.
    /// See doc/GAMEPLAY_SCREEN_ANATOMY.md.
    ///
    /// Windows NTFS is case-insensitive, so the UPPERCASE-on-disk names resolve from
    /// the mixed-case names used in DdrGamePlay.xml without extra work.
    /// </summary>
    public static class SdoExtracted
    {
        /// <summary>Absolute root of the extracted SDO assets (override before first use if needed).</summary>
        public static string Root = @"H:\65_remake\assets\sdox_offline\Extracted";

        /// <summary>UI/GAMEPLAY folder holding the gameplay HUD .an files.</summary>
        public static string GameplayUiDir => Path.Combine(Root, "UI", "GAMEPLAY");

        /// <summary>EFFECT/EFT_&lt;skin&gt; folder holding judgment words + combo digits + bursts.</summary>
        public static string EftDir(int skin = 2) => Path.Combine(Root, "EFFECT", "EFT_" + skin);

        /// <summary>UI/STATIS folder: result-screen (結算) panel art, digits, rank badges, win/lose banner.</summary>
        public static string StatisDir => Path.Combine(Root, "UI", "STATIS");

        /// <summary>UI/STATIS/STATISTIC: the ONLINE result screen art — DDRSTATISTIC.XML layout. Background tiles
        /// (Statis0..11), the BALANCE.png sheet (win/lose banner + OK/save buttons), sliding rank rows, head frames,
        /// and the bottom G幣/EXP digit strips (score_num / score_numS / Num8 / Num3).</summary>
        public static string ResultStatisDir => Path.Combine(StatisDir, "STATISTIC");

        /// <summary>Shipped sound-effects folder (sdox_offline/SE), sibling of Extracted.</summary>
        public static string SeDir => Path.Combine(Path.GetDirectoryName(Root) ?? Root, "SE");

        // ---- .an parsing (pure; testable without Unity) ----

        public struct AnFrame
        {
            public string Image;       // png filename (relative to the .an's folder)
            public bool HasCrop;
            public int X, Y, W, H;     // sub-rectangle (top-left origin) when HasCrop
        }

        /// <summary>Parse .an text into frames. One non-empty line per frame.</summary>
        public static List<AnFrame> ParseAnText(string text)
        {
            var frames = new List<AnFrame>();
            if (string.IsNullOrEmpty(text)) return frames;
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var fr = new AnFrame();
                int paren = line.IndexOf('(');
                if (paren >= 0 && line.EndsWith(")"))
                {
                    fr.Image = line.Substring(0, paren).Trim();
                    var inner = line.Substring(paren + 1, line.Length - paren - 2);
                    var nums = inner.Split(',');
                    if (nums.Length == 4 &&
                        int.TryParse(nums[0].Trim(), out fr.X) && int.TryParse(nums[1].Trim(), out fr.Y) &&
                        int.TryParse(nums[2].Trim(), out fr.W) && int.TryParse(nums[3].Trim(), out fr.H))
                        fr.HasCrop = true;
                }
                else fr.Image = line;
                if (fr.Image.Length > 0) frames.Add(fr);
            }
            return frames;
        }

        // ---- sprite loading (Unity) ----

        private static readonly Dictionary<string, Texture2D> _texCache = new Dictionary<string, Texture2D>();

        private static Texture2D LoadTexture(string path)
        {
            if (_texCache.TryGetValue(path, out var t) && t != null) return t;
            if (!File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(File.ReadAllBytes(path));   // PNG/BMP
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            _texCache[path] = tex;
            return tex;
        }

        private static Sprite SpriteFromFrame(string folder, AnFrame fr)
        {
            // .an frames reference a png in the same folder; fall back to the named image directly.
            var tex = LoadTexture(Path.Combine(folder, fr.Image));
            if (tex == null) return null;
            Rect rect = fr.HasCrop
                ? new Rect(fr.X, tex.height - fr.Y - fr.H, fr.W, fr.H)  // flip Y (top-left -> bottom-left)
                : new Rect(0, 0, tex.width, tex.height);
            return Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
        }

        /// <summary>Load every frame of an .an file (in <paramref name="folder"/>) as sprites.</summary>
        public static Sprite[] LoadAn(string folder, string anName)
        {
            var anPath = Path.Combine(folder, anName.EndsWith(".an") ? anName : anName + ".an");
            if (!File.Exists(anPath)) return new Sprite[0];
            var frames = ParseAnText(File.ReadAllText(anPath));
            var sprites = new List<Sprite>(frames.Count);
            foreach (var fr in frames)
            {
                var s = SpriteFromFrame(folder, fr);
                if (s != null) sprites.Add(s);
            }
            return sprites.ToArray();
        }

        /// <summary>First frame of an .an (most HUD .an files are single-frame).</summary>
        public static Sprite LoadAn1(string folder, string anName)
        {
            var all = LoadAn(folder, anName);
            return all.Length > 0 ? all[0] : null;
        }

        /// <summary>Load a bare image file (png/bmp) under a folder as one sprite.</summary>
        public static Sprite LoadImage(string folder, string imageName)
        {
            var tex = LoadTexture(Path.Combine(folder, imageName));
            return tex == null ? null
                : Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
        }

        /// <summary>
        /// Load an image treating near-black pixels as TRANSPARENT — the SDO engine's color-key for
        /// no-alpha (RGB) sprites like MyHpBack.png (a black-centred frame). Returns a copy.
        /// </summary>
        public static Sprite LoadImageBlackKeyed(string folder, string imageName, int thresh = 20)
        {
            var src = LoadTexture(Path.Combine(folder, imageName));
            if (src == null) return null;
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = src.GetPixels32();
            for (int i = 0; i < px.Length; i++)
                if (px[i].r < thresh && px[i].g < thresh && px[i].b < thresh) px[i].a = 0;
            tex.SetPixels32(px); tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                 new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
        }

        /// <summary>Load a bare image as a raw Texture2D (RGBA), so the caller can keep the ORIGINAL pixels
        /// (e.g. to re-derive alpha-scaled sprites live without re-reading the file). Returns null if missing.</summary>
        public static Texture2D LoadTextureRaw(string folder, string imageName)
            => LoadTexture(Path.Combine(folder, imageName));

        /// <summary>
        /// Build a sprite from <paramref name="src"/> with every per-pixel alpha multiplied by <paramref name="gain"/>
        /// (clamped to 255). gain=1 reproduces the source EXACTLY (its native alpha curve, all detail intact);
        /// gain&gt;1 pushes the translucent body toward opaque while PRESERVING the relative alpha gradient (the
        /// shading/bevel detail) until it clips, and fully-transparent pixels stay transparent (0×gain=0) so the
        /// chamfer/cut corners survive. No opaque backing rect needed. Each call allocates a new Texture2D — the
        /// caller owns it (Destroy the previous one when regenerating).
        /// </summary>
        public static Sprite AlphaScaledSprite(Texture2D src, float gain)
        {
            if (src == null) return null;
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = src.GetPixels32();
            if (gain != 1f)
                for (int i = 0; i < px.Length; i++)
                    px[i].a = (byte)Mathf.Clamp(Mathf.RoundToInt(px[i].a * gain), 0, 255);
            tex.SetPixels32(px); tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                 new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
        }

        /// <summary>
        /// Bleed opaque RGB into transparent pixels (alpha kept) so bilinear filtering can't pull the texture's
        /// transparent-WHITE matte into the visible edges — kills the white halo/seam on sprites whose PNG stores
        /// (255,255,255,0) in the empty area (e.g. the hold body/tail caps). A few dilation passes in place.
        /// </summary>
        public static void AlphaBleed(Texture2D tex, int passes = 4)
        {
            if (tex == null) return;
            int w = tex.width, h = tex.height;
            var px = tex.GetPixels32();
            for (int p = 0; p < passes; p++)
            {
                var src = (Color32[])px.Clone();
                bool changed = false;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (src[i].a > 8) continue;                 // only fill (near-)transparent pixels
                        int r = 0, g = 0, b = 0, n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                var c = src[ny * w + nx];
                                if (c.a > 8) { r += c.r; g += c.g; b += c.b; n++; }
                            }
                        if (n > 0) { px[i].r = (byte)(r / n); px[i].g = (byte)(g / n); px[i].b = (byte)(b / n); changed = true; }
                    }
                if (!changed) break;
            }
            tex.SetPixels32(px); tex.Apply();
        }

        /// <summary>
        /// Flatten the bright near-white highlight pixels of a (small) sprite to its average opaque colour, removing
        /// the glossy rim that reads as a "white seam" — used on the hold tail cap. Opaque pixels whose luminance
        /// exceeds <paramref name="threshMul"/>× the average opaque luminance are set to the average colour.
        /// </summary>
        public static void DampenBrightRim(Texture2D tex, float threshMul = 1.5f)
        {
            if (tex == null) return;
            var px = tex.GetPixels32();
            long r = 0, g = 0, b = 0; int n = 0;
            foreach (var c in px) if (c.a > 40) { r += c.r; g += c.g; b += c.b; n++; }
            if (n == 0) return;
            byte ar = (byte)(r / n), ag = (byte)(g / n), ab = (byte)(b / n);
            float avgLum = 0.299f * ar + 0.587f * ag + 0.114f * ab;
            float thresh = avgLum * threshMul;
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i].a <= 40) continue;
                float lum = 0.299f * px[i].r + 0.587f * px[i].g + 0.114f * px[i].b;
                if (lum > thresh) { px[i].r = ar; px[i].g = ag; px[i].b = ab; }   // kill the bright rim highlight
            }
            tex.SetPixels32(px); tex.Apply();
        }

        /// <summary>
        /// Return a sprite over a FRESH, UNIQUE copy of <paramref name="src"/>'s texture (de-fringed + de-rimmed).
        /// Each lane's cap gets its OWN texture this way, so it can never be confused with another lane's cap
        /// (no shared cache texture), and the white fringe/rim is removed. Original colours are preserved.
        /// </summary>
        public static Sprite CleanCapCopy(Sprite src)
        {
            if (src == null) return null;
            var t0 = src.texture;
            var tex = new Texture2D(t0.width, t0.height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            tex.SetPixels32(t0.GetPixels32()); tex.Apply();
            AlphaBleed(tex);        // kill transparent-white fringe
            DampenBrightRim(tex);   // kill the bright rim "white seam"
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
        }

        // convenience for the gameplay HUD
        public static Sprite Hud(string anName) => LoadAn1(GameplayUiDir, anName);
        public static Sprite Eft(string imageName, int skin = 2) => LoadImage(EftDir(skin), imageName);
    }
}
