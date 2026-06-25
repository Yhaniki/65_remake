using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// Fires the 100/200/300 COMBO bursts and captures each at bloom to compare against the official screenshots.
    /// Run: -runTests -testPlatform PlayMode -testFilter Sdo.Tests.ComboBurstCaptureTest
    /// Output: H:/65_remake/combo-T0.png (100), combo-T1.png (200), combo-T2.png (300), + combo-cap-*.png (100 over time)
    /// </summary>
    public class ComboBurstCaptureTest
    {
        private const int W = 800, H = 600;

        [UnityTest]
        public IEnumerator Capture_ComboBurst()
        {
            yield return new WaitForSecondsRealtime(2.2f);
            var game = Object.FindAnyObjectByType<Sdo.Game.ScreenGameplay>();
            Assert.IsNotNull(game, "ScreenGameplay not found");
            game.SetCamModeForTest(0);
            yield return new WaitForSecondsRealtime(0.3f);

            // dancer height (world units) → calibrate effect scale
            var root = game.AvatarRootForTest;
            if (root != null)
            {
                var rs = root.GetComponentsInChildren<Renderer>();
                if (rs.Length > 0)
                {
                    Bounds b = rs[0].bounds;
                    for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                    Debug.Log($"[avatar] world height={b.size.y:F1} y[{b.min.y:F1}..{b.max.y:F1}] center={b.center}");
                }
            }

            // 100COMBO over time (the reference effect)
            game.SpawnComboBurstForTest(0);
            float[] shots = { 0.12f, 0.40f, 0.65f, 1.0f, 1.4f, 1.8f }; float prev = 0f;
            for (int i = 0; i < shots.Length; i++) { yield return new WaitForSecondsRealtime(shots[i] - prev); prev = shots[i]; Cap($"H:/65_remake/combo-cap-{i}.png"); }
            yield return new WaitForSecondsRealtime(1.0f);

            // one bloom shot of each tier (incl. 400 = tier 3, which SHOULD have the ring_l X-cross)
            for (int tier = 0; tier < 4; tier++)
            {
                game.SpawnComboBurstForTest(tier);
                yield return new WaitForSecondsRealtime(0.7f);
                Cap($"H:/65_remake/combo-T{tier}.png");
                yield return new WaitForSecondsRealtime(2.8f);
            }
        }

        /// <summary>
        /// Fires 200 and 300 COMBO and captures each over time so the RISING fountain bloom (peak ≈ tick 8-24 =
        /// 0.16-0.48s) is caught — the single 0.7s shot in Capture_ComboBurst is PAST the peak (fountain already faded),
        /// which is why 300's rising column "isn't seen". Output: H:/65_remake/combo-200-t#.png, combo-300-t#.png.
        /// Run: -testFilter Sdo.Tests.ComboBurstCaptureTest.Capture_200_300
        /// </summary>
        [UnityTest]
        public IEnumerator Capture_200_300()
        {
            yield return new WaitForSecondsRealtime(2.2f);
            var game = Object.FindAnyObjectByType<Sdo.Game.ScreenGameplay>();
            Assert.IsNotNull(game, "ScreenGameplay not found");
            game.SetCamModeForTest(0);
            yield return new WaitForSecondsRealtime(0.3f);

            float[] shots = { 0.10f, 0.16f, 0.24f, 0.32f, 0.40f, 0.48f, 0.60f, 0.72f };
            foreach (int tier in new[] { 1, 2 })   // 200, 300
            {
                game.SpawnComboBurstForTest(tier);
                float prev = 0f;
                for (int i = 0; i < shots.Length; i++)
                {
                    yield return new WaitForSecondsRealtime(shots[i] - prev); prev = shots[i];
                    Cap($"H:/65_remake/combo-{(tier + 1) * 100}-t{i}_{(int)(shots[i] * 1000)}ms.png");
                }
                yield return new WaitForSecondsRealtime(2.0f);   // let it die before the next tier
            }
        }

        /// <summary>
        /// Like Capture_200_300 but HIDES the bright palace stage first so the additive burst shows on a BLACK
        /// background (the only honest way to compare colour/brightness/height against the official's dark night scene;
        /// on the lit palace the additive glow washes out). Output: H:/65_remake/combo-dark-200-t#.png, dark-300-t#.png.
        /// Run: -testFilter Sdo.Tests.ComboBurstCaptureTest.Capture_ComboDark
        /// </summary>
        [UnityTest]
        public IEnumerator Capture_ComboDark()
        {
            yield return new WaitForSecondsRealtime(2.2f);
            var game = Object.FindAnyObjectByType<Sdo.Game.ScreenGameplay>();
            Assert.IsNotNull(game, "ScreenGameplay not found");
            game.SetCamModeForTest(0);
            yield return new WaitForSecondsRealtime(0.3f);
            game.HideStageForTest();
            yield return new WaitForSecondsRealtime(0.1f);

            float[] shots = { 0.06f, 0.12f, 0.18f, 0.26f, 0.34f, 0.44f, 0.56f, 0.70f, 0.90f };
            foreach (int tier in new[] { 1, 2 })   // 200, 300
            {
                game.SpawnComboBurstForTest(tier);
                float prev = 0f;
                for (int i = 0; i < shots.Length; i++)
                {
                    yield return new WaitForSecondsRealtime(shots[i] - prev); prev = shots[i];
                    Cap($"H:/65_remake/combo-dark-{(tier + 1) * 100}-t{i}_{(int)(shots[i] * 1000)}ms.png");
                }
                yield return new WaitForSecondsRealtime(2.0f);
            }
        }

        /// <summary>
        /// DEBUG: isolate the AEF_3_00 blue MESH (slot0) — hide all balls/trails/disks (DebugMeshOnly) on a black bg so
        /// the mesh's true shape/colour/position is visible. Output: H:/65_remake/mesh-only-{200,300}-t#.png.
        /// Run: -testFilter Sdo.Tests.ComboBurstCaptureTest.Capture_MeshOnly
        /// </summary>
        [UnityTest]
        public IEnumerator Capture_MeshOnly()
        {
            yield return new WaitForSecondsRealtime(2.2f);
            var game = Object.FindAnyObjectByType<Sdo.Game.ScreenGameplay>();
            Assert.IsNotNull(game, "ScreenGameplay not found");
            game.SetCamModeForTest(0);
            yield return new WaitForSecondsRealtime(0.3f);
            game.HideStageForTest();
            yield return new WaitForSecondsRealtime(0.1f);
            Sdo.Game.EftEffect.DebugMeshOnly = true;
            float[] shots = { 0.06f, 0.12f, 0.18f, 0.26f, 0.34f, 0.44f, 0.56f, 0.70f, 0.90f };
            foreach (int tier in new[] { 1, 2 })   // 200, 300
            {
                game.SpawnComboBurstForTest(tier);
                float prev = 0f;
                for (int i = 0; i < shots.Length; i++)
                {
                    yield return new WaitForSecondsRealtime(shots[i] - prev); prev = shots[i];
                    Cap($"H:/65_remake/mesh-only-{(tier + 1) * 100}-t{i}_{(int)(shots[i] * 1000)}ms.png");
                }
                yield return new WaitForSecondsRealtime(2.0f);
            }
            Sdo.Game.EftEffect.DebugMeshOnly = false;
        }

        /// <summary>
        /// Captures 200COMBO with each root slot ISOLATED (dark bg) so each element's true on-screen colour/shape is
        /// visible: slot1=external→trail(pink), slot2=tex31 fountain(orange core+blue-violet halo), slot3=tex4(faint
        /// orange radial). Output: H:/65_remake/slot200-s{n}-t{i}.png. Run: -testFilter ...Capture_200Slots
        /// </summary>
        [UnityTest]
        public IEnumerator Capture_200Slots()
        {
            yield return new WaitForSecondsRealtime(2.2f);
            var game = Object.FindAnyObjectByType<Sdo.Game.ScreenGameplay>();
            Assert.IsNotNull(game, "ScreenGameplay not found");
            game.SetCamModeForTest(0);
            yield return new WaitForSecondsRealtime(0.3f);
            game.HideStageForTest();
            float[] shots = { 0.18f, 0.34f, 0.50f };
            foreach (int slot in new[] { 1, 2, 3 })
            {
                Sdo.Game.EftEffect.OnlyRootSlot = slot;
                game.SpawnComboBurstForTest(1);   // 200
                float prev = 0f;
                for (int i = 0; i < shots.Length; i++)
                {
                    yield return new WaitForSecondsRealtime(shots[i] - prev); prev = shots[i];
                    Cap($"H:/65_remake/slot200-s{slot}-t{i}.png");
                }
                yield return new WaitForSecondsRealtime(1.6f);
            }
            Sdo.Game.EftEffect.OnlyRootSlot = -1;
        }

        /// <summary>
        /// Dumps MY sim's per-particle effect-relative WORLD trajectories for 200/300/400/500 in the same format as
        /// the Frida hook (eft_online_log.txt) so the two can be diffed line-for-line to find where my distances diverge.
        /// Run: -runTests -testPlatform PlayMode -testFilter Sdo.Tests.ComboBurstCaptureTest.Dump_ComboTraj
        /// </summary>
        [UnityTest]
        public IEnumerator Dump_ComboTraj()
        {
            // -nographics makes RenderTexture.Create log [Error] every frame; the test framework auto-fails on any
            // unhandled error log. We only need the particle SIM (Debug.Log trajectory), not rendering → ignore them.
            LogAssert.ignoreFailingMessages = true;
            yield return new WaitForSecondsRealtime(2.2f);
            var game = Object.FindAnyObjectByType<Sdo.Game.ScreenGameplay>();
            Assert.IsNotNull(game, "ScreenGameplay not found");
            game.SetCamModeForTest(0);
            yield return new WaitForSecondsRealtime(0.3f);

            Sdo.Game.EftEffect.DumpTraj = true;
            for (int tier = 1; tier <= 3; tier++)   // 200, 300, 400
            {
                Sdo.Game.EftEffect.ResetTrajDump();
                Sdo.Game.EftEffect.DumpLog($"===== MYSIM COMBO {(tier + 1) * 100} =====");
                game.SpawnComboBurstForTest(tier);
                yield return new WaitForSecondsRealtime(3.0f);   // let the whole burst run + die
            }
            // FINISHED result firework — verify the velocity TURBULENCE makes its sparks dart/crackle (the flicker)
            Sdo.Game.EftEffect.ResetTrajDump();
            Sdo.Game.EftEffect.DumpLog("===== MYSIM FINISHED =====");
            game.SpawnNamedEftForTest("FINISHED", 5f);
            yield return new WaitForSecondsRealtime(4.5f);

            Sdo.Game.EftEffect.DumpTraj = false;
            Sdo.Game.EftEffect.DumpLog("===== MYSIM DUMP DONE =====");
        }

        /// <summary>
        /// Fires FINISHED and captures a ~3s sequence at ~50ms intervals so the bright-pixel oscillation (the firework
        /// FLICKER) can be measured the same way as the official capture (final/*.jpg → ±2–4% @ ~18 Hz).
        /// Run: -testFilter Sdo.Tests.ComboBurstCaptureTest.Capture_FinishedFlicker  → H:/65_remake/finflick/f##.png
        /// </summary>
        [UnityTest]
        public IEnumerator Capture_FinishedFlicker()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return new WaitForSecondsRealtime(2.2f);
            var game = Object.FindAnyObjectByType<Sdo.Game.ScreenGameplay>();
            Assert.IsNotNull(game, "ScreenGameplay not found");
            game.SetCamModeForTest(0);
            yield return new WaitForSecondsRealtime(0.3f);
            System.IO.Directory.CreateDirectory("H:/65_remake/finflick");
            game.SpawnNamedEftForTest("FINISHED", 5f);
            for (int i = 0; i < 60; i++)
            {
                yield return new WaitForSecondsRealtime(0.05f);
                Cap($"H:/65_remake/finflick/f{i:D2}.png");
            }
        }

        /// <summary>Fires 400COMBO and captures the X-cross developing over ~2s so I can SEE it (not just trajectory data).</summary>
        [UnityTest]
        public IEnumerator Capture_400Cross()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return new WaitForSecondsRealtime(2.2f);
            var game = Object.FindAnyObjectByType<Sdo.Game.ScreenGameplay>();
            Assert.IsNotNull(game, "ScreenGameplay not found");
            game.SetCamModeForTest(0);
            yield return new WaitForSecondsRealtime(0.3f);
            System.IO.Directory.CreateDirectory("H:/65_remake/cross400");
            game.SpawnComboBurstForTest(3);   // 400
            float[] shots = { 0.2f, 0.45f, 0.7f, 1.0f, 1.4f, 1.9f }; float prev = 0f;
            for (int i = 0; i < shots.Length; i++)
            {
                yield return new WaitForSecondsRealtime(shots[i] - prev); prev = shots[i];
                Cap($"H:/65_remake/cross400/c{i}_{(int)(shots[i]*1000)}ms.png");
            }
        }

        private static void Cap(string path)
        {
            var main = Camera.main; if (main == null) return;
            var rt = new RenderTexture(W, H, 24);
            foreach (var c in Camera.allCameras) if (c != main && c.targetTexture != null) c.Render();
            main.targetTexture = rt; main.Render(); main.targetTexture = null;
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.Destroy(tex); Object.Destroy(rt);
            Debug.Log("[ComboBurstCapture] saved " + path);
        }
    }
}
