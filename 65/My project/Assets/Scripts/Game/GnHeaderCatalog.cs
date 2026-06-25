using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Runtime lookup for per-song .gn header data, decoded GB2312 -> UTF-8 at IMPORT time
    /// (tools/build_gn_header_catalog.py -> StreamingAssets/gn_header_catalog.json).
    ///
    /// Why a catalog (same reasoning as <see cref="SongCatalog"/>): the .gn header text is GB2312
    /// (cp936); this runtime (.NET Standard 2.1 / IL2CPP) has no cp936 codec, so on-device decoding
    /// would only ever produce mojibake that also shifts with the OS locale. The catalog is pure UTF-8
    /// so the runtime only ever touches Unicode — no locale-dependent garbling on any platform.
    ///
    /// Song name / artist are stored per language as <see cref="Name"/> { zhCN, zhTW, en } so the UI
    /// can switch language without re-decoding. zhCN is the source text, zhTW is opencc s2twp
    /// (simplified->traditional), en is filled only when the source name is already Latin.
    /// </summary>
    public static class GnHeaderCatalog
    {
        public enum Lang { ZhCN, ZhTW, En }

        [Serializable] public class Name
        {
            public string zhCN; public string zhTW; public string en;

            /// <summary>Pick a language; falls back to zhCN when the requested one is empty.</summary>
            public string For(Lang lang)
            {
                switch (lang)
                {
                    case Lang.ZhTW: return string.IsNullOrEmpty(zhTW) ? zhCN : zhTW;
                    case Lang.En:   return string.IsNullOrEmpty(en) ? zhCN : en;
                    default:        return zhCN;
                }
            }
        }

        [Serializable] public class Entry
        {
            public string gn; public int fileId; public string fileType; public string mode; public float bpm;
            public int[] levels; public int[] noteCounts; public int[] measurements; public int[] durations;
            public Name title; public Name artist; public string producer; public string origName;
        }
        [Serializable] private class Catalog { public Entry[] songs = new Entry[0]; }  // filled by JsonUtility

        private const string FileName = "gn_header_catalog.json";
        private static Dictionary<string, Entry> _byGn;   // key = lowercase .gn filename

        /// <summary>Look up by a .gn path or filename (case-insensitive). Null if absent.</summary>
        public static Entry Get(string gnPathOrName)
        {
            if (string.IsNullOrEmpty(gnPathOrName)) return null;
            EnsureLoaded();
            return _byGn.TryGetValue(Path.GetFileName(gnPathOrName).ToLowerInvariant(), out var e) ? e : null;
        }

        public static string Title(string gnPathOrName, Lang lang = Lang.ZhTW) => Get(gnPathOrName)?.title?.For(lang);
        public static string Artist(string gnPathOrName, Lang lang = Lang.ZhTW) => Get(gnPathOrName)?.artist?.For(lang);

        private static void EnsureLoaded()
        {
            if (_byGn != null) return;
            _byGn = new Dictionary<string, Entry>(StringComparer.Ordinal);

            var path = Path.Combine(Application.streamingAssetsPath, FileName);
            // Editor / standalone read directly; on Android read via UnityWebRequest (see ScreenGameplay ogg).
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[GnHeaderCatalog] {path} missing — run tools/build_gn_header_catalog.py");
                return;
            }
            try
            {
                var cat = JsonUtility.FromJson<Catalog>(File.ReadAllText(path, Encoding.UTF8));
                if (cat?.songs == null) return;
                foreach (var e in cat.songs)
                    if (!string.IsNullOrEmpty(e?.gn)) _byGn[e.gn.ToLowerInvariant()] = e;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GnHeaderCatalog] failed to load {path}: {ex.Message}");
            }
        }
    }
}
