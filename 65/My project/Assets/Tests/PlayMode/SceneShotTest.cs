using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// 通用的舞台截圖 + 渲染探針。指定場景資料夾就拍,不用為每個場景各寫一支測試。
    ///
    /// 為什麼需要:排序/混色/硬邊這類問題,看圖是猜的 —— 要看每個 renderer 的 shader、renderQueue、
    /// bounds 才是證據;而固定機位永遠盯著舞者,場邊的燈具/光柱常常根本不在畫面裡,得另外拉自由機位
    /// 繞著場景包圍盒拍。這支把這兩件事包起來。
    ///
    /// 環境變數:
    ///   SDO_SHOT_SCENE = 場景資料夾(例 SCN0029);沒給就跳過
    ///   SDO_SHOT_DIR   = 輸出資料夾(預設 H:/65_remake)
    ///   SDO_SHOT_TAG   = 檔名前綴(預設 = 場景名)
    ///   SDO_SHOT_GREP  = 只印名字含這段字的 renderer(預設全印,但上限 60 行)
    ///
    /// Run: -runTests -batchmode -force-d3d11 -projectPath "h:\65_remake\65\My project" -testPlatform PlayMode
    ///      -testFilter Sdo.Tests.SceneShotTest -logFile &lt;log&gt;    (不要加 -nographics)
    /// </summary>
    public class SceneShotTest
    {
        private const int W = 800, H = 600;
        private static string Scene => System.Environment.GetEnvironmentVariable("SDO_SHOT_SCENE");
        private static string OutDir => System.Environment.GetEnvironmentVariable("SDO_SHOT_DIR") ?? "H:/65_remake";
        private static string Tag => System.Environment.GetEnvironmentVariable("SDO_SHOT_TAG") ?? (Scene ?? "scene");
        private static string Grep => System.Environment.GetEnvironmentVariable("SDO_SHOT_GREP");

        [UnityTearDown]
        public IEnumerator TearDown() => GameplayBoot.Teardown();

        [UnityTest]
        public IEnumerator Capture_Scene()
        {
            var folder = Scene;
            if (string.IsNullOrEmpty(folder)) { Assert.Ignore("SDO_SHOT_SCENE 沒設,跳過"); yield break; }

            Sdo.Game.ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g, g =>
            {
                g.scenePath = "SCENE/" + folder;
                g.observeBurstMode = true;
            });
            Assert.IsTrue(game.scenePath.ToUpperInvariant().EndsWith(folder.ToUpperInvariant()),
                $"scenePath={game.scenePath}");
            game.SetCamModeForTest(0);
            yield return new WaitForSecondsRealtime(0.6f);

            // SDO_SHOT_CAMSWEEP=1:輪過每一個固定機位停一下,讓各元件的診斷 log(例如 lens flare 的
            // 可見性判定)有機會在每個機位各印一次 —— 用來回答「是實作壞了還是這個機位本來就看不到」。
            if (System.Environment.GetEnvironmentVariable("SDO_SHOT_CAMSWEEP") == "1")
            {
                int cams = game.FixedCamCountForTest;
                Debug.Log($"[shot-probe] cam sweep: {cams} 個固定機位");
                for (int c = 0; c < cams; c++)
                {
                    game.SetCamModeForTest(c);
                    Debug.Log($"[shot-probe] === cam {c} ===");
                    yield return new WaitForSecondsRealtime(1.2f);
                }
                game.SetCamModeForTest(0);
                yield return new WaitForSecondsRealtime(0.4f);
            }

            // 探針 + 場景包圍盒(自由機位要靠它決定拍多遠)
            var all = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
            var world = new Bounds();
            bool haveBounds = false;
            int printed = 0;
            foreach (var mr in all)
            {
                if (!haveBounds) { world = mr.bounds; haveBounds = true; }
                else world.Encapsulate(mr.bounds);
                var n = mr.gameObject.name;
                if (!string.IsNullOrEmpty(Grep) && n.IndexOf(Grep, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (printed++ >= 60) continue;
                var m = mr.sharedMaterial;
                Debug.Log($"[shot-probe] {n} (parent={(mr.transform.parent ? mr.transform.parent.name : "-")}) " +
                          $"shader={m?.shader?.name} queue={m?.renderQueue} " +
                          $"spread={(m != null && m.HasProperty("_Spread") ? m.GetFloat("_Spread").ToString("F2") : "-")} " +
                          $"c={mr.bounds.center} s={mr.bounds.size}");
            }
            Debug.Log($"[shot-probe] {all.Length} renderer(s), world bounds c={world.center} s={world.size}");

            var camGo = new GameObject("SceneShotCam");
            var free = camGo.AddComponent<Camera>();
            free.fieldOfView = 55f; free.nearClipPlane = 1f; free.farClipPlane = 30000f;
            foreach (var c in Camera.allCameras)
                if (!c.orthographic) { free.cullingMask = c.cullingMask; free.clearFlags = c.clearFlags; free.backgroundColor = c.backgroundColor; break; }
            var backdrop = GameObject.Find("SceneBackdrop");   // 主 ortho 相機的合成 quad,自由機位收到會擋畫面
            if (backdrop != null) free.cullingMask &= ~(1 << backdrop.layer);

            // 繞著舞點(原點附近)拍四個方位 + 一個俯瞰。半徑用場景寬度推,免得每個場景都要手調。
            var look = new Vector3(0f, Mathf.Min(150f, world.size.y * 0.15f), 200f);
            float r = Mathf.Clamp(Mathf.Max(world.size.x, world.size.z) * 0.35f, 400f, 2500f);
            var shots = new (string Tag, Vector3 Pos)[]
            {
                ("n",  look + new Vector3(0f,     r * 0.35f, -r)),
                ("ne", look + new Vector3(r * .7f, r * 0.35f, -r * .7f)),
                ("nw", look + new Vector3(-r * .7f, r * 0.35f, -r * .7f)),
                ("up", look + new Vector3(0f,     r * 1.1f,  -r * .5f)),
            };
            foreach (var s in shots)
            {
                camGo.transform.position = s.Pos;
                camGo.transform.rotation = Quaternion.LookRotation(look - s.Pos, Vector3.up);
                CapFrom(free, $"{OutDir}/{Tag}-{s.Tag}.png");
                yield return new WaitForSecondsRealtime(0.4f);
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
            Debug.Log("[shot-cap] saved " + path);
        }
    }
}
