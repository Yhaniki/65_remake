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

        // ---- slice continuity: which seams are one continuous run of the clip, and how long a row's frame ramp is ----
        // The official files write a continued seam TWO ways and mix them inside one file (12459 does both): 84% of the
        // same-clip seams read StartF == prev EndF + 1, 12% repeat prev EndF. A row that runs on covers the gap to the
        // NEXT row's first frame, so the two ramps meet instead of replaying or skipping a frame at the boundary.

        private static DpsLoader Seam(int aStart, int aEnd, int bStart, string bMot = "wdance0238.mot") => new DpsLoader
        {
            Rows = new[]
            {
                new DpsLoader.Row { Mot = "wdance0238.mot", StartF = aStart, EndF = aEnd,       Dur = 4f, TStart = 0f },
                new DpsLoader.Row { Mot = bMot,             StartF = bStart, EndF = bStart + 90, Dur = 4f, TStart = 4f },
            },
            Total = 8f,
        };

        [Test]
        public void SliceContinues_BothWaysTheOfficialFilesWriteAContinuedSeam()
        {
            Assert.IsTrue(Seam(355, 474, 475).SliceContinues(0, 1), "EndF+1 (10731 的 474→475) 是接續");
            Assert.IsTrue(Seam(288, 432, 432).SliceContinues(0, 1), "重複 EndF (15085 的 432→432) 也是接續");
        }

        [Test]
        public void SliceContinues_FalseOnARealCut()
        {
            Assert.IsFalse(Seam(0, 39, 0).SliceContinues(0, 1), "回朔 (10027 wdance0101 192→83) 是真的切換");
            Assert.IsFalse(Seam(0, 39, 60).SliceContinues(0, 1), "往前跳過一段也是真的切換");
            Assert.IsFalse(Seam(355, 474, 475, "wdance0060.mot").SliceContinues(0, 1), "換一支 clip 一定是切換");
        }

        [Test]
        public void SliceContinues_FalseForNonAdjacentRowsAndTheLastRow()
        {
            var dps = Seam(355, 474, 475);
            Assert.IsFalse(dps.SliceContinues(1, 0), "倒著問(row 只會往前走)");
            Assert.IsFalse(dps.SliceContinues(0, 2), "不相鄰的 row");
            Assert.IsFalse(dps.SliceContinues(1, 2), "最後一個 row 後面沒有東西可接");
            Assert.IsFalse(dps.SliceContinues(-1, 0), "idle → 第一個 slice");
        }

        [Test]
        public void SliceSpan_ReachesTheNextRowsFirstFrame()
        {
            Assert.AreEqual(120f, Seam(355, 474, 475).SliceSpan(0), 1e-4f);   // 475-355：ratio 1 → 剛好 475
            Assert.AreEqual(144f, Seam(288, 432, 432).SliceSpan(0), 1e-4f);   // 432-288：ratio 1 → 剛好 432
        }

        [Test]
        public void SliceSpan_ACutKeepsStartToEnd()
        {
            // 切換的 row 停在自己的 EndF —— 那一幀正是 crossfade 要交接出去的姿勢。
            Assert.AreEqual(39f, Seam(0, 39, 0).SliceSpan(0), 1e-4f);
            Assert.AreEqual(90f, Seam(355, 474, 475).SliceSpan(1), 1e-4f);    // 最後一個 row
            Assert.AreEqual(0f, Seam(355, 474, 475).SliceSpan(7), 1e-4f);     // 界外
        }

        [Test]
        public void Sample_FrameRampsMeetAcrossAContinuedSeam()
        {
            // 這就是空翻在 row 邊界頓一下的正本：舊公式在這裡會從 473.x 直接跳到 475。
            var dps = Seam(355, 474, 475);
            dps.Sample(4f - 1e-4f, out _, out float before, out int rBefore);
            dps.Sample(4f, out _, out float after, out int rAfter);
            Assert.AreNotEqual(rBefore, rAfter, "確實跨過了 row 邊界");
            Assert.AreEqual(475f, before, 0.02f, "前一個 slice 的 ratio→1 要剛好走到下一個 slice 的第一幀");
            Assert.AreEqual(475f, after, 1e-3f);
        }

        [Test]
        public void Sample_ContinuedSeamAdvancesOneFramePerFrame()
        {
            // 120 幀鋪 4 秒 = 30 fps；用 EndF-StartF(119) 會變 29.75 fps,邊界再補跳一幀。
            var dps = Seam(355, 474, 475);
            dps.Sample(1f, out _, out float f1, out _);
            dps.Sample(2f, out _, out float f2, out _);
            Assert.AreEqual(30f, f2 - f1, 1e-3f);
        }

        // ---- which changes actually need a crossfade ----

        [Test]
        public void SliceNeedsBlend_FalseWhenTheSliceRunsStraightOn()
        {
            object clip = new object();
            var dps = Seam(355, 474, 475);
            Assert.IsFalse(SdoAvatar.SliceNeedsBlend(clip, clip, dps, dps, 0, 1),
                           "同一支 clip 接著播不需要交接 — 混色只會讓快動作卡住再暴衝");
        }

        [Test]
        public void SliceNeedsBlend_TrueOnEveryRealCut()
        {
            object clip = new object();
            var rewind = Seam(0, 39, 0);
            Assert.IsTrue(SdoAvatar.SliceNeedsBlend(clip, clip, rewind, rewind, 0, 1), "回朔的 row 仍要混色");
            var other = Seam(355, 474, 475, "wdance0060.mot");
            Assert.IsTrue(SdoAvatar.SliceNeedsBlend(new object(), new object(), other, other, 0, 1), "換 clip");
            var dps = Seam(355, 474, 475);
            Assert.IsTrue(SdoAvatar.SliceNeedsBlend(clip, clip, Seam(355, 474, 475), dps, 0, 0), "換一份編舞(ShowTime)");
            Assert.IsTrue(SdoAvatar.SliceNeedsBlend(clip, clip, null, dps, -1, 0), "idle → 第一個 slice");
            Assert.IsTrue(SdoAvatar.SliceNeedsBlend(clip, clip, dps, null, 1, -1), "slice → idle");
        }

        [Test]
        public void SliceNeedsBlend_FalseWhenNothingChanged()
        {
            object clip = new object();
            var dps = Seam(355, 474, 475);
            Assert.IsFalse(SdoAvatar.SliceNeedsBlend(clip, clip, dps, dps, 1, 1));
            Assert.IsFalse(SdoAvatar.SliceNeedsBlend(clip, clip, null, null, -1, -1));
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
        public void LateUpdate_DoesNotBlendAcrossAContinuedSeam()
        {
            // 空翻那個 case:同一支 clip、下一個 slice 接著 EndF+1 播 —— 混色進來只會把動作壓扁再暴衝。
            var go = new GameObject("dancer");
            try
            {
                float t = 1f;
                var av = Dancer(go, Seam(355, 474, 475), () => t);
                Frame(av); Frame(av);
                t = 5f; Frame(av);                          // row 0 -> 1,frame 474 -> 475
                Assert.IsFalse(Blending(av), "接著播的 slice 不該起混色 — 這就是空翻在 row 邊界頓一下的正本");
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
