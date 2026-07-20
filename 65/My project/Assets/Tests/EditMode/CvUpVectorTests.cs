using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// Covers the ".cv" per-keyframe UP vector — the middle array that decodes the auto-director's roll (SDO
    /// 傾斜地圖). It used to be discarded as a "mid control point", so every camera rendered level; these tests
    /// pin that (a) the up array is parsed at the right offset (eye/target stay unshifted), (b) it is sampled and
    /// looped like eye/target, (c) a non-vertical up actually rolls a Unity camera, and (d) degenerate up is safe.
    /// </summary>
    public class CvUpVectorTests
    {
        // A ~15° roll: up = (sin15, cos15, 0). Angle from world-up = 15°.
        static readonly Vector3 Tilt15 = new Vector3(Mathf.Sin(15f * Mathf.Deg2Rad), Mathf.Cos(15f * Mathf.Deg2Rad), 0f);

        // Build a "CV0000000003" blob: magic(12) frameCount eyeFlag moveFlag keyCount | eye[16/key] up[12/key] tgt[16/key].
        // When eyeStatic/tgtStatic are false the corresponding array is per-key (keyCount entries); up is per-key
        // unless BOTH eye and target are static (matches nUp = eyeFlag && moveFlag in the decompiled loader).
        static byte[] BuildCv(Vector3[] eye, Vector3[] up, Vector3[] tgt, int keyCount, bool eyeStatic, bool tgtStatic, int frameCount = 1000)
        {
            var ms = new System.IO.MemoryStream();
            var bw = new System.IO.BinaryWriter(ms);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("CV0000000003"));
            bw.Write(frameCount);
            bw.Write((byte)(eyeStatic ? 1 : 0));
            bw.Write((byte)(tgtStatic ? 1 : 0));
            bw.Write(keyCount);
            void V16(Vector3 v) { bw.Write(v.x); bw.Write(v.y); bw.Write(v.z); bw.Write(0f); }
            void V12(Vector3 v) { bw.Write(v.x); bw.Write(v.y); bw.Write(v.z); }
            foreach (var v in eye) V16(v);
            foreach (var v in up) V12(v);
            foreach (var v in tgt) V16(v);
            return ms.ToArray();
        }

        [Test]
        public void UpArray_ParsedWithoutShifting_EyeOrTarget()
        {
            var eye = new[] { new Vector3(1, 2, 3), new Vector3(4, 5, 6) };
            var up  = new[] { Tilt15, Tilt15 };
            var tgt = new[] { new Vector3(7, 8, 9), new Vector3(10, 11, 12) };
            var cv = CvLoader.Load(BuildCv(eye, up, tgt, 2, false, false));
            Assert.NotNull(cv);
            Assert.AreEqual(2, cv.Up.Length, "up is per-key when eye/target animated");

            // Check the parsed arrays directly: if the up array were mis-sized/mis-offset, eye[1] or target[1]
            // would read garbage. (Sample loops — Mathf.Repeat(1,1)==0 — so assert on the arrays, not Sample(1).)
            Assert.Less(Vector3.Distance(cv.Eye[0], eye[0]), 1e-4f, "eye[0]");
            Assert.Less(Vector3.Distance(cv.Eye[1], eye[1]), 1e-4f, "eye[1] still correct → up array did not shift the offsets");
            Assert.Less(Vector3.Distance(cv.Target[0], tgt[0]), 1e-4f, "target[0]");
            Assert.Less(Vector3.Distance(cv.Target[1], tgt[1]), 1e-4f, "target[1] still correct");
            Assert.Less(Vector3.Distance(cv.Up[0], Tilt15), 1e-4f, "up[0] parsed");
            Assert.Less(Vector3.Distance(cv.Up[1], Tilt15), 1e-4f, "up[1] parsed");

            cv.Sample(0f, out var e0, out var t0, out var u0);
            Assert.Less(Vector3.Distance(e0, eye[0]), 1e-4f);
            Assert.Less(Vector3.Distance(t0, tgt[0]), 1e-4f);
            Assert.Less(Vector3.Distance(u0, Tilt15), 1e-4f);
        }

        [Test]
        public void VerticalUp_StaysVertical()
        {
            var cv = CvLoader.Load(BuildCv(new[] { Vector3.zero, Vector3.zero }, new[] { Vector3.up, Vector3.up },
                                           new[] { Vector3.forward, Vector3.forward }, 2, false, false));
            cv.Sample(0.5f, out _, out _, out var up);
            Assert.Less(Vector3.Angle(up, Vector3.up), 0.01f, "a vertical .cv up must stay level (no phantom roll)");
        }

        [Test]
        public void StaticUp_IsSingleEntry()
        {
            // eyeStatic && tgtStatic => nUp = 1 (the loader packs a single up key).
            var cv = CvLoader.Load(BuildCv(new[] { new Vector3(0, 0, -5) }, new[] { Tilt15 }, new[] { Vector3.zero }, 4, true, true));
            Assert.NotNull(cv);
            Assert.AreEqual(1, cv.Up.Length);
            cv.Sample(0.3f, out _, out _, out var up);
            Assert.Less(Vector3.Distance(up, Tilt15), 1e-4f, "a static up applies on every frame");
        }

        [Test]
        public void ZeroUp_FallsBackToWorldUp()
        {
            var cv = CvLoader.Load(BuildCv(new[] { Vector3.zero, Vector3.zero }, new[] { Vector3.zero, Vector3.zero },
                                           new[] { Vector3.forward, Vector3.forward }, 2, false, false));
            cv.Sample(0f, out _, out _, out var up);
            Assert.AreEqual(Vector3.up, up, "a degenerate (0,0,0) up must fall back to world-up so LookAt never NaNs");
        }

        [Test]
        public void TiltedUp_RollsTheCamera()
        {
            var cv = CvLoader.Load(BuildCv(new[] { new Vector3(0, 0, -10) }, new[] { Tilt15 }, new[] { Vector3.zero }, 1, true, true));
            cv.Sample(0f, out var eye, out var tgt, out var up);
            var go = new GameObject("cvcam");
            try
            {
                go.transform.position = eye;
                go.transform.LookAt(tgt, up);   // exactly what ScreenGameplay does
                Assert.That(Vector3.Angle(go.transform.up, Vector3.up), Is.EqualTo(15f).Within(0.5f),
                    "feeding the .cv up to LookAt rolls the camera ~15° — this is the tilted-map look the remake was missing");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
