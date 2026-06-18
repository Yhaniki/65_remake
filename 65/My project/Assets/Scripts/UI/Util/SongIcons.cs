using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Best-effort song-icon loader. Resolves the ICONS folder by SCANNING assets/ for any subfolder
    /// containing DatasSDO/UI/MUSIC/ICONS (so the oddly-encoded asset folder name need not be hardcoded).
    /// Returns null when unavailable; callers fall back to a placeholder.
    /// </summary>
    public static class SongIcons
    {
        private static string _dir;
        private static bool _resolved;

        private static string Dir()
        {
            if (_resolved) return _dir;
            _resolved = true;
            try
            {
                // SdoExtracted.Root = .../assets/sdox_offline/Extracted  ->  assets/
                var assets = Path.GetDirectoryName(Path.GetDirectoryName(SdoExtracted.Root));
                if (assets != null && Directory.Exists(assets))
                {
                    foreach (var d in Directory.GetDirectories(assets))
                    {
                        var cand = Path.Combine(d, "DatasSDO", "UI", "MUSIC", "ICONS");
                        if (Directory.Exists(cand)) { _dir = cand; break; }
                    }
                }
            }
            catch { /* best effort */ }
            return _dir;
        }

        public static Sprite Load(int fileId)
        {
            var dir = Dir();
            if (dir == null) return null;
            return SdoExtracted.LoadImage(dir, fileId + ".PNG") ?? SdoExtracted.LoadImage(dir, fileId + ".png");
        }
    }
}
