using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Loads the official OPTIONDLG art with the baked-in Chinese text painted out
    /// (<c>OPTIONDLG.clean.png</c>, produced by <c>tools/build_optiondlg_clean.py</c>), so the remake can
    /// overlay dynamic localized TMP text on the exact official pink frame. Sprites are cropped straight from
    /// the 1024×1024 atlas by pixel rect (top-left origin, Y-flipped for Unity); the .an indirection is bypassed
    /// because those .an files point at the ORIGINAL (text-baked) OPTIONDLG.png.
    ///
    /// Folder resolution mirrors <see cref="RoomDlgArt"/>: prefer the online 閉撰敃氪 set in dev, fall back to
    /// DATA/UI/OPTIONDLG beside a built exe (package_build overlays the clean atlas there). If the clean atlas is
    /// missing it falls back to the raw one (text still baked, but nothing crashes).
    /// </summary>
    public static class OptionDlgArt
    {
        private const string CleanAtlas = "OPTIONDLG.clean.png";
        private const string RawAtlas = "OPTIONDLG.PNG";

        private static string _dir;
        private static Texture2D _atlas;
        private static readonly Dictionary<long, Sprite> _cache = new Dictionary<long, Sprite>();

        /// <summary>Resolved OPTIONDLG art folder (lazy). Settable for tests (clears the sprite cache).</summary>
        public static string Dir
        {
            get { return _dir ?? (_dir = ResolveDir()); }
            set { _dir = value; _atlas = null; _cache.Clear(); }
        }

        private static string ResolveDir()
        {
            try
            {
                var ordered = new List<string>();
                var assets = Path.GetDirectoryName(Path.GetDirectoryName(SdoExtracted.Root));
                if (assets != null && Directory.Exists(assets))
                    foreach (var d in Directory.GetDirectories(assets))
                        ordered.Add(Path.Combine(d, "DatasSDO", "UI", "OPTIONDLG"));
                ordered.Add(Path.Combine(SdoExtracted.Root, "UI", "OPTIONDLG"));
                return RoomDlgArt.PickDir(ordered, Directory.Exists);   // reuse the same pure picker
            }
            catch { return Path.Combine(SdoExtracted.Root, "UI", "OPTIONDLG"); }
        }

        private static Texture2D Atlas =>
            _atlas != null ? _atlas
            : (_atlas = SdoExtracted.LoadTextureRaw(Dir, CleanAtlas) ?? SdoExtracted.LoadTextureRaw(Dir, RawAtlas));

        /// <summary>Crop a sprite from the clean atlas by top-left pixel rect (cached). Null if the atlas is missing.</summary>
        public static Sprite Crop(int x, int y, int w, int h)
        {
            long key = ((long)x << 40) | ((long)y << 20) | ((long)w << 10) | (uint)h;
            if (_cache.TryGetValue(key, out var s) && s != null) return s;
            var tex = Atlas;
            if (tex == null) return null;
            var rect = new Rect(x, tex.height - y - h, w, h);          // top-left origin -> Unity bottom-left
            s = Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
            _cache[key] = s;
            return s;
        }

        // ---- named crops (atlas rects from the OPTIONDLG .an table) ----
        // frame quadrants (title text removed in the clean atlas)
        public static Sprite FrameTL => Crop(355, 0, 256, 256);
        public static Sprite FrameTR => Crop(611, 0, 133, 256);
        public static Sprite FrameBL => Crop(355, 256, 256, 120);
        public static Sprite FrameBR => Crop(611, 256, 138, 120);
        // flat inner board (sunken panel) — reused for every tab
        public static Sprite Board => Crop(674, 384, 350, 206);
        // action buttons (text removed): normal + pushed
        public static Sprite SaveN => Crop(742, 0, 95, 33);
        public static Sprite SaveP => Crop(743, 36, 95, 33);
        public static Sprite ExitN => Crop(743, 72, 95, 33);
        public static Sprite ExitP => Crop(743, 108, 95, 33);
        public static Sprite DefaultN => Crop(744, 144, 95, 33);
        public static Sprite DefaultP => Crop(744, 180, 95, 33);
        // close (X) — no baked text, unaffected by cleaning
        public static Sprite CloseN => Crop(322, 246, 33, 33);
        public static Sprite CloseP => Crop(322, 279, 33, 33);
        // radio dot (toggle) off / on
        public static Sprite RadioOff => Crop(1009, 0, 15, 15);
        public static Sprite RadioOn => Crop(994, 0, 15, 15);
        // slider handle
        public static Sprite SliderHandle => Crop(951, 0, 43, 23);
        // key-cap chip (keyboard tab) — magenta chip behind the bound-key text
        public static Sprite KeyCap => Crop(876, 0, 37, 37);
    }
}
