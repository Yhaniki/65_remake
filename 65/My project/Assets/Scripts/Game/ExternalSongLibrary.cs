using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Sdo.Osu;
using Sdo.Settings;

namespace Sdo.Game
{
    /// <summary>
    /// Unity glue that drives <see cref="ExternalSongScanner"/> at boot and merges the discovered osu!/StepMania
    /// songs into <see cref="SongCatalog"/> as <see cref="SongCatalog.Entry"/> rows (marked <c>external</c>). Runs as
    /// a coroutine so the boot progress bar can advance per song (the scan reads + note-counts every candidate chart,
    /// which is the slow part of startup for large libraries). Roots = the exe-sibling <c>Songs/</c> plus every
    /// <c>AdditionalSongFolders</c> entry from config.ini.
    ///
    /// One folder may yield SEVERAL songs (several beatmap sets dropped in flat, several .sm files) — each becomes its
    /// own Entry, told apart by <see cref="ExternalSong.SongKey"/>.
    ///
    /// Each external Entry gets a synthetic, content-stable gn (<c>ext_&lt;hash&gt;k.gn</c>) so it survives SongListModel
    /// curation, and a NEGATIVE fileId so it never collects a NEW badge, never appears in 最新, and never hits the
    /// fileId-keyed jacket loader (the disc uses <c>imagePath</c> instead). Gameplay resolves the actual chart/audio
    /// from the Entry's external fields (see FrontendApp.StartGameplay / ScreenGameplay.LoadChart).
    /// </summary>
    public static class ExternalSongLibrary
    {
        /// <summary>Song-folder roots to scan: exe-sibling Songs/ first, then config's AdditionalSongFolders
        /// (existing directories only, de-duplicated).</summary>
        public static List<string> Roots()
        {
            var roots = new List<string>();
            Add(roots, SdoExtracted.SongsDir);
            if (RoomConfig.additionalSongFolders != null)
                foreach (var f in RoomConfig.additionalSongFolders) Add(roots, f);
            return roots;
        }

        private static void Add(List<string> roots, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            string full;
            try { full = Path.GetFullPath(dir); } catch { return; }
            try { if (!Directory.Exists(full)) return; } catch { return; }
            foreach (var r in roots) if (string.Equals(r, full, StringComparison.OrdinalIgnoreCase)) return;
            roots.Add(full);
        }

        /// <summary>Scan all roots and register the results into <see cref="SongCatalog"/>. Reports progress
        /// (fraction 0..1, current title) every frame. Never throws out of the coroutine — a bad folder is skipped.
        ///
        /// The scan itself (walk the tree, then read + note-count every candidate chart) is pure System.IO and pure
        /// arithmetic — no Unity API — so it runs on a worker thread and the coroutine just polls it. That keeps the
        /// boot bar alive during the directory walk, which used to run to completion inside the coroutine's first
        /// frame, and it stops the per-folder parse from being throttled to a few folders per frame on a big library.
        /// The bar stays determinate: the walk finishes first, and its folder count is the denominator (a folder can
        /// yield several songs, so the bar counts FOLDERS, not songs). Catalog registration stays on the main thread.</summary>
        public static IEnumerator ScanAndRegisterCo(Action<float, string> onProgress)
        {
            var job = new ScanJob(Roots());
            var task = Task.Run((Action)job.Run);

            while (!task.IsCompleted)
            {
                Report(onProgress, job);
                yield return null;
            }

            var entries = new List<SongCatalog.Entry>();
            foreach (var song in job.Songs)   // ToEntry + the catalog are main-thread only
            {
                if (song == null || !song.Playable) continue;
                entries.Add(ToEntry(song, entries.Count));   // index per SONG → fileIds stay unique
            }

            SongCatalog.RegisterExternal(entries);
            onProgress?.Invoke(1f, "");
        }

        private static void Report(Action<float, string> onProgress, ScanJob job)
        {
            int total = job.Total;   // 0 while the tree is still being walked → hold the bar at its floor
            float f = total > 0 ? Math.Min(1f, job.Done / (float)total) : 0f;
            onProgress?.Invoke(f, job.Current ?? "");
        }

        /// <summary>The off-thread half of the scan. Only reads the filesystem; publishes its counters for the boot
        /// bar to poll, and its songs once <see cref="Run"/> has returned.</summary>
        private sealed class ScanJob
        {
            private readonly List<string> _roots;
            private volatile int _total;    // folders to load — 0 until the walk is done (bar is "searching")
            private volatile int _done;     // folders loaded so far
            private volatile string _current = "";

            public ScanJob(List<string> roots) { _roots = roots; }

            public int Total => _total;
            public int Done => _done;
            public string Current => _current;
            public readonly List<ExternalSong> Songs = new List<ExternalSong>();

