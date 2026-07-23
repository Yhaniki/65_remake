using System.Collections.Generic;
using System.IO;
using Sdo.Settings;
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
        // 解析本身住在 Sdo.Settings.SdoDataRoot（單一來源；存檔 ProfileManager 也走同一個根，不會再分裂）。

        /// <summary>Root of the SDO game data tree. Resolves lazily on first use; settable for tests/overrides.</summary>
        public static string Root
        {
            get { return SdoDataRoot.Root; }
            set { SdoDataRoot.Root = value; }
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

        /// <summary>Sound-effects folder. Built: DATA/SE; dev: sdox_offline/SE or sibling extracted asset dumps.</summary>
        public static string SeDir => ResolveSeDir();

        private static string ResolveSeDir()
        {
            var candidates = new List<string>();
            AddUniqueDir(candidates, Path.Combine(Root, "SE"));
            AddUniqueDir(candidates, Path.Combine(Path.GetDirectoryName(Root) ?? Root, "SE"));
            try
            {
                var assets = Path.GetDirectoryName(Path.GetDirectoryName(Root));
                if (assets != null && Directory.Exists(assets))
                    foreach (var d in Directory.GetDirectories(assets))
                        AddUniqueDir(candidates, Path.Combine(d, "SE"));
            }
            catch { }

            foreach (var c in candidates)
            {
                try
                {
                    if (Directory.Exists(c) && File.Exists(Path.Combine(c, "Bubble.wav")))
                        return c;
                }
                catch { }
            }
            return FirstDir(candidates.ToArray());
        }

        private static void AddUniqueDir(List<string> dirs, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try { dir = Path.GetFullPath(dir); } catch { }
            foreach (var d in dirs)
                if (string.Equals(d, dir, System.StringComparison.OrdinalIgnoreCase)) return;
            dirs.Add(dir);
        }

        /// <summary>Song chart/audio folder. Built: DATA/MUSIC (uppercase); dev: sdox_offline/music (sibling).</summary>
        public static string MusicDir => FirstDir(Path.Combine(Root, "MUSIC"), Path.Combine(Path.GetDirectoryName(Root) ?? Root, "music"));

        /// <summary>Front-end lobby/room background-music folder (endless random *.ogg / *.mp3, driven by
        /// <see cref="Sdo.UI.Util.BgmPlayer"/>). Built player: <c>DATA/BGM</c> — the lobby tracks (bgm_000..007.ogg) ship at
        /// the DATA root; editor/dev falls back to their SDO-authentic location <c>Extracted/UI/BGM</c>. (The original
        /// game's top-level BGM set BMG_/TEACHING had no consumer in the remake and is no longer shipped, so DATA/BGM
        /// holds the lobby tracks.)</summary>
        public static string UiBgmDir => FirstDir(
            Path.Combine(Root, "BGM"),                                        // built player: DATA/BGM (relocated lobby bgm)
            Path.Combine(Root, "UI", "BGM"),                                  // editor/dev: Extracted/UI/BGM (authentic SDO location)
            Path.Combine(Path.GetDirectoryName(Root) ?? Root, "UI", "BGM")); // sibling extracted-dump fallback

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

        // DEV load-trace: when env SDO_TRACE_LOADS names a file, EVERY attempted texture path (png/bmp/dds) is appended
        // there — the ground-truth "what gameplay actually loads", used by the dead-art prune to avoid false-deletes.
        private static System.IO.StreamWriter _trace;
        private static bool _traceInit;
        private static void TraceLoad(string path)
        {
            if (!_traceInit)
            {
                _traceInit = true;
                try { var p = System.Environment.GetEnvironmentVariable("SDO_TRACE_LOADS");
                      if (!string.IsNullOrEmpty(p)) _trace = new StreamWriter(p, true) { AutoFlush = true }; } catch { }
            }
            if (_trace == null) return;
            try { lock (_trace) _trace.WriteLine(path); } catch { }
        }

        private static Texture2D LoadTexture(string path)
        {
            TraceLoad(path);
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

        /// <summary>First frame of an .an copied onto its OWN texture (crop + <paramref name="pad"/>px transparent
        /// border, Clamp-wrapped, alpha-bled + de-matted). Use for a sprite whose atlas crop abuts a NEIGHBOURING
        /// sprite: bilinear filtering at the crop edge would otherwise drag the neighbour's opaque pixels in as a
        /// white/coloured fringe (the 全身购买 orb bleeds the light sprite sitting to its left/right). AlphaBleed /
        /// DeMatteWhite alone can't fix that — they only treat the transparent matte, not an opaque neighbour. On its
        /// own texture there is no neighbour, so the fringe is gone. Returns null if the .an/png is missing.</summary>
        public static Sprite LoadAnSolo(string folder, string anName, int pad = 2)
            => LoadAnSoloImpl(folder, anName, pad, circular: false);

        /// <summary>As <see cref="LoadAnSolo"/> but for ROUND buttons (the shop's ♂/♀/reset/cart orbs): after de-matting,
        /// flood the orb colour across the WHOLE transparent region (not just a 1px ring) and multiply alpha by a soft
        /// inscribed-circle mask centred on the opaque disc. The orbs are authored composited on WHITE, so a ring of
        /// (255,255,255,~0) matte texels sits just outside the disc; AlphaBleed's 1px reach leaves the far ones white and
        /// bilinear MAGNIFICATION pulls that white into a fringe. Flood kills the white RGB everywhere; the circle mask
        /// gives a clean round alpha cut-off. Use only for actually-round sprites. (SHOP now uses premultiplied alpha
        /// instead — see <see cref="LoadAnSoloPremultiplied"/> — so this is currently unused, kept for reference.)</summary>
        public static Sprite LoadAnSoloCircular(string folder, string anName, int pad = 0)
            => LoadAnSoloImpl(folder, anName, pad, circular: true);

        /// <summary>As <see cref="LoadAnSolo"/> but SUPERSAMPLED, so the round button edge is crisp AND smooth at any
        /// display size. The room buttons (開始/旁觀/房主設置…) are ~73px crops of a near 1-bit disc; the default window is
        /// 800×600 so a button shows at ~73px (1:1) — where a hard edge is jagged and a blur is mushy (使用者回報「鋸齒」
        /// 然後「往外糊」). The fix is genuine supersampling: clip the baked outer glow, then upsample the crop
        /// <see cref="ButtonSupersample"/>× onto a MIPMAPPED/Trilinear texture and hand it back at pixelsPerUnit = SS so it
        /// DISPLAYS at the logical size — the GPU then area-downsamples the high-res texture, giving a clean ~1px AA edge
        /// (crisp, not soft) whether the window is 1:1 or stretched fullscreen. Requires UIKit.ApplySprite to size by
        /// rect.size / pixelsPerUnit. Falls back to the shared atlas via the AnSolo path if the crop fails.</summary>
        public static Sprite LoadAnSoloMip(string folder, string anName, int pad = 0)
            => LoadAnSoloImpl(folder, anName, pad, circular: false, mip: true);

        /// <summary>As <see cref="LoadAnSoloMip"/> but for ROUND ICON buttons whose disc has a wide SOFT anti-aliased rim
        /// (房間右上角 head-bar:設定/邀請/返回/交易/天使 — 34px CommonButtonNew discs). The plain-mip α&lt;128→0 clip binarises
        /// that soft rim → a jagged/破碎 circle; instead this runs <see cref="CircleMask"/> (a smoothstep round edge fitted
        /// to the disc radius, halo trimmed) and THEN supersamples, so the round edge stays smooth AND crisp when the
        /// 800×600 design is magnified fullscreen. Falls back to the plain mip button path if the crop fails.</summary>
        public static Sprite LoadAnSoloCircleMip(string folder, string anName, int pad = 0)
            => LoadAnSoloImpl(folder, anName, pad, circular: true, mip: true);

        /// <summary>Supersample factor for room-button textures (see <see cref="LoadAnSoloMip"/>). 3× keeps the button
        /// crisp from 1:1 up to ~3× fullscreen stretch; higher just costs memory (a 73px crop → 219²·RGBA·mips ≈ 255 KB).</summary>
        public const int ButtonSupersample = 3;

        /// <summary>Alpha at/above which a room-button texel is part of the SOLID disc (the dark rim and inward). Texels
        /// below it are the baked outer bright-cyan glow / matte and are cleared before supersampling (see LoadAnSoloImpl).
        /// 128 is safe: the α≥128 region of the button crops is a single stable contour (barely moves over α 110-150).</summary>
        private const byte SolidAlphaThreshold = 128;

        private static Sprite LoadAnSoloImpl(string folder, string anName, int pad, bool circular, bool mip = false)
        {
            var anPath = Path.Combine(folder, anName.EndsWith(".an") ? anName : anName + ".an");
            if (!File.Exists(anPath)) return null;
            var frames = ParseAnText(File.ReadAllText(anPath));
            if (frames.Count == 0) return null;
            var fr = frames[0];
            var src = LoadTexture(Path.Combine(folder, fr.Image));
            if (src == null) return null;
            int cx, cy, cw, ch;
            if (fr.HasCrop) { cx = fr.X; cy = src.height - fr.Y - fr.H; cw = fr.W; ch = fr.H; }   // top-left -> bottom-left
            else { cx = 0; cy = 0; cw = src.width; ch = src.height; }
            if (cw <= 0 || ch <= 0 || cx < 0 || cy < 0 || cx + cw > src.width || cy + ch > src.height) return null;
            var block = src.GetPixels(cx, cy, cw, ch);
            int W = cw + pad * 2, H = ch + pad * 2;
            var outTex = new Texture2D(W, H, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            outTex.SetPixels(new Color[W * H]);            // transparent border
            outTex.SetPixels(pad, pad, cw, ch, block);
            outTex.Apply(false);
            // DeMatteWhite BEFORE AlphaBleed: DeMatteWhite auto-detects a white/light matte from the still-untouched
            // transparent region (光球鈕的透明區是白/淺 → 整顆在白底上合成 → mid-alpha 邊是「帶色的白暈」). AlphaBleed 之後
            // 透明區被填成不透明色,偵測就失效了。順序:先 demat 邊,再把 (demated) 邊色 dilate 進透明 pad。
            DeMatteWhite(outTex);    // un-composite the white matte on the crop's own AA edges (kills the light halo)
            if (circular)
            {
                AlphaFlood(outTex);  // flood orb colour into the ENTIRE transparent region → no white RGB left to bleed
                CircleMask(outTex);  // soft inscribed-circle alpha cut-off → clean round edge under magnification
                if (mip)
                {
                    // Round ICON buttons (房間右上角 head-bar:設定/邀請/返回/交易/天使…) are 34px CommonButtonNew discs
                    // with a genuine ~3px SOFT AA rim (α 145→87→30 over three rings). The non-circular mip path's
                    // α<128→0 clip would BINARISE that rim into a 1-bit circle → jagged/破碎 at the fullscreen-magnified
                    // 800×600 design. CircleMask already gave a clean smoothstep round edge (halo trimmed); supersample it
                    // the SAME way as the plain button path so it stays crisp when stretched — NO α-clip, the smoothstep IS
                    // the anti-aliasing we must keep. (Room15-style near-1-bit discs stay on the clip path via mip-only.)
                    int ssc = ButtonSupersample;
                    var upc = UpsampleBilinear(outTex.GetPixels32(), W, H, ssc);
                    return Sprite.Create(upc, new Rect(0, 0, W * ssc, H * ssc), new Vector2(0.5f, 0.5f), ssc, 0, SpriteMeshType.FullRect);
                }
                return Sprite.Create(outTex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
            }
            if (mip)
            {
                // (1) CLIP TO THE SOLID DISC. The art bakes a jagged low-alpha BRIGHT-CYAN glow just outside the dark rim
                // (rgb≈(193,255,254) at α≈35-49); snapping every sub-solid texel to α=0 drops it so the dark rim (α≥128,
                // a stable single contour) is the outer edge (使用者回報「描邊之外的異常亮光」). Interior art (α=255) is kept.
                var pc = outTex.GetPixels32();
                for (int i = 0; i < pc.Length; i++) if (pc[i].a < SolidAlphaThreshold) pc[i].a = 0;
                outTex.SetPixels32(pc); outTex.Apply(false);
                AlphaFlood(outTex);  // fill the cleared ring with the rim colour so the AA edge is dark-rim, not black/cyan
                // (2) SUPERSAMPLE: upsample SS× onto a mipmapped/trilinear texture; return it at ppu = SS so it displays at
                // the logical size and the GPU area-downsamples → crisp ~1px AA edge at 1:1 or fullscreen.
                int ss = ButtonSupersample;
                var up = UpsampleBilinear(outTex.GetPixels32(), W, H, ss);
                return Sprite.Create(up, new Rect(0, 0, W * ss, H * ss), new Vector2(0.5f, 0.5f), ss, 0, SpriteMeshType.FullRect);
            }
            AlphaBleed(outTex);      // non-mip path: dilate the crop's opaque colour into the transparent pad (kills halo)
            return Sprite.Create(outTex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
        }

        /// <summary>Upsample RGBA texels by integer factor <paramref name="ss"/> with bilinear interpolation onto a fresh
        /// MIPMAPPED, Trilinear, Clamp texture (all four channels). Used to supersample room buttons (see LoadAnSoloMip).</summary>
        private static Texture2D UpsampleBilinear(Color32[] src, int w, int h, int ss)
        {
            int W = w * ss, H = h * ss;
            var dst = new Color32[W * H];
            for (int y = 0; y < H; y++)
            {
                float sy = (y + 0.5f) / ss - 0.5f;
                int y0 = Mathf.Clamp(Mathf.FloorToInt(sy), 0, h - 1), y1 = Mathf.Clamp(y0 + 1, 0, h - 1);
                float fy = Mathf.Clamp01(sy - Mathf.Floor(sy));
                for (int x = 0; x < W; x++)
                {
                    float sx = (x + 0.5f) / ss - 0.5f;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(sx), 0, w - 1), x1 = Mathf.Clamp(x0 + 1, 0, w - 1);
                    float fx = Mathf.Clamp01(sx - Mathf.Floor(sx));
                    Color32 c00 = src[y0 * w + x0], c10 = src[y0 * w + x1], c01 = src[y1 * w + x0], c11 = src[y1 * w + x1];
                    dst[y * W + x] = new Color32(
                        (byte)(Mathf.Lerp(Mathf.Lerp(c00.r, c10.r, fx), Mathf.Lerp(c01.r, c11.r, fx), fy) + 0.5f),
                        (byte)(Mathf.Lerp(Mathf.Lerp(c00.g, c10.g, fx), Mathf.Lerp(c01.g, c11.g, fx), fy) + 0.5f),
                        (byte)(Mathf.Lerp(Mathf.Lerp(c00.b, c10.b, fx), Mathf.Lerp(c01.b, c11.b, fx), fy) + 0.5f),
                        (byte)(Mathf.Lerp(Mathf.Lerp(c00.a, c10.a, fx), Mathf.Lerp(c01.a, c11.a, fx), fy) + 0.5f));
                }
            }
            var t = new Texture2D(W, H, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            t.SetPixels32(dst); t.Apply(true);
            return t;
        }

        /// <summary>First frame of an .an cropped onto its OWN texture with its RGB PREMULTIPLIED by alpha (bilinear, no
        /// mipmaps, 1px transparent pad). Pair with a premultiplied-alpha material (Blend One OneMinusSrcAlpha —
        /// <c>Sdo/SpritePremultiply</c>). For a sprite that gets MAGNIFIED on screen (the result 「YOU WIN／LOSE」 banner
        /// zooms screen-width→1, and everything is stretched from the 800×600 design to the real window): with the
        /// default STRAIGHT-alpha material, bilinear interpolates colour and coverage SEPARATELY across each glyph's
        /// opaque→transparent edge, so the transparent matte keeps a bright RGB while its alpha fades — the candy bevel /
        /// white matte leak outward as a semi-transparent PALE halo (the 「白邊」, worst on flat letter tops like U). It's
        /// a GPU-sampler artifact, NOT baked in the PNG (the art composites cleanly over black at native size), so
        /// AlphaBleed / DeMatteWhite can't touch it. Premultiplied colour makes the transparent texels (0,0,0,0): now
        /// interpolation fades only COVERAGE, the edge keeps the glyph colour (no halo) and stays SMOOTH at any scale
        /// (unlike point filtering). Premultiply is done in LINEAR space (the project is Linear; the sRGB texture is
        /// decoded on sample) then re-encoded to sRGB, so the antialiased edge isn't darkened into a faint rim. Its own
        /// texture keeps this off the shared BALANCE.png atlas (the OK/SAVE buttons crop it too and stay straight-alpha).</summary>
        public static Sprite LoadAnSoloPremultiplied(string folder, string anName, int pad = 1, bool cleanMatte = false)
        {
            var anPath = Path.Combine(folder, anName.EndsWith(".an") ? anName : anName + ".an");
            if (!File.Exists(anPath)) return null;
            var frames = ParseAnText(File.ReadAllText(anPath));
            if (frames.Count == 0) return null;
            var fr = frames[0];
            var src = LoadTexture(Path.Combine(folder, fr.Image));
            if (src == null) return null;
            int cx, cy, cw, ch;
            if (fr.HasCrop) { cx = fr.X; cy = src.height - fr.Y - fr.H; cw = fr.W; ch = fr.H; }   // top-left -> bottom-left
            else { cx = 0; cy = 0; cw = src.width; ch = src.height; }
            if (cw <= 0 || ch <= 0 || cx < 0 || cy < 0 || cx + cw > src.width || cy + ch > src.height) return null;
            int W = cw + pad * 2, H = ch + pad * 2;
            var outTex = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            outTex.SetPixels(new Color[W * H]);           // (0,0,0,0) transparent border — premult-clean under bilinear
            outTex.SetPixels(pad, pad, cw, ch, src.GetPixels(cx, cy, cw, ch));
            outTex.Apply(false);
            var cols = outTex.GetPixels();                // straight-alpha, sRGB-stored floats
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                float a = c.a;
                // cleanMatte: 白底出圖在鈕外緣(尤其右上角)留一圈「低透明度純白」matte (a<~48)。premult 會把它「正確」合成成一層
                // 淡白霧 —— 疊在深色商城 UI 上就顯出「右上外圍沒清乾淨的白邊」。這種低-alpha 泛白像素是 matte 殘留(鈕本身 AA 邊
                // a≥59、白色圖示 a=255 都在門檻外),直接清成全透明。純白條件避免誤傷帶色 AA 邊。
                if (cleanMatte && a < 48f / 255f && c.r > 170f / 255f && c.g > 170f / 255f && c.b > 170f / 255f)
                { cols[i] = new Color(0f, 0f, 0f, 0f); continue; }
                var lin = c.linear;                       // sRGB → linear (A untouched)
                lin.r *= a; lin.g *= a; lin.b *= a;       // premultiply in linear space
                var outc = lin.gamma;                     // linear → sRGB for storage; GPU decode yields linear·a
                outc.a = a;
                cols[i] = outc;
            }
            outTex.SetPixels(cols); outTex.Apply(false);
            _premultTextures.Add(outTex);                 // mark so UGUI can pair it with the premult material (UIKit.ApplySprite)
            return Sprite.Create(outTex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
        }

        // Textures produced by LoadAnSoloPremultiplied (RGB already × alpha). A sprite on one of these MUST render with a
        // premultiplied-alpha material (Blend One OneMinusSrcAlpha) or it looks wrong — UIKit.ApplySprite auto-pairs them.
        private static readonly HashSet<Texture> _premultTextures = new HashSet<Texture>();
        /// <summary>True if <paramref name="t"/> is a premultiplied-alpha texture from <see cref="LoadAnSoloPremultiplied"/>.</summary>
        public static bool IsPremultTexture(Texture t) => t != null && _premultTextures.Contains(t);

        private static Material _premultUiMat;
        /// <summary>Shared premultiplied-alpha material (<c>Sdo/SpritePremultiply</c>, Blend One OneMinusSrcAlpha) for
        /// UI Images / SpriteRenderers showing a premult texture. One instance serves all — UGUI binds each renderer's own
        /// texture, and SpriteRenderers set their own. Null if the shader was stripped (caller keeps the default material,
        /// which shows the premult texture too dark — so registration in BuildScript.RequiredShaders matters).</summary>
        public static Material PremultUiMaterial
        {
            get
            {
                if (_premultUiMat == null)
                {
                    var sh = Shader.Find("Sdo/SpritePremultiply");
                    if (sh != null) _premultUiMat = new Material(sh) { name = "SdoPremultUI" };
                }
                return _premultUiMat;
            }
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
            TraceLoad(path);
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

        /// <summary>Flood every (near-)transparent texel with the nearest opaque colour (alpha kept), propagating across
        /// the WHOLE texture — unlike <see cref="AlphaBleed"/>, whose filled texels stay a≤8 and so never become sources,
        /// giving it only a 1px reach. Used before <see cref="CircleMask"/> on the round orb buttons: it erases the
        /// (255,255,255,~0) white matte ring the art bakes just outside the disc, so bilinear MAGNIFICATION has no white
        /// RGB to pull into the edge. Alpha is untouched (purely a colour fill under the eventual mask).</summary>
        public static void AlphaFlood(Texture2D tex)
        {
            if (tex == null) return;
            int w = tex.width, h = tex.height;
            var px = tex.GetPixels32();
            var known = new bool[px.Length];
            int unknown = 0;
            for (int i = 0; i < px.Length; i++) { known[i] = px[i].a > 8; if (!known[i]) unknown++; }
            int guard = w + h + 4;                                    // upper bound on propagation distance
            while (unknown > 0 && guard-- > 0)
            {
                var srcKnown = (bool[])known.Clone();
                var src = (Color32[])px.Clone();
                bool changed = false;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (srcKnown[i]) continue;
                        int r = 0, g = 0, b = 0, n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                int j = ny * w + nx;
                                if (srcKnown[j]) { var c = src[j]; r += c.r; g += c.g; b += c.b; n++; }
                            }
                        if (n > 0) { px[i].r = (byte)(r / n); px[i].g = (byte)(g / n); px[i].b = (byte)(b / n); known[i] = true; unknown--; changed = true; }
                    }
                if (!changed) break;                                 // nothing left reachable (fully transparent texture)
            }
            tex.SetPixels32(px); tex.Apply();
        }

        /// <summary>Multiply alpha by a soft inscribed-circle mask centred on the opaque disc, auto-fitting the radius per
        /// texture — clips the square crop's corners and the residual sub-pixel matte just outside the disc so a round
        /// button reads clean over any background under bilinear filtering. Radius/feather are measured from the alpha
        /// radial profile: maskRadius = the outermost ring still mostly-opaque (mean α≥128, the perceptual edge),
        /// feather spans out to the AA edge (mean α≥16) plus a soft margin, so the orb's own antialiased rim is kept and
        /// only the halo beyond it is zeroed. Orientation-invariant (radially symmetric), so bottom-up textures are fine.</summary>
        public static void CircleMask(Texture2D tex, float featherExtra = 1.5f)
        {
            if (tex == null) return;
            int w = tex.width, h = tex.height;
            var px = tex.GetPixels32();
            // alpha-weighted centroid = the disc centre (robust to the crop not being perfectly centred)
            double sx = 0, sy = 0, sw = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) { int a = px[y * w + x].a; if (a > 0) { sx += (double)x * a; sy += (double)y * a; sw += a; } }
            if (sw <= 0) return;
            float cx = (float)(sx / sw), cy = (float)(sy / sw);
            // mean alpha per integer-radius ring → find the α≥128 (maskRadius) and α≥16 (edgeRadius) crossings
            int maxR = (int)Mathf.Ceil(Mathf.Sqrt(w * w + h * h)) + 1;
            var sum = new double[maxR + 1];
            var cnt = new int[maxR + 1];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int R = (int)Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (R <= maxR) { sum[R] += px[y * w + x].a; cnt[R]++; }
                }
            int maskR = 0, edgeR = 0;
            for (int R = 0; R <= maxR; R++)
            {
                if (cnt[R] == 0) continue;
                double mean = sum[R] / cnt[R];
                if (mean >= 128) maskR = R;
                if (mean >= 16) edgeR = R;
            }
            float feather = Mathf.Max(edgeR - maskR + featherExtra, 1.0f);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (px[i].a == 0) continue;
                    float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    float t = Mathf.Clamp01((maskR + feather - d) / feather);
                    float s = t * t * (3f - 2f * t);                 // smoothstep: 1 inside maskR, 0 by maskR+feather
                    px[i].a = (byte)Mathf.Clamp(px[i].a * s, 0f, 255f);
                }
            tex.SetPixels32(px); tex.Apply();
        }

        /// <summary>Remove a WHITE matte from anti-aliased sprite edges: pixels the source art blended against a white
        /// background (0&lt;alpha&lt;opaque, colour pulled toward white) show a white halo when composited over the dark
        /// UI. Recover the true colour by un-compositing over white — c_true = (c − 255·(1−a)) / a — so edges read
        /// clean. Alpha is untouched (purely cosmetic). Only mid-alpha edge pixels are touched; the fully-opaque
        /// interior and the (near-)transparent matte (handled by <see cref="AlphaBleed"/>) are skipped. Idempotent-ish;
        /// call once per texture.</summary>
        public static void DeMatteWhite(Texture2D tex, int loThresh = 8, int hiThresh = 250, int whiteMin = 140)
        {
            if (tex == null) return;
            var px = tex.GetPixels32();
            // WHITE-MATTE detection: is the fully-transparent region light on average? If so the WHOLE sprite was
            // composited over white (光球鈕/圓 icon 常是白底出圖) → its mid-alpha AA edges carry a *hued* white halo
            // (紅球邊=粉白、藍球邊=青白),不是純白 → demat ALL edge pixels. A sprite NOT on white keeps a dark
            // transparent region → stay on the conservative pure-whiteish path (don't darken its真正帶色 AA 邊).
            // 只取「非近黑」的透明 texel:LoadAnSolo 加的 2px 透明邊框是 RGB=0,會把平均拉低 → 排除;真正黑透明(無白暈)
            // 也一併排除 → tn=0 → 保守路徑。剩下的若偏亮 → 判定白/淺底 matte。
            long tl = 0; int tn = 0;
            foreach (var c in px) if (c.a < loThresh && (c.r > 16 || c.g > 16 || c.b > 16)) { tl += (c.r * 30 + c.g * 59 + c.b * 11) / 100; tn++; }
            bool whiteMatte = tn > 0 && tl / tn > 150;
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                int a = c.a;
                if (a < loThresh || a >= hiThresh) continue;
                // 非白底 sprite → 只碰純白邊 (帶色 AA 邊留著,免被 demat 弄暗);白底 sprite → 全邊 demat。
                if (!whiteMatte && (c.r < whiteMin || c.g < whiteMin || c.b < whiteMin)) continue;
                float f = a / 255f, inv = 255f * (1f - f);
                px[i].r = (byte)Mathf.Clamp((c.r - inv) / f, 0f, 255f);
                px[i].g = (byte)Mathf.Clamp((c.g - inv) / f, 0f, 255f);
                px[i].b = (byte)Mathf.Clamp((c.b - inv) / f, 0f, 255f);
            }
            tex.SetPixels32(px); tex.Apply();
        }

        /// <summary>
        /// Return a sprite over a FRESH, UNIQUE copy of <paramref name="src"/>'s texture (de-fringed only).
        /// Each lane's cap gets its OWN texture this way, so it can never be confused with another lane's cap
        /// (no shared cache texture), and the transparent-matte fringe is removed. Original colours are preserved.
        /// NB: do NOT dampen the bright rim — the cap art ships a designed WHITE/silver metallic rim (the outer
        /// taper edge). Flattening it to the cap's average opaque colour tints the edge (teal-green on the
        /// left/right cap, brown on up/down) instead of the intended white.
        /// </summary>
        public static Sprite CleanCapCopy(Sprite src)
        {
            if (src == null) return null;
            var t0 = src.texture;
            var tex = new Texture2D(t0.width, t0.height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            tex.SetPixels32(t0.GetPixels32()); tex.Apply();
            AlphaBleed(tex);        // kill transparent-white fringe (the visible white rim is left intact)
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
