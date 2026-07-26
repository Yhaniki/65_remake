using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// SCN0024(雪景街景)的三個舞台元素的人眼複核截圖:
    ///   1. Harrahs 光球招牌的變色動畫 —— XUEJING/BIAODONGHUA,官方每 500 ms 換一張,5 張一輪 2.5 秒
    ///      (粉紅→洋紅→粉紅→深藍→青藍)。連拍要看得出顏色在變。
    ///   2. 背景探照燈 —— XUEJING/DONGHUA,靠 DONGHUA.MOT 的 182 支旋轉 key 掃動;它的長軸被 bind/mot
    ///      拉長 ×3.9,那個拉長被通用防呆丟掉時只剩一截短樁埋在背景裡。
    ///   3. 招牌上的三顆 GUANG_.TGA 光暈 billboard(官方 case 0x18 直接建的 CBillboardSet,不是 mapobj)。
    /// 不是斷言測試,存 PNG 給人眼比對原版。
    /// Run: -runTests -batchmode -force-d3d11 -projectPath "h:\65_remake\65\My project" -testPlatform PlayMode
    ///      -testFilter Sdo.Tests.Scn0024CaptureTest -logFile &lt;log&gt;    (不要加 -nographics)
    /// </summary>
    public class Scn0024CaptureTest
    {
        private const int W = 800, H = 600;
        private static string OutDir => System.Environment.GetEnvironmentVariable("SDO_SHOT_DIR") ?? "H:/65_remake";

        // 只有舞台的乾淨開機:載 SCN0024,不要音符/音樂/HUD。
        private static void SceneOnlyScn0024(Sdo.Game.ScreenGameplay g)
        {
            g.scenePath = "SCENE/SCN0024";
            g.observeBurstMode = true;
        }

        [UnityTearDown]
        public IEnumerator TearDown() => GameplayBoot.Teardown();

        [UnityTest]
        public IEnumerator Capture_Scn0024_Sign_And_Searchlight()
        {
            Sdo.Game.ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g, SceneOnlyScn0024);
            Assert.IsTrue(game.observeBurstMode && game.scenePath.ToUpperInvariant().Contains("SCN0024"),
                $"not a clean SCN0024 scene-only boot (scenePath={game.scenePath}, observe={game.observeBurstMode})");
            game.SetCamModeForTest(0);
            yield return new WaitForSecondsRealtime(0.5f);

            // 招牌一輪 2.5 s(每 500 ms 一張),探照燈一輪 ~6.5 s(196 幀 @30fps)——
            // 取樣要橫跨兩者:0.6 s 一格連拍 12 張 = 7.2 s,招牌走完近三輪、光柱走完一輪。
            for (int i = 0; i < 12; i++)
            {
                Cap($"{OutDir}/scn0024-t{i}.png");
                yield return new WaitForSecondsRealtime(0.6f);
            }
        }

        // 全景:把場景相機拉遠拉高照著招牌那一側,才看得到掃天空的光柱與三顆光暈(固定機位都盯著舞者)。
        // 同時把光柱 mesh 與三顆 billboard 的世界包圍盒印出來,確認縮放沒有被丟掉。
        [UnityTest]
        public IEnumerator Capture_Scn0024_Overview()
        {
            Sdo.Game.ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g, SceneOnlyScn0024);
            yield return new WaitForSecondsRealtime(0.5f);

            foreach (var mr in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                var n = mr.gameObject.name;
                if (n.StartsWith("DONGHUA") || n == "Flame" || n.StartsWith("BIAODONGHUA"))
                    Debug.Log($"[scn0024-probe] {n} parent={(mr.transform.parent ? mr.transform.parent.name : "-")} " +
                              $"bounds c={mr.bounds.center} s={mr.bounds.size} shader={mr.sharedMaterial?.shader?.name}");
            }

            // 遊戲的場景相機每幀被機位系統重寫,所以另外開一台「只照舞台層(4)」的自由相機來拍。
            var camGo = new GameObject("Scn0024OverviewCam");
            var free = camGo.AddComponent<Camera>();
            free.cullingMask = 1 << 4;
            free.clearFlags = CameraClearFlags.SolidColor;
            free.backgroundColor = new Color(0.02f, 0.02f, 0.06f, 1f);
            free.nearClipPlane = 1f; free.farClipPlane = 20000f; free.fieldOfView = 60f;
            free.enabled = false;   // 自己 Render(),不進主迴圈

            // 三個機位:正面遠拍(看招牌+光柱)、側面(看光柱掃的弧)、以及從光柱底座往上看。
            var shots = new (string Tag, Vector3 Pos, Vector3 Look)[]
            {
                ("front", new Vector3(300f, 600f, -1800f), new Vector3(300f, 500f, 900f)),
                ("side",  new Vector3(-2400f, 700f, 900f), new Vector3(300f, 500f, 1000f)),
                ("up",    new Vector3(1400f, 200f, 200f),  new Vector3(301f, 900f, 1101f)),
            };
            for (int i = 0; i < shots.Length; i++)
            {
                camGo.transform.position = shots[i].Pos;
                camGo.transform.rotation = Quaternion.LookRotation(shots[i].Look - shots[i].Pos, Vector3.up);
                // 光柱一輪 ~6.5 s:每個機位取 4 個相位,才看得出它在掃。
                for (int k = 0; k < 4; k++)
                {
                    CapFrom(free, $"{OutDir}/scn0024-ov-{shots[i].Tag}{k}.png");
                    yield return new WaitForSecondsRealtime(1.5f);
                }
            }
            Object.Destroy(camGo);
        }

        private static void CapFrom(Camera cam, string path)
        {
            var rt = new RenderTexture(W, H, 24);
            cam.targetTexture = rt; cam.Render(); cam.targetTexture = null;
            var tex = ReadRGBA(rt);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.Destroy(tex); Object.Destroy(rt);
            Debug.Log("[scn0024-cap] saved " + path);
        }

        // 場景畫在 RenderTexture 上、再由主 ortho 相機的全螢幕 quad 合成。批次模式不會自動 render 離屏相機,
        // 所以先手動 Render() 每台有 targetTexture 的相機,再 render 主相機。另外把場景 RT 單獨存成 *-bg.png
        // (沒有 HUD 疊層)方便看舞台本身。與 SceneEftCaptureTest.Cap 同一套。
        private static void Cap(string path)
        {
            var main = Camera.main; if (main == null) return;
            var rt = new RenderTexture(W, H, 24);
            RenderTexture sceneRT = null;
            foreach (var c in Camera.allCameras)
                if (c != main && c.targetTexture != null) { c.Render(); sceneRT = c.targetTexture; }
            if (sceneRT != null)
            {
                var bgt = ReadRGBA(sceneRT);
                File.WriteAllBytes(path.Replace(".png", "-bg.png"), bgt.EncodeToPNG());
                Object.Destroy(bgt);
            }
            main.targetTexture = rt; main.Render(); main.targetTexture = null;
            var tex = ReadRGBA(rt);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.Destroy(tex); Object.Destroy(rt);
            Debug.Log("[scn0024-cap] saved " + path);
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
