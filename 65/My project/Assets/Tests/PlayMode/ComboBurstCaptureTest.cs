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
            var game = Object.FindObjectOfType<Sdo.Game.Step1Game>();
            Assert.IsNotNull(game, "Step1Game not found");
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
