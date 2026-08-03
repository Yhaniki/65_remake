using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// SCN0004 海灘「水面接縫 / 動畫節奏」診斷。
    ///
    /// 兩個回報症狀:
    ///   1. 海水的流動(SEA/LANG 的正弦 UV)與海浪換幀動畫(SEA_UP=B001..B032 / SEA_DOWN=A001..A032)
    ///      節奏對不上。
    ///   2. 海水裡有奇怪的分割線。
    ///
    /// 這支不做斷言(它是探針):把四片水的世界包圍盒、shader、queue、ZWrite、貼圖、wrap mode、
    /// 每片的 UV offset 與當下換幀貼圖名逐格印出來,並用「貼著水面往海看」的自由機位連拍 ——
    /// 分割線是幾何邊界、z-fighting、還是 UV 拼貼,看圖 + 看 probe 才分得出來。
    ///
    /// Run: -runTests -batchmode -force-d3d11 -projectPath "h:\65_remake\65\My project" -testPlatform PlayMode
    ///      -testFilter Sdo.Tests.Scn0004WaterSeamTest -logFile &lt;log&gt;   (不要加 -nographics)
    /// </summary>
    public class Scn0004WaterSeamTest
    {
        private const int W = 800, H = 600;
        private static string OutDir => System.Environment.GetEnvironmentVariable("SDO_SHOT_DIR") ?? Sdo.Game.SdoTestOutput.Dir("scene");
        private static string Tag => System.Environment.GetEnvironmentVariable("SDO_SHOT_TAG") ?? "seam";

        private static readonly string[] Layers = { "SEA_UP", "SEA_DOWN", "SEA", "LANG" };

        [UnityTearDown]
        public IEnumerator TearDown() => GameplayBoot.Teardown();

        [UnityTest]
        public IEnumerator Diag_Scn0004_Water_Seams()
        {
            Sdo.Game.ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g, g => { g.scenePath = "SCENE/SCN0004"; g.observeBurstMode = true; });
            yield return new WaitForSecondsRealtime(0.5f);

            Probe("t=0.5");

            // 每片水的世界包圍盒 → 自動架機位,不用猜場景朝向。
            var box = new Dictionary<string, Bounds>();
            foreach (var layer in Layers) { var b = WorldBounds(layer); if (b.HasValue) box[layer] = b.Value; }
            foreach (var kv in box)
                Debug.Log($"[seam] bounds {kv.Key}: c={kv.Value.center.ToString("F1")} size={kv.Value.size.ToString("F1")} " +
                          $"y[{kv.Value.min.y:F1}..{kv.Value.max.y:F1}]");

            var camGo = new GameObject("Scn0004SeamCam");
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 55f; cam.nearClipPlane = 1f; cam.farClipPlane = 20000f;
            foreach (var c in Camera.allCameras) if (!c.orthographic) { cam.cullingMask = c.cullingMask; cam.clearFlags = c.clearFlags; cam.backgroundColor = c.backgroundColor; break; }
            var backdrop = GameObject.Find("SceneBackdrop");
            if (backdrop != null) cam.cullingMask &= ~(1 << backdrop.layer);

            // 機位 A「deck」:站在舞台(棧橋)上、貼著甲板高度往海面看 —— 使用者實機截圖的角度。
            // 機位 B「down」:同一點但直接俯視水面,分割線在俯視角最清楚。
            // 機位 C「wide」:整片水的上方,看得到四片水彼此的邊界。
            Vector3 sea = box.ContainsKey("SEA") ? box["SEA"].center : Vector3.zero;
            Vector3 all = Vector3.zero; foreach (var kv in box) all += kv.Value.center; if (box.Count > 0) all /= box.Count;
            float seaY = box.ContainsKey("SEA") ? box["SEA"].max.y : 0f;

            var shots = new (string Tag, Vector3 Pos, Vector3 Look)[]
            {
                ("deck", new Vector3(0f, seaY + 90f, 0f), new Vector3(sea.x, seaY, sea.z)),
                ("down", new Vector3(sea.x * 0.4f, seaY + 260f, sea.z * 0.4f), new Vector3(sea.x * 0.45f, seaY, sea.z * 0.45f)),
                ("wide", new Vector3(all.x, seaY + 1600f, all.z - 1400f), all),
                // 使用者實機截圖的角度:站在海面上、朝沙灘(+x)略俯 —— 近景是水、遠景是棧橋與草屋。
                ("user", new Vector3(-450f, seaY + 75f, 250f), new Vector3(250f, seaY - 6f, 250f)),
            };

            foreach (var s in shots)
            {
                Aim(camGo, s.Pos, s.Look);
                yield return null;
                CapFrom(cam, $"{OutDir}/{Tag}-{s.Tag}-all.png");
                foreach (var layer in Layers)
                {
                    foreach (var o in Layers) if (o != layer) SetLayerVisible(o, false);
                    yield return null;
                    CapFrom(cam, $"{OutDir}/{Tag}-{s.Tag}-only{layer}.png");
                    foreach (var o in Layers) SetLayerVisible(o, true);
                    yield return null;
                }
            }

            // 歸屬圖:四片水各染一色(SEA 紅 / SEA_UP 綠 / SEA_DOWN 藍 / LANG 黃),畫面上每一條分割線
            // 是「哪兩片的交界」還是「同一片自己的 UV 拼貼縫」,在這張圖上一眼可判。
            var tint = new Dictionary<string, Color>
            {
                { "SEA", new Color(1f, 0.25f, 0.25f) }, { "SEA_UP", new Color(0.25f, 1f, 0.25f) },
                { "SEA_DOWN", new Color(0.3f, 0.4f, 1f) }, { "LANG", new Color(1f, 0.95f, 0.3f) },
            };
            var saved = new List<(Material M, Color C)>();
            foreach (var kv in tint)
                foreach (var m in MatsOf(kv.Key)) { saved.Add((m, m.color)); m.color = kv.Value; }
            foreach (var s in shots)
            {
                Aim(camGo, s.Pos, s.Look);
                yield return null;
                CapFrom(cam, $"{OutDir}/{Tag}-{s.Tag}-tint.png");
            }
            foreach (var sv in saved) sv.M.color = sv.C;
            yield return null;

            // 動畫節奏:0.25 s 一格連拍 16 張(4 秒)。每格印四片水的 UV offset 與當下換幀貼圖名 ——
            // 「流速」與「換幀」是兩套時鐘,對不上就會在這張表上直接看出來。
            Aim(camGo, shots[1].Pos, shots[1].Look);
            for (int i = 0; i < 16; i++)
            {
                Probe($"anim t={i * 0.25f:F2}");
                CapFrom(cam, $"{OutDir}/{Tag}-anim{i:00}.png");
                yield return new WaitForSecondsRealtime(0.25f);
            }
            Object.Destroy(camGo);
        }

        private static void Aim(GameObject go, Vector3 pos, Vector3 look)
        {
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation((look - pos).normalized, Vector3.up);
        }

        private static Bounds? WorldBounds(string group)
        {
            Bounds? acc = null;
            foreach (var mr in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                var pn = mr.transform.parent ? mr.transform.parent.name : "";
                if (!Belongs(mr.gameObject.name, group) && !Belongs(pn, group)) continue;
                if (acc.HasValue) { var b = acc.Value; b.Encapsulate(mr.bounds); acc = b; }
                else acc = mr.bounds;
            }
            return acc;
        }

        private static List<Material> MatsOf(string group)
        {
            var list = new List<Material>();
            foreach (var mr in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                var pn = mr.transform.parent ? mr.transform.parent.name : "";
                if (!Belongs(mr.gameObject.name, group) && !Belongs(pn, group)) continue;
                foreach (var m in mr.sharedMaterials) if (m != null && m.HasProperty("_Color")) list.Add(m);
            }
            return list;
        }

        private static int SetLayerVisible(string group, bool on)
        {
            int n = 0;
            foreach (var mr in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                var pn = mr.transform.parent ? mr.transform.parent.name : "";
                if (!Belongs(mr.gameObject.name, group) && !Belongs(pn, group)) continue;
                mr.enabled = on; n++;
            }
            return n;
        }

        private static bool Belongs(string name, string group)
            => name == group || name == group + "_mesh" || name.StartsWith(group + "_0");

        private static void Probe(string when)
        {
            foreach (var mr in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                var n = mr.gameObject.name;
                var pn = mr.transform.parent ? mr.transform.parent.name : "-";
                bool water = false;
                foreach (var l in Layers) if (Belongs(n, l) || Belongs(pn, l)) water = true;
                if (!water) continue;
                var mesh = mr.GetComponent<MeshFilter>() != null ? mr.GetComponent<MeshFilter>().sharedMesh : null;
                var mats = mr.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i]; if (m == null) continue;
                    var tex = m.mainTexture;
                    string sub = mesh != null && i < mesh.subMeshCount
                        ? $"subBounds y[{mesh.GetSubMesh(i).bounds.min.y:F1}..{mesh.GetSubMesh(i).bounds.max.y:F1}]" : "-";
                    Debug.Log($"[seam:{when}] {n}(p={pn})[{i}] shader={m.shader?.name} q={m.renderQueue} " +
                              $"zwrite={(m.HasProperty("_ZWrite") ? m.GetFloat("_ZWrite").ToString("F0") : "n/a")} " +
                              $"cull={(m.HasProperty("_Cull") ? m.GetFloat("_Cull").ToString("F0") : "n/a")} " +
                              $"cutoff={(m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff").ToString("F2") : "n/a")} " +
                              $"uv={m.mainTextureOffset.ToString("F3")} scale={m.mainTextureScale.ToString("F2")} " +
                              $"tex={(tex != null ? tex.name : "null")} " +
                              $"{(tex != null ? $"{tex.width}x{tex.height} mips={(tex is Texture2D t2 ? t2.mipmapCount : -1)} wrap={tex.wrapMode} filter={tex.filterMode}" : "")} " +
                              $"worldY[{mr.bounds.min.y:F1}..{mr.bounds.max.y:F1}] {sub}");
                }
            }
        }

        private static void CapFrom(Camera cam, string path)
        {
            var rt = new RenderTexture(W, H, 24);
            cam.targetTexture = rt; cam.Render(); cam.targetTexture = null;
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.Destroy(tex); Object.Destroy(rt);
            Debug.Log("[seam-cap] saved " + path);
        }
    }
}
