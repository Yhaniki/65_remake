using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Loads an SDO ".mot" motion ("Animation" 16-byte header, then per-bone nodes:
    /// bone_id|flag|rot_cnt|scale_cnt|pos_cnt then rot(quat+time)*N, scale(xyz+time)*N, pos(xyz+time)*N;
    /// 4-byte float max_time footer). bone_id indexes the HRC bone array. time = integer frame index.
    /// Rotation interpolation = NLERP (shortest path); position = LERP. See MOT_HRC_FORMAT.md.
    /// </summary>
    public sealed class MotLoader
    {
        public sealed class Node { public float[] Rot; public float[] Scl; public float[] Pos; public int Rc, Sc, Pc; }
        public readonly Dictionary<int, Node> Bones = new Dictionary<int, Node>();
        public float MaxTime;

        public static MotLoader Load(byte[] d)
        {
            if (d == null || d.Length < 16 || System.Text.Encoding.ASCII.GetString(d, 0, 9) != "Animation") return null;
            var m = new MotLoader();
            int o = 16;
            while (o + 20 <= d.Length)
            {
                int bid = BitConverter.ToInt32(d, o);
                int flag = BitConverter.ToInt32(d, o + 4);
                int rc = BitConverter.ToInt32(d, o + 8), sc = BitConverter.ToInt32(d, o + 12), pc = BitConverter.ToInt32(d, o + 16);
                if (flag != 0 || rc == 0 || sc == 0 || pc == 0) break;
                o += 20;
                var n = new Node { Rc = rc, Sc = sc, Pc = pc, Rot = new float[rc * 5], Scl = new float[sc * 4], Pos = new float[pc * 4] };
                for (int i = 0; i < rc * 5; i++) { n.Rot[i] = BitConverter.ToSingle(d, o); o += 4; }
                for (int i = 0; i < sc * 4; i++) { n.Scl[i] = BitConverter.ToSingle(d, o); o += 4; }
                for (int i = 0; i < pc * 4; i++) { n.Pos[i] = BitConverter.ToSingle(d, o); o += 4; }
                m.Bones[bid] = n;
            }
            m.MaxTime = o + 4 <= d.Length ? BitConverter.ToSingle(d, o) : 0f;
            return m;
        }

        // sample rotation quaternion (qx,qy,qz,qw) at frame t — NLERP with sign fix
        public static void SampleRot(Node n, float t, out float qx, out float qy, out float qz, out float qw)
        {
            int c = n.Rc;
            if (c == 1) { qx = n.Rot[0]; qy = n.Rot[1]; qz = n.Rot[2]; qw = n.Rot[3]; return; }
            int i = FindSeg(n.Rot, 5, c, t, out float f);
            int a = i * 5, b = (i + 1) * 5;
            float bx = n.Rot[b], by = n.Rot[b + 1], bz = n.Rot[b + 2], bw = n.Rot[b + 3];
            float ax = n.Rot[a], ay = n.Rot[a + 1], az = n.Rot[a + 2], aw = n.Rot[a + 3];
            if (ax * bx + ay * by + az * bz + aw * bw < 0f) { bx = -bx; by = -by; bz = -bz; bw = -bw; }
            qx = ax + (bx - ax) * f; qy = ay + (by - ay) * f; qz = az + (bz - az) * f; qw = aw + (bw - aw) * f;
            float len = Mathf.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (len > 1e-6f) { qx /= len; qy /= len; qz /= len; qw /= len; }
        }

        public static Vector3 SamplePos(Node n, float t)
        {
            int c = n.Pc;
            if (c == 1) return new Vector3(n.Pos[0], n.Pos[1], n.Pos[2]);
            int i = FindSeg(n.Pos, 4, c, t, out float f);
            int a = i * 4, b = (i + 1) * 4;
            return new Vector3(n.Pos[a] + (n.Pos[b] - n.Pos[a]) * f,
                               n.Pos[a + 1] + (n.Pos[b + 1] - n.Pos[a + 1]) * f,
                               n.Pos[a + 2] + (n.Pos[b + 2] - n.Pos[a + 2]) * f);
        }

        // find the keyframe segment [i, i+1] containing time t; returns i and lerp fraction f. stride = comps incl. time.
        private static int FindSeg(float[] keys, int stride, int count, float t, out float f)
        {
            int timeIdx = stride - 1;
            for (int i = 0; i < count - 1; i++)
            {
                float t0 = keys[i * stride + timeIdx], t1 = keys[(i + 1) * stride + timeIdx];
                if (t <= t1 || i == count - 2) { f = t1 <= t0 ? 0f : Mathf.Clamp01((t - t0) / (t1 - t0)); return i; }
            }
            f = 0f; return 0;
        }
    }
}
