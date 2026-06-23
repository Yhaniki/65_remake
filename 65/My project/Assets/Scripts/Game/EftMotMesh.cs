using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Renders an EFT-attached .mot rigid mesh (the SCN0008 delta_line colour bars) by replicating the engine's EXACT
    /// FK + per-vertex skin in D3DX ROW-MAJOR — verified 1:1 against a Frida capture of sdo_stand_alone
    /// (Skeleton_ComputeWorldMtx_00409860): per bone local = R·S·T, world = local·parent (root parent = identity =
    /// effect-relative; this component's GameObject supplies the owner = particle world). Each colour bar's bone-local
    /// verts are baked by its bone's world matrix every frame (row-vector × row-major matrix).
    ///
    /// NOT the avatar SdoAvatar FK: that uses Unity's column-major Transforms, which can't represent the engine's
    /// rotate-then-scale (non-uniform .mot scale) and scattered the bars in 3D. The quat→matrix below is the D3DX
    /// row-major form (the avatar path used its transpose — the actual root cause of the scatter).
    /// </summary>
    public sealed class EftMotMesh : MonoBehaviour
    {
        HrcLoader _hrc; MotLoader _mot;
        struct Bar { public int bone; public Mesh mesh; public Vector3[] src; }
        readonly List<Bar> _bars = new List<Bar>();
        public float Fps = 30f;             // unused (EftEffect drives FrameOverride at the engine's 12.5fps); kept for API.
        public float FrameOverride = -1f;   // EftEffect sets this = min(ageTicks*0.25, MaxTime) each tick → the .mot plays
                                            // ONCE over the particle life (engine 030:7616, frame += 0.25/tick = 12.5fps).

        // SELF-TEST: dump each bar's WORLD transform + frame to a file, to diff against the official Frida capture.
        public static bool Dbg = false;
        static System.IO.StreamWriter _dw;
        static void DbgLog(string s) { if (_dw == null) { try { _dw = new System.IO.StreamWriter("H:/65_remake/mysim-delta.log", false) { AutoFlush = true }; } catch { } } if (_dw != null) _dw.WriteLine(s); }

        public void Setup(HrcLoader hrc, MotLoader mot) { _hrc = hrc; _mot = mot; }
        public void AddBar(int bone, Mesh mesh, Vector3[] src) { if (bone >= 0 && mesh != null && src != null) _bars.Add(new Bar { bone = bone, mesh = mesh, src = src }); }

        void LateUpdate()
        {
            if (_hrc == null || _mot == null || _bars.Count == 0 || _hrc.Names == null) return;
            // The frame is driven by EftEffect (per-particle age at the engine's 12.5fps, clamped = play once). Each
            // re-fired instance has its own age, so the 40-tick-younger instance naturally lags by 10 frames = the overlap.
            float t = FrameOverride >= 0f ? FrameOverride : 0f;
            int bc = _hrc.Names.Length;
            bool dbgFrame = Dbg && Time.frameCount % 10 == 0;
            var world = new Matrix4x4[bc];   // ROW-MAJOR storage (indexed [row,col]); NOT a Unity column-major transform
            var localArr = dbgFrame ? new Matrix4x4[bc] : null;   // only kept for the self-test dump
            for (int i = 0; i < bc; i++)
            {
                Matrix4x4 local = _mot.Bones.TryGetValue(i, out var node) ? LocalRST(node, t)
                                : (_hrc.RawRest != null && i < _hrc.RawRest.Length ? _hrc.RawRest[i] : Matrix4x4.identity);
                if (localArr != null) localArr[i] = local;
                int p = (_hrc.Parent != null && i < _hrc.Parent.Length) ? _hrc.Parent[i] : -1;
                world[i] = (p >= 0 && p < bc) ? RowMul(local, world[p]) : local;   // world = local·parent; root parent = identity
            }
            foreach (var bar in _bars)
            {
                if (bar.bone < 0 || bar.bone >= bc || bar.mesh == null) continue;
                var w = world[bar.bone];
                var dst = new Vector3[bar.src.Length];
                for (int v = 0; v < bar.src.Length; v++) dst[v] = RowPoint(bar.src[v], w);   // v' = [v,1]·world (row-vector)
                bar.mesh.vertices = dst; bar.mesh.RecalculateBounds();
                if (dbgFrame)
                {
                    // per-bar VISIBILITY: a colour renders only when BOTH width sx>0 AND length sy>0 (sx is the staggered
                    // on/off gate). Log instance id + clip frame t + sx/sy/vis so the combined colour sequence (across the
                    // 2 instances) can be checked against the official 紅→紅藍→藍黃→黃.
                    Vector3 sc = _mot.Bones.TryGetValue(bar.bone, out var n2) ? MotLoader.SampleScale(n2, t) : Vector3.one;
                    bool vis = sc.x > 0.02f && sc.y > 0.02f;
                    Vector3 o = transform.TransformPoint(RowPoint(Vector3.zero, w));
                    string nm = (_hrc.Names != null && bar.bone < _hrc.Names.Length) ? _hrc.Names[bar.bone] : ("bone" + bar.bone);
                    string col = nm.Contains("aka") ? "R" : nm.Contains("ao") ? "B" : nm.Contains("ki") ? "Y" : "?";
                    DbgLog($"f{Time.frameCount} inst={GetInstanceID()} t={t:F2} {col} origin=({o.x:F0},{o.z:F0}) sx={sc.x:F2} sy={sc.y:F2} vis={(vis ? 1 : 0)}");
                }
            }
        }

        // local = S·R·T (row-major, row vectors): scale-then-rotate-then-translate. Verified 1:1 against the Frida
        // capture (eft_delta_bones.log): the engine scales the ROW (S·R = scale row i by s_i), NOT the column. With
        // S·R the mesh's local Y maps to s_y·R_row1, so the bar length follows scale.Y (the channel that ramps 0→2.028)
        // and the X/Z width follows scale.X/Z. (The old R·S = column-scale routed the length onto scale.Z — the width
        // pulse — so the bars extended on the wrong axis at the wrong time = "角度/順序/時間全錯".)
        static Matrix4x4 LocalRST(MotLoader.Node n, float t)
        {
            MotLoader.SampleRot(n, t, out float qx, out float qy, out float qz, out float qw);
            Vector3 s = MotLoader.SampleScale(n, t);
            Vector3 p = MotLoader.SamplePos(n, t);
            var m = QuatRowMajor(qx, qy, qz, qw);
            m[0, 0] *= s.x; m[0, 1] *= s.x; m[0, 2] *= s.x;   // S·R : row i scaled by s_i (scale BEFORE rotate)
            m[1, 0] *= s.y; m[1, 1] *= s.y; m[1, 2] *= s.y;
            m[2, 0] *= s.z; m[2, 1] *= s.z; m[2, 2] *= s.z;
            m[3, 0] = p.x; m[3, 1] = p.y; m[3, 2] = p.z; m[3, 3] = 1f;   // ·T : translation in row 3
            return m;
        }

        // D3DXMatrixRotationQuaternion as a ROW-MAJOR matrix (the avatar QuatToLocal used the transpose of this).
        static Matrix4x4 QuatRowMajor(float x, float y, float z, float w)
        {
            var m = Matrix4x4.identity;
            float n = x * x + y * y + z * z + w * w;
            if (n < 1e-9f) return m;
            float s = 2f / n;
            float xx = x * x * s, yy = y * y * s, zz = z * z * s, xy = x * y * s, xz = x * z * s, yz = y * z * s, wx = w * x * s, wy = w * y * s, wz = w * z * s;
            m[0, 0] = 1f - (yy + zz); m[0, 1] = xy - wz;       m[0, 2] = xz + wy;
            m[1, 0] = xy + wz;        m[1, 1] = 1f - (xx + zz); m[1, 2] = yz - wx;
            m[2, 0] = xz - wy;        m[2, 1] = yz + wx;        m[2, 2] = 1f - (xx + yy);
            return m;
        }

        // row-major multiply: C = A·B  (C[i,j] = Σ A[i,k]·B[k,j])
        static Matrix4x4 RowMul(Matrix4x4 a, Matrix4x4 b)
        {
            var c = new Matrix4x4();
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    float s = 0f;
                    for (int k = 0; k < 4; k++) s += a[i, k] * b[k, j];
                    c[i, j] = s;
                }
            return c;
        }

        // row-vector × row-major matrix: [v.x,v.y,v.z,1] · m  →  point
        static Vector3 RowPoint(Vector3 v, Matrix4x4 m) => new Vector3(
            v.x * m[0, 0] + v.y * m[1, 0] + v.z * m[2, 0] + m[3, 0],
            v.x * m[0, 1] + v.y * m[1, 1] + v.z * m[2, 1] + m[3, 1],
            v.x * m[0, 2] + v.y * m[1, 2] + v.z * m[2, 2] + m[3, 2]);
    }
}
