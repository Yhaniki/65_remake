using System;
using System.Collections.Generic;
using System.IO;

namespace Sdo.Osu
{
    /// <summary>
    /// Scans user song folders — <c>Songs/&lt;group&gt;/&lt;songFolder&gt;/</c> (and each AdditionalSongFolders root,
    /// treated as another Songs root, i.e. a group-parent, exactly like StepMania) — for osu!mania and StepMania
    /// charts, producing <see cref="ExternalSong"/> records. Engine-free (System.IO only) so it stays in Sdo.Osu and
    /// is unit-testable; the Unity glue (register into the catalog, load textures) lives in Sdo.Game.ExternalSongLibrary.
    ///
    /// Per song folder: prefer .osu (a whole beatmap set = many .osu difficulties in one folder), else .sm. Only 4K
    /// difficulties are used (osu CircleSize==4 &amp; Mode==3; StepMania dance-single). The three highest note-count
    /// difficulties fill hard/normal/easy (<see cref="ExternalDifficultyPicker"/>). Missing dance choreography is fine —
    /// gameplay falls back to a generic dance.
    /// </summary>
    public static class ExternalSongScanner
    {
        public sealed class SongDir { public string Group = ""; public string Path = ""; }

        public sealed class ScanProgress { public int Done; public int Total; public string Current = ""; }

        private static readonly string[] AudioExt = { ".ogg", ".mp3", ".wav" };
        private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".bmp" };

