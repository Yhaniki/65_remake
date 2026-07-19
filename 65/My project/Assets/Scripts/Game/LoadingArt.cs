using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Original loading-screen art (DATA/LOADING): full-screen tip backgrounds LOADING_1..N.PNG (800×600)
    /// shown behind everything while a screen boots, plus a small "Loading..." badge LOADINGS_1..M.PNG (158×81) for the
    /// bottom-right corner. Both are picked at RANDOM on each show.
    ///
    /// Folder resolution reads DATA/LOADING under <see cref="SdoExtracted.Root"/> ONLY — no assets/ scan (kept here in
    /// Sdo.Game so gameplay can use it without depending on the UI assembly). Returns null when the art is absent;
    /// callers fall back (ScreenGameplay → plain black).
    /// </summary>
    public static class LoadingArt
    {
        public const float BadgeW = 158f, BadgeH = 81f;   // LOADINGS_*.PNG native size (for corner placement)

        private static string _dir;
        private static string[] _backgrounds, _badges;

        /// <summary>Resolved LOADING folder (lazy). Settable for tests/overrides.</summary>
        public static string Dir
        {
            get { return _dir ?? (_dir = ResolveDir()); }
            set { _dir = value; _backgrounds = _badges = null; }
        }

        private static string ResolveDir()
        {
            try
            {
                // Use the resolved data root ONLY — no assets/ scan (data_root.txt points this at the clean pack).
                return Path.Combine(SdoExtracted.Root, "LOADING");
            }
            catch { return Path.Combine(SdoExtracted.Root, "LOADING"); }
        }

        /// <summary>Full-screen background files (LOADING_*.PNG, excludes the LOADINGS_ badges).</summary>
        public static string[] Backgrounds { get { EnsureLists(); return _backgrounds; } }
        /// <summary>Corner badge files (LOADINGS_*.PNG).</summary>
        public static string[] Badges { get { EnsureLists(); return _badges; } }

        private static void EnsureLists()
        {
            if (_backgrounds != null) return;
            var bg = new List<string>(); var bd = new List<string>();
            try
            {
                if (Directory.Exists(Dir))
                    foreach (var f in Directory.GetFiles(Dir, "*.png"))
                    {
                        // "LOADINGS_*" is the badge; "LOADING_*" (but not LOADINGS_) is the full-screen tip. Filter in
                        // C# (not a glob) so LOADING_* can't accidentally swallow LOADINGS_* on case-insensitive FSes.
                        var n = Path.GetFileName(f).ToUpperInvariant();
                        if (n.StartsWith("LOADINGS_")) bd.Add(f);
                        else if (n.StartsWith("LOADING_")) bg.Add(f);
                    }
            }
            catch { }
            _backgrounds = bg.ToArray(); _badges = bd.ToArray();
        }

        /// <summary>A random full-screen loading background sprite (null if none found).</summary>
        public static Sprite RandomBackground() => RandomOf(Backgrounds, bleed: false);
        /// <summary>A random "Loading..." corner-badge sprite (null if none found).</summary>
        public static Sprite RandomBadge() => RandomOf(Badges, bleed: true);   // bleed: the badge has a transparent matte

        private static Sprite RandomOf(string[] files, bool bleed)
        {
            if (files == null || files.Length == 0) return null;
            var path = files[Random.Range(0, files.Length)];
            return SdoExtracted.LoadImage(Path.GetDirectoryName(path), Path.GetFileName(path), bleed);
        }
    }
}
