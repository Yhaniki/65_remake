using System;
using System.Globalization;

namespace Sdo.Game
{
    /// <summary>
    /// Screenshot filename rules (pure / Unity-free so the format is unit-tested). Shots are timestamped
    /// yyyyMMdd_HHmmss.jpg — e.g. 20260409_120711.jpg — matching the original client's screensave naming.
    /// </summary>
    public static class ScreenshotNaming
    {
        private const string Stamp = "yyyyMMdd_HHmmss";

        /// <summary>Base name for a shot taken at <paramref name="when"/>, e.g. 20260409_120711.jpg.</summary>
        public static string FileName(DateTime when) =>
            when.ToString(Stamp, CultureInfo.InvariantCulture) + ".jpg";

        /// <summary>
        /// Same-second collision guard: returns <see cref="FileName"/>, or the first free "…_2 / _3 …" variant when
        /// that name is already taken (two shots within one second). <paramref name="exists"/> reports whether a
        /// candidate name already exists in the target folder.
        /// </summary>
        public static string UniqueFileName(DateTime when, Func<string, bool> exists)
        {
            var baseName = FileName(when);
            if (exists == null || !exists(baseName)) return baseName;

            var stem = when.ToString(Stamp, CultureInfo.InvariantCulture);
            for (int n = 2; ; n++)
            {
                var cand = stem + "_" + n + ".jpg";
                if (!exists(cand)) return cand;
            }
        }
    }
}
