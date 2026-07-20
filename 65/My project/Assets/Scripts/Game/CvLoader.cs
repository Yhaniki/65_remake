using System;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Loads an SDO ".cv" camera path (decompiled Camera_ctor_FromFile / Camera_GetEyePos / GetTargetPos).
    /// Format "CV0000000003": magic(12) | frameCount(u32) | eyeFlag(1) | moveFlag(1) | keyCount(u32) | then
    /// eye[16B/key] (xyz+pad) | up[12B/key] (xyz) | target[16B/key]; if a flag is set that array is a single entry.
    /// The game indexes eye[frame]/target[frame]/UP[frame] and feeds LookAtLH(eye,target,up)+PerspectiveFov.
    ///
    /// ⚠️ The middle array is the per-keyframe UP VECTOR — NOT a "mid control point" (that earlier note was wrong).
    /// Camera_Update_0040c920 reads it at (cam+0x20)[frame] and passes it as the 4th LookAtLH arg; a non-vertical
    /// up ROLLS the camera, which is what makes some auto-director shots look like a tilted map (SDO 傾斜地圖 —
    /// egypt/ghosthill ~40°, palace ~30°, and the 墓地/"mu di" director 3ren/6ren/shan up to ~60°). The up-static
    /// flag (cam+0x13) = eyeFlag && moveFlag, matching nUp below. Discarding this array = every camera stays level.
    /// </summary>
    public sealed class CvLoader
    {
        public int FrameCount, KeyCount;
        public Vector3[] Eye;      // per-key eye positions
        public Vector3[] Up;       // per-key up vectors (LookAtLH 4th arg); non-vertical => camera roll (tilted map)
        public Vector3[] Target;   // per-key target positions (often 1 = static)

        public static CvLoader Load(byte[] d)
        {
            // Camera_ctor_FromFile_0040db80 reads three identical layouts: "CV0000000002/3/4". The CV3 and CV4
            // parse blocks are byte-identical in the decomp; accept both (CV2 omits the eye-static flag — rare,
            // unused by the dance cams, so skipped).
            if (d == null || d.Length < 22) return null;
            string magic = System.Text.Encoding.ASCII.GetString(d, 0, 12);
            if (magic != "CV0000000003" && magic != "CV0000000004") return null;
            int p = 12;
            int frameCount = BitConverter.ToInt32(d, p); p += 4;
            byte eyeFlag = d[p++], moveFlag = d[p++];
            int keyCount = BitConverter.ToInt32(d, p); p += 4;
            if (keyCount <= 0 || keyCount > 100000) return null;
            int f13 = (eyeFlag != 0 && moveFlag != 0) ? 1 : 0;
            int nEye = eyeFlag != 0 ? 1 : keyCount;
            int nUp  = f13 != 0 ? 1 : keyCount;
            int nTgt = moveFlag != 0 ? 1 : keyCount;
            if (p + nEye * 16 + nUp * 12 + nTgt * 16 > d.Length) return null;
            var eye = new Vector3[nEye];
            for (int i = 0; i < nEye; i++) { int o = p + i * 16; eye[i] = new Vector3(F(d, o), F(d, o + 4), F(d, o + 8)); }
            p += nEye * 16;
            var up = new Vector3[nUp];
            for (int i = 0; i < nUp; i++) { int o = p + i * 12; up[i] = new Vector3(F(d, o), F(d, o + 4), F(d, o + 8)); }
            p += nUp * 12;
            var tgt = new Vector3[nTgt];
            for (int i = 0; i < nTgt; i++) { int o = p + i * 16; tgt[i] = new Vector3(F(d, o), F(d, o + 4), F(d, o + 8)); }
            return new CvLoader { FrameCount = frameCount, KeyCount = keyCount, Eye = eye, Up = up, Target = tgt };
        }

        /// <summary>Eye/target at normalized progress t∈[0,1] (loops the dolly). Up defaults to world-up; use the
        /// 4-arg overload to also get the .cv roll. Kept for callers that don't need the up vector.</summary>
        public void Sample(float t, out Vector3 eye, out Vector3 target) => Sample(t, out eye, out target, out _);

        /// <summary>Eye/target/UP at normalized progress t∈[0,1] (loops the dolly). Verbatim SDO world coords — D3D9
        /// and Unity are both LH, and the avatar/scene meshes are no longer X-negated, so the camera isn't either.
        /// The frame index runs 0..KeyCount-1 (each array clamps to its own length: a static array returns its one
        /// entry). A zero/degenerate up falls back to world-up so LookAt never NaNs.</summary>
        public void Sample(float t, out Vector3 eye, out Vector3 target, out Vector3 up)
        {
            int keys = Mathf.Max(1, KeyCount);
            float fi = Mathf.Repeat(t, 1f) * (keys - 1);
            eye = Lerp(Eye, fi);
            target = Target.Length == 1 ? Target[0] : Lerp(Target, fi);
            Vector3 u = (Up == null || Up.Length == 0) ? Vector3.up : Lerp(Up, fi);
            up = u.sqrMagnitude < 1e-8f ? Vector3.up : u;
        }

        private static Vector3 Lerp(Vector3[] a, float fi)
        {
            int i0 = Mathf.Clamp((int)fi, 0, a.Length - 1); int i1 = Mathf.Min(i0 + 1, a.Length - 1);
            return Vector3.Lerp(a[i0], a[i1], fi - i0);
        }
        private static float F(byte[] d, int o) => BitConverter.ToSingle(d, o);
    }
}
