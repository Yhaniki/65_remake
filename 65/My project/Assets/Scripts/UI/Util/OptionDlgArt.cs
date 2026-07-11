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
        // 遊戲 tab board (OptScreenBoard) — the OptionGameWindow board with ALL its labels + option captions BAKED
        // (遊戲畫面/全屏泛光效果/NOTES面板位置/… + 全屏/窗口/開啟/關閉/… + the 面板透明度 MIN…MAX track). We overlay only
        // the selection dots + the transparency handle at the measured screen positions (see OptionDlgModal.BuildGame).
        public static Sprite Board => Crop(674, 384, 350, 206);
        // 音效 tab board (OptVolumeBoard) — labels 背景音樂/遊戲音樂/遊戲音效 + MIN…MAX tracks baked into the art.
        public static Sprite AudioBoard => Crop(324, 384, 350, 207);
        // 進階 tab board — a CLEAN板 (no baked text) carrying one 深紛紅 label-pill template + two option circles;
        // OptionDlgModal overlays the display settings (視窗大小/顯示模式/垂直同步/語言) as dynamic 華康儷中黑 TMP text.
        public static Sprite AdvBoard => Crop(324, 669, 351, 214);
        // reusable 深紛紅 label-pill (blank) cropped from the 進階 board template — placed under each left-side label.
        public static Sprite AdvPill => Crop(342, 687, 95, 21);
        // official main-tab pills (normal = lighter/unselected, active = deeper/selected). Each carries its 中文 baked
        // (遊戲/音效/鍵盤/進階). CRITICAL: normal + active of the SAME tab MUST be cropped to IDENTICAL dimensions and
        // pill-centre so swapping states on select doesn't move/resize the pill (the pills share an x-centre; only the
        // gloss/notch extents differ). Placed by centre (pivot 0.5,1) in OptionDlgModal. Measured off OPTIONDLG.clean.png.
        public static Sprite TabAudioN => Crop(82, 81, 100, 38);
        public static Sprite TabAudioA => Crop(82, 121, 100, 38);
        public static Sprite TabKeyN => Crop(0, 163, 93, 38);
        public static Sprite TabKeyA => Crop(0, 205, 93, 38);
        public static Sprite TabGameN => Crop(173, 0, 94, 38);
        public static Sprite TabGameA => Crop(173, 40, 94, 38);
        // NB 進階 normal: crop TOP starts at the pill's dark top border (y=589), NOT higher — above it sits an
        // unrelated element (a gloss dot + notch, y≈576-588) that the old y=586 crop clipped into the tab.
        public static Sprite TabAdvN => Crop(580, 591, 95, 37);
        public static Sprite TabAdvA => Crop(580, 631, 95, 37);
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
        // radio dot (toggle) — ON is the orange orb; OFF is the lavender orb. The atlas has NO standalone off-sprite
        // (that region is transparent): the official boards BAKE the lavender "off" orbs straight in. So RadioOff crops
        // the 17×17 lavender orb baked into the 進階 board's row-1 template (atlas centre 480,698 → on the board's own
        // #F2DFF1 panel). Because the dynamically-placed rows sit on that SAME board, the crop's panel-coloured corners
        // blend invisibly, and the orb is pixel-identical to row 1's baked circle. RadioOn (gold) overlays it when selected.
        public static Sprite RadioOff => Crop(472, 690, 17, 17);
        public static Sprite RadioOn => Crop(994, 0, 15, 15);
        // slider handle
        public static Sprite SliderHandle => Crop(951, 0, 43, 23);
        // keyboard sub-tabs (each is a full 322-wide strip carrying one tab; stack them to form the 4鍵|6鍵|激鼓 bar).
        // 4鍵 = active (lighter pushed state, Option_Game36 @305); 6鍵/激鼓 = inactive normal state (Option_Game37/40).
        // Only 4鍵 is functional; 6鍵/激鼓 are drawn for fidelity but inert.
        public static Sprite FourKeyTab => Crop(0, 305, 322, 30);
        public static Sprite SixKeyTab => Crop(0, 396, 322, 30);
        public static Sprite DrumTab => Crop(0, 456, 322, 30);
        // key-cap chips (keyboard tab). Matches the official k4 CheckBox states: bgnormal = gray key.an (@913)
        // when idle, bgpushed = purple key1/key2.an (@876) when the key is selected for rebinding.
        // The bound-key LETTER is a separate 27×36 glyph PNG blitted on top (see KeysArt) — NOT baked into the chip.
        public static Sprite KeyCapNormal => Crop(913, 0, 38, 37);     // gray = idle
        public static Sprite KeyCapCapturing => Crop(876, 0, 37, 37);  // purple = selected (waiting for a keypress)
    }
}
