using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Runtime lookup for song display text (title / artist) that was decoded GB2312 -> UTF-8 at
    /// IMPORT time.
    ///
    /// Why a catalog instead of decoding in <see cref="Sdo.Osu.GnChart"/>: original SDO data
    /// (SongList.dat and the .gn header) stores names in GB2312 (cp936). This runtime
    /// (.NET Standard 2.1 / Android IL2CPP) ships no cp936 codec, so on-device decoding is
    /// impossible and would only produce mojibake. tools/build_song_catalog.py decodes once on a
    /// machine that has gb18030 and writes StreamingAssets/song_catalog.json as pure UTF-8.
    /// Runtime therefore only ever reads Unicode -> no locale-dependent garbling, on any platform.
    /// </summary>
    public static class SongCatalog
    {
        [Serializable] public class Entry
        {
            public string gn; public int fileId; public string title; public string artist;
            // Optional metadata (emitted by build_song_catalog.py; absent in older catalogs -> defaults).
            public float bpm = -1f;
            public int diffEasy = -1, diffNormal = -1, diffHard = -1;
            public int notesEasy, notesNormal, notesHard;
            public int durEasy, durNormal, durHard;   // seconds per difficulty

            /// <summary>Difficulty level for d (0=easy,1=normal,2=hard); -1 if unknown.</summary>
            public int Diff(int d) => d <= 0 ? diffEasy : (d == 1 ? diffNormal : diffHard);
            public int NoteCount(int d) => d <= 0 ? notesEasy : (d == 1 ? notesNormal : notesHard);
            public int DurationSec(int d) => d <= 0 ? durEasy : (d == 1 ? durNormal : durHard);

            /// <summary>Whether difficulty d has a real, playable chart. Judged by the actual NOTE COUNT
            /// (an empty chart has 0 notes), NOT the level field — some songs store level 0 for a
            /// difficulty that carries no notes (e.g. 動畫歌曲串燒 sdom2140k: easy=3417 notes, normal/hard=0).
            /// Those empty difficulties are greyed out / non-selectable in song-select.</summary>
            public bool HasChart(int d) => NoteCount(d) > 0;
        }
        [Serializable] private class Catalog { public Entry[] songs = new Entry[0]; }   // populated by JsonUtility; init to silence CS0649

        // Hand-editable name overrides (StreamingAssets/song_name_overrides.json), seeded from the
        // official songlist.dat open songs. See BuildOverrideMap / tools/build_song_name_overrides.py.
        [Serializable] private class Override { public string gn = ""; public string title = ""; public string artist = ""; }   // init to silence CS0649 (JsonUtility fills these)
        [Serializable] private class OverrideDoc { public Override[] songs = new Override[0]; }

        private const string FileName = "song_catalog.json";
        private const string OverrideFileName = "song_name_overrides.json";
        private static Dictionary<string, Entry> _byGn;   // key = lowercase .gn filename
        private static List<Entry> _all;                  // in file order

        /// <summary>All catalog entries in file order (empty if no catalog).</summary>
        public static IReadOnlyList<Entry> All { get { EnsureLoaded(); return _all; } }

        /// <summary>Look up by a .gn path or filename (case-insensitive). Null if absent / no catalog.</summary>
        public static Entry Get(string gnPathOrName)
        {
            if (string.IsNullOrEmpty(gnPathOrName)) return null;
            EnsureLoaded();
            var key = Path.GetFileName(gnPathOrName).ToLowerInvariant();
            return _byGn.TryGetValue(key, out var e) ? e : null;
        }

        public static string Title(string gnPathOrName) => Get(gnPathOrName)?.title;
        public static string Artist(string gnPathOrName) => Get(gnPathOrName)?.artist;

        private static void EnsureLoaded()
        {
            if (_byGn != null) return;
            _byGn = new Dictionary<string, Entry>(StringComparer.Ordinal);
            _all = new List<Entry>();

            var path = Path.Combine(Application.streamingAssetsPath, FileName);
            // NOTE: direct File IO from StreamingAssets works in Editor / standalone. On Android the
            // catalog lives compressed inside the APK and must be read via UnityWebRequest instead
            // (same as the .ogg loader in ScreenGameplay). Wire that when packaging for Android.
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[SongCatalog] {path} missing — run tools/build_song_catalog.py");
                return;
            }

            try
            {
                // explicit UTF-8 so the read never falls back to the OS/locale default encoding
                var cat = JsonUtility.FromJson<Catalog>(File.ReadAllText(path, Encoding.UTF8));
                if (cat?.songs == null) return;
                foreach (var e in cat.songs)
                    if (!string.IsNullOrEmpty(e?.gn)) { _byGn[e.gn.ToLowerInvariant()] = e; _all.Add(e); }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SongCatalog] failed to load {path}: {ex.Message}");
            }

            ApplyNameOverrides();
        }

        /// <summary>
        /// Overlay the hand-editable name list (StreamingAssets/song_name_overrides.json) onto the
        /// catalog: for every song whose gn-stem (sdomNNNN, without the k/t chart suffix) is listed,
        /// replace title/artist. Songs absent from the override keep their k.gn-derived names.
        ///
        /// Why: some .gn headers carry stale/wrong titles (recycled file slots). The list is a
        /// full, hand-editable roster (tools/build_song_name_overrides.py): open songs are pre-filled
        /// from the authoritative official songlist.dat, the rest from k.gn — edit any line to fix a
        /// name. Only display names are overridden; bpm/levels/note counts stay from the actual
        /// chart. Missing/blank/malformed file -> catalog unchanged (k.gn names).
        /// </summary>
        private static void ApplyNameOverrides()
        {
            var path = Path.Combine(Application.streamingAssetsPath, OverrideFileName);
            if (!File.Exists(path)) return;   // optional: no overrides -> keep k.gn names

            Dictionary<string, Override> byStem;
            try
            {
                var doc = JsonUtility.FromJson<OverrideDoc>(File.ReadAllText(path, Encoding.UTF8));
                if (doc?.songs == null || doc.songs.Length == 0) return;
                byStem = new Dictionary<string, Override>(StringComparer.Ordinal);
                foreach (var o in doc.songs)
                    if (!string.IsNullOrEmpty(o?.gn)) byStem[Stem(o.gn)] = o;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SongCatalog] failed to load {path}: {ex.Message}");
                return;
            }

            foreach (var e in _all)
            {
                if (e == null || string.IsNullOrEmpty(e.gn)) continue;
                if (!byStem.TryGetValue(Stem(e.gn), out var o)) continue;   // not listed -> keep k.gn name
                if (!string.IsNullOrEmpty(o.title)) e.title = o.title;
                if (!string.IsNullOrEmpty(o.artist)) e.artist = o.artist;
            }
        }

        /// <summary>gn filename/path -> chart-pair stem, e.g. "sdom0001k.gn" / "SDOM0001T" -> "sdom0001".
        /// The k/t suffix distinguishes the two charts of one song; both share a title.</summary>
        private static string Stem(string gnPathOrName)
        {
            var name = Path.GetFileName(gnPathOrName ?? string.Empty).ToLowerInvariant();
            if (name.EndsWith(".gn")) name = name.Substring(0, name.Length - 3);
            if (name.Length > 0 && (name[name.Length - 1] == 'k' || name[name.Length - 1] == 't'))
                name = name.Substring(0, name.Length - 1);
            return name;
        }
    }
}
