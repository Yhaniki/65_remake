using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// SCN0019(舞鬥競技場)場後四支聚光燈的人眼複核。
    ///
    /// PK/GUANG1..4 各是一片 4 頂點的光錐 quad,貼 dengzhu_.dds(全軟 alpha、soft/visible = 1.000、平均 32.4、
    /// 官方材質旗標 0x1),各自帶 .MOT 掃動 —— 和 SCN0016 的 JIGUANG1..3 是同一種東西。這種「亮 RGB + 全軟
    /// alpha」的貼圖必然被 LooksLikeAdditiveGlow 判成加法,而純加法會讓光錐左右出現硬邊。修法是走和其他
    /// 聚光燈一樣的 Sdo/UnlitSpotGlow(沿寬度把光暈抹開,核心不動、只在邊緣補漸層)。
    ///
    /// dengzhu_.dds 是「一張圖並排兩個光錐」,所以 _Spread 要縮到 0.1,否則邊緣的模糊會 Repeat 繞到隔壁那
    /// 個錐、多出一條假光暈 —— 探針會把每支的 shader 與 _Spread 印出來確認。
    ///
    /// Run: -runTests -batchmode -force-d3d11 -projectPath "h:\65_remake\65\My project" -testPlatform PlayMode
    ///      -testFilter Sdo.Tests.Scn0019CaptureTest -logFile &lt;log&gt;    (不要加 -nographics)
    /// </summary>
    public class Scn0019CaptureTest
    {
        private const int W = 800, H = 600;
        private static string OutDir => System.Environment.GetEnvironmentVariable("SDO_SHOT_DIR") ?? "H:/65_remake";
        private static string Tag => System.Environment.GetEnvironmentVariable("SDO_SHOT_TAG") ?? "scn0019";

        private static void SceneOnlyScn0019(Sdo.Game.ScreenGameplay g)
        {
            g.scenePath = "SCENE/SCN0019";
            g.observeBurstMode = true;
        }

        [UnityTearDown]
        public IEnumerator TearDown() => GameplayBoot.Teardown();

        [UnityTest]
        public IEnumerator Capture_Scn0019_Spotlights()
        {
            Sdo.Game.ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g, SceneOnlyScn0019);
            Assert.IsTrue(game.observeBurstMode && game.scenePath.ToUpperInvariant().Contains("SCN0019"),
                $"not a clean SCN0019 scene-only boot (scenePath={game.scenePath})");
            game.SetCamModeForTest(0);
            yield return new WaitForSecondsRealtime(0.5f);

            var centre = Vector3.zero;
            int found = 0;
            foreach (var mr in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                var pn = mr.transform.parent ? mr.transform.parent.name : "";
                if (!mr.gameObject.name.StartsWith("GUANG") && !pn.StartsWith("GUANG")) continue;
                var m = mr.sharedMaterial;
                Debug.Log($"[scn0019-probe] {mr.gameObject.name} (parent={pn}) shader={m?.shader?.name} " +
                          $"spread={(m != null && m.HasProperty("_Spread") ? m.GetFloat("_Spread").ToString("F2") : "n/a")} " +
                          $"queue={m?.renderQueue} bounds c={mr.bounds.center} s={mr.bounds.size}");
                centre += mr.bounds.center; found++;
            }
            if (found > 0) centre /= found;
            Debug.Log($"[scn0019-probe] {found} spotlight renderer(s), centre {centre}");

            // 固定機位盯著舞者,四支燈在場後 —— 另外拉一台相機從舞池方向照過去才看得到整片光錐。
            var camGo = new GameObject("Scn0019FreeCam");
            var free = camGo.AddComponent<Camera>();
            free.fieldOfView = 50f; free.nearClipPlane = 1f; free.farClipPlane = 20000f;
            foreach (var c in Camera.allCameras)
                if (!c.orthographic) { free.cullingMask = c.cullingMask; free.clearFlags = c.clearFlags; free.backgroundColor = c.backgroundColor; break; }
            var backdrop = GameObject.Find("SceneBackdrop");
            if (backdrop != null) free.cullingMask &= ~(1 << backdrop.layer);

            var shots = new (string Tag, Vector3 Pos)[]
            {
                ("front", centre + new Vector3(0f, -60f, -700f)),
                ("close", centre + new Vector3(180f, 0f, -320f)),
            };
            foreach (var s in shots)
            {
                camGo.transform.position = s.Pos;
                camGo.transform.rotation = Quaternion.LookRotation(centre - s.Pos, Vector3.up);
                // .MOT 在掃,取兩個相位免得剛好都停在同一個角度
                for (int k = 0; k < 2; k++)
                {
                    CapFrom(free, $"{OutDir}/{Tag}-{s.Tag}{k}.png");
                    yield return new WaitForSecondsRealtime(1.0f);
                }
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
            Debug.Log("[scn0019-cap] saved " + path);
        }
    }
}
