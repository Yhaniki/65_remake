using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// Not a real assertion test — drives the self-booting Step1Game for a few seconds and
    /// saves an offscreen render to disk so the gameplay layout can be reviewed.
    /// Run: -runTests -testPlatform PlayMode
    /// </summary>
    public class CaptureTest
    {
        private const int W = 800, H = 600;   // exact DdrGamePlay.xml design frame (1px = 1 design unit)

        [UnityTest]
        public IEnumerator Capture_Gameplay()
        {
            yield return new WaitForSecondsRealtime(1.7f);
            var game = Object.FindAnyObjectByType<Sdo.Game.Step1Game>();
            if (game != null) game.SetCamModeForTest(0);   // fixed front cam0 to inspect hair + note board
            yield return new WaitForSecondsRealtime(6.0f);
            Cap("H:/65_remake/play-capture.png");
        }

        // The scene renders to a RenderTexture shown by a full-screen quad in the main ortho cam (the live game's
        // single-camera composite). Batchmode does not auto-render off-screen cameras, so refresh each RT-target
        // camera (the SceneCam -> sceneRT) first, then render the main cam (quad + HUD). Also dumps the scene RT as
        // *-bg.png (scene without HUD) for avatar inspection.
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
            Debug.Log("[CaptureTest] saved " + path);
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
