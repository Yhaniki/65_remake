using System;
using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>
    /// Loads an SDO ".cdt" Camera Data Table — the auto-director shot list (decompiled
    /// CameraMgr_LoadCamerasBin_0040e0e0). Format: u32 count, then `count` NUL-terminated ASCII strings,
    /// each `relPath:durationMs:flag` (path is backslash-separated, relative to the CAMERA/ folder), e.g.
    /// "1\cam0005\001.cv:13000:0". The game plays the shots in order, showing each for durationMs while its
    /// .cv keyframes animate (a moving dolly), then auto-cuts to the next and loops (CameraSeq_AdvanceA).
    /// F2 toggles between this director (state -1) and 6 fixed cameras (CameraSeq_SetPlaying / ChangeCamera).
    /// </summary>
    public sealed class CdtLoader
    {
        public struct Shot { public string CvRelPath; public int DurationMs; public int Flag; }
        public List<Shot> Shots = new List<Shot>();

        public static CdtLoader Load(byte[] d)
        {
            if (d == null || d.Length < 4) return null;
            int count = BitConverter.ToInt32(d, 0);
            if (count <= 0 || count > 4096) return null;
            var res = new CdtLoader();
            int p = 4;
            for (int i = 0; i < count && p < d.Length; i++)
            {
                int start = p;
                while (p < d.Length && d[p] != 0) p++;
                string s = System.Text.Encoding.ASCII.GetString(d, start, p - start);
                p++; // skip NUL
                if (s.Length == 0) continue;
                var parts = s.Split(':');
                var shot = new Shot { CvRelPath = parts[0], DurationMs = 5000, Flag = 0 };
                if (parts.Length > 1) int.TryParse(parts[1], out shot.DurationMs);
                if (parts.Length > 2) int.TryParse(parts[2], out shot.Flag);
                if (shot.DurationMs <= 0) shot.DurationMs = 5000;
                res.Shots.Add(shot);
            }
            return res.Shots.Count > 0 ? res : null;
        }
    }
}
