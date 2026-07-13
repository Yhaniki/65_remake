using System;
using System.Collections.Generic;
using System.Globalization;

namespace Sdo.Osu
{
    /// <summary>
    /// Splits the charts found in ONE folder into songs. A folder is not always one song: people drop several
    /// beatmap sets in flat next to each other, and StepMania packs put several .sm files side by side.
    ///
    /// The grouping key is the AUDIO FILE, because that is a hard constraint of the engine, not a preference:
    /// <see cref="ExternalSong"/> carries a single AudioPath and a single preview window, so two charts pointing at
    /// different audio files can never be the same song — merge them and one of the two plays against the wrong music.
    /// Fallbacks only matter when a chart names no audio at all:
    ///   1. <c>audio:&lt;filename&gt;</c> — [General] AudioFilename (every .osu has it; matches the engine's constraint)
    ///   2. <c>set:&lt;id&gt;</c>        — [Metadata] BeatmapSetID, only when &gt; 0 (converted/local charts are −1)
    ///   3. <c>meta:&lt;artist&gt;|&lt;title&gt;</c> — when either is non-empty
    ///   4. <c>file:&lt;chart filename&gt;</c> — last resort: rather split one song in two than merge two songs into one
    /// Keys are content-derived (never scan order) because the catalog gn — and therefore favourites — hashes them.
    ///
    /// Pure/testable: metadata in, index groups out.
    /// </summary>
    public static class ExternalSongGrouper
    {
        /// <summary>One song's worth of charts inside a folder.</summary>
        public sealed class SongGroup
        {
            public string Key = "";
            /// <summary>Indices into the caller's candidate lists, hardest (most notes) first.</summary>
            public readonly List<int> Charts = new List<int>();
        }

        /// <summary>Group osu candidates into songs. Groups come out in first-appearance order and their charts are
        /// sorted by note count DESC (ties keep input order), which is the order the difficulty slots and the display
        /// metadata are taken in.</summary>
        public static List<SongGroup> GroupOsu(IReadOnlyList<OsuMeta> metas, IReadOnlyList<string> chartFileNames)
        {
            var groups = new List<SongGroup>();
            if (metas == null) return groups;

            var byKey = new Dictionary<string, SongGroup>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < metas.Count; i++)
            {
                string file = chartFileNames != null && i < chartFileNames.Count ? chartFileNames[i] : "";
                string key = KeyOf(metas[i], file);
                if (!byKey.TryGetValue(key, out var g))
                {
                    g = new SongGroup { Key = key };
                    byKey[key] = g;
                    groups.Add(g);
                }
                g.Charts.Add(i);
            }

            foreach (var g in groups)
                g.Charts.Sort((a, b) =>
                {
                    int c = metas[b].NoteCount.CompareTo(metas[a].NoteCount);
                    return c != 0 ? c : a.CompareTo(b);
                });
            return groups;
        }

        /// <summary>The song identity of one .osu inside its folder (see the fallback chain in the class doc).</summary>
        public static string KeyOf(OsuMeta m, string chartFileName)
        {
            if (m != null)
            {
                string audio = BaseName(m.AudioFilename);
                if (audio.Length > 0) return "audio:" + audio.ToLowerInvariant();

                if (m.BeatmapSetId > 0) return "set:" + m.BeatmapSetId.ToString(CultureInfo.InvariantCulture);

                string artist = (m.Artist ?? "").Trim();
                string title = (m.Title ?? "").Trim();
                if (artist.Length > 0 || title.Length > 0)
                    return "meta:" + artist.ToLowerInvariant() + "|" + title.ToLowerInvariant();
            }
            return "file:" + BaseName(chartFileName).ToLowerInvariant();
        }

        /// <summary>Song identity of a .sm file: one file = one song, so the filename IS the key.</summary>
        public static string SmKeyOf(string smFileName) => "file:" + BaseName(smFileName).ToLowerInvariant();

        /// <summary>The audio a group plays, as a bare lower-case filename ("" = the charts name none). Used to let
        /// an .osu song shadow the .sm of the same song sitting in the same folder.</summary>
        public static string AudioNameOf(string key)
            => key != null && key.StartsWith("audio:", StringComparison.Ordinal) ? key.Substring(6) : "";

        /// <summary>Filename part of a chart-declared path (osu/StepMania tags may carry folders and backslashes).</summary>
        public static string BaseName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string s = path.Replace('\\', '/').Trim();
            int slash = s.LastIndexOf('/');
            if (slash >= 0) s = s.Substring(slash + 1);
            return s.Trim();
        }
    }
}