        /// <summary>Flatten roots → the list of song folders to load (group = the folder one level under a root).
        /// Groups and songs are sorted alphabetically for a stable, readable browse order.</summary>
        public static List<SongDir> BuildWorklist(IReadOnlyList<string> roots)
        {
            var work = new List<SongDir>();
            if (roots == null) return work;
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string[] groups;
                try { if (!Directory.Exists(root)) continue; groups = Directory.GetDirectories(root); }
                catch { continue; }
                Array.Sort(groups, StringComparer.OrdinalIgnoreCase);
                foreach (var groupDir in groups)
                {
                    string groupName = new DirectoryInfo(groupDir).Name;
                    string[] songs;
                    try { songs = Directory.GetDirectories(groupDir); }
                    catch { continue; }
                    Array.Sort(songs, StringComparer.OrdinalIgnoreCase);
                    foreach (var songDir in songs)
                        work.Add(new SongDir { Group = groupName, Path = songDir });
                }
            }
            return work;
        }

        /// <summary>Non-incremental scan (used by tests / as a fallback). Prefer BuildWorklist + LoadOne when you
        /// want to yield between songs for a progress bar.</summary>
        public static List<ExternalSong> Scan(IReadOnlyList<string> roots, Action<ScanProgress> onProgress = null)
        {
            var work = BuildWorklist(roots);
            var songs = new List<ExternalSong>();
            var prog = new ScanProgress { Total = work.Count };
            for (int i = 0; i < work.Count; i++)
            {
                var song = LoadOne(work[i].Group, work[i].Path);
                if (song != null) songs.Add(song);
                prog.Done = i + 1;
                prog.Current = song?.Title ?? "";
                onProgress?.Invoke(prog);
            }
            return songs;
        }

        /// <summary>Load a single song folder → <see cref="ExternalSong"/>, or null if it holds no playable 4K chart.</summary>
        public static ExternalSong LoadOne(string group, string songDir)
        {
            string[] files;
            try { files = Directory.GetFiles(songDir); }
            catch { return null; }

            var osu = new List<string>();
            var sm = new List<string>();
            var audio = new List<string>();
            var images = new List<string>();   // basenames
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".osu") osu.Add(f);
                else if (ext == ".sm") sm.Add(f);
                else if (Array.IndexOf(AudioExt, ext) >= 0) audio.Add(f);
                else if (Array.IndexOf(ImageExt, ext) >= 0) images.Add(Path.GetFileName(f));
            }

            try
            {
                if (osu.Count > 0) return BuildOsu(group, songDir, osu, audio, images);
                if (sm.Count > 0) return BuildSm(group, songDir, sm[0], audio, images);
            }
            catch { /* a malformed folder must never abort the whole scan */ }
            return null;
        }

        private static ExternalSong BuildOsu(string group, string dir, List<string> osuFiles,
            List<string> audioFiles, List<string> images)
        {
            var candFile = new List<string>();
            var candMeta = new List<OsuMeta>();
            foreach (var path in osuFiles)
            {
                OsuMeta meta;
                try { meta = OsuBeatmapParser.ReadMeta(File.ReadAllText(path)); }
                catch { continue; }
                if (meta.Mode != 3) continue;                // mania only
                if (meta.Keys != 4) continue;                // 4K only
                if (meta.NoteCount <= 0) continue;
                candFile.Add(path); candMeta.Add(meta);
            }
            if (candFile.Count == 0) return null;

            var counts = new List<int>(candMeta.Count);
            foreach (var m in candMeta) counts.Add(m.NoteCount);
            var slots = ExternalDifficultyPicker.Assign(counts);

            var song = new ExternalSong { Group = group, FolderPath = dir, Format = SongFormat.Osu };
            for (int s = 0; s < 3; s++)
            {
                int c = slots[s];
                if (c < 0) continue;
                song.Charts[s] = new ExternalChart
                {
                    FilePath = candFile[c], ChartIndex = 0, NoteCount = candMeta[c].NoteCount,
                    Level = OsuLevel(candFile[c]),   // 星數×5 → 等級（只對選中的 3 張全解析，掃描才不慢）
                };
            }
            var lead = candMeta[slots[2] >= 0 ? slots[2] : 0];   // hardest chart's metadata
            song.Title = Coalesce(lead.Title, Path.GetFileNameWithoutExtension(candFile[0]));
            song.Artist = lead.Artist ?? "";
            song.Bpm = lead.Bpm;
            song.AudioPath = ResolveFile(dir, audioFiles, lead.AudioFilename);
            song.ImagePath = ResolveImage(dir, images, "", lead.BackgroundFilename, "");
            song.PreviewStartMs = lead.PreviewTime;   // osu PreviewTime (ms); -1 = none. osu carries no length → default.
            song.PreviewLengthMs = 0;
            return song;
        }

        private static ExternalSong BuildSm(string group, string dir, string smPath,
            List<string> audioFiles, List<string> images)
        {
            SmChart.SmSong smSong;
            try { smSong = SmChart.Parse(File.ReadAllText(smPath)); }
            catch { return null; }

            var candIndex = new List<int>();
            var candCount = new List<int>();
            var candLevel = new List<int>();
            for (int i = 0; i < smSong.Charts.Count; i++)
            {
                var ch = smSong.Charts[i];
                if (!SmChart.IsDanceSingle(ch)) continue;
                int notes = SmChart.NoteCount(ch.NoteData);
                if (notes <= 0) continue;
                candIndex.Add(i); candCount.Add(notes); candLevel.Add(ch.Meter);
            }
            if (candIndex.Count == 0) return null;

            var slots = ExternalDifficultyPicker.Assign(candCount);
            var song = new ExternalSong { Group = group, FolderPath = dir, Format = SongFormat.Sm };
            for (int s = 0; s < 3; s++)
            {
                int c = slots[s];
                if (c < 0) continue;
                song.Charts[s] = new ExternalChart
                {
                    FilePath = smPath, ChartIndex = candIndex[c], NoteCount = candCount[c],
                    Level = SmLevel(smSong, candIndex[c], candLevel[c]),   // 星數×5 → 等級（同 osu 一致的量尺）；失敗回退譜面 meter
                };
            }
            song.Title = Coalesce(smSong.Title, Path.GetFileNameWithoutExtension(smPath));
            song.Artist = smSong.Artist ?? "";
            song.Bpm = smSong.FirstBpm;
            song.AudioPath = ResolveFile(dir, audioFiles, smSong.Music);
            song.ImagePath = ResolveImage(dir, images, smSong.Banner, smSong.Background, smSong.CdTitle);
            song.PreviewStartMs = smSong.SampleStart >= 0 ? (int)System.Math.Round(smSong.SampleStart * 1000.0) : -1;
            song.PreviewLengthMs = smSong.SampleLength > 0 ? (int)System.Math.Round(smSong.SampleLength * 1000.0) : 0;
            return song;
        }

        // Level from osu!mania star rating (star × 5, clamped) — full-parse only the chosen chart. 0 on failure.
        private static int OsuLevel(string osuPath)
        {
            try { return ManiaStarRating.Level(OsuBeatmapParser.Parse(File.ReadAllText(osuPath))); }
            catch { return 0; }
        }

        // Level for a StepMania chart: convert to the osu representation and use the SAME star scale as osu; fall back
        // to the chart's own #NOTES meter if the conversion/rating fails.
        private static int SmLevel(SmChart.SmSong song, int blockIndex, int fallbackMeter)
        {
            try { return ManiaStarRating.Level(SmChart.ToBeatmap(song, blockIndex)); }
            catch { return fallbackMeter; }
        }

        // Resolve a named file to an absolute path: prefer the named one (case-insensitive basename match against the
        // folder's files); else the first available file of that kind.
        private static string ResolveFile(string dir, List<string> available, string named)
        {
            if (!string.IsNullOrEmpty(named))
            {
                string want = Path.GetFileName(named.Replace('\\', '/'));
                foreach (var f in available)
                    if (string.Equals(Path.GetFileName(f), want, StringComparison.OrdinalIgnoreCase)) return f;
            }
            return available.Count > 0 ? available[0] : "";
        }

        private static string ResolveImage(string dir, List<string> images, string banner, string background, string cdtitle)
        {
            string chosen = ExternalImagePicker.Pick(images, Base(banner), Base(background), Base(cdtitle));
            return string.IsNullOrEmpty(chosen) ? "" : Path.Combine(dir, chosen);
        }

        private static string Base(string tag)
            => string.IsNullOrEmpty(tag) ? "" : Path.GetFileName(tag.Replace('\\', '/'));

        private static string Coalesce(string a, string b) => !string.IsNullOrEmpty(a) ? a : (b ?? "");
    }
}
