using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Best-effort song-icon loader. Looks in two places, in order:
    ///   1. Root/UI/MUSIC/ICONS — the built layout, where packaging overlays the FULL online icon set into DATA;
    ///   2. the dev fallback: SCAN assets/ for any subfolder holding DatasSDO/UI/MUSIC/ICONS (the online client),
    ///      so the editor still shows the complete icon set without hardcoding the oddly-encoded folder name.
    /// Returns null when unavailable; callers fall back to a placeholder.
    /// </summary>
    public static class SongIcons
    {
        private static string[] _dirs;

        private static string[] Dirs()
        {
            if (_dirs != null) return _dirs;
            var list = new System.Collections.Generic.List<string>();
            try
            {
                // 1) built/overlaid icons under the data root.
                var inData = Path.Combine(SdoExtracted.Root, "UI", "MUSIC", "ICONS");
                if (Directory.Exists(inData)) list.Add(inData);

                // 2) dev fallback: scan assets/ siblings for the online DatasSDO icons.
                //    SdoExtracted.Root = .../assets/sdox_offline/Extracted  ->  assets/
                var assets = Path.GetDirectoryName(Path.GetDirectoryName(SdoExtracted.Root));
                if (assets != null && Directory.Exists(assets))
                {
                    foreach (var d in Directory.GetDirectories(assets))
                    {
                        var cand = Path.Combine(d, "DatasSDO", "UI", "MUSIC", "ICONS");
                        if (Directory.Exists(cand) && !list.Contains(cand)) list.Add(cand);
                    }
                }
            }
            catch { /* best effort */ }
            _dirs = list.ToArray();
            return _dirs;
        }

        public static Sprite Load(int fileId)
        {
            foreach (var dir in Dirs())
            {
                var s = SdoExtracted.LoadImage(dir, fileId + ".PNG") ?? SdoExtracted.LoadImage(dir, fileId + ".png");
                if (s != null) return s;
            }
            return null;
        }

        /// <summary>Load a named icon from the same ICONS folder (e.g. RANDOM.PNG / NONE.PNG). Null if unavailable.</summary>
        public static Sprite LoadNamed(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            foreach (var dir in Dirs())
            {
                var s = SdoExtracted.LoadImage(dir, fileName);
                if (s != null) return s;
            }
            return null;
        }
    }
}
