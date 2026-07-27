using NUnit.Framework;
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
    }
}
