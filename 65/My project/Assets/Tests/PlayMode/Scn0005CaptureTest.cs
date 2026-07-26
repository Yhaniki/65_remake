using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// SCN0005(聖誕夜)的橘色光球人眼複核。
    ///
    /// 那顆光球是 guangxiao_.tga 的相機朝向 billboard(官方 case 5 建的 CBillboardSet,100×100,
    /// 位置 −97.7 / 115 / 222.4)。貼圖是一張平滑放射漸層:alpha 由邊緣 6 → 中心 248 → 18 的鐘形,
    /// RGB 全圖固定暖橘 (234,158,27)。加法混色下**畫兩次**會讓中心 0.97×0.92 加倍後直接 clip,
    /// 整片中段變純色、柔和衰減塌成硬邊圓盤 —— 使用者回報的「發光太硬」。
    ///
    /// 這支把光球所在的那一側單獨拉近拍,並印出它的 shader / 三角形數(判斷單繞序還是雙繞序)/ 世界包圍盒。
    ///
    /// Run: -runTests -batchmode -force-d3d11 -projectPath "h:\65_remake\65\My project" -testPlatform PlayMode
    ///      -testFilter Sdo.Tests.Scn0005CaptureTest -logFile &lt;log&gt;    (不要加 -nographics)
    /// </summary>
    public class Scn0005CaptureTest
    {
        private const int W = 800, H = 600;
        private static string OutDir => System.Environment.GetEnvironmentVariable("SDO_SHOT_DIR") ?? "H:/65_remake";
        private static string Tag => System.Environment.GetEnvironmentVariable("SDO_SHOT_TAG") ?? "scn0005";

        private static void SceneOnlyScn0005(Sdo.Game.ScreenGameplay g)
        {
            g.scenePath = "SCENE/SCN0005";
            g.observeBurstMode = true;
        }

        [UnityTearDown]
        public IEnumerator TearDown() => GameplayBoot.Teardown();

        [UnityTest]
        public IEnumerator Capture_Scn0005_Orange_Glow_Ball()
        {
            Sdo.Game.ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g, SceneOnlyScn0005);
            Assert.IsTrue(game.observeBurstMode && game.scenePath.ToUpperInvariant().Contains("SCN0005"),
                $"not a clean SCN0005 scene-only boot (scenePath={game.scenePath})");
            yield return new WaitForSecondsRealtime(0.5f);

            foreach (var mr in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                if (mr.gameObject.name != "Flame") continue;
                var mf = mr.GetComponent<MeshFilter>();
                int tris = mf != null && mf.sharedMesh != null ? mf.sharedMesh.triangles.Length / 3 : -1;
                Debug.Log($"[scn0005-probe] Flame tris={tris} (2=單繞序/一次, 4=雙繞序/兩次) " +
                          $"shader={mr.sharedMaterial?.shader?.name} queue={mr.sharedMaterial?.renderQueue} " +
                          $"pos={mr.transform.position} scale={mr.transform.localScale} bounds={mr.bounds.size}");
            }

            // 光球在 (−97.7, 115, 222.4);從它正面拉開一點,讓整顆連同衰減都進畫面才看得出硬邊。
            var camGo = new GameObject("Scn0005FreeCam");
            var free = camGo.AddComponent<Camera>();
            free.fieldOfView = 45f; free.nearClipPlane = 1f; free.farClipPlane = 20000f;
            foreach (var c in Camera.allCameras)
                if (!c.orthographic) { free.cullingMask = c.cullingMask; free.clearFlags = c.clearFlags; free.backgroundColor = c.backgroundColor; break; }
            var backdrop = GameObject.Find("SceneBackdrop");
            if (backdrop != null) free.cullingMask &= ~(1 << backdrop.layer);

            var ball = new Vector3(-97.7f, 115f, 222.4f);
            var shots = new (string Tag, Vector3 Pos)[]
            {
                ("near", ball + new Vector3(0f, 0f, -260f)),
                ("wide", ball + new Vector3(120f, 60f, -600f)),
            };
            foreach (var s in shots)
            {
                camGo.transform.position = s.Pos;
                camGo.transform.rotation = Quaternion.LookRotation(ball - s.Pos, Vector3.up);
                CapFrom(free, $"{OutDir}/{Tag}-{s.Tag}.png");
                yield return new WaitForSecondsRealtime(0.3f);
            }
            Object.Destroy(camGo);
        }

        private static void CapFrom(Camera cam, string path)
        {
            var rt = new RenderTexture(W, H, 24);
            cam.targetTexture = rt; cam.Render(); cam.targetTexture = null;
            RenderTexture.active = rt;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(0, 0, W, H), 0, 0); t.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(path, t.EncodeToPNG());
            Object.Destroy(t); Object.Destroy(rt);
            Debug.Log("[scn0005-cap] saved " + path);
        }
    }
}
