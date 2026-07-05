using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Loads original SDO art straight from the game data tree at runtime (see <see cref="Root"/> for how the
    /// tree is located: DATA beside the built exe, or the repo-relative extracted tree in the editor).
    /// Understands the ".an" container format:
    /// plain text, one line = one animation frame = a .png filename relative to the
    /// .an's own folder, with an optional trailing " (x,y,w,h)" sub-rectangle crop.
    /// See doc/GAMEPLAY_SCREEN_ANATOMY.md.
    ///
    /// Windows NTFS is case-insensitive, so the UPPERCASE-on-disk names resolve from
    /// the mixed-case names used in DdrGamePlay.xml without extra work.
    /// </summary>
    public static class SdoExtracted
    {
        // ---- root resolution (NO hardcoded absolute path) ----
        // Two on-disk layouts are supported transparently:
        //   • Built player:  <exeDir>/DATA   (all game data lives under one DATA folder beside the exe).
        //                    Application.dataPath == <exeDir>/<exe>_Data, so the exe dir is its parent.
        //   • Editor / dev:  <repo>/assets/sdox_offline/Extracted  (derived repo-relative from Application.dataPath,
        //                    which is <repo>/65/My project/Assets — three levels up = repo root).
        private static string _root;

        /// <summary>Root of the SDO game data tree. Resolves lazily on first use; settable for tests/overrides.</summary>
        public static string Root
        {
            get { return _root ?? (_root = ResolveRoot()); }
            set { _root = value; }
        }

        private static string ResolveRoot()
        {
            // 1) Built player: a DATA folder beside the exe.
            try
            {
                var exeDir = Directory.GetParent(Application.dataPath)?.FullName;
                if (exeDir != null)
                {
                    var data = Path.Combine(exeDir, "DATA");
                    if (Directory.Exists(data)) return data;
                }
            }
            catch { /* Application.dataPath unavailable in some contexts — fall through */ }

            // 2) Editor / dev: repo-relative extracted tree.
            try
            {
                var repo = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
                var ex = Path.Combine(repo, "assets", "sdox_offline", "Extracted");
                if (Directory.Exists(ex)) return ex;
            }
            catch { /* ignore */ }

            // 3) Last resort: assume the built layout even if not present yet.
            try { return Path.Combine(Directory.GetParent(Application.dataPath)?.FullName ?? ".", "DATA"); }
            catch { return "DATA"; }
        }

        /// <summary>Directory of the (built) exe — parent of Application.dataPath; "." if unavailable.</summary>
        private static string ExeDir
        {
            get { try { return Directory.GetParent(Application.dataPath)?.FullName ?? "."; } catch { return "."; } }
        }

        /// <summary>First of <paramref name="candidates"/> that exists on disk, else the first candidate.</summary>
        private static string FirstDir(params string[] candidates)
        {
            foreach (var c in candidates) { try { if (Directory.Exists(c)) return c; } catch { } }
            return candidates.Length > 0 ? candidates[0] : Root;
        }

        /// <summary>UI/GAMEPLAY folder holding the gameplay HUD .an files.</summary>
        public static string GameplayUiDir => Path.Combine(Root, "UI", "GAMEPLAY");

        /// <summary>EFFECT/EFT_&lt;skin&gt; folder holding judgment words + combo digits + bursts.</summary>
        public static string EftDir(int skin = 2) => Path.Combine(Root, "EFFECT", "EFT_" + skin);

        /// <summary>EFFECT/EFT_&lt;suffix&gt; where suffix can be non-numeric (e.g. "PET"); for the note-effect skins.</summary>
        public static string EftDir2(string suffix) => Path.Combine(Root, "EFFECT", "EFT_" + suffix);

        /// <summary>UI/STATIS folder: result-screen (結算) panel art, digits, rank badges, win/lose banner.</summary>
        public static string StatisDir => Path.Combine(Root, "UI", "STATIS");

        /// <summary>UI/STATIS/STATISTIC: the ONLINE result screen art — DDRSTATISTIC.XML layout. Background tiles
        /// (Statis0..11), the BALANCE.png sheet (win/lose banner + OK/save buttons), sliding rank rows, head frames,
        /// and the bottom G幣/EXP digit strips (score_num / score_numS / Num8 / Num3).</summary>
        public static string ResultStatisDir => Path.Combine(StatisDir, "STATISTIC");

        /// <summary>Sound-effects folder. Built: DATA/SE; dev: sdox_offline/SE (sibling of Extracted).</summary>
        public static string SeDir => FirstDir(Path.Combine(Root, "SE"), Path.Combine(Path.GetDirectoryName(Root) ?? Root, "SE"));

        /// <summary>Song chart/audio folder. Built: DATA/MUSIC (uppercase); dev: sdox_offline/music (sibling).</summary>
        public static string MusicDir => FirstDir(Path.Combine(Root, "MUSIC"), Path.Combine(Path.GetDirectoryName(Root) ?? Root, "music"));

        /// <summary>Background-music folder. Built: DATA/BGM; dev: sdox_offline/BGM (sibling).</summary>
        public static string BgmDir => FirstDir(Path.Combine(Root, "BGM"), Path.Combine(Path.GetDirectoryName(Root) ?? Root, "BGM"));

        /// <summary>Replay save folder (under DATA). Created on demand by callers.</summary>
        public static string ReplayDir => Path.Combine(Root, "REPLAY");

        /// <summary>Screenshot output folder, kept beside the exe (NOT under DATA). Editor: repo-root/screensave.</summary>
        public static string ScreensaveDir => Path.Combine(ExeDir, "screensave");

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
        private static readonly HashSet<Texture2D> _bled = new HashSet<Texture2D>();

        private static Texture2D LoadTexture(string path)
        {
            if (_texCache.TryGetValue(path, out var t) && t != null) return t;
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            // Unity's Texture2D.LoadImage decodes ONLY PNG/JPG. Several EFT hit-burst skins (EFT_2/5/8/9/10/PET) ship
            // their Eft_Hit*.bmp as raw 24-bit BMP, so decode those by hand; everything else goes through LoadImage.
            Texture2D tex;
            if (path.EndsWith(".bmp", System.StringComparison.OrdinalIgnoreCase))
            {
                tex = DecodeBmp(bytes);
                if (tex == null) return null;
            }
            else
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.LoadImage(bytes);   // PNG/JPG
            }
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            _texCache[path] = tex;
            return tex;
        }

        /// <summary>Minimal uncompressed-BMP decoder (24/32-bit BI_RGB) → upright RGBA32, matching the PNG path's
        /// orientation. SDO's Eft_Hit*.bmp are all 24-bit BI_RGB; returns null for anything else (RLE/paletted/etc).</summary>
        private static Texture2D DecodeBmp(byte[] d)
        {
            if (d == null || d.Length < 54 || d[0] != 'B' || d[1] != 'M') return null;
            int dataOff = System.BitConverter.ToInt32(d, 10);
            int hdr = System.BitConverter.ToInt32(d, 14);
            int w = System.BitConverter.ToInt32(d, 18);
            int h = System.BitConverter.ToInt32(d, 22);
            int bpp = System.BitConverter.ToUInt16(d, 28);
            int comp = System.BitConverter.ToInt32(d, 30);
            if (hdr < 40 || comp != 0 || (bpp != 24 && bpp != 32) || w <= 0 || h == 0) return null;
            bool topDown = h < 0; int H = Mathf.Abs(h);
            int bpe = bpp / 8;                                   // bytes per pixel
            int stride = ((w * bpe + 3) / 4) * 4;               // rows padded to 4 bytes
            if (dataOff + stride * H > d.Length) return null;
            var px = new Color32[w * H];
            for (int y = 0; y < H; y++)                          // y = Unity row (0 = bottom); BMP is bottom-up by default
            {
                int srcRow = dataOff + (topDown ? (H - 1 - y) : y) * stride;
                int dstRow = y * w;
                for (int x = 0; x < w; x++)
                {
                    int s = srcRow + x * bpe;                    // BMP stores BGR(A)
                    byte a = bpe == 4 ? d[s + 3] : (byte)255;
                    px[dstRow + x] = new Color32(d[s + 2], d[s + 1], d[s], a);
                }
            }
            var tex = new Texture2D(w, H, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply(false);
            return tex;
        }

        // As LoadTexture, but dilate opaque RGB into the transparent matte ONCE per cached texture (see AlphaBleed):
        // most SDO art stores (255,255,255,0) in the empty area, so bilinear filtering drags that white into the glyph
        // edges (a white halo). Bleeding kills it; alpha is untouched, so it's purely cosmetic + safe to share.
        private static Texture2D LoadTextureBled(string path)
        {
            var tex = LoadTexture(path);
            if (tex != null && _bled.Add(tex)) AlphaBleed(tex);
            return tex;
        }

        private static Sprite SpriteFromFrame(string folder, AnFrame fr, bool bleed = false)
        {
            // .an frames reference a png in the same folder; fall back to the named image directly.
            var tex = bleed ? LoadTextureBled(Path.Combine(folder, fr.Image)) : LoadTexture(Path.Combine(folder, fr.Image));
            if (tex == null) return null;
            Rect rect = fr.HasCrop
                ? new Rect(fr.X, tex.height - fr.Y - fr.H, fr.W, fr.H)  // flip Y (top-left -> bottom-left)
                : new Rect(0, 0, tex.width, tex.height);
            // some .an files declare the ORIGINAL DDS canvas (ENERGY_Y.AN says 128×32) while the extracted PNG is the
            // trimmed content (85×17) — an out-of-bounds rect makes Sprite.Create THROW (it froze gameplay boot), so
            // fall back to the full texture.
            if (rect.x < 0f || rect.y < 0f || rect.xMax > tex.width || rect.yMax > tex.height)
                rect = new Rect(0, 0, tex.width, tex.height);
            return Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
        }

        /// <summary>Load every frame of an .an file (in <paramref name="folder"/>) as sprites.
        /// <paramref name="bleed"/> dilates the transparent-white matte to remove the bilinear white halo on edges.</summary>
        public static Sprite[] LoadAn(string folder, string anName, bool bleed = false)
        {
            var anPath = Path.Combine(folder, anName.EndsWith(".an") ? anName : anName + ".an");
            if (!File.Exists(anPath)) return new Sprite[0];
            var frames = ParseAnText(File.ReadAllText(anPath));
            var sprites = new List<Sprite>(frames.Count);
            foreach (var fr in frames)
            {
                var s = SpriteFromFrame(folder, fr, bleed);
                if (s != null) sprites.Add(s);
            }
            return sprites.ToArray();
        }

        /// <summary>First frame of an .an (most HUD .an files are single-frame).</summary>
        public static Sprite LoadAn1(string folder, string anName, bool bleed = false)
        {
            var all = LoadAn(folder, anName, bleed);
            return all.Length > 0 ? all[0] : null;
        }

        /// <summary>Load a bare image file (png/bmp) under a folder as one sprite.
        /// <paramref name="bleed"/> dilates the transparent-white matte to remove the bilinear white halo on edges.</summary>
        public static Sprite LoadImage(string folder, string imageName, bool bleed = false)
        {
            var tex = bleed ? LoadTextureBled(Path.Combine(folder, imageName)) : LoadTexture(Path.Combine(folder, imageName));
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

        // Separate cache for linear-import textures (D3D9-gamma-compatible: no sRGB decode on sampling).
        // Used for EFT particle textures so their soft gradients match D3D9 gamma appearance: dark edge pixels
        // (value 0.1 gamma) are kept as 0.1 in Unity linear → display shows 0.35 brightness, same as D3D9.
        // With sRGB import the same 0.1 would decode to 0.01 linear → display ~0.10 → near-invisible → hard edge.
        private static readonly Dictionary<string, Texture2D> _texLinearCache = new Dictionary<string, Texture2D>();

        private static Texture2D LoadTextureLinear(string path)
        {
            if (_texLinearCache.TryGetValue(path, out var t) && t != null) return t;
            if (!File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);   // linear=true
            tex.LoadImage(File.ReadAllBytes(path));
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            _texLinearCache[path] = tex;
            return tex;
        }

        public static Texture2D LoadTextureRawLinear(string folder, string imageName)
            => LoadTextureLinear(Path.Combine(folder, imageName));

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

        // convenience for the gameplay HUD. bleed dilates the transparent-WHITE matte so bilinear filtering can't
        // pull it into the glyph edges (the "白邊" fuzzy halo) — the designed outline in the art is untouched.
        public static Sprite Hud(string anName, bool bleed = false) => LoadAn1(GameplayUiDir, anName, bleed);
        public static Sprite Eft(string imageName, int skin = 2, bool bleed = false) => LoadImage(EftDir(skin), imageName, bleed);

        /// <summary>ShowTime gameplay UI art (energy meter, banner) — UI/GAMEPLAY/PLAYSHOWTIME.</summary>
        public static string ShowtimeUiDir => Path.Combine(GameplayUiDir, "PLAYSHOWTIME");
        public static Sprite ShowtimeArt(string anName, bool bleed = false) => LoadAn1(ShowtimeUiDir, anName, bleed);
        public static Sprite[] ShowtimeFrames(string anName, bool bleed = false) => LoadAn(ShowtimeUiDir, anName, bleed);
        /// <summary>Load a 0..9 digit atlas from a PLAYSHOWTIME sub-folder (e.g. "ENERGYBONUS"), or null if incomplete.</summary>
        public static Sprite[] ShowtimeDigits(string subFolder)
        {
            var dir = Path.Combine(ShowtimeUiDir, subFolder);
            var d = new Sprite[10];
            for (int i = 0; i < 10; i++) { d[i] = LoadImage(dir, i + ".png"); if (d[i] == null) return null; }
            return d;
        }
    }
}
