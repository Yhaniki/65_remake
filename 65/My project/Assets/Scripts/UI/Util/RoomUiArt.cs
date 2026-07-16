using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Loads the original waiting-room (DDRROOM) UI art. Mirrors <see cref="RoomDlgArt"/> exactly — the only difference
    /// is the resolved folder leaf (ROOM instead of ROOMDLG). The .an files reference crops of WaitingRoom.png /
    /// CommonButtonNew.png etc. sitting in the same folder, so <see cref="SdoExtracted.LoadAn1"/> (PNG + crop + Y-flip,
    /// 底圖快取) loads them directly — no DDS decode needed.
    ///
    /// Folder resolution reads DATA/UI/ROOM under <see cref="SdoExtracted.Root"/> ONLY — no assets/ scan. The resolved
    /// data root (e.g. the pruned clean pack, pointed at via data_root.txt) is the single source for UI art.
    /// Returns null for a missing asset; callers guard.
    /// </summary>
    public static class RoomUiArt
    {
        private static string _dir;
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Sprite> _soloCache = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Sprite[]> _framesCache = new Dictionary<string, Sprite[]>();

        /// <summary>Resolved ROOM art folder (lazy). Settable for tests.</summary>
        public static string Dir
        {
            get { return _dir ?? (_dir = ResolveDir()); }
            set { _dir = value; _cache.Clear(); _soloCache.Clear(); _framesCache.Clear(); }
        }

        private static string ResolveDir()
        {
            try
            {
                // Use the resolved data root ONLY — no assets/ scan (data_root.txt points this at the clean pack).
                return Path.Combine(SdoExtracted.Root, "UI", "ROOM");
            }
            catch
            {
                return Path.Combine(SdoExtracted.Root, "UI", "ROOM");
            }
        }

        /// <summary>First frame of a ROOM .an as a sprite (cached); null if missing. Alpha-bleed ON: ROOM art stores
        /// (255,255,255,0) in transparent areas, so bilinear filtering drags that white into the button/label edges (a
        /// white halo); dilating the opaque RGB into the matte kills it (alpha untouched — purely cosmetic).</summary>
        public static Sprite An(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            if (_cache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAn1(Dir, anName, bleed: true);
            _cache[anName] = s;
            return s;
        }

        /// <summary>As <see cref="An"/> but the first frame is copied onto its OWN texture (LoadAnSolo) — kills the
        /// white/coloured fringe that ATLAS-NEIGHBOUR bleed drags into a crop edge when the .an abuts another opaque
        /// sprite in the shared PNG (旁觀/開始 鈕的白邊). AlphaBleed (the bleed:true path above) only dilates the
        /// transparent-white matte, so it can't fix an opaque neighbour — on its own texture there is no neighbour.
        /// Falls back to the shared-atlas <see cref="An"/> if the solo crop fails. Use only for the buttons that
        /// actually show the fringe.</summary>
        public static Sprite AnSolo(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            if (_soloCache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAnSolo(Dir, anName, pad: 2) ?? An(anName);
            _soloCache[anName] = s;
            return s;
        }

        public static Sprite AnExtractedFirst(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            string key = "extracted:" + anName;
            if (_cache.TryGetValue(key, out var s) && s != null) return s;
            s = SdoExtracted.LoadAn1(Path.Combine(SdoExtracted.Root, "UI", "ROOM"), anName, bleed: true) ?? An(anName);
            _cache[key] = s;
            return s;
        }

        /// <summary>ALL frames of a ROOM .an as sprites (cached). Multi-frame .an = one sprite per option/animation
        /// frame (e.g. moveuphelp0.an holds the 4 arrow-key frames, Team.an the name-plate strip).</summary>
        public static Sprite[] AnFrames(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return new Sprite[0];
            if (_framesCache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAn(Dir, anName, bleed: true);   // dilate transparent-white matte → no edge halo
            _framesCache[anName] = s;
            return s;
        }

        /// <summary>A bare PNG/BMP file sitting in the ROOM folder (NOT an .an crop) as one sprite (cached); null if
        /// missing. Used for standalone room art like FREE.PNG (the "random note-effect" slot icon).</summary>
        public static Sprite Image(string imageName)
        {
            if (string.IsNullOrEmpty(imageName)) return null;
            if (_cache.TryGetValue(imageName, out var s) && s != null) return s;
            s = SdoExtracted.LoadImage(Dir, imageName, bleed: true);
            _cache[imageName] = s;
            return s;
        }

        /// <summary>Crop atlas PNG at top-left (x,y,w,h) — matches official .an crop coords.</summary>
        public static Sprite AtlasCrop(string imageName, int x, int y, int w, int h)
        {
            if (string.IsNullOrEmpty(imageName) || w <= 0 || h <= 0) return null;
            string key = "atlas:" + imageName + ":" + x + "," + y + "," + w + "," + h;
            if (_cache.TryGetValue(key, out var s) && s != null) return s;

            var tex = SdoExtracted.LoadTextureRaw(Dir, imageName)
                      ?? SdoExtracted.LoadTextureRaw(Dir, imageName.ToUpperInvariant())
                      ?? SdoExtracted.LoadTextureRaw(Path.Combine(SdoExtracted.Root, "UI", "ROOM"), imageName);
            if (tex == null) return null;
            if (x + w > tex.width || y + h > tex.height) return null;

            var rect = new Rect(x, tex.height - y - h, w, h);
            s = Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
            _cache[key] = s;
            return s;
        }

        // PopNormalExpression1/2.an + tab/arrow crops from ExpressionInfo.png (ROOMPOPMENU).
        public static Sprite ExpressionInfoPage(int page)
            => AtlasCrop("EXPRESSIONINFO.PNG", 0, Mathf.Clamp(page, 0, 1) * 132, 165, 132);

        public static Sprite ExpressionNormalTab(bool selected)
            => AtlasCrop("EXPRESSIONINFO.PNG", selected ? 0 : 38, 396, 38, 18);

        public static Sprite ExpressionPageArrow(bool left, int state)
        {
            // state 0=normal 1=hover 2=pushed — PopLeftArrow_*/PopRightArrow_*
            int col = left ? (state == 0 ? 0 : 17) : (state == 0 ? 34 : 51);
            int row = state == 2 ? 449 : 450;
            return AtlasCrop("EXPRESSIONINFO.PNG", col, row, 17, 17);
        }

        public static Sprite[] ExpressionPageArrowFrames(bool left)
            => new[]
            {
                ExpressionPageArrow(left, 0),
                ExpressionPageArrow(left, 1),
                ExpressionPageArrow(left, 2),
            };
    }
}
