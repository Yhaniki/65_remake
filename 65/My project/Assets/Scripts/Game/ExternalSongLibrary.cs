using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
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
        /// <summary>Song-folder roots to scan: DATA/ADDON/SONG first, then the legacy exe-sibling Songs/ (honoured
        /// when it still exists), then config's AdditionalSongFolders (existing directories only, de-duplicated).</summary>
        public static List<string> Roots()
        {
            var roots = new List<string>();
            Add(roots, SdoExtracted.SongsDir);         // DATA/ADDON/SONG — the documented default
            Add(roots, SdoExtracted.LegacySongsDir);   // pre-ADDON exe-sibling Songs/ — kept working on upgrade
            if (RoomConfig.additionalSongFolders != null)
                foreach (var f in RoomConfig.additionalSongFolders) Add(roots, f);
            return roots;
        }

        // Per-user writable location for the scan cache. Application.persistentDataPath is main-thread only, so this is
        // resolved on the coroutine (main thread) and the resulting path handed to the worker — never touched off-thread.
        private static string CacheFilePath()
        {
            try { return Path.Combine(Application.persistentDataPath, "external_song_cache.json"); }
            catch { return ""; }
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
        /// yield several songs, so the bar counts FOLDERS, not songs). Catalog registration stays on the main thread.
        ///
        /// <paramref name="onProgress"/> is (fraction 0..1, folder line, detail line): the folder currently being read
        /// (group / name — updated BEFORE each folder loads, so on a big library the names flick past under the bar),
        /// and a detail line with the last song found + a running total. The boot overlay shows both under the bar.
        ///
        /// RE-RUNNABLE: the same coroutine is what 選歌 → 分類瀏覽 → 更新 runs to pick up songs added / edited /
        /// deleted while the game was open, so it ends in <see cref="SongCatalog.ReplaceExternal"/> — the previous
        /// scan's rows are swapped out, not merely added to. A re-scan is cheap because unchanged folders come
        /// straight from <see cref="ExternalScanCache"/> (signature = file stats, no parse). Only one scan runs at a
        /// time (<see cref="Scanning"/>); a second call while one is in flight just waits for it and returns.</summary>
        public static IEnumerator ScanAndRegisterCo(Action<float, string, string> onProgress)
        {
            if (Scanning)   // a boot scan / another refresh is already running — don't start a second worker
            {
                while (Scanning) yield return null;
                onProgress?.Invoke(1f, "", "");
                yield break;
            }

            // Cache I/O (JsonUtility + persistentDataPath) stays on the main thread; the worker does only pure
            // System.IO + arithmetic. Load the previous scan, hand it to the worker, save the fresh one when it returns.
            string cacheFile = CacheFilePath();
            var job = new ScanJob(Roots(), ExternalScanCache.Load(cacheFile));
            // The flag is cleared by the WORKER's continuation, not by this coroutine: a refresh coroutine dies with
            // its screen (leaving the scene mid-scan), and clearing it here would then leave Scanning stuck true and
            // every later scan waiting forever on a worker that finished long ago.
            Scanning = true;
            var task = Task.Run((Action)job.Run).ContinueWith(_ => { Scanning = false; });

            while (!task.IsCompleted)
            {
                Report(onProgress, job);
                yield return null;
            }

            ExternalScanCache.Save(cacheFile, job.CacheLines);   // rewrite (prunes folders that are gone now)

            var entries = new List<SongCatalog.Entry>();
            foreach (var song in job.Songs)   // ToEntry + the catalog are main-thread only
            {
                if (song == null || !song.Playable) continue;
                entries.Add(ToEntry(song, entries.Count));   // index per SONG → fileIds stay unique
            }

            // Snapshot BEFORE the swap so the 更新 toast can say what actually changed on disk (see LastDelta) —
            // "已更新 18 首" when all 18 were the same songs as before is a lie the player can't check.
            var before = ExternalEntries();
            SongCatalog.ReplaceExternal(entries);   // swap, so a deleted song really disappears on a re-scan
            LastDelta = Diff(before, entries);
            onProgress?.Invoke(1f, "", "");
        }

        /// <summary>True while a scan is in flight (boot or refresh) — the 更新 button greys out on it.</summary>
        public static bool Scanning { get; private set; }

        /// <summary>What the last completed scan actually changed (for the 更新 toast).</summary>
        public static ScanDelta LastDelta { get; private set; }

        /// <summary>The external rows currently in the catalog (the previous scan's result).</summary>
        private static List<SongCatalog.Entry> ExternalEntries()
        {
            var res = new List<SongCatalog.Entry>();
            foreach (var e in SongCatalog.All) if (e != null && e.external) res.Add(e);
            return res;
        }

        // ---------------- what a re-scan changed ----------------

        /// <summary>How one scan differs from the previous one: songs that appeared, songs whose FILES changed, and
        /// songs that are gone. <see cref="Total"/> is just how many are in the library now.</summary>
        public struct ScanDelta
        {
            public int Added, Changed, Removed, Total;
            /// <summary>Nothing on disk differs from last time — the 更新 toast then says "沒有變更", not "已更新".</summary>
            public bool Any => Added > 0 || Changed > 0 || Removed > 0;
        }

        /// <summary>Compare two scans by gn (a song's stable identity): present only in <paramref name="after"/> =
        /// added, only in <paramref name="before"/> = removed, in both but with a different <see cref="Fingerprint"/>
        /// = changed. Pure — public for tests.</summary>
        public static ScanDelta Diff(IEnumerable<SongCatalog.Entry> before, IEnumerable<SongCatalog.Entry> after)
        {
            var old = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (before != null)
                foreach (var e in before)
                    if (e != null && !string.IsNullOrEmpty(e.gn)) old[e.gn] = Fingerprint(e);

            var d = new ScanDelta();
            if (after != null)
                foreach (var e in after)
                {
                    if (e == null || string.IsNullOrEmpty(e.gn)) continue;
                    d.Total++;
                    if (!old.TryGetValue(e.gn, out var was)) { d.Added++; continue; }
                    if (!string.Equals(was, Fingerprint(e), StringComparison.Ordinal)) d.Changed++;
                    old.Remove(e.gn);   // seen → what's left over at the end was removed from disk
                }
            d.Removed = old.Count;
            return d;
        }

        /// <summary>Everything about an entry that comes from the song's own FILES — so an edited chart, a renamed
        /// audio file or a retitled beatmap all count as "changed". Deliberately EXCLUDES the fields the running game
        /// writes back into the entry (<c>cdPath</c> composed on first select, <c>durX</c> measured on first play,
        /// <c>offsetMs</c> nudged in the editor): those move without anything on disk changing for the player, and
        /// counting them would report phantom updates. Public for tests.</summary>
        public static string Fingerprint(SongCatalog.Entry e)
        {
            if (e == null) return "";
            const char sep = '';   // unit separator — can't occur in a title or a path
            var sb = new System.Text.StringBuilder();
            sb.Append(e.title).Append(sep).Append(e.artist).Append(sep)
              .Append(e.bpm.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(sep)
              .Append(e.group).Append(sep).Append(e.folderPath).Append(sep).Append(e.songKey).Append(sep)
              .Append(e.chartFormat).Append(sep).Append(e.audioPath).Append(sep).Append(e.imagePath).Append(sep)
              .Append(e.previewStartMs).Append(sep).Append(e.previewLengthMs).Append(sep)
              // pack-supplied files: swapping in a folder whose .tsv now points at real jackets / previews / dances IS
              // a change the player can see, even though every chart file stayed the same.
              .Append(e.previewPath).Append(sep).Append(e.dpsPath).Append(sep).Append(e.chartSeed);
            for (int d = 0; d < 3; d++)
                sb.Append(sep).Append(e.ChartPath(d)).Append(sep).Append(e.ChartIndex(d))
                  .Append(sep).Append(e.NoteCount(d)).Append(sep).Append(e.Diff(d));
            return sb.ToString();
        }

        private static void Report(Action<float, string, string> onProgress, ScanJob job)
        {
            int total = job.Total;   // 0 while the tree is still being walked → hold the bar at its floor
            float f = total > 0 ? Math.Min(1f, job.Done / (float)total) : 0f;
            onProgress?.Invoke(f, job.Folder ?? "", Detail(job));
        }

        // The line under the folder path: the last song found + how many so far. Blank until the first song lands.
        private static string Detail(ScanJob job)
        {
            int n = job.Found;
            if (n <= 0) return "";
            string title = job.Current;
            return string.IsNullOrEmpty(title) ? ("已找到 " + n + " 首") : ("♪ " + title + "　·　已找到 " + n + " 首");
        }

        /// <summary>The off-thread half of the scan. Only reads the filesystem; publishes its counters for the boot
        /// bar to poll, and its songs once <see cref="Run"/> has returned.</summary>
        private sealed class ScanJob
        {
            private readonly List<string> _roots;
            private readonly Dictionary<string, ExternalScanCache.Folder> _cache;   // previous scan, read-only in Run
            private volatile int _total;    // folders to load — 0 until the walk is done (bar is "searching")
            private volatile int _done;     // folders loaded so far
            private volatile int _found;    // songs discovered so far (across folders)
            private volatile string _folder = "";   // the folder being read right now (group / name)
            private volatile string _current = "";  // the last song's title

            public ScanJob(List<string> roots, Dictionary<string, ExternalScanCache.Folder> cache)
            {
                _roots = roots;
                _cache = cache ?? new Dictionary<string, ExternalScanCache.Folder>(StringComparer.OrdinalIgnoreCase);
            }

            public int Total => _total;
            public int Done => _done;
            public int Found => _found;
            public string Folder => _folder;
            public string Current => _current;
            public readonly List<ExternalSong> Songs = new List<ExternalSong>();
            public readonly List<ExternalScanCache.Folder> CacheLines = new List<ExternalScanCache.Folder>();   // to persist (main thread)

            public void Run()
            {
                List<ExternalSongScanner.SongDir> work;
                try { work = ExternalSongScanner.BuildWorklist(_roots); }
                catch { work = new List<ExternalSongScanner.SongDir>(); }
                _total = work.Count;
                if (work.Count == 0) { _folder = ""; return; }   // CacheLines stays empty → the coroutine saves an empty cache

                // Folders are independent and LoadFolder is pure (System.IO + arithmetic — no Unity API), so parse them
                // across all cores instead of one at a time. Each result goes into its own slot so the final order stays
                // the deterministic alphabetical walk order (external fileIds are assigned from it); only the progress
                // counters are shared, and those go through Interlocked. Songs is filled single-threaded afterwards.
                //
                // Before parsing a folder we check the cache (loaded on the main thread, read-only here → safe to share):
                // if its source files are unchanged since last boot we reuse the parsed result (only re-reading the tiny
                // sidecar, for a disc built since), skipping the expensive chart parse + star-rating entirely. lines[i] is
                // the cache entry to persist for folder i (hit or fresh) — the coroutine writes them out when Run returns.
                var results = new List<ExternalSong>[work.Count];
                var lines = new ExternalScanCache.Folder[work.Count];
                int done = 0, found = 0;
                var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) };
                try
                {
                    Parallel.For(0, work.Count, opts, i =>
                    {
                        var sd = work[i];
                        _folder = FolderLabel(sd);   // racy across threads — it only drives the boot bar's flicker
                        try
                        {
                            string sig = ExternalScanCache.Signature(sd.Path);
                            List<ExternalSong> f;
                            if (sig.Length > 0 && _cache.TryGetValue(sd.Path, out var hit) && hit.sig == sig)
                            {
                                f = ExternalScanCache.FromFolder(hit);                        // reuse — no parse
                                foreach (var s in f) ExternalSongScanner.ReapplySidecar(s);   // pick up a disc built since caching
                            }
                            else
                            {
                                f = ExternalSongScanner.LoadFolder(sd.Group, sd.Path);        // cold / changed → parse
                            }
                            results[i] = f;   // one folder can hold several songs (several sets / several .sm)
                            if (sig.Length > 0) lines[i] = ExternalScanCache.ToFolder(sd.Path, sig, f);   // don't cache unreadable folders
                            if (f != null && f.Count > 0)
                            {
                                _found = Interlocked.Add(ref found, f.Count);
                                _current = f[f.Count - 1].Title ?? "";
                            }
                        }
                        catch { /* a bad folder must never abort the scan */ }
                        _done = Interlocked.Increment(ref done);
                    });
                }
                catch (AggregateException) { /* per-folder errors are already swallowed; guard the whole loop anyway */ }

                foreach (var list in results)
                    if (list != null && list.Count > 0) Songs.AddRange(list);
                foreach (var ln in lines)
                    if (ln != null) CacheLines.Add(ln);   // in worklist order; the coroutine persists them (prunes gone folders)
                _folder = "";
            }

            // "group / folderName" for the boot bar — what the player sees being read. Path's leaf is the folder name.
            private static string FolderLabel(ExternalSongScanner.SongDir d)
            {
                string name = "";
                try { name = new System.IO.DirectoryInfo(d.Path).Name; } catch { }
                string group = d.Group ?? "";
                if (group.Length == 0) return name;
                if (name.Length == 0 || string.Equals(name, group, StringComparison.OrdinalIgnoreCase)) return group;
                return group + " / " + name;
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
                previewPath = song.PreviewAudioPath ?? "",
                dpsPath = song.DpsPath ?? "",
                chartSeed = song.GnSeed,
                previewStartMs = song.PreviewStartMs,
                previewLengthMs = song.PreviewLengthMs,
                // hand-calibrated per-song offset from the folder sidecar → drives gameplay's songOffsetMs (see FrontendApp).
                offsetMs = Mathf.Clamp(song.OffsetMs, -SongCatalog.MaxOffsetMs, SongCatalog.MaxOffsetMs),
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
