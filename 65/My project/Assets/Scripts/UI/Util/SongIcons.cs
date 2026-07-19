using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Best-effort song-icon loader. Reads Root/UI/MUSIC/ICONS under <see cref="SdoExtracted.Root"/> ONLY — no assets/
    /// scan (the resolved data root, e.g. the pruned clean pack via data_root.txt, is the single icon source).
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
                // Icons under the resolved data root ONLY — no assets/ scan (data_root.txt points this at the clean pack).
                var inData = Path.Combine(SdoExtracted.Root, "UI", "MUSIC", "ICONS");
                if (Directory.Exists(inData)) list.Add(inData);
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
