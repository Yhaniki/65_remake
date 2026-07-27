using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// DPS slice sampling + the pose-source change test that arms the dancer's crossfade.
    /// Regression for "同一個 mot 切不同 row 會突然回朔一下再繼續跳": consecutive DPS rows often share one .mot, and
    /// ~1% of the official rows step BACKWARD in frame at that seam (10027 wdance0101 192→83, 10410 wdance0351 227→0).
    /// The blend used to be armed only when the MotLoader reference changed, so those seams were a hard cut. The
    /// original arms it on EVERY slice boundary (Dancer_AdvanceMotionStep → MotionDriver_PlayClip snapshots the live
    /// pose and resets the blend weight, never comparing clips). Pure logic, no Unity scene.
    /// </summary>
    public class DpsSliceBlendTests
    {
        // Three slices of ONE clip, laid out like the real 10027.DPS opening: row 1 rewinds to frame 0, row 2 jumps.
        private static DpsLoader SameClipRewind() => new DpsLoader
        {
            Rows = new[]
            {
                new DpsLoader.Row { Mot = "wdance0101.mot", StartF = 0,  EndF = 39,  Dur = 1.0f, TStart = 0f },
                new DpsLoader.Row { Mot = "wdance0101.mot", StartF = 0,  EndF = 44,  Dur = 1.0f, TStart = 1f },
                new DpsLoader.Row { Mot = "wdance0101.mot", StartF = 45, EndF = 126, Dur = 2.0f, TStart = 2f },
            },
            Total = 4f,
        };

        [Test]
        public void Sample_ReportsTheRowSupplyingTheFrame()
        {
            var dps = SameClipRewind();
            dps.Sample(0.5f, out _, out _, out int r0);
            dps.Sample(1.5f, out _, out _, out int r1);
            dps.Sample(3.0f, out _, out _, out int r2);
            Assert.AreEqual(0, r0);
            Assert.AreEqual(1, r1);
            Assert.AreEqual(2, r2);
        }

        [Test]
        public void Sample_ClampsRowAtBothEnds()
        {
            var dps = SameClipRewind();
            dps.Sample(-5f, out string mBefore, out float fBefore, out int rBefore);   // before the choreography
            dps.Sample(99f, out string mAfter, out float fAfter, out int rAfter);      // past Total
            Assert.AreEqual(0, rBefore);
            Assert.AreEqual("wdance0101.mot", mBefore);
            Assert.AreEqual(0f, fBefore, 1e-4f);
            Assert.AreEqual(2, rAfter);
            Assert.AreEqual(126f, fAfter, 1e-4f);
            Assert.AreEqual("wdance0101.mot", mAfter);
        }

        [Test]
        public void Sample_EmptyChoreography_ReportsNoRow()
        {
            var dps = new DpsLoader { Rows = new DpsLoader.Row[0], Total = 0f };
            dps.Sample(1f, out string mot, out float frame, out int row);
            Assert.IsNull(mot);
            Assert.AreEqual(0f, frame);
            Assert.AreEqual(-1, row);
        }

        [Test]
        public void Sample_StretchesEachSliceOverItsDuration()
        {
            var dps = SameClipRewind();
            dps.Sample(2.5f, out _, out float mid, out _);        // 25% into row 2 (45..126)
            Assert.AreEqual(45f + 0.25f * (126f - 45f), mid, 1e-3f);
            dps.Sample(0f, out _, out float atZero, out _);
            Assert.AreEqual(0f, atZero, 1e-4f);
        }

        [Test]
        public void Sample_RowBoundaryOfTheSameClipRewindsTheFrame()
        {
            // The seam this fix is about: end of row 0 is frame 39, the very next sample is row 1 at frame 0.
            var dps = SameClipRewind();
            dps.Sample(0.999f, out string a, out float fa, out int ra);
            dps.Sample(1.001f, out string b, out float fb, out int rb);
            Assert.AreEqual(a, b);                 // same clip …
            Assert.AreNotEqual(ra, rb);            // … different slice …
            Assert.Less(fb, fa);                   // … and the frame goes BACKWARD
        }

        [Test]
        public void PoseSourceChanged_TrueOnNewRowOfTheSameClip()
        {
            object clip = new object(), dps = new object();
            Assert.IsTrue(SdoAvatar.PoseSourceChanged(clip, clip, dps, dps, 0, 1));
        }

        [Test]
        public void PoseSourceChanged_FalseWhileTheSameSlicePlays()
        {
            object clip = new object(), dps = new object();
            Assert.IsFalse(SdoAvatar.PoseSourceChanged(clip, clip, dps, dps, 3, 3));
        }

        [Test]
        public void PoseSourceChanged_TrueOnClipSwitch()
        {
            object dps = new object();
            Assert.IsTrue(SdoAvatar.PoseSourceChanged(new object(), new object(), dps, dps, 2, 2));
        }

        [Test]
        public void PoseSourceChanged_TrueWhenTheChoreographyItselfSwaps()
        {
            // ShowTime swaps in a breakdance DPS mid-song; row indices can coincide across the two scripts.
            object clip = new object();
            Assert.IsTrue(SdoAvatar.PoseSourceChanged(clip, clip, new object(), new object(), 4, 4));
        }

        [Test]
        public void PoseSourceChanged_TrueEnteringAndLeavingTheDance()
        {
            object clip = new object(), dps = new object();
            Assert.IsTrue(SdoAvatar.PoseSourceChanged(clip, clip, null, dps, -1, 0));   // idle -> first slice
            Assert.IsTrue(SdoAvatar.PoseSourceChanged(clip, clip, dps, null, 7, -1));   // slice -> idle (dance gate stop)
            Assert.IsFalse(SdoAvatar.PoseSourceChanged(clip, clip, null, null, -1, -1)); // idle keeps looping
        }

        // ---- end-to-end: LateUpdate really has to feed (Dps, row) into the blend decision ----
        // Sampling and the truth table are both correct in isolation; what regressed was the WIRING between them, so
        // these drive a live SdoAvatar and watch _blendStart. Deleting "dps = Dps; dpsRow = row;" from LateUpdate, or
        // restoring the old "if (_mot == _lastMot) return;", turns them red — the pure tests above stay green.

        private static HrcLoader OneBone() => new HrcLoader
        {
            Names = new[] { "Bip01" }, Parent = new[] { -1 },
            RawRest = new[] { Matrix4x4.identity }, LocalRest = new[] { Matrix4x4.identity },
            BindWorld = new[] { Matrix4x4.identity }, InvBindWorld = new[] { Matrix4x4.identity },
            Index = new Dictionary<string, int> { { "Bip01", 0 } },
        };

        private static void Frame(SdoAvatar a) => typeof(SdoAvatar)
            .GetMethod("LateUpdate", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(a, null);

        private static bool Blending(SdoAvatar a) => (float)typeof(SdoAvatar)
            .GetField("_blendStart", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(a) >= 0f;

        // One clip for every row -> MotResolver hands back the SAME MotLoader, which is exactly the case the old
        // reference test could not see. RestMot stays null so the idle branch never takes over.
        private static SdoAvatar Dancer(GameObject go, DpsLoader dps, System.Func<float> clock)
        {
            var av = go.AddComponent<SdoAvatar>();
            var clip = new MotLoader();
            av.Setup(OneBone(), clip);
            av.Dps = dps; av.MotResolver = _ => clip; av.DanceTimeSec = clock;
            return av;
        }

        [Test]
        public void LateUpdate_ArmsTheCrossfadeOnASameClipRowChange()
        {
            var go = new GameObject("dancer");
            try
            {
                float t = 0.5f;
                var av = Dancer(go, SameClipRewind(), () => t);
                Frame(av);                                  // first pose: nothing to blend FROM yet
                Frame(av);                                  // still row 0
                Assert.IsFalse(Blending(av), "同一個 slice 內不該每幀重新起混色");
                t = 1.5f; Frame(av);                        // same .mot, row 0 -> 1 (the rewinding seam)
                Assert.IsTrue(Blending(av), "同一個 mot 換 row 必須混色 — 這就是回朔 bug 的正本");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void LateUpdate_StaysContinuousWhileOneSlicePlays()
        {
            var go = new GameObject("dancer");
            try
            {
                float t = 0.5f;
                var av = Dancer(go, SameClipRewind(), () => t);
                Frame(av); Frame(av);
                t = 0.6f; Frame(av);                        // advanced within row 0
                t = 0.9f; Frame(av);
                Assert.IsFalse(Blending(av), "slice 內推進時間不該起混色(否則整首舞被混糊)");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void LateUpdate_ArmsTheCrossfadeWhenTheChoreographySwaps()
        {
            // ShowTime swaps in a breakdance DPS mid-song; its row 0 can collide with the row already playing.
            var go = new GameObject("dancer");
            try
            {
                float t = 0.5f;
                var av = Dancer(go, SameClipRewind(), () => t);
                Frame(av); Frame(av);
                Assert.IsFalse(Blending(av));
                av.Dps = SameClipRewind();                  // different DpsLoader, still row 0, still one clip
                Frame(av);
                Assert.IsTrue(Blending(av), "換一份編舞就算 row 索引撞號也要混色");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
