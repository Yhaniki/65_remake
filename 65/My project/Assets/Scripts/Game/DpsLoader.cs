using System;
using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>
    /// Loads an SDO ".dps" dance script — the per-song choreography that sequences motion
    /// SLICES along the music timeline. Ported from bms_sdo/dps_archive.py: rows are located by their
    /// ".mot" filename (row_start = motNameOffset - 12), accepted only at this version's two strides (so a
    /// ".mot" string inside a mid block is skipped). Each row: preamble(12) + name(16) + mid; the mid holds
    /// start_frame@244, end_frame@248, duration_sec@252. Rows play sequentially (time accumulates by dur).
    /// Sample(t) -> (motName, interpolated frame) drives the avatar in sync with the song.
    ///
    /// TWO versions ship in DATA/DANCE: 2445 files are PAS00003, and 44 of the oldest songs (10001..10056,
    /// e.g. 10003) are PAS00002. The original engine reads both (sdo_stand_alone FUN_00407a30 has a branch
    /// per magic), and the two sub-item loops are identical except for ONE trailing byte the v3 loop reads
    /// into +0x3c (v2 hardcodes it to 0) — so v2's row is exactly one byte shorter and everything up to and
    /// including the mid's 244/248/252 fields sits at the same offset. Rejecting PAS00002 left those 44 songs
    /// with no choreography at all ("這首在遊戲裡面不會動").
    /// </summary>
    public sealed class DpsLoader
    {
        public struct Row { public string Mot; public int StartF, EndF; public float Dur; public float TStart; }
        public Row[] Rows;
        public float Total;

        public static DpsLoader Load(byte[] d)
        {
            if (d == null || d.Length < 16) return null;
            // Stride between two rows: the shorter one is the next sub-item of the same item, the longer one adds
            // that item's 12-byte preamble (pre_a, pre_b, sub_count). PAS00002's sub-item is one byte shorter.
            int subStride, itemStride;
            switch (Ascii(d, 0, 8))
            {
                case "PAS00003": subStride = 305; itemStride = 317; break;
                case "PAS00002": subStride = 304; itemStride = 316; break;
                default: return null;
            }
            var starts = new List<int>();
            for (int i = 0; i + 4 <= d.Length; i++)
            {
                if (d[i] != (byte)'.') continue;
                if (!(IsM(d[i + 1]) && IsO(d[i + 2]) && IsT(d[i + 3]))) continue;
                int ns = i; while (ns > 0 && IsName(d[ns - 1])) ns--;
                if (ns == i) continue;                       // no name chars before ".mot"
                int rs = ns - 12; if (rs < 12) continue;
                if (starts.Count == 0) starts.Add(rs);
                else { int gap = rs - starts[starts.Count - 1]; if (gap == subStride || gap == itemStride) starts.Add(rs); }
            }
            if (starts.Count == 0) return null;

            var rows = new List<Row>();
            float t = 0f;
            for (int i = 0; i < starts.Count; i++)
            {
                int rs = starts[i];
                if (rs + 28 + 256 > d.Length) break;          // need room for v244/248/252
                string mot = Ascii(d, rs + 12, 16).Split('\0')[0];
                int midOff = rs + 28;
                int v244 = BitConverter.ToInt32(d, midOff + 244);
                int v248 = BitConverter.ToInt32(d, midOff + 248);
                float v252 = BitConverter.ToSingle(d, midOff + 252);
                rows.Add(new Row { Mot = mot, StartF = v244, EndF = v248, Dur = v252, TStart = t });
                t += v252;
            }
            return new DpsLoader { Rows = rows.ToArray(), Total = t };
        }

        /// <summary>Active motion + interpolated frame at dance time t (seconds).</summary>
        public void Sample(float t, out string mot, out float frame) => Sample(t, out mot, out frame, out _);

        /// <summary>
        /// True when row <paramref name="row"/> picks the SAME clip up where row <paramref name="prev"/> left it, so the
        /// two slices are one continuous run of the motion and the pose needs no hand-off. The official rows write that
        /// seam two ways and MIX them inside one file (12459 does both): 84% continue at EndF+1, 12% repeat EndF. Anything
        /// else — a rewind (10027 wdance0101 192→83), a jump, another clip, a non-adjacent row — is a real cut.
        /// </summary>
        public bool SliceContinues(int prev, int row)
        {
            if (Rows == null || row != prev + 1 || prev < 0 || row >= Rows.Length) return false;
            var a = Rows[prev]; var b = Rows[row];
            if (!string.Equals(a.Mot, b.Mot, StringComparison.OrdinalIgnoreCase)) return false;
            return b.StartF == a.EndF || b.StartF == a.EndF + 1;
        }

        /// <summary>
        /// Frames row <paramref name="row"/> travels over its duration. A slice that runs on into the next one covers
        /// exactly the gap to that row's FIRST frame, so the two frame ramps meet: at ratio 1 this row lands on
        /// <c>Rows[row+1].StartF</c> whichever way the seam is written (EndF+1 → span EndF-StartF+1, repeated EndF →
        /// span EndF-StartF). Sampling StartF..EndF instead would replay or skip a frame at every boundary — invisible
        /// in a slow phrase, a hard jolt in a fast one (wdance0238's 432-536 backflip peaks at 39°/frame, and the
        /// official choreographies cut it right down the middle: 15085 at 432, 10731 at 474, 12459 at 519).
        /// A slice that does NOT continue keeps StartF..EndF: its last frame is the pose the crossfade hands off FROM.
        /// </summary>
        public float SliceSpan(int row)
        {
            if (Rows == null || row < 0 || row >= Rows.Length) return 0f;
            var r = Rows[row];
            return SliceContinues(row, row + 1) ? Rows[row + 1].StartF - r.StartF : r.EndF - r.StartF;
        }

        /// <summary>
        /// Active motion + interpolated frame at dance time t (seconds), plus the INDEX of the row supplying them.
        /// Callers must crossfade the pose whenever <paramref name="row"/> changes AND the new slice does not continue
        /// the old one (<see cref="SliceContinues"/>): ~1% of the official rows step BACKWARD at the seam
        /// (10027 wdance0101 frame 192 → 83, 10410 wdance0351 227 → 0), and without the blend the dancer visibly
        /// rewinds a beat and jumps back in ("同一個 mot 切 row 突然回朔"). The other 96% run straight on through the
        /// clip, where blending is pure damage: the 0.5 s hand-off freezes the pose the boundary was crossed on and
        /// eases toward a clip that keeps moving, so a fast phrase stalls and then overshoots catching up.
        /// </summary>
        public void Sample(float t, out string mot, out float frame, out int row)
        {
            if (Rows.Length == 0) { mot = null; frame = 0; row = -1; return; }
            if (t <= 0f) { row = 0; mot = Rows[0].Mot; frame = Rows[0].StartF; return; }
            if (t >= Total) { row = Rows.Length - 1; var last = Rows[row]; mot = last.Mot; frame = last.EndF; return; }
            int lo = 0, hi = Rows.Length;                     // largest row with TStart <= t
            while (lo < hi) { int m = (lo + hi) / 2; if (Rows[m].TStart <= t) lo = m + 1; else hi = m; }
            row = Math.Max(0, lo - 1);
            var r = Rows[row];
            float ratio = r.Dur > 1e-6f ? (t - r.TStart) / r.Dur : 0f;
            mot = r.Mot;
            frame = r.StartF + UnityEngine.Mathf.Clamp01(ratio) * SliceSpan(row);
        }

        private static bool IsName(byte b) => (b >= '0' && b <= '9') || (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || b == '_';
        private static bool IsM(byte b) => b == 'm' || b == 'M';
        private static bool IsO(byte b) => b == 'o' || b == 'O';
        private static bool IsT(byte b) => b == 't' || b == 'T';
        private static string Ascii(byte[] d, int o, int n)
        {
            int len = Math.Min(n, d.Length - o); if (len <= 0) return "";
            return System.Text.Encoding.ASCII.GetString(d, o, len);
        }
    }
}
