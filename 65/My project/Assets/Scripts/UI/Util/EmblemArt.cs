using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Loads 家族徽章 (family emblem) art from DATA/EMBLEM. Each emblem is a bare 24×24 RGBA PNG (SMALL0..49.PNG); the
    /// sibling .an is just a filename pointer, so we load the PNG directly via <see cref="SdoExtracted.LoadImage"/>
    /// (no crop/atlas). Shown in front of the family name on the room's floating nameplate (RoomScreen), sized down by
    /// the caller. Cached; returns null for a blank/missing name so the caller just hides the emblem.
    /// </summary>
    public static class EmblemArt
    {
        private static string _dir;
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        /// <summary>Resolved EMBLEM folder (lazy) — DATA/EMBLEM under the resolved data root. Settable for tests.</summary>
        public static string Dir
        {
            get { return _dir ?? (_dir = Path.Combine(SdoExtracted.Root, "EMBLEM")); }
            set { _dir = value; _cache.Clear(); }
        }

        /// <summary>Emblem sprite for a config value (e.g. "SMALL43"); null if blank or the file is missing. Cached.
        /// bleed:true dilates the transparent-white matte so bilinear filtering can't drag a white halo into the edge.</summary>
        public static Sprite Emblem(string name)
        {
            string file = FileName(name);
            if (string.IsNullOrEmpty(file)) return null;
            if (_cache.TryGetValue(file, out var s) && s != null) return s;
            s = SdoExtracted.LoadImage(Dir, file, bleed: true);
            _cache[file] = s;
            return s;
        }

        /// <summary>Normalise a config emblem value to a PNG filename under EMBLEM. Blank → "". Accepts "SMALL43",
        /// "small43", a bare number "43" (→ SMALL43), or a name already ending in an image extension (used as-is).
        /// Pure — unit-tested.</summary>
        public static string FileName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "";
            if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                return name;
            bool allDigits = true;
            foreach (var c in name) if (c < '0' || c > '9') { allDigits = false; break; }
            if (allDigits) name = "SMALL" + name;
            return name + ".PNG";
        }
    }
}
