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
    /// Folder resolution prefers the ONLINE art (閉撰敃氪/DatasSDO/UI/ROOM) over the standalone Extracted tree, because
    /// the user picked the 閉撰敃氪 set as the canonical look (same convention as RoomDlgArt):
    ///   1. dev: scan assets/ for any &lt;sub&gt;/DatasSDO/UI/ROOM (hits 閉撰敃氪/DatasSDO/...; the oddly-encoded folder
    ///      name is matched by STRUCTURE, never hardcoded);
    ///   2. built/fallback: DATA/UI/ROOM (packaging overlays the 閉撰敃氪 art there beside the exe).
    /// In a built player there is no assets/ tree, so (1) finds nothing and it falls to DATA/UI/ROOM.
    /// Returns null for a missing asset; callers guard.
    /// </summary>
    public static class RoomUiArt
    {
        private static string _dir;
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Sprite[]> _framesCache = new Dictionary<string, Sprite[]>();

        /// <summary>Resolved ROOM art folder (lazy). Settable for tests.</summary>
        public static string Dir
        {
            get { return _dir ?? (_dir = ResolveDir()); }
            set { _dir = value; _cache.Clear(); _framesCache.Clear(); }
        }

        private static string ResolveDir()
        {
            try
            {
                var ordered = new List<string>();
                // 1) dev: assets/*/DatasSDO/UI/ROOM (online 閉撰敃氪 art preferred).
                //    SdoExtracted.Root = .../assets/sdox_offline/Extracted -> assets/ is two parents up.
                var assets = Path.GetDirectoryName(Path.GetDirectoryName(SdoExtracted.Root));
                if (assets != null && Directory.Exists(assets))
                    foreach (var d in Directory.GetDirectories(assets))
                        ordered.Add(Path.Combine(d, "DatasSDO", "UI", "ROOM"));
                // 2) built/fallback: DATA/UI/ROOM (last = used when nothing above exists).
                ordered.Add(Path.Combine(SdoExtracted.Root, "UI", "ROOM"));
                return RoomDlgArt.PickDir(ordered, Directory.Exists);
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
    }
}
