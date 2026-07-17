using System;
using System.Collections.Generic;
using System.IO;

namespace Sdo.Osu
{
    /// <summary>
    /// Scans user song folders — <c>Songs/&lt;group&gt;/…</c> (and each AdditionalSongFolders root, treated as another
    /// Songs root, i.e. a group-parent, exactly like StepMania) — for osu!mania and StepMania charts, producing
    /// <see cref="ExternalSong"/> records. Engine-free (System.IO only) so it stays in Sdo.Osu and is unit-testable;
    /// the Unity glue (register into the catalog, load textures) lives in Sdo.Game.ExternalSongLibrary.
    ///
    /// A song folder is the FIRST folder holding chart files on the way down from a group — so a pack downloaded as one
    /// more folder level is still found, while a song folder's own subfolders (a StepMania editor's FileBackup/, an osu
    /// storyboard dir) stay assets rather than becoming songs. One such folder may hold MORE THAN ONE song: several
    /// beatmap sets dropped in flat, or several .sm files. <see cref="ExternalSongGrouper"/> splits them by audio file;
    /// each song then fills its own hard/normal/easy slots from ITS three highest-note-count 4K difficulties
    /// (<see cref="ExternalDifficultyPicker"/>). Only 4K is used (osu CircleSize==4 &amp; Mode==3; StepMania
    /// dance-single), and only songs whose audio file is actually present. Missing dance choreography is fine —
    /// gameplay falls back to a generic dance.
    /// </summary>
    public static class ExternalSongScanner
    {
        public sealed class SongDir { public string Group = ""; public string Path = ""; }

        public sealed class ScanProgress { public int Done; public int Total; public string Current = ""; }

        private static readonly string[] AudioExt = { ".ogg", ".mp3", ".wav" };
        private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".bmp" };

        /// <summary>How deep below a group folder we still look for charts. Packs nest one or two levels; the cap is
        /// there so a mis-configured root (e.g. C:\) can't walk the world.</summary>
        private const int MaxDepth = 8;

