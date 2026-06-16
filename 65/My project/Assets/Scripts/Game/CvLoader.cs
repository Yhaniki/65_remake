using System;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Loads an SDO ".cv" camera path (decompiled Camera_ctor_FromFile / Camera_GetEyePos / GetTargetPos).
    /// Format "CV0000000003": magic(12) | frameCount(u32) | eyeFlag(1) | moveFlag(1) | keyCount(u32) | then
    /// eye[16B/key] (xyz+pad) | mid[12B/key] | target[16B/key]; if a flag is set that array is a single entry.
    /// The game indexes eye[frame]/target[frame] and feeds LookAtLH(eye,target,up)+PerspectiveFov. These
    /// CAMERA/1/CAM* paths are AVATAR cameras (knee-height dolly across the front, looking at chest height).
    /// </summary>
    public sealed class CvLoader
    {
        public int FrameCount, KeyCount;
        public Vector3[] Eye;      // per-key eye positions
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
            int nMid = f13 != 0 ? 1 : keyCount;
            int nTgt = moveFlag != 0 ? 1 : keyCount;
            if (p + nEye * 16 + nMid * 12 + nTgt * 16 > d.Length) return null;
            var eye = new Vector3[nEye];
            for (int i = 0; i < nEye; i++) { int o = p + i * 16; eye[i] = new Vector3(F(d, o), F(d, o + 4), F(d, o + 8)); }
            p += nEye * 16 + nMid * 12;     // skip mid
            var tgt = new Vector3[nTgt];
            for (int i = 0; i < nTgt; i++) { int o = p + i * 16; tgt[i] = new Vector3(F(d, o), F(d, o + 4), F(d, o + 8)); }
            return new CvLoader { FrameCount = frameCount, KeyCount = keyCount, Eye = eye, Target = tgt };
        }

        /// <summary>Eye/target at normalized progress t∈[0,1] (loops the dolly). Verbatim SDO world coords — D3D9
        /// and Unity are both LH, and the avatar/scene meshes are no longer X-negated, so the camera isn't either.</summary>
        public void Sample(float t, out Vector3 eye, out Vector3 target)
        {
            float fi = Mathf.Repeat(t, 1f) * (Eye.Length - 1);
            eye = Lerp(Eye, fi);
            target = Target.Length == 1 ? Target[0] : Lerp(Target, fi);
        }

        private static Vector3 Lerp(Vector3[] a, float fi)
        {
            int i0 = Mathf.Clamp((int)fi, 0, a.Length - 1); int i1 = Mathf.Min(i0 + 1, a.Length - 1);
            return Vector3.Lerp(a[i0], a[i1], fi - i0);
        }
        private static float F(byte[] d, int o) => BitConverter.ToSingle(d, o);
    }
}
