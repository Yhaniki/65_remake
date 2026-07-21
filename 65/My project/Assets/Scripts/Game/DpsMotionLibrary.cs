using System.IO;
using System.Collections.Generic;
using Sdo.Osu;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// What a generated dance (<see cref="RandomDps"/>) draws from: the motion pool, the official openings, and each
    /// motion's length. All of it is BAKED into the data tree as <c>DANCE/DPSINDEX.TXT</c> by
    /// <c>tools/build_dps_index.py</c> (re-run it after changing MOTION/ or DANCE/), so the game only reads one small
    /// text file — it never walks MOTION/'s 7k clips nor cracks open the 2k official .dps at startup.
    ///
    /// The file is read on FIRST USE, not at boot: a session that never plays an external song never touches it.
    /// Missing file → empty pool → songs keep the single fallback dance clip, exactly as before this feature.
    /// </summary>
    public static class DpsMotionLibrary
    {
        private static DpsIndex _index;

        /// <summary>The baked index (empty when <c>DANCE/DPSINDEX.TXT</c> is missing).</summary>
        public static DpsIndex Index
        {
            get
            {
                if (_index != null) return _index;
                _index = DpsIndex.Parse(ReadIndex());
                if (_index.IsEmpty)
                    Debug.LogWarning($"[dps] no {DpsIndex.RelPath} in the data tree — external songs keep the fallback " +
                                     "dance clip (run tools/build_dps_index.py)");
                else
                    Debug.Log($"[dps] index: {_index.Pool.Count} dance clips, {_index.Intros.Count} official openings, " +
                              $"{_index.Groups.Count} three-motion groups");
                return _index;
            }
        }

        /// <summary>Generic dance clips (<c>wdanceNNNN.mot</c>) — female names; ScreenGameplay.ResolveMot maps them to
        /// the male clips.</summary>
        public static IReadOnlyList<string> Pool => Index.Pool;

        /// <summary>Openings harvested from the official choreographies: each is one dance's opening rows (up to its
        /// fourth distinct motion), with the frame slice every row plays.</summary>
        public static IReadOnlyList<IntroSlice[]> Intros => Index.Intros;

        /// <summary>The rest of the official choreographies, cut into three-motion groups (openings excluded) — what
        /// the body of a generated dance is drawn from, a whole group at a time.</summary>
        public static IReadOnlyList<IntroSlice[]> Groups => Index.Groups;

        /// <summary>Frame count of a motion (&gt;= 1).</summary>
        public static int Frames(string mot) => Index.Frames(mot);

        /// <summary>Drop the cached index (a changed data root ships a different one).</summary>
        public static void Clear() => _index = null;

        private static string ReadIndex()
        {
            try
            {
                string path = Path.Combine(SdoExtracted.Root, DpsIndex.RelPath.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(path) ? File.ReadAllText(path) : "";
            }
            catch { return ""; }
        }
    }
}
