using System;
using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>
    /// Loads an SDO ".dps" dance script (PAS00003) — the per-song choreography that sequences motion
    /// SLICES along the music timeline. Ported from bms_sdo/dps_archive.py: rows are located by their
    /// ".mot" filename (row_start = motNameOffset - 12), accepted only at the 305/317-byte strides (so a
    /// ".mot" string inside a mid block is skipped). Each row: preamble(12) + name(16) + mid; the mid holds
    /// start_frame@244, end_frame@248, duration_sec@252. Rows play sequentially (time accumulates by dur).
    /// Sample(t) -> (motName, interpolated frame) drives the avatar in sync with the song.
    /// </summary>
    public sealed class DpsLoader
    {
        public struct Row { public string Mot; public int StartF, EndF; public float Dur; public float TStart; }
        public Row[] Rows;
        public float Total;

        public static DpsLoader Load(byte[] d)
        {
            if (d == null || d.Length < 16 || Ascii(d, 0, 8) != "PAS00003") return null;
            var starts = new List<int>();
            for (int i = 0; i + 4 <= d.Length; i++)
            {
                if (d[i] != (byte)'.') continue;
                if (!(IsM(d[i + 1]) && IsO(d[i + 2]) && IsT(d[i + 3]))) continue;
                int ns = i; while (ns > 0 && IsName(d[ns - 1])) ns--;
                if (ns == i) continue;                       // no name chars before ".mot"
                int rs = ns - 12; if (rs < 12) continue;
                if (starts.Count == 0) starts.Add(rs);
                else { int gap = rs - starts[starts.Count - 1]; if (gap == 305 || gap == 317) starts.Add(rs); }
            }
            if (starts.Count == 0) return null;

            var rows = new List<Row>();
            float t = 0f;
            for (int i = 0; i < starts.Count; i++)
            {
                int rs = starts[i];
                int rsz = (i + 1 < starts.Count) ? starts[i + 1] - rs : 317;
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
        public void Sample(float t, out string mot, out float frame)
        {
            if (Rows.Length == 0) { mot = null; frame = 0; return; }
            if (t <= 0f) { mot = Rows[0].Mot; frame = Rows[0].StartF; return; }
            if (t >= Total) { var r = Rows[Rows.Length - 1]; mot = r.Mot; frame = r.EndF; return; }
            int lo = 0, hi = Rows.Length;                     // largest row with TStart <= t
            while (lo < hi) { int m = (lo + hi) / 2; if (Rows[m].TStart <= t) lo = m + 1; else hi = m; }
            var row = Rows[Math.Max(0, lo - 1)];
            float ratio = row.Dur > 1e-6f ? (t - row.TStart) / row.Dur : 0f;
            mot = row.Mot;
            frame = row.StartF + UnityEngine.Mathf.Clamp01(ratio) * (row.EndF - row.StartF);
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
