using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// lilToon 後端**真的畫得出來**。翻譯規則那邊（<see cref="MmdLilToonTests"/>）測的是屬性寫對了沒，
    /// 但屬性寫對、shader 卻在這個 Unity/URP 版本上編不起來的話，畫面就是一片洋紅 —— 而那只有渲染才看得見。
    ///
    /// 這裡渲一顆用 <see cref="MmdLilToon.Configure"/> 設好的球，守三件事：
    ///   ① 不是洋紅（shader error 的顏色）
    ///   ② 不是全黑（lilToon 吃光照；沒有 <see cref="MmdKeyLight"/> 或亮度下限設太低就會整片黑）
    ///   ③ 真的有 cel 分界（亮面與暗面差得夠開；沒有的話就只是一顆平光球，開這個後端等於沒開）
    ///
    /// lilToon 沒裝就整組 Ignore（見 docs/MMD_MODELS.md，它不入庫）。
    /// </summary>
    public class MmdLilToonRenderTests
    {
        private const int Size = 128;

        [Test]
        public void ALilToonSphereRendersLitWithACelBoundary()
        {
            var shader = Shader.Find(MmdLilToon.ShaderOpaque);
            if (shader == null) Assert.Ignore("lilToon 沒裝（Assets/lilToon）—— 見 docs/MMD_MODELS.md。");

            var mat = new Material(shader);
            MmdLilToon.Configure(mat, Texture2D.whiteTexture, Color.white, doubleSided: false,
                                 MmdMaterialRenderMode.Opaque, sphereTex: null, sphereMode: 0, toonRamp: null,
                                 edgeColor: Color.black, edgeSize: 0f);

            var pixels = Render(mat, out var sphere, out var cam, out var rt, out var light);
            try
            {
                int magenta = 0, body = 0;
                float min = 1f, max = 0f; double sum = 0;
                foreach (var p in pixels)
                {
                    if (p.a < 0.5f) continue;                                   // 相機的清除色（透明）
                    // shader error 的洋紅是純 (1,0,1)。材質是白的，不可能自己畫出綠色為零的粉紅。
                    if (p.r > 0.85f && p.g < 0.15f && p.b > 0.85f) { magenta++; continue; }
                    float v = Mathf.Max(p.r, Mathf.Max(p.g, p.b));
                    body++; sum += v;
                    min = Mathf.Min(min, v); max = Mathf.Max(max, v);
                }
                // 亮/暗是相對這顆球自己的中點分的,不是絕對值 —— lilToon 的實際亮度受 color space、
                // 主光強度、_LightMaxLimit 一起影響,寫死門檻只會變成「換個設定就假紅」。
                float mid = (min + max) * 0.5f;
                int lit = 0, dark = 0;
                foreach (var p in pixels)
                {
                    if (p.a < 0.5f) continue;
                    if (p.r > 0.85f && p.g < 0.15f && p.b > 0.85f) continue;
                    float v = Mathf.Max(p.r, Mathf.Max(p.g, p.b));
                    if (v > mid) lit++; else dark++;
                }
                string stats = $"（body={body} min={min:F3} max={max:F3} mean={(body > 0 ? sum / body : 0):F3} lit={lit} dark={dark}）";

                Assert.AreEqual(0, magenta, "畫出洋紅 = lilToon 的 shader 在這個 Unity/URP 版本上編不起來。" + stats);
                Assert.Greater(body, 200, "球根本沒畫出來（相機/RT 沒對上）。" + stats);
                Assert.Greater(max, 0.25f, "整顆都是黑的 —— key light 沒作用，或 _LightMinLimit 被設太低。" + stats);
                Assert.Greater(lit, 50, "沒有亮面。" + stats);
                Assert.Greater(dark, 50, "沒有暗面 —— cel 陰影沒生效（_UseShadow / border 錯了），等於平光。" + stats);
                Assert.Greater(max - min, 0.15f, "亮暗差太小,看不出 cel 分界。" + stats);
            }
            finally
            {
                Cleanup(mat, sphere, cam, rt, light);
            }
        }

        [Test]
        public void TurningTheCelShadingOffFlattensTheSphere()
        {
            // 設定面板的「卡通著色」在這個後端管的是 lilToon 的 cel 陰影（MmdAvatar.SetToon 寫 _UseShadow）。
            // 關掉之後應該就沒有明顯的明暗分界了 —— 這條同時證明那個開關真的接到 lilToon 上。
            var shader = Shader.Find(MmdLilToon.ShaderOpaque);
            if (shader == null) Assert.Ignore("lilToon 沒裝（Assets/lilToon）。");

            // 同一顆球渲兩次,只差 _UseShadow —— 比「跟某個絕對亮度比」穩:兩張圖受 color space / 光強度的
            // 影響一模一樣,差的只有陰影本身。
            float withShadow = MeanBrightness(shader, celOn: true);
            float withoutShadow = MeanBrightness(shader, celOn: false);

            Assert.Greater(withoutShadow, withShadow + 0.05f,
                $"關掉 cel 陰影之後沒有變亮（有陰影 {withShadow:F3} → 沒陰影 {withoutShadow:F3}）"
                + " —— 設定面板的「卡通著色」沒接到 lilToon 的 _UseShadow 上。");
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>渲一顆 lilToon 球，回它畫出來的平均亮度（背景不算）。</summary>
        private static float MeanBrightness(Shader shader, bool celOn)
        {
            var mat = new Material(shader);
            MmdLilToon.Configure(mat, Texture2D.whiteTexture, Color.white, doubleSided: false,
                                 MmdMaterialRenderMode.Opaque, sphereTex: null, sphereMode: 0, toonRamp: null,
                                 edgeColor: Color.black, edgeSize: 0f);
            mat.SetFloat(MmdLilToon.ToonProperty, celOn ? 1f : 0f);

            var pixels = Render(mat, out var sphere, out var cam, out var rt, out var light);
            try
            {
                double sum = 0; int n = 0;
                foreach (var p in pixels)
                {
                    if (p.a < 0.5f) continue;
                    sum += Mathf.Max(p.r, Mathf.Max(p.g, p.b)); n++;
                }
                Assert.Greater(n, 200, "球根本沒畫出來（相機/RT 沒對上）。");
                return (float)(sum / n);
            }
            finally { Cleanup(mat, sphere, cam, rt, light); }
        }

        private static Color[] Render(Material mat, out GameObject sphere, out Camera cam, out RenderTexture rt, out GameObject light)
        {
            // 專屬圖層不好在測試裡保證乾淨（專案自己也在用），所以改用「場上只有這顆球」+ 透明清除色來認背景。
            sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.transform.position = Vector3.zero;
            sphere.GetComponent<Renderer>().sharedMaterial = mat;

            // MmdKeyLight 的燈是 DontDestroyOnLoad 的常駐物件，測試不該留下它 → 這裡自己擺一顆同角度同強度的。
            light = new GameObject("TestKeyLight");
            light.transform.eulerAngles = MmdKeyLight.EulerAngles;
            var l = light.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = MmdKeyLight.Intensity;

            var camGo = new GameObject("TestCam");
            cam = camGo.AddComponent<Camera>();
            cam.transform.position = new Vector3(0f, 0f, -3f);
            cam.transform.rotation = Quaternion.identity;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.orthographic = true;
            cam.orthographicSize = 0.8f;

            rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;

            // lilToon 的 shader 很大，而 Unity 預設非同步編譯：第一次要求渲染時它還沒編好，那一幀畫出來的是內建的
            // 替代品（黑）。不關掉的話這個測試會「第一次跑紅、第二次就綠」—— 踩過。
            bool asyncWas = UnityEditor.EditorSettings.asyncShaderCompilation;
            UnityEditor.EditorSettings.asyncShaderCompilation = false;
            try { cam.Render(); }
            finally { UnityEditor.EditorSettings.asyncShaderCompilation = asyncWas; }

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            tex.Apply(false, false);
            RenderTexture.active = prev;

            var px = tex.GetPixels();
            Object.DestroyImmediate(tex);
            return px;
        }

        private static void Cleanup(Material mat, GameObject sphere, Camera cam, RenderTexture rt, GameObject light)
        {
            if (cam != null) { cam.targetTexture = null; Object.DestroyImmediate(cam.gameObject); }
            if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
            if (sphere != null) Object.DestroyImmediate(sphere);
            if (light != null) Object.DestroyImmediate(light);
            if (mat != null) Object.DestroyImmediate(mat);
        }
    }
}
