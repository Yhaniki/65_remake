using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Loads the original 儲物櫃 (INVENTORY / 衣櫃) UI art — the CabinetAndTv_Dlg atlas sliced by the .an files in the
    /// MYHOUSEDLG folder (BACKGROUND.an = the 621×500 window frame, coat/gift tabs, label_* category column, class up/down,
    /// left/right page arrows, DeleteCostume, HouseCabinetDlg32/36/39/42/46 slot chrome, close). Mirrors
    /// <see cref="ShopArt"/> exactly; only the folder leaf differs (MYHOUSEDLG). The .an frames reference crops of
    /// CabinetAndTv_Dlg.png in the same folder, so <see cref="SdoExtracted.LoadAn1"/> loads them directly. Folder
    /// resolution prefers the ONLINE art (閉撰敃氪/DatasSDO/UI/MYHOUSEDLG); a built player falls back to DATA/UI/MYHOUSEDLG.
    /// Returns null for a missing asset; callers guard.
    /// </summary>
    public static class CabinetArt
    {
        private static string _dir;
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Sprite[]> _framesCache = new Dictionary<string, Sprite[]>();
        private static readonly HashSet<Texture2D> _deMatted = new HashSet<Texture2D>();

        /// <summary>Resolved MYHOUSEDLG art folder (lazy). Settable for tests.</summary>
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
                var assets = Path.GetDirectoryName(Path.GetDirectoryName(SdoExtracted.Root));   // .../assets
                if (assets != null && Directory.Exists(assets))
                    foreach (var d in Directory.GetDirectories(assets))
                        ordered.Add(Path.Combine(d, "DatasSDO", "UI", "MYHOUSEDLG"));
                ordered.Add(Path.Combine(SdoExtracted.Root, "UI", "MYHOUSEDLG"));
                return RoomDlgArt.PickDir(ordered, Directory.Exists);
            }
            catch { return Path.Combine(SdoExtracted.Root, "UI", "MYHOUSEDLG"); }
        }

        private static Sprite DeMatte(Sprite s)
        {
            if (s != null && s.texture != null && _deMatted.Add(s.texture)) SdoExtracted.DeMatteWhite(s.texture);
            return s;
        }

        /// <summary>First frame of a MYHOUSEDLG .an as a sprite (cached). Loads onto its own texture (LoadAnSolo) to avoid
        /// the atlas neighbour white-fringe, falling back to the shared-atlas path.</summary>
        public static Sprite An(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            if (_cache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAnSolo(Dir, anName, pad: 0)
                ?? DeMatte(SdoExtracted.LoadAn1(Dir, anName, bleed: true));
            _cache[anName] = s;
            return s;
        }

        /// <summary>All frames of a MYHOUSEDLG .an (cached).</summary>
        public static Sprite[] AnFrames(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return new Sprite[0];
            if (_framesCache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAn(Dir, anName, bleed: true);
            foreach (var fr in s) DeMatte(fr);
            _framesCache[anName] = s;
            return s;
        }
    }
}
