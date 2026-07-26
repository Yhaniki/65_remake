using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// SCN0004(海灘)的人眼複核截圖 + 渲染排序探針。
    ///
    /// 這支是為了追「海浪漫到房子上」而寫的:海面 (SEA) 與岸浪 (BEACH/LANG) 是兩塊很大的水面片
    /// (SEA 平躺在 y = −48.9、x −2134..682、z −931..2432;LANG y −69.5..−5.7),官方讓它們用正弦
    /// 來回盪 (StageScene_UpdateWaveTilt_004afd30)。它們的貼圖是 DXT3:haishuei2_ 的 alpha 最低只到 51
    /// (**永遠不會全透明**)、wave_ 的 alpha 平均才 11.6。一旦這兩片被排到房子/棧道前面畫,
    /// 就會整片藍色蓋上去。
    ///
    /// 所以除了存 PNG,更重要的是把每個水面片與場景本體的 shader / renderQueue / bounds 印出來 ——
    /// 排序問題用肉眼看圖是猜的,看 queue 才是證據。
    ///
    /// Run: -runTests -batchmode -force-d3d11 -projectPath "h:\65_remake\65\My project" -testPlatform PlayMode
    ///      -testFilter Sdo.Tests.Scn0004CaptureTest -logFile &lt;log&gt;    (不要加 -nographics)
    /// </summary>
    public class Scn0004CaptureTest
    {
        private const int W = 800, H = 600;
        private static string OutDir => System.Environment.GetEnvironmentVariable("SDO_SHOT_DIR") ?? "H:/65_remake";
        private static string Tag => System.Environment.GetEnvironmentVariable("SDO_SHOT_TAG") ?? "scn0004";

        private static void SceneOnlyScn0004(Sdo.Game.ScreenGameplay g)
        {
            g.scenePath = "SCENE/SCN0004";
            g.observeBurstMode = true;
        }

        [UnityTearDown]
        public IEnumerator TearDown() => GameplayBoot.Teardown();

        [UnityTest]
        public IEnumerator Capture_Scn0004_Water_Against_Houses()
        {
            Sdo.Game.ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g, SceneOnlyScn0004);
            Assert.IsTrue(game.observeBurstMode && game.scenePath.ToUpperInvariant().Contains("SCN0004"),
                $"not a clean SCN0004 scene-only boot (scenePath={game.scenePath}, observe={game.observeBurstMode})");
            game.SetCamModeForTest(0);
            yield return new WaitForSecondsRealtime(0.5f);

            Probe();

            // 診斷用:SDO_HIDE_WATER=1 把四片水面關掉再拍,用來分辨「棧道上的藍色是水面片穿透」還是
            // 「本來就畫在 SCENE.MSH 的甲板貼圖上」—— 這種問題看圖猜沒有用,要能一次關掉變因。
            if (System.Environment.GetEnvironmentVariable("SDO_HIDE_WATER") == "1")
            {
                int hidden = 0;
                foreach (var mr in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
                {
                    var pn = mr.transform.parent ? mr.transform.parent.name : "";
                    if (!mr.gameObject.name.StartsWith("SEA") && !mr.gameObject.name.StartsWith("LANG") &&
                        !pn.StartsWith("SEA") && !pn.StartsWith("LANG")) continue;
                    mr.enabled = false; hidden++;
                }
                Debug.Log($"[scn0004-probe] hid {hidden} water renderer(s)");
            }

            // 浪一輪 2.65 s、海一輪 10.6 s —— 取樣要橫跨慢的那個:1.3 s 一格連拍 10 張 = 13 s。
            for (int i = 0; i < 10; i++)
            {
                Cap($"{OutDir}/{Tag}-t{i}.png");
                yield return new WaitForSecondsRealtime(1.3f);
            }
            Probe();   // 動完再印一次:offset 有沒有真的在跑
        }

        /// <summary>
        /// 自由機位俯瞰:固定機位一律盯著舞者(棧橋上),看不到沙灘那一側。判斷「水該蓋住沙灘、但不該蓋住
        /// 房子」必須同時看到兩者,所以另外拉一台相機從幾個角度照整片海岸。
        /// </summary>
        [UnityTest]
        public IEnumerator Capture_Scn0004_Shoreline_Overview()
        {
            Sdo.Game.ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g, SceneOnlyScn0004);
            yield return new WaitForSecondsRealtime(0.5f);

            var camGo = new GameObject("Scn0004FreeCam");
            var free = camGo.AddComponent<Camera>();
            free.fieldOfView = 55f; free.nearClipPlane = 1f; free.farClipPlane = 20000f;
            var stage = Camera.allCameras;
            foreach (var c in stage) if (!c.orthographic) { free.cullingMask = c.cullingMask; free.clearFlags = c.clearFlags; free.backgroundColor = c.backgroundColor; break; }
            // SceneBackdrop 是主 ortho 相機用來合成場景 RT 的全螢幕 quad(z=90 的 800×600 平面)。自由機位若
            // 收到它,畫面正中央會多一塊「天空面板」擋住海岸線 —— 那是測試自己的假象,不是場景問題。剔掉它的 layer。
            var backdrop = GameObject.Find("SceneBackdrop");
            if (backdrop != null) free.cullingMask &= ~(1 << backdrop.layer);

            // 場景包圍盒約 x[−1339..1614] y[−114..1343] z[−1055..2056];岸浪 LANG 在 x[−400..862] z[−1018..2033]。
            var shots = new (string Tag, Vector3 Pos, Vector3 Look)[]
            {
                ("far",   new Vector3(-1400f,  700f, -900f),  new Vector3(200f, -40f,  700f)),
                ("shore", new Vector3( 1500f,  350f, -600f),  new Vector3(  0f, -40f,  700f)),
                ("top",   new Vector3(  200f, 1400f,  -600f), new Vector3(200f, -40f,  700f)),
            };
            for (int i = 0; i < shots.Length; i++)
            {
                camGo.transform.position = shots[i].Pos;
                camGo.transform.rotation = Quaternion.LookRotation(shots[i].Look - shots[i].Pos, Vector3.up);
                // 浪一輪 2.65 s:每個機位取 2 個相位就夠判斷遮蔽關係。
                for (int k = 0; k < 2; k++)
                {
                    CapFrom(free, $"{OutDir}/{Tag}-ov-{shots[i].Tag}{k}.png");
                    yield return new WaitForSecondsRealtime(1.3f);
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
            Debug.Log("[scn0004-cap] saved " + path);
        }

        // 把水面片與場景本體的排序資訊全部印出來。renderQueue 才是「誰蓋誰」的答案。
        private static void Probe()
        {
            foreach (var mr in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                var n = mr.gameObject.name;
                var pn = mr.transform.parent ? mr.transform.parent.name : "-";
                bool water = n.StartsWith("SEA") || n.StartsWith("LANG") || pn.StartsWith("SEA") || pn.StartsWith("LANG");
                bool scene = n.ToUpperInvariant().Contains("SCENE") || pn.ToUpperInvariant().Contains("SCENE");
                if (!water && !scene) continue;
                var m = mr.sharedMaterial;
                Debug.Log($"[scn0004-probe] {n} (parent={pn}) shader={m?.shader?.name} queue={m?.renderQueue} " +
                          $"zwrite={(m != null && m.HasProperty("_ZWrite") ? m.GetFloat("_ZWrite").ToString() : "n/a")} " +
                          $"uvOffset={(m != null ? m.mainTextureOffset.ToString("F3") : "-")} " +
                          $"bounds c={mr.bounds.center} s={mr.bounds.size}");
            }
        }

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
            Debug.Log("[scn0004-cap] saved " + path);
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
