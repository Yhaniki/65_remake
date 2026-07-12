using System;
using System.Collections.Generic;

namespace Sdo.Osu
{
    /// <summary>
    /// Chooses ONE cover image for the song-select "CD" slot from a song folder, in priority order
    /// <b>jacket → banner → background</b> (per the feature spec). Explicit chart tags (StepMania #BANNER /
    /// #BACKGROUND, osu! [Events] background) win over filename guesses; a #CDTITLE-style small logo is never
    /// used as the tile. Pure/testable — takes the folder's image filenames + the explicit tag hints and returns
    /// the chosen filename ("" = none, caller shows the placeholder disc).
    /// </summary>
    public static class ExternalImagePicker
    {
        /// <param name="imageFiles">image filenames present in the folder (any case; jpg/png/…)</param>
        /// <param name="bannerTag">explicit banner filename from the chart, or ""</param>
        /// <param name="backgroundTag">explicit background filename from the chart / osu [Events], or ""</param>
        /// <param name="cdtitleTag">explicit cdtitle filename (excluded from the tile), or ""</param>
        public static string Pick(IReadOnlyList<string> imageFiles, string bannerTag, string backgroundTag, string cdtitleTag)
        {
            // 1) jacket — no format here carries a jacket tag, so match by filename hint only.
            var jacket = FirstHinted(imageFiles, cdtitleTag, "jacket", "cover", "cdimage", "disc");
            if (jacket != "") return jacket;

            // 2) banner — explicit tag first, else filename hint.
            var banner = Resolve(imageFiles, bannerTag, cdtitleTag, "banner", "bn");
            if (banner != "") return banner;

            // 3) background — explicit tag first, else filename hint.
            var background = Resolve(imageFiles, backgroundTag, cdtitleTag, "background", "bg");
            if (background != "") return background;

            // 4) last resort: any image that isn't the cdtitle logo.
            if (imageFiles != null)
                foreach (var f in imageFiles)
                    if (!string.IsNullOrEmpty(f) && !NameEq(f, cdtitleTag)) return f;

            return "";
        }

        // Explicit tag (if it names a file actually present) else a filename hint.
        private static string Resolve(IReadOnlyList<string> files, string tag, string cdtitleTag, params string[] hints)
        {
            if (!string.IsNullOrEmpty(tag) && Contains(files, tag)) return Match(files, tag);
            return FirstHinted(files, cdtitleTag, hints);
        }

        private static string FirstHinted(IReadOnlyList<string> files, string cdtitleTag, params string[] hints)
        {
            if (files == null) return "";
            foreach (var f in files)
            {
                if (string.IsNullOrEmpty(f) || NameEq(f, cdtitleTag)) continue;
                var lower = f.ToLowerInvariant();
                foreach (var h in hints)
                    if (lower.Contains(h)) return f;
            }
            return "";
        }

        private static bool Contains(IReadOnlyList<string> files, string name)
        {
            if (files == null) return false;
            foreach (var f in files) if (NameEq(f, name)) return true;
            return false;
        }

        private static string Match(IReadOnlyList<string> files, string name)
        {
            if (files != null) foreach (var f in files) if (NameEq(f, name)) return f;
            return name;
        }

        private static bool NameEq(string a, string b)
            => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
               string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
