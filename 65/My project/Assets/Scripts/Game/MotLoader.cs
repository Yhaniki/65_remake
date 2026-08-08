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

        // 同一個 bone_id 在檔案裡出現不只一次時,這裡留下**全部**候選 (依檔案順序);沒有重複的檔案永遠是 null。
        // 官方 7,389 支 MOT 只有 16 支這樣,全是掛件 (翅膀/尾巴) 的 _G rig。挑法見 ResolveDuplicateNodes。
        private Dictionary<int, List<Node>> _dupes;
        private HrcLoader _resolvedFor;   // Bones 目前這組選擇是照哪副骨架挑的 (冪等用)

        /// <summary>這支 clip 裡有沒有「同一根骨兩份 node」(需要 <see cref="ResolveDuplicateNodes"/> 才能定案)。</summary>
        public bool HasDuplicateNodes => _dupes != null;

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
                if (m.Bones.TryGetValue(bid, out var prev))   // 同一根骨的第二份 node → 兩份都留著,等 ResolveDuplicateNodes 挑
                {
                    if (m._dupes == null) m._dupes = new Dictionary<int, List<Node>>();
                    if (!m._dupes.TryGetValue(bid, out var cands)) { cands = new List<Node> { prev }; m._dupes[bid] = cands; }
                    cands.Add(n);
                }
                m.Bones[bid] = n;
            }
            m.MaxTime = o + 4 <= d.Length ? BitConverter.ToSingle(d, o) : 0f;
            return m;
        }

        /// <summary>
        /// 決定「同一根骨被寫了兩份 node」時要用哪一份:**position 首鍵最接近該骨 HRC rest 平移**的那一份。
        ///
        /// 官方 7,389 支 MOT 裡有 16 支重複 (全是翅膀/尾巴的 _G rig,美術存檔時留下的殘骨)。其中 14 支「檔案裡後面
        /// 那份」剛好就等於 rest,所以單純「後者覆寫」看不出問題;但 021089/021090 (MOTION White Gumiho 白色九尾狐
        /// 男/女) 最後那份 root (Bip01) 是別的掛點留下的殘留 —— pos (0,37.01,0) 而 rest 是 (0,33.92,3.97)、繞 Y 還差
        /// 180°。照後者覆寫,五條尾巴會整組轉到身體前面蓋住臉 (使用者回報「翅膀的 motion 方向前後相反、畫到臉的
        /// 前面」)。以「對得上 rest」當判準,16 支全部都選到正確的那一份。
        ///
        /// 兩份都對得上時 (025924/025925 是整份 node 複製) 維持原本的「取最後一份」。這副骨架沒有的骨也維持原樣。
        /// 冪等 —— 同一副骨架只算一次,所以可以每幀從 <see cref="SdoAvatar"/> 呼叫。
        /// </summary>
        public void ResolveDuplicateNodes(HrcLoader hrc)
        {
            if (_dupes == null || hrc == null || hrc.LocalRest == null || ReferenceEquals(_resolvedFor, hrc)) return;
            _resolvedFor = hrc;
            foreach (var kv in _dupes)
            {
                int bone = kv.Key;
                if (bone < 0 || bone >= hrc.LocalRest.Length) continue;   // 這副骨架沒這根骨 → 沒判準可用,維持原樣
                Vector3 rest = hrc.LocalRest[bone].GetColumn(3);
                var cands = kv.Value;
                var best = cands[cands.Count - 1];                        // 基準 = 原本的行為 (後者覆寫)
                float bestD = RestDistance(best, rest);
                for (int i = 0; i < cands.Count - 1; i++)
                {
                    float d = RestDistance(cands[i], rest);
                    if (d < bestD - 1e-3f) { best = cands[i]; bestD = d; }   // 嚴格更近才換 → 平手保留最後一份
                }
                Bones[bone] = best;
            }
        }

        // 這份 node 的起始 position 離骨頭 rest 平移多遠 (重複的那些 node 位移軌都是單一靜態 key)。
        private static float RestDistance(Node n, Vector3 rest)
        {
            if (n == null || n.Pc < 1 || n.Pos == null || n.Pos.Length < 3) return float.MaxValue;
            return Vector3.Distance(new Vector3(n.Pos[0], n.Pos[1], n.Pos[2]), rest);
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

        // sample the scale (sx,sy,sz) at frame t — LERP, same layout as SamplePos (Scl stride = xyz+time).
        public static Vector3 SampleScale(Node n, float t)
        {
            int c = n.Sc;
            if (c == 1) return new Vector3(n.Scl[0], n.Scl[1], n.Scl[2]);
            int i = FindSeg(n.Scl, 4, c, t, out float f);
            int a = i * 4, b = (i + 1) * 4;
            return new Vector3(n.Scl[a] + (n.Scl[b] - n.Scl[a]) * f,
                               n.Scl[a + 1] + (n.Scl[b + 1] - n.Scl[a + 1]) * f,
                               n.Scl[a + 2] + (n.Scl[b + 2] - n.Scl[a + 2]) * f);
        }

        // true if this bone's scale track actually animates (more than one distinct keyframe value). Lets callers
        // (SdoAvatar.UpdateBoneFollowers) apply the MOT scale only for clips that use it — e.g. the SCN0008 delta_line
        // bars extend via scale.Y 0→2.028 — while leaving the bind-scale path in control for clips whose scale is a
        // constant 1 (so rigid props like the SCN0014 sea screen are unaffected).
        public static bool ScaleVaries(Node n)
        {
            if (n == null || n.Sc <= 1) return false;
            float x0 = n.Scl[0], y0 = n.Scl[1], z0 = n.Scl[2];
            for (int i = 1; i < n.Sc; i++)
            {
                int a = i * 4;
                if (Mathf.Abs(n.Scl[a] - x0) > 1e-4f || Mathf.Abs(n.Scl[a + 1] - y0) > 1e-4f || Mathf.Abs(n.Scl[a + 2] - z0) > 1e-4f) return true;
            }
            return false;
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