        /// <summary>Flatten roots → the list of song folders to load. The group is the folder one level under a root
        /// (the browse tab); chart folders nested deeper inside it still belong to that group. Groups and folders are
        /// walked in alphabetical order for a stable, readable browse order.</summary>
        public static List<SongDir> BuildWorklist(IReadOnlyList<string> roots)
        {
            var work = new List<SongDir>();
            if (roots == null) return work;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // junction/symlink loop guard
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                try { if (!Directory.Exists(root)) continue; } catch { continue; }

                // Charts dropped straight into a root (no group folder) still count — the root names their group.
                Probe(root, out bool rootCharts, out _);
                if (rootCharts) work.Add(new SongDir { Group = DirName(root), Path = root });

                string[] groups;
                try { groups = Directory.GetDirectories(root); } catch { continue; }
                Array.Sort(groups, StringComparer.OrdinalIgnoreCase);
                foreach (var groupDir in groups)
                    Collect(work, groupDir, DirName(groupDir), 0, visited);
            }
            return work;
        }

        private static void Collect(List<SongDir> work, string dir, string group, int depth, HashSet<string> visited)
        {
            string full;
            try { full = Path.GetFullPath(dir); } catch { return; }
            if (!visited.Add(full)) return;

            Probe(dir, out bool isSong, out _);
            if (isSong) work.Add(new SongDir { Group = group, Path = dir });
            if (depth >= MaxDepth) return;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { return; }
            Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
            foreach (var sub in subs)
            {
                // Inside a song folder, subfolders are its assets — a StepMania editor's FileBackup/ (dozens of
                // autosaved .sm), an osu storyboard dir — so only descend into one that is a song in its own right:
                // charts AND its own audio. Outside a song folder we always keep walking, because a pack is just one
                // more folder level, and one stray chart lying at pack level must not hide the songs beneath it.
                if (isSong)
                {
                    Probe(sub, out bool subCharts, out bool subAudio);
                    if (!subCharts || !subAudio) continue;
                }
                Collect(work, sub, group, depth + 1, visited);
            }
        }

        /// <summary>Does this folder hold chart files / audio files? One enumeration, stops as soon as both are known
        /// (the whole tree is walked before boot can show a determinate progress bar, so this stays cheap).</summary>
        private static void Probe(string dir, out bool charts, out bool audio)
        {
            charts = false; audio = false;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".osu" || ext == ".sm") charts = true;
                    else if (Array.IndexOf(AudioExt, ext) >= 0) audio = true;
                    if (charts && audio) return;
                }
            }
            catch { /* unreadable folder → not a song folder */ }
        }

        private static string DirName(string dir)
        {
            try { return new DirectoryInfo(dir).Name; } catch { return ""; }
        }

        /// <summary>Non-incremental scan (fallback / harness). Prefer BuildWorklist + LoadFolder when you want to
        /// yield between folders for a progress bar.</summary>
        public static List<ExternalSong> Scan(IReadOnlyList<string> roots, Action<ScanProgress> onProgress = null)
        {
            var work = BuildWorklist(roots);
            var songs = new List<ExternalSong>();
            var prog = new ScanProgress { Total = work.Count };
            for (int i = 0; i < work.Count; i++)
            {
                var found = LoadFolder(work[i].Group, work[i].Path);
                songs.AddRange(found);
                prog.Done = i + 1;
                prog.Current = found.Count > 0 ? found[0].Title : "";
                onProgress?.Invoke(prog);
            }
            return songs;
        }

        /// <summary>Load one folder → every playable 4K song in it (empty list when it holds none).</summary>
        public static List<ExternalSong> LoadFolder(string group, string songDir)
        {
            var songs = new List<ExternalSong>();
            string[] files;
            try { files = Directory.GetFiles(songDir); } catch { return songs; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);   // deterministic: grouping must not ride on FS order

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

            var drafts = new List<Draft>();
            try
            {
                DraftOsu(drafts, osu);
                DraftSm(drafts, sm);
            }
            catch { /* a malformed folder must never abort the whole scan */ }
            if (drafts.Count == 0) return songs;

            // Songs are the drafts whose audio we can actually find: a chart with no music is unplayable, and dropping
            // it is what keeps chart-only junk (an editor's autosaves, an orphaned .sm) out of the song list. Guessing
            // "the folder's first audio file" is only safe when the folder holds ONE song — with several it would hand
            // song B the music of song A, and silence beats playing the wrong track.
            var kept = new List<Draft>(drafts.Count);
            var tracks = new List<string>(drafts.Count);
            foreach (var d in drafts)
            {
                string audioPath = ResolveAudio(audio, d.AudioName);
                if (audioPath.Length == 0) continue;
                kept.Add(d); tracks.Add(audioPath);
            }
            if (kept.Count == 0 && drafts.Count == 1 && audio.Count > 0)
            {
                kept.Add(drafts[0]); tracks.Add(audio[0]);   // sole song whose chart names its audio wrongly
            }
            if (kept.Count == 0) return songs;

            bool sole = kept.Count == 1;
            // A folder that holds SEVERAL songs (several osu sets / several .sm dropped in flat) is its OWN pack: name
            // it after the folder rather than letting its songs dissolve into the parent group's flat song list. A
            // single-song folder keeps the group it was found under (the pack it sits in). DirName("") never fires
            // because a multi-song folder always has a name.
            string packGroup = sole ? group : DirName(songDir);
            if (string.IsNullOrEmpty(packGroup)) packGroup = group;

            var claimed = ClaimedImages(kept);
            var sidecar = ReadSidecar(songDir);
            RemoveGeneratedCds(images, kept, sole, sidecar);
            for (int i = 0; i < kept.Count; i++)
                songs.Add(Materialize(kept[i], packGroup, songDir, tracks[i], images, sole, claimed, sidecar));

            Disambiguate(songs, kept);
            return songs;
        }

        // The discs we generate are written INTO the song folder, so on the next scan they are just more images sitting
        // next to the cover — and a folder whose cover carries no filename hint would hand the picker its own disc back
        // (a disc composed from a disc). Take every name the sidecar records as a CD, plus the names this folder's songs
        // would generate, out of the cover pool.
        private static void RemoveGeneratedCds(List<string> images, List<Draft> kept, bool sole,
            List<SongSidecarEntry> sidecar)
        {
            var cds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in sidecar)
                if (e != null && !string.IsNullOrEmpty(e.CdImage)) cds.Add(e.CdImage);
            foreach (var d in kept) cds.Add(SongSidecar.CdFileName(sole ? "" : d.Key));
            images.RemoveAll(f => cds.Contains(f));
        }

        /// <summary>The folder's sdo.header, or an empty list when it has none / is unreadable.</summary>
        private static List<SongSidecarEntry> ReadSidecar(string songDir)
        {
            try
            {
                var path = Path.Combine(songDir, SongSidecar.FileName);
                if (File.Exists(path)) return SongSidecar.Parse(File.ReadAllText(path));
            }
            catch { /* a corrupt sidecar just means "nothing recorded yet" */ }
            return new List<SongSidecarEntry>();
        }

        /// <summary>Kept for callers that only want the folder's first song (e.g. a preview probe).</summary>
        public static ExternalSong LoadOne(string group, string songDir)
        {
            var songs = LoadFolder(group, songDir);
            return songs.Count > 0 ? songs[0] : null;
        }

        // ---- drafting: everything decided from the chart files alone, before folder-wide resolution ----

        /// <summary>One song, still unresolved against the folder's audio/image files.</summary>
        private sealed class Draft
        {
            public string Key = "";
            public SongFormat Format;
            public readonly ExternalChart[] Charts = new ExternalChart[3];
            public string Title = "", Artist = "", Version = "";
            public double Bpm;
            public int PreviewStartMs = -1, PreviewLengthMs;
            public string AudioName = "", BannerName = "", BackgroundName = "", CdTitleName = "";
        }

        private static void DraftOsu(List<Draft> drafts, List<string> osuFiles)
        {
            var candFile = new List<string>();
            var candMeta = new List<OsuMeta>();
            var candName = new List<string>();
            foreach (var path in osuFiles)
            {
                OsuMeta meta;
                try { meta = OsuBeatmapParser.ReadMeta(File.ReadAllText(path)); }
                catch { continue; }
                if (meta.Mode != 3) continue;                // mania only
                if (meta.Keys != 4) continue;                // 4K only
                if (meta.NoteCount <= 0) continue;
                candFile.Add(path); candMeta.Add(meta); candName.Add(Path.GetFileName(path));
            }
            if (candFile.Count == 0) return;

            foreach (var g in ExternalSongGrouper.GroupOsu(candMeta, candName))
            {
                var counts = new List<int>(g.Charts.Count);
                foreach (var i in g.Charts) counts.Add(candMeta[i].NoteCount);

                var d = new Draft { Key = g.Key, Format = SongFormat.Osu };
                var slots = ExternalDifficultyPicker.Assign(counts);   // per SONG — never across the folder
                for (int s = 0; s < 3; s++)
                {
                    int c = slots[s];
                    if (c < 0) continue;
                    int i = g.Charts[c];
                    var st = OsuStats(candFile[i]);   // 星數×5 → 等級 + 譜長（只對選中的 3 張全解析，掃描才不慢）
                    d.Charts[s] = new ExternalChart
                    {
                        FilePath = candFile[i], ChartIndex = 0, NoteCount = candMeta[i].NoteCount,
                        Level = st.Level, DurationSec = st.DurationSec,
                    };
                }

                // Display fields: hardest chart first, but a single chart often leaves a field blank (converters emit
                // empty artists, PreviewTime:-1, an empty [Events] bg) — so take the first chart that HAS each field.
                int lead = g.Charts[0];
                d.Title = First(g.Charts, i => candMeta[i].Title, Path.GetFileNameWithoutExtension(candFile[lead]));
                d.Artist = First(g.Charts, i => candMeta[i].Artist, "");
                d.Version = First(g.Charts, i => candMeta[i].Version, "");
                // Basenamed like the grouping key and like the .sm branch: a chart may spell its audio/background with
                // a folder prefix or backslashes, and the folder's files are matched by filename.
                d.AudioName = ExternalSongGrouper.BaseName(First(g.Charts, i => candMeta[i].AudioFilename, ""));
                d.BackgroundName = ExternalSongGrouper.BaseName(First(g.Charts, i => candMeta[i].BackgroundFilename, ""));
                foreach (var i in g.Charts) { if (candMeta[i].Bpm > 0) { d.Bpm = candMeta[i].Bpm; break; } }
                d.PreviewStartMs = -1;
                foreach (var i in g.Charts) { if (candMeta[i].PreviewTime >= 0) { d.PreviewStartMs = candMeta[i].PreviewTime; break; } }
                d.PreviewLengthMs = 0;   // osu carries no preview length → default window
                drafts.Add(d);
            }
        }

        private static void DraftSm(List<Draft> drafts, List<string> smFiles)
        {
            foreach (var smPath in smFiles)
            {
                SmChart.SmSong smSong;
                try { smSong = SmChart.Parse(File.ReadAllText(smPath)); }
                catch { continue; }

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
                if (candIndex.Count == 0) continue;

                string audioName = ExternalSongGrouper.BaseName(smSong.Music);
                // The same song shipped as both .osu and .sm (same audio) stays ONE song — the .osu wins, as before.
                if (audioName.Length > 0 && HasAudio(drafts, audioName)) continue;

                var d = new Draft { Key = ExternalSongGrouper.SmKeyOf(Path.GetFileName(smPath)), Format = SongFormat.Sm };
                var slots = ExternalDifficultyPicker.Assign(candCount);
                for (int s = 0; s < 3; s++)
                {
                    int c = slots[s];
                    if (c < 0) continue;
                    var st = SmStats(smSong, candIndex[c], candLevel[c]);   // 同 osu 一致的星數量尺；失敗回退譜面 meter
                    d.Charts[s] = new ExternalChart
                    {
                        FilePath = smPath, ChartIndex = candIndex[c], NoteCount = candCount[c],
                        Level = st.Level, DurationSec = st.DurationSec,
                    };
                }
                d.Title = Coalesce(smSong.Title, Path.GetFileNameWithoutExtension(smPath));
                d.Artist = smSong.Artist ?? "";
                d.Version = "";
                d.Bpm = smSong.FirstBpm;
                d.AudioName = audioName;
                d.BannerName = ExternalSongGrouper.BaseName(smSong.Banner);
                d.BackgroundName = ExternalSongGrouper.BaseName(smSong.Background);
                d.CdTitleName = ExternalSongGrouper.BaseName(smSong.CdTitle);
                d.PreviewStartMs = smSong.SampleStart >= 0 ? (int)Math.Round(smSong.SampleStart * 1000.0) : -1;
                d.PreviewLengthMs = smSong.SampleLength > 0 ? (int)Math.Round(smSong.SampleLength * 1000.0) : 0;
                drafts.Add(d);
            }
        }

        private static bool HasAudio(List<Draft> drafts, string audioName)
        {
            foreach (var d in drafts)
                if (string.Equals(d.AudioName, audioName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---- resolution against the folder's files ----

        private static ExternalSong Materialize(Draft d, string group, string dir, string audioPath,
            List<string> images, bool sole, HashSet<string> claimed, List<SongSidecarEntry> sidecar)
        {
            var song = new ExternalSong
            {
                Group = group,
                FolderPath = dir,
                SongKey = sole ? "" : d.Key,   // sole song → "" keeps the folder-only gn (and its favourites)
                Format = d.Format,
                Title = d.Title,
                Artist = d.Artist,
                Bpm = d.Bpm,
                PreviewStartMs = d.PreviewStartMs,
                PreviewLengthMs = d.PreviewLengthMs,
                AudioPath = audioPath,
                AudioDurationSec = AudioDuration.Seconds(audioPath),   // 時間欄用「音樂檔長度」，不是譜尾
                ImagePath = ResolveImage(dir, ImagePool(images, d, sole, claimed), d),
            };
            for (int s = 0; s < 3; s++) song.Charts[s] = d.Charts[s];
            ApplySidecar(song, SongSidecar.Find(sidecar, song.SongKey), dir);
            return song;
        }

        // What the folder's sdo.header already records for this song: the CD disc built on an earlier run (skip
        // composing it again) and — reserved — its dance/camera files. A recorded name whose file is gone is ignored,
        // so deleting cd.png is all it takes to have the disc rebuilt.
        private static void ApplySidecar(ExternalSong song, SongSidecarEntry e, string dir)
        {
            if (e == null) return;
            song.CdImagePath = SidecarFile(dir, e.CdImage);
            song.MotPath = SidecarFile(dir, e.Mot);
            song.CameraPath = SidecarFile(dir, e.Camera);
        }

        private static string SidecarFile(string dir, string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            try
            {
                var path = Path.Combine(dir, name);
                return File.Exists(path) ? path : "";
            }
            catch { return ""; }
        }

        /// <summary>The audio file a chart names, matched case-insensitively against the folder ("" = not there).</summary>
        private static string ResolveAudio(List<string> available, string named)
        {
            if (!string.IsNullOrEmpty(named))
                foreach (var f in available)
                    if (string.Equals(Path.GetFileName(f), named, StringComparison.OrdinalIgnoreCase)) return f;
            return "";
        }

        // Images another song in this folder explicitly claimed (its banner/background) are off-limits to the rest —
        // otherwise ExternalImagePicker's "any image left" rule hands every song the same cover.
        private static HashSet<string> ClaimedImages(List<Draft> drafts)
        {
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in drafts)
            {
                if (d.BannerName.Length > 0) claimed.Add(d.BannerName);
                if (d.BackgroundName.Length > 0) claimed.Add(d.BackgroundName);
            }
            return claimed;
        }

        private static List<string> ImagePool(List<string> images, Draft d, bool sole, HashSet<string> claimed)
        {
            if (sole || claimed.Count == 0) return images;
            var pool = new List<string>(images.Count);
            foreach (var f in images)
                if (!claimed.Contains(f) || NameEq(f, d.BannerName) || NameEq(f, d.BackgroundName)) pool.Add(f);
            return pool;
        }

        private static string ResolveImage(string dir, List<string> pool, Draft d)
        {
            string chosen = ExternalImagePicker.Pick(pool, d.BannerName, d.BackgroundName, d.CdTitleName);
            return string.IsNullOrEmpty(chosen) ? "" : Path.Combine(dir, chosen);
        }

        // Two songs in one folder routinely share a title (a sped-up edit is a second audio file with the same name
        // on it). Tag the clashing ones with their difficulty/audio so the song list can tell them apart.
        private static void Disambiguate(List<ExternalSong> songs, List<Draft> drafts)
        {
            if (songs.Count < 2) return;
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in songs)
            {
                seen.TryGetValue(s.Title, out int n);
                seen[s.Title] = n + 1;
            }
            for (int i = 0; i < songs.Count; i++)
            {
                if (!seen.TryGetValue(songs[i].Title, out int n) || n < 2) continue;
                string tag = drafts[i].Version;
                if (string.IsNullOrEmpty(tag)) tag = Path.GetFileNameWithoutExtension(drafts[i].AudioName);
                if (!string.IsNullOrEmpty(tag)) songs[i].Title += " (" + tag + ")";
            }
        }

        /// <summary>Level + play time of ONE chart. Both come from the same full parse — the star rating already needs
        /// it, so the 時間 column costs nothing extra (the scan still only full-parses the 3 chosen charts).</summary>
        public struct ChartStats { public int Level; public int DurationSec; }

        // Level from osu!mania star rating (star × 5, clamped). Level 0 / no duration on failure.
        private static ChartStats OsuStats(string osuPath)
        {
            try { return StatsOf(OsuBeatmapParser.Parse(File.ReadAllText(osuPath)), 0); }
            catch { return new ChartStats { Level = 0 }; }
        }

        // Same for a StepMania chart: convert to the osu representation and use the SAME star scale as osu; fall back
        // to the chart's own #NOTES meter if the conversion/rating fails.
        private static ChartStats SmStats(SmChart.SmSong song, int blockIndex, int fallbackMeter)
        {
            try { return StatsOf(SmChart.ToBeatmap(song, blockIndex), fallbackMeter); }
            catch { return new ChartStats { Level = fallbackMeter }; }
        }

        /// <summary>Rating + duration of a parsed chart. Duration = the last note's time (hold ends included), which is
        /// the same measure the official catalog's dur* carries — so both kinds of song read alike in the list.</summary>
        public static ChartStats StatsOf(OsuBeatmap beatmap, int fallbackLevel)
        {
            if (beatmap == null) return new ChartStats { Level = fallbackLevel };
            return new ChartStats
            {
                Level = ManiaStarRating.Level(beatmap),
                DurationSec = (int)Math.Round(Math.Max(0.0, beatmap.LastNoteMs) / 1000.0),
            };
        }

        private static string First(List<int> charts, Func<int, string> field, string fallback)
        {
            foreach (var i in charts)
            {
                var v = field(i);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return fallback ?? "";
        }

        private static bool NameEq(string a, string b)
            => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
               string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string Coalesce(string a, string b) => !string.IsNullOrEmpty(a) ? a : (b ?? "");
    }
}
