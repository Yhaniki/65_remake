using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Loads the original ROOMDLG (MUSICSELDLG) song-select art. The .an files reference crops of
    /// MUSICSELDLG.PNG / SCENE.PNG sitting in the same folder, so <see cref="SdoExtracted.LoadAn1"/>
    /// (PNG + crop + Y-flip,底圖快取) loads them directly — no DDS decode needed.
    ///
    /// Folder resolution prefers the ONLINE art (閉撰敃氪/DatasSDO/UI/ROOMDLG) over the standalone
    /// Extracted tree, because the user picked the 閉撰敃氪 set as the canonical look:
    ///   1. dev: scan assets/ for any &lt;sub&gt;/DatasSDO/UI/ROOMDLG (hits 閉撰敃氪/DatasSDO/... ; the
    ///      oddly-encoded folder name is matched by structure, never hardcoded);
    ///   2. built/fallback: DATA/UI/ROOMDLG (packaging overlays the 閉撰敃氪 art there beside the exe).
    /// In a built player there is no assets/ tree, so (1) finds nothing and it falls to DATA/UI/ROOMDLG.
    /// Returns null for a missing asset; callers guard.
    /// </summary>
    public static class RoomDlgArt
    {
        private static string _dir;
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Sprite[]> _framesCache = new Dictionary<string, Sprite[]>();

        /// <summary>Resolved ROOMDLG art folder (lazy). Settable for tests.</summary>
        public static string Dir
        {
            get { return _dir ?? (_dir = ResolveDir()); }
            set { _dir = value; _cache.Clear(); }
        }

        private static string ResolveDir()
        {
            try
            {
                var ordered = new List<string>();
                // 1) dev: assets/*/DatasSDO/UI/ROOMDLG (online 閉撰敃氪 art preferred).
                //    SdoExtracted.Root = .../assets/sdox_offline/Extracted -> assets/ is two parents up.
                var assets = Path.GetDirectoryName(Path.GetDirectoryName(SdoExtracted.Root));
                if (assets != null && Directory.Exists(assets))
                    foreach (var d in Directory.GetDirectories(assets))
                        ordered.Add(Path.Combine(d, "DatasSDO", "UI", "ROOMDLG"));
                // 2) built/fallback: DATA/UI/ROOMDLG (last = used when nothing above exists).
                ordered.Add(Path.Combine(SdoExtracted.Root, "UI", "ROOMDLG"));
                return PickDir(ordered, Directory.Exists);
            }
            catch
            {
                return Path.Combine(SdoExtracted.Root, "UI", "ROOMDLG");
            }
        }

        /// <summary>
        /// Pure pick: first candidate that <paramref name="exists"/>, else the LAST candidate
        /// (the built DATA fallback, which may not exist yet on a fresh package). Null if empty.
        /// </summary>
        public static string PickDir(IList<string> ordered, Func<string, bool> exists)
        {
            if (ordered == null || ordered.Count == 0) return null;
            foreach (var c in ordered)
                if (c != null && exists(c)) return c;
            return ordered[ordered.Count - 1];
        }

        /// <summary>First frame of a ROOMDLG .an as a sprite (cached); null if missing.</summary>
        public static Sprite An(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            if (_cache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAn1(Dir, anName);
            _cache[anName] = s;
            return s;
        }

        /// <summary>ALL frames of a ROOMDLG .an as sprites (cached). Multi-frame .an = one sprite per option,
        /// e.g. LABEL_SDO.an holds the 13 SDO mode-name slices (自由模式=frame 0, 普通模式=frame 1, …).</summary>
        public static Sprite[] AnFrames(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return new Sprite[0];
            if (_framesCache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAn(Dir, anName);
            _framesCache[anName] = s;
            return s;
        }
    }
}