            public void Run()
            {
                List<ExternalSongScanner.SongDir> work;
                try { work = ExternalSongScanner.BuildWorklist(_roots); }
                catch { work = new List<ExternalSongScanner.SongDir>(); }
                _total = work.Count;

                for (int i = 0; i < work.Count; i++)
                {
                    try
                    {
                        var found = ExternalSongScanner.LoadFolder(work[i].Group, work[i].Path);
                        if (found != null && found.Count > 0)
                        {
                            Songs.AddRange(found);   // one folder can hold several songs (several sets / several .sm)
                            _current = found[found.Count - 1].Title ?? "";
                        }
                    }
                    catch { /* a bad folder must never abort the scan */ }
                    _done = i + 1;
                }
            }
        }

        /// <summary>Convert one scanned song into a catalog Entry (pure — no IO). Public for tests.</summary>
        public static SongCatalog.Entry ToEntry(ExternalSong song, int index)
        {
            var e = new SongCatalog.Entry
            {
                gn = "ext_" + FnvHex(IdentityOf(song, index)) + "k.gn",
                // negative → excluded from NEW/newest + the fileId jacket loader. Base at −1000 so no external id is
                // ever −1 (that's SongSelectScreen's "preview stopped" sentinel → would break the first song's preview).
                fileId = -(index + 1000),
                title = string.IsNullOrEmpty(song.Title) ? "(untitled)" : song.Title,
                artist = song.Artist ?? "",
                bpm = song.Bpm > 0 ? (float)song.Bpm : -1f,
                external = true,
                group = song.Group ?? "",
                audioPath = song.AudioPath ?? "",
                imagePath = song.ImagePath ?? "",
                folderPath = song.FolderPath ?? "",
                songKey = song.SongKey ?? "",
                cdPath = song.CdImagePath ?? "",   // "" → ExternalCdImage composes (and records) the disc on first select
                chartFormat = (int)song.Format,
                previewStartMs = song.PreviewStartMs,
                previewLengthMs = song.PreviewLengthMs,
            };
            SetSlot(e, 0, song.Charts[0], song.AudioDurationSec);
            SetSlot(e, 1, song.Charts[1], song.AudioDurationSec);
            SetSlot(e, 2, song.Charts[2], song.AudioDurationSec);
            return e;
        }

        /// <summary>Fill one difficulty slot. The 時間 column shows the MUSIC FILE's length (<paramref name="audioSec"/>,
        /// same for all three difficulties — it's one song); the chart's own last-note time is only the fallback for
        /// audio we couldn't measure, since a chart usually stops before the track's outro.</summary>
        private static void SetSlot(SongCatalog.Entry e, int d, ExternalChart c, int audioSec)
        {
            if (c == null) return;   // empty slot: notes stay 0 → HasChart false → greyed row
            int level = c.Level > 0 ? c.Level : -1;
            int dur = audioSec > 0 ? audioSec : c.DurationSec;
            switch (d)
            {
                case 0: e.notesEasy = c.NoteCount; e.diffEasy = level; e.chartEasy = c.FilePath; e.chartIdxEasy = c.ChartIndex; e.durEasy = dur; break;
                case 1: e.notesNormal = c.NoteCount; e.diffNormal = level; e.chartNormal = c.FilePath; e.chartIdxNormal = c.ChartIndex; e.durNormal = dur; break;
                default: e.notesHard = c.NoteCount; e.diffHard = level; e.chartHard = c.FilePath; e.chartIdxHard = c.ChartIndex; e.durHard = dur; break;
            }
        }

        // What the gn hashes: the folder, plus — when the folder holds several songs — WHICH song (its SongKey, e.g.
        // the audio file it plays). Content-derived, never the scan index: the gn is what favourites and the restored
        // selection are keyed on, so it has to survive a re-scan. A folder with one song keeps SongKey "", i.e. the
        // plain folder hash, so those favourites are unaffected by this feature.
        private static string IdentityOf(ExternalSong song, int index)
        {
            string folder = song.FolderPath?.ToLowerInvariant();
            if (string.IsNullOrEmpty(folder)) return index.ToString();
            string key = song.SongKey?.ToLowerInvariant();
            return string.IsNullOrEmpty(key) ? folder : folder + "|" + key;
        }

        // FNV-1a 32-bit → 8 hex chars. Stable per song identity (favorites/selection survive re-scans), collision-safe
        // enough for a personal library; RegisterExternal skips any duplicate gn anyway.
        private static string FnvHex(string s)
        {
            uint h = 2166136261u;
            if (s != null) foreach (char ch in s) { h ^= (byte)ch; h *= 16777619u; }
            return h.ToString("x8");
        }
    }
}
