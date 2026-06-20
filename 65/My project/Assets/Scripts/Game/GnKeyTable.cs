using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Runtime loader for the precomputed .gn key table (tools/gn_keytable.py ->
    /// StreamingAssets/gn_keytable.json).
    ///
    /// Why: SDOM .gn charts (the ~2545 K/T files) store their LCG seed nowhere in the file; the
    /// original engine brute-forces it (~5 s/file in pure Python). The table caches every seed so the
    /// runtime can decrypt instantly. <see cref="Sdo.Osu.GnChart"/> is engine-free (Sdo.Osu has
    /// noEngineReferences) and cannot read StreamingAssets, so this class loads the seeds and hands
    /// them to GnChart.Load. Because the LCG keystream depends only on the low 24 bits of state, the
    /// whole corpus uses only ~148 distinct seeds — trying them all + validating is microseconds, so
    /// decryption is robust even for renamed/moved/missing-from-table SDOM files.
    ///
    /// Mirrors <see cref="SongCatalog"/>: pure UTF-8 JSON, JsonUtility, keyed by lowercase .gn name.
    /// On Android the file lives compressed in the APK and must be read via UnityWebRequest (wire that
    /// when packaging, same as the .ogg loader in Step1Game).
    /// </summary>
    public static class GnKeyTable
    {
        // seed/seed1/seed2 are uint32 (can exceed int.MaxValue) -> store as long, cast to uint on use.
        [Serializable] public class Entry
        {
            public string gn; public string enc; public string mode;
            public long seed; public int innerOff; public long seed1; public long seed2;
            public int fileId; public float bpm;
        }
        [Serializable] private class Table { public Entry[] songs = new Entry[0]; }  // filled by JsonUtility

        private const string FileName = "gn_keytable.json";
        private static Dictionary<string, Entry> _byGn;     // key = lowercase .gn filename
        private static uint[] _sdomSeeds = Array.Empty<uint>();

        /// <summary>Look up an entry by .gn path or filename (case-insensitive). Null if absent.</summary>
        public static Entry Get(string gnPathOrName)
        {
            if (string.IsNullOrEmpty(gnPathOrName)) return null;
            EnsureLoaded();
            return _byGn.TryGetValue(Path.GetFileName(gnPathOrName).ToLowerInvariant(), out var e) ? e : null;
        }

        /// <summary>All distinct SDOM seeds (~148). GnChart can decrypt any SDOM .gn by trying these.</summary>
        public static uint[] SdomSeeds { get { EnsureLoaded(); return _sdomSeeds; } }

        /// <summary>
        /// Candidate seeds for a given .gn, ready to pass to <see cref="Sdo.Osu.GnChart.Load"/>:
        /// the file's own seed first (fast path) then every other distinct seed (fallback).
        /// Returns the full distinct set if the file is unknown / not SDOM.
        /// </summary>
        public static uint[] SeedsFor(string gnPathOrName)
        {
            EnsureLoaded();
            var e = Get(gnPathOrName);
            if (e == null || e.enc != "sdom") return _sdomSeeds;
            uint own = (uint)e.seed;
            var list = new List<uint>(_sdomSeeds.Length + 1) { own };
            foreach (var s in _sdomSeeds) if (s != own) list.Add(s);
            return list.ToArray();
        }

        private static void EnsureLoaded()
        {
            if (_byGn != null) return;
            _byGn = new Dictionary<string, Entry>(StringComparer.Ordinal);
            var seeds = new List<uint>(); var seen = new HashSet<uint>();

            var path = Path.Combine(Application.streamingAssetsPath, FileName);
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[GnKeyTable] {path} missing — run tools/gn_keytable.py");
                return;
            }
            try
            {
                var t = JsonUtility.FromJson<Table>(File.ReadAllText(path, Encoding.UTF8));
                if (t?.songs != null)
                    foreach (var e in t.songs)
                    {
                        if (string.IsNullOrEmpty(e?.gn)) continue;
                        _byGn[e.gn.ToLowerInvariant()] = e;
                        if (e.enc == "sdom")
                        {
                            uint s = (uint)e.seed;
                            if (seen.Add(s)) seeds.Add(s);
                        }
                    }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GnKeyTable] failed to load {path}: {ex.Message}");
            }
            _sdomSeeds = seeds.ToArray();
        }
    }
}
