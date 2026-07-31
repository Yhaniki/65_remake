using System;
using System.Collections.Generic;
using System.IO;
using Sdo.Osu;

namespace Sdo.Game
{
    /// <summary>
    /// Reading an external (user <c>Songs/</c>) chart file into an <see cref="OsuBeatmap"/> — one place, because two
    /// callers need the same four format branches: gameplay loads the difficulty being played
    /// (<c>ScreenGameplay.LoadChartRaw</c>), and <see cref="ExternalDps"/> measures ALL of a song's difficulties to
    /// decide how long its generated dance is.
    ///
    /// What comes back is the chart AS WRITTEN — no lead-in, no short-hold collapse, no bomb removal, no level/BPM
    /// patching. Those are gameplay's own post-steps and they depend on player settings; the dance must not.
    /// </summary>
    public static class ExternalChartIO
    {
        /// <summary>Parse one chart; throws on unreadable/malformed input.</summary>
        /// <param name="format">Sdo.Osu.SongFormat: 1=osu, 2=sm, 3=.gn 歌曲包, 4=Malody .mc.</param>
        /// <param name="index">.sm #NOTES block / .gn pack difficulty (osu and .mc: 0).</param>
        /// <param name="seed">.gn pack's own LCG key (0 = unknown → the shared pool).</param>
        public static OsuBeatmap Parse(int format, string path, int index, long seed)
        {
            if (format == 3)
                // .gn 歌曲包：一個檔裝三個難度，index 就是難度。金鑰優先用這首自己的，失敗才退回共用池。
                return GnChart.Load(File.ReadAllBytes(path), index, ScreenGameplay.GnSeedsFor(seed));
            if (format == 4)
                return MalodyChart.ToBeatmap(MalodyChart.Parse(File.ReadAllText(path)));   // .mc — one difficulty per file
            return format == 2
                ? SmChart.ToBeatmap(SmChart.Parse(File.ReadAllText(path)), index)          // .sm block
                : OsuBeatmapParser.Parse(File.ReadAllText(path));                          // .osu
        }

        /// <summary>Parse one chart; null when the file is missing or won't parse.</summary>
        public static OsuBeatmap TryParse(int format, string path, int index, long seed)
        {
            if (format == 0 || string.IsNullOrEmpty(path)) return null;
            try
            {
                if (!File.Exists(path)) return null;
                return Parse(format, path, index, seed);
            }
            catch (Exception ex)
            {
                SdoLog.Note("chart", "could not read " + path + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The note windows of every difficulty of one song — what <see cref="DanceInputs.UnionSeconds"/> turns into
        /// the dance's length. Empty slots ("" paths) and unreadable/noteless charts are skipped.
        ///
        /// A .sm block list and a .gn pack are ONE file holding all three difficulties, so the file is read once and
        /// re-sliced per index instead of three times.
        /// </summary>
        public static List<ChartWindow> Windows(int format, IReadOnlyList<string> paths, IReadOnlyList<int> indices, long seed)
        {
            var windows = new List<ChartWindow>();
            if (paths == null) return windows;

            var songs = new Dictionary<string, SmChart.SmSong>(StringComparer.OrdinalIgnoreCase);
            var packs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (string.IsNullOrEmpty(path)) continue;
                int index = indices != null && i < indices.Count ? indices[i] : 0;
                OsuBeatmap map = null;
                try
                {
                    if (!File.Exists(path)) continue;
                    if (format == 2)
                    {
                        if (!songs.TryGetValue(path, out var sm)) songs[path] = sm = SmChart.Parse(File.ReadAllText(path));
                        map = SmChart.ToBeatmap(sm, index);
                    }
                    else if (format == 3)
                    {
                        if (!packs.TryGetValue(path, out var bytes)) packs[path] = bytes = File.ReadAllBytes(path);
                        map = GnChart.Load(bytes, index, ScreenGameplay.GnSeedsFor(seed));
                    }
                    else map = Parse(format, path, index, seed);
                }
                catch (Exception ex) { SdoLog.Note("chart", "could not measure " + path + ": " + ex.Message); continue; }

                if (map != null && map.HitObjects.Count > 0) windows.Add(new ChartWindow(map.FirstNoteMs, map.LastNoteMs));
            }
            return windows;
        }
    }
}
