using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
    /// Each external Entry gets a synthetic, path-stable gn (<c>ext_&lt;hash&gt;k.gn</c>) so it survives SongListModel
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

        /// <summary>Scan all roots and register the results into <see cref="SongCatalog"/>. Yields between songs and
        /// reports progress (fraction 0..1, current title). Never throws out of the coroutine — a bad folder is skipped.</summary>
        public static IEnumerator ScanAndRegisterCo(Action<float, string> onProgress)
        {
            List<ExternalSongScanner.SongDir> work;
            try { work = ExternalSongScanner.BuildWorklist(Roots()); }
            catch { work = new List<ExternalSongScanner.SongDir>(); }

            var entries = new List<SongCatalog.Entry>();
            int total = work.Count;
            if (total == 0) { onProgress?.Invoke(1f, ""); yield break; }

            for (int i = 0; i < total; i++)
            {
                ExternalSong song = null;
                try { song = ExternalSongScanner.LoadOne(work[i].Group, work[i].Path); }
                catch { song = null; }
                if (song != null && song.Playable) entries.Add(ToEntry(song, entries.Count));

                if ((i & 3) == 3 || i == total - 1)   // report + yield every 4 songs
                {
                    onProgress?.Invoke((i + 1) / (float)total, song?.Title ?? "");
                    yield return null;
                }
            }

            SongCatalog.RegisterExternal(entries);
            onProgress?.Invoke(1f, "");
        }

        /// <summary>Convert one scanned song into a catalog Entry (pure — no IO). Public for tests.</summary>
        public static SongCatalog.Entry ToEntry(ExternalSong song, int index)
        {
            var e = new SongCatalog.Entry
            {
                gn = "ext_" + FnvHex(song.FolderPath?.ToLowerInvariant() ?? index.ToString()) + "k.gn",
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
                chartFormat = (int)song.Format,
                previewStartMs = song.PreviewStartMs,
                previewLengthMs = song.PreviewLengthMs,
            };
            SetSlot(e, 0, song.Charts[0]);
            SetSlot(e, 1, song.Charts[1]);
            SetSlot(e, 2, song.Charts[2]);
            return e;
        }

        private static void SetSlot(SongCatalog.Entry e, int d, ExternalChart c)
        {
            if (c == null) return;   // empty slot: notes stay 0 → HasChart false → greyed row
            int level = c.Level > 0 ? c.Level : -1;
            switch (d)
            {
                case 0: e.notesEasy = c.NoteCount; e.diffEasy = level; e.chartEasy = c.FilePath; e.chartIdxEasy = c.ChartIndex; break;
                case 1: e.notesNormal = c.NoteCount; e.diffNormal = level; e.chartNormal = c.FilePath; e.chartIdxNormal = c.ChartIndex; break;
                default: e.notesHard = c.NoteCount; e.diffHard = level; e.chartHard = c.FilePath; e.chartIdxHard = c.ChartIndex; break;
            }
        }

        // FNV-1a 32-bit → 8 hex chars. Stable per folder path (favorites/selection survive re-scans), collision-safe
        // enough for a personal library; RegisterExternal skips any duplicate gn anyway.
        private static string FnvHex(string s)
        {
            uint h = 2166136261u;
            if (s != null) foreach (char ch in s) { h ^= (byte)ch; h *= 16777619u; }
            return h.ToString("x8");
        }
    }
}
