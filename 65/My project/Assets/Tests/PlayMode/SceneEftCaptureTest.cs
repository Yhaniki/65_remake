using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// Captures the SCN0008 (Egyptian) stage background EFT — the ground magic circle "結界" (kikkai_3) with its
    /// tex42 light bars and the .mot-driven delta_line colour bars — on a CLEAN scene-only stage, over time so the
    /// delta_line EXTEND animation + loop are visible. Not an assertion test; saves PNGs for manual comparison
    /// against the original (sdo_stand_alone.exe SCN0008 screenshot / Frida transform dump).
    ///
    /// The clean SCN0008 scene-only boot is configured HERE (scenePath + observeBurstMode), not via the
    /// $env:SDO_SCENE / SDO_SCENE_ONLY vars the shipped auto-boot reads — the front-end owns startup now, so nothing
    /// self-boots and the env vars would never be consumed. Previously, forgetting them silently captured the wrong scene.
    /// Run: -runTests -batchmode -projectPath "h:\65_remake\65\My project" -testPlatform PlayMode
    ///      -testFilter Sdo.Tests.SceneEftCaptureTest -logFile &lt;log&gt;     (do NOT pass -nographics)
    /// Output: H:/65_remake/scn0008-0..N.png (composite) + -bg.png (scene only, no HUD).
    /// </summary>
    public class SceneEftCaptureTest
    {
        private const int W = 800, H = 600;   // 800×600 4:3 design frame

        // 乾淨的「只有舞台」開機：載 SCN0008（SCENE.MSH + mapobjs + 常駐 EFT），不要音符/音樂/HUD，舞者待機在舞點。
        // 等同官方 auto-boot 讀 SDO_SCENE='SCN0008' + SDO_SCENE_ONLY='1' 的那段。
        private static void SceneOnlyScn0008(Sdo.Game.ScreenGameplay g)
        {
            g.scenePath = "SCENE/SCN0008";
            g.observeBurstMode = true;
        }

        [UnityTearDown]
        public IEnumerator TearDown() => GameplayBoot.Teardown();

        [UnityTest]
        public IEnumerator Capture_Scn0008SceneEft()
        {
            Sdo.Game.ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g, SceneOnlyScn0008);
            Assert.IsTrue(game.observeBurstMode && game.scenePath.ToUpperInvariant().Contains("SCN0008"),
                $"not a clean SCN0008 scene-only boot (scenePath={game.scenePath}, observe={game.observeBurstMode})");
            game.SetCamModeForTest(0);   // fixed front cam to inspect the ground circle
            yield return new WaitForSecondsRealtime(0.5f);

            // sample over ~6s: the tex42 light bars re-fire every 30 ticks (continuous), the delta_line bars extend
            // via DELTA_LINE.MOT (scale.Y, ~1s loop), so several shots catch the disc + bars + the extend/loop cycle.
            float[] shots = { 0f, 0.5f, 1.0f, 1.5f, 2.0f, 3.0f, 4.5f, 6.0f };
            float prev = 0f;
            for (int i = 0; i < shots.Length; i++)
            {
                yield return new WaitForSecondsRealtime(shots[i] - prev); prev = shots[i];
                Cap($"H:/65_remake/scn0008-{i}.png");
            }
        }

        // SELF-TEST: dump the delta_line bars' WORLD transforms over ~9s (the .mot fires periodically) so they can be
        // diffed against the official Frida capture (eft_delta_bones.log). Output: H:/65_remake/mysim-delta.log.
        [UnityTest]
        public IEnumerator Dump_DeltaLine()
        {
            // 旗標要在 boot 之前設 —— 常駐 EFT 是在舞台載入時就生出來的，設晚了那一批就沒被記錄到。
            Sdo.Game.EftMotMesh.Dbg = true;
            Sdo.Game.EftEffect.DumpTraj = true;   // also log MW(tex117)/disc(tex69) WORLD pos + alpha → mysim-traj.log
            Sdo.Game.ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g, SceneOnlyScn0008);
            game.SetCamModeForTest(0);
            // dense capture across the whole ~10s disc life; tag each with the disc's current alpha so a phase-aligned
            // (bright vs dim) pair can be diffed to verify the disc pulse renders.
            for (int i = 0; i < 12; i++) { yield return new WaitForSecondsRealtime(1.0f); Cap($"H:/65_remake/scn0008-chk{i}.png"); Debug.Log($"[discalpha] chk{i} a={Sdo.Game.EftEffect.LastDiscAlpha:F0}"); }
            Debug.Log("[delta-dump] done");
        }

        // The scene renders to a RenderTexture shown by a full-screen quad in the main ortho cam (the live game's
        // single-camera composite). Batchmode does not auto-render off-screen cameras, so refresh each RT-target
        // camera (the SceneCam -> sceneRT) first, then render the main cam. Also dumps the scene RT as *-bg.png
        // (scene without the HUD overlay) for clean inspection. Mirrors CaptureTest.Cap.
        private static void Cap(string path)
        {
            var main = Camera.main; if (main == null) return;
            var rt = new RenderTexture(W, H, 24);
            RenderTexture sceneRT = null;
            foreach (var c in Camera.allCameras)
                if (c != main && c.targetTexture != null) { c.Render(); sceneRT = c.targetTexture; }
            if (sceneRT != null) { var bgt = ReadRGBA(sceneRT); File.WriteAllBytes(path.Replace(".png", "-bg.png"), bgt.EncodeToPNG()); Object.Destroy(bgt); }
            main.targetTexture = rt; main.Render(); main.targetTexture = null;
            var tex = ReadRGBA(rt);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.Destroy(tex); Object.Destroy(rt);
            Debug.Log("[scn0008-cap] saved " + path);
        }

        private static Texture2D ReadRGBA(RenderTexture rt)
        {
            RenderTexture.active = rt;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(0, 0, W, H), 0, 0); t.Apply();
            RenderTexture.active = null; return t;
        }
    }
}
