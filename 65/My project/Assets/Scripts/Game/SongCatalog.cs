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
        [Serializable] public class Entry { public string gn; public int fileId; public string title; public string artist; }
        [Serializable] private class Catalog { public Entry[] songs = new Entry[0]; }   // populated by JsonUtility; init to silence CS0649

        private const string FileName = "song_catalog.json";
        private static Dictionary<string, Entry> _byGn;   // key = lowercase .gn filename

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

            var path = Path.Combine(Application.streamingAssetsPath, FileName);
            // NOTE: direct File IO from StreamingAssets works in Editor / standalone. On Android the
            // catalog lives compressed inside the APK and must be read via UnityWebRequest instead
            // (same as the .ogg loader in Step1Game). Wire that when packaging for Android.
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
                    if (!string.IsNullOrEmpty(e?.gn)) _byGn[e.gn.ToLowerInvariant()] = e;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SongCatalog] failed to load {path}: {ex.Message}");
            }
        }
    }
}
