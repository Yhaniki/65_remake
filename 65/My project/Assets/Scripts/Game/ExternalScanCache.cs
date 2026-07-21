using System;
using System.Collections.Generic;
using System.IO;
using Sdo.Osu;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Boot-time cache of the external-song scan. The scan's real cost is PARSING every candidate chart (ReadMeta on
    /// each .osu, plus a full parse + star-rating on the three chosen ones per song); the directory walk is cheap by
    /// comparison. So on each boot we still walk the tree, but for every folder whose source files are unchanged since
    /// last time we reuse the parsed result instead of re-parsing it.
    ///
    /// A folder's "unchanged" token is a <see cref="Signature"/> over its chart/audio/image files' (name, size, mtime)
    /// — cheap file stats, no content read. Generated artifacts (the <c>sdo.header</c> sidecar, the composed
    /// <c>cd*.png</c> disc, the <c>dance*.dps</c>) are excluded, so composing a disc or building a dance never
    /// invalidates the cache line for the very song you just played. A cache hit still re-reads the tiny sidecar
    /// (<see cref="ExternalSongScanner.ReapplySidecar"/>) so a disc built since caching is picked up.
    ///
    /// Stored as one JSON file in a per-user writable dir; its path is resolved on the main thread and handed to the
    /// scan worker. Any read/parse failure just means a cold cache — the scan re-parses and rewrites it.
    /// </summary>
    public static class ExternalScanCache
    {
        // Bump when the PARSE RESULT for the same source files changes (not just the schema), so old cache lines are
        // dropped and every folder re-parses with the new logic. v2: osu pack sets now show the song name (Version)
        // instead of the shared pack-label title — cached titles from v1 must be discarded to pick that up.
        // v3: the displayed LV changed from star × 5 to star × 7, so every cached `level` is stale.
        // v4: .gn song packs are scanned now — old lines for a pack folder cached it as "yields nothing".
        public const int Version = 4;

        // JsonUtility-friendly records (plain [Serializable], public fields, no UnityEngine.Object refs → safe to
        // serialize on the scan worker thread). Empty difficulty slots are simply ABSENT from `charts` — never a null
        // array element (which JsonUtility can't represent).
        [Serializable] public sealed class Chart { public int slot; public string file = ""; public int idx, notes, level, dur; }

        [Serializable]
        public sealed class Song
        {
            public string songKey = "", title = "", artist = "", audioPath = "", imagePath = "";
            public string cdImagePath = "", motPath = "", cameraPath = "";
            public string previewAudioPath = "", dpsPath = "";   // .gn pack: its own preview clip / choreography
            public double bpm;
            public int format, previewStartMs, previewLengthMs, audioDurationSec;
            public int fileId;      // .gn pack: the official song number its art/preview/dance are named by
            public long gnSeed;     // .gn pack: LCG seed (uint32 — long so it survives JsonUtility unsigned)
            public List<Chart> charts = new List<Chart>();
        }

        [Serializable]
        public sealed class Folder
        {
            public string path = "", sig = "", group = "";   // group is the same for every song in a folder
            public List<Song> songs = new List<Song>();
        }

        [Serializable] private sealed class CacheData { public int version = Version; public List<Folder> folders = new List<Folder>(); }

        // ---- signature: what makes a folder's parse result stale ----

        // .gn = a native SDO chart; .tsv = a pack's sdo_pack.tsv (re-running the converter must invalidate the folder,
        // since titles/seeds/art paths all come from there).
        private static readonly string[] Chartish = { ".osu", ".sm", ".gn", ".tsv", ".ogg", ".mp3", ".wav", ".png", ".jpg", ".jpeg", ".bmp" };

        /// <summary>A token that changes iff the folder's SOURCE files change — file stats only, no content read.
        /// Generated files (the sidecar, composed <c>cd*.png</c> discs, <c>dance*.dps</c>) are skipped so runtime output
        /// never invalidates a song's own cache line. "" if the folder holds nothing scannable / can't be read (→ the
        /// caller treats "" as an always-miss and never caches it).</summary>
        public static string Signature(string folderPath)
        {
            try
            {
                var items = new List<string>();
                foreach (var f in Directory.EnumerateFiles(folderPath))
                {
                    string name = Path.GetFileName(f);
                    if (IsGenerated(name)) continue;
                    string ext = Path.GetExtension(name).ToLowerInvariant();
                    if (Array.IndexOf(Chartish, ext) < 0) continue;
                    var fi = new FileInfo(f);
                    items.Add(name.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks);
                }
                if (items.Count == 0) return "";
                items.Sort(StringComparer.Ordinal);   // FS enumeration order is not stable → sort for a deterministic token
                return Hash(string.Join("\n", items));
            }
            catch { return ""; }
        }

        // The scan's own output living in the song folder: the sidecar, and the disc / dance it names (SongSidecar's
        // CdFileName/DpsFileName produce exactly these — "cd.png"/"cd_<slug>_<hash>.png", "dance.dps"/"dance_<…>.dps").
        private static bool IsGenerated(string name)
        {
            if (string.Equals(name, SongSidecar.FileName, StringComparison.OrdinalIgnoreCase)) return true;
            string n = name.ToLowerInvariant();
            return n == "cd.png" || n.StartsWith("cd_") || n == "dance.dps" || n.StartsWith("dance_");
        }

        private static string Hash(string s)   // FNV-1a 64-bit
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
            return h.ToString("x16");
        }

        // ---- load / save ----

        /// <summary>Read the cache into a path → folder map (case-insensitive). Empty on any failure / version bump.</summary>
        public static Dictionary<string, Folder> Load(string cacheFilePath)
        {
            var map = new Dictionary<string, Folder>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrEmpty(cacheFilePath) || !File.Exists(cacheFilePath)) return map;
                var data = JsonUtility.FromJson<CacheData>(File.ReadAllText(cacheFilePath));
                if (data == null || data.version != Version || data.folders == null) return map;
                foreach (var f in data.folders)
                    if (f != null && !string.IsNullOrEmpty(f.path)) map[f.path] = f;
            }
            catch { /* corrupt / old cache → cold start */ }
            return map;
        }

        /// <summary>Persist the current scan's folder lines (best-effort — a write failure just makes next boot cold).</summary>
        public static void Save(string cacheFilePath, List<Folder> folders)
        {
            try
            {
                if (string.IsNullOrEmpty(cacheFilePath)) return;
                var data = new CacheData { version = Version, folders = folders ?? new List<Folder>() };
                var dir = Path.GetDirectoryName(cacheFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(cacheFilePath, JsonUtility.ToJson(data));
            }
            catch { /* cache is best-effort */ }
        }

        // ---- mapping ExternalSong <-> cache record ----

        /// <summary>A folder cache line from a freshly-parsed (or refreshed) song list. group is taken from the songs
        /// (they all share it); an empty folder still caches as "yields nothing" so it isn't re-parsed either.</summary>
        public static Folder ToFolder(string path, string sig, List<ExternalSong> songs)
        {
            var f = new Folder { path = path, sig = sig };
            if (songs != null && songs.Count > 0)
            {
                f.group = songs[0].Group ?? "";
                foreach (var s in songs) if (s != null) f.songs.Add(ToSong(s));
            }
            return f;
        }

        private static Song ToSong(ExternalSong s)
        {
            var o = new Song
            {
                songKey = s.SongKey ?? "", title = s.Title ?? "", artist = s.Artist ?? "",
                audioPath = s.AudioPath ?? "", imagePath = s.ImagePath ?? "",
                cdImagePath = s.CdImagePath ?? "", motPath = s.MotPath ?? "", cameraPath = s.CameraPath ?? "",
                previewAudioPath = s.PreviewAudioPath ?? "", dpsPath = s.DpsPath ?? "",
                bpm = s.Bpm, format = (int)s.Format, fileId = s.FileId, gnSeed = s.GnSeed,
                previewStartMs = s.PreviewStartMs, previewLengthMs = s.PreviewLengthMs, audioDurationSec = s.AudioDurationSec,
            };
            for (int i = 0; i < 3; i++)
            {
                var c = s.Charts[i];
                if (c == null) continue;
                o.charts.Add(new Chart { slot = i, file = c.FilePath ?? "", idx = c.ChartIndex, notes = c.NoteCount, level = c.Level, dur = c.DurationSec });
            }
            return o;
        }

        /// <summary>Rebuild a folder's songs from its cache line (no parse). The stored group is reused verbatim: a
        /// folder can only change group by moving or by its files changing, and both change the key/signature → a miss,
        /// so a hit's group is always still valid.</summary>
        public static List<ExternalSong> FromFolder(Folder f)
        {
            var list = new List<ExternalSong>();
            if (f == null || f.songs == null) return list;
            foreach (var cs in f.songs) if (cs != null) list.Add(FromSong(cs, f.group, f.path));
            return list;
        }

        private static ExternalSong FromSong(Song o, string group, string folderPath)
        {
            var s = new ExternalSong
            {
                Group = group ?? "", FolderPath = folderPath ?? "", SongKey = o.songKey ?? "",
                Title = o.title ?? "", Artist = o.artist ?? "", Bpm = o.bpm,
                AudioPath = o.audioPath ?? "", AudioDurationSec = o.audioDurationSec, ImagePath = o.imagePath ?? "",
                Format = (SongFormat)o.format,
                CdImagePath = o.cdImagePath ?? "", MotPath = o.motPath ?? "", CameraPath = o.cameraPath ?? "",
                PreviewAudioPath = o.previewAudioPath ?? "", DpsPath = o.dpsPath ?? "",
                FileId = o.fileId, GnSeed = (uint)o.gnSeed,
                PreviewStartMs = o.previewStartMs, PreviewLengthMs = o.previewLengthMs,
            };
            if (o.charts != null)
                foreach (var c in o.charts)
                    if (c != null && c.slot >= 0 && c.slot < 3)
                        s.Charts[c.slot] = new ExternalChart
                        {
                            FilePath = c.file ?? "", ChartIndex = c.idx, NoteCount = c.notes, Level = c.level, DurationSec = c.dur,
                        };
            return s;
        }
    }
}
