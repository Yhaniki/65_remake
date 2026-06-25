using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// Diagnostic capture of the DEFAULT (auto-director) camera — NOT an assertion. Saves frames of the
    /// director's opening crane (t=0, mid), a fixed cam, and the fixed->default RE-ENTRY, so the current
    /// behaviour can be eyeballed against the official reference frames before any fix.
    /// </summary>
    public class CameraDefaultCaptureTest
    {
        private const int W = 800, H = 600;

        [UnityTest]
        public IEnumerator Capture_Default_Director()
        {
            yield return new WaitForSecondsRealtime(2.5f);
            var game = Object.FindAnyObjectByType<Sdo.Game.ScreenGameplay>();
            Assert.IsNotNull(game, "ScreenGameplay not booted");
            Assert.Greater(game.FixedCamCountForTest, 0, "cameras not loaded");

            // 1) director shot 0 at t~0 = the crane's start position
            game.RestartDirectorForTest();
            yield return null; yield return null;
            Cap("H:/65_remake/cam-default-start.png");

            // 2) ~4s into the crane (it should be descending toward the dancer)
            yield return new WaitForSecondsRealtime(4f);
            Cap("H:/65_remake/cam-default-4s.png");

            // 3) each of the 6 fixed F2 cams (compare angles to the official reference frames)
            game.RestartDirectorForTest();
            for (int i = 0; i < game.FixedCamCountForTest; i++)
            {
                game.CycleCamModeForTest();             // -> fixed i
                yield return null; yield return null;
                Cap("H:/65_remake/cam-fixed" + game.CamModeForTest + ".png");
            }

            // 4) F2 cycle back to default (-1) = RE-ENTRY, captured immediately (must NOT be the crane)
            int guard = 0;
            do { game.CycleCamModeForTest(); } while (game.CamModeForTest != -1 && guard++ < 32);
            yield return null; yield return null;
            Cap("H:/65_remake/cam-default-reentry.png");

            // 5) VENUE OVERVIEW: high top-down ortho over the dance-spot, to see the column/wall layout vs the dancer.
            var sc = game.SceneCamForTest; var spot = game.DanceSpotForTest;
            if (sc != null)
            {
                sc.orthographic = true; sc.orthographicSize = 500f;
                sc.transform.position = spot + new Vector3(0, 2000, 0);
                sc.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);  // look -Y, +Z = up in frame
                yield return null; yield return null;
                Cap("H:/65_remake/venue-topdown.png");
            }
        }

        private static void Cap(string path)
        {
            var main = Camera.main; if (main == null) return;
            var rt = new RenderTexture(W, H, 24);
            foreach (var c in Camera.allCameras)
                if (c != main && c.targetTexture != null) c.Render();   // refresh the scene RT first (batchmode)
            main.targetTexture = rt; main.Render(); main.targetTexture = null;
            RenderTexture.active = rt;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(0, 0, W, H), 0, 0); t.Apply();
            RenderTexture.active = null;
            System.IO.File.WriteAllBytes(path, t.EncodeToPNG());
            Object.Destroy(t); Object.Destroy(rt);
            Debug.Log("[CameraDefaultCapture] saved " + path);
        }
    }
}
