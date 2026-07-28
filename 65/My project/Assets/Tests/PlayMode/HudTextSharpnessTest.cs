using System.Collections;
using System.IO;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// 底列 HUD 白字(歌名／LV／時間)的 A/B 渲染驗證 —— 真的把畫面畫出來讀像素，不是只算數學。
    ///
    /// 起因：歌名先前被改成「依 designPx × scaleY 光柵」，但 designPx(11) 不是字在螢幕上真正佔的高度
    /// (em 盒＝1.28×11≈14 設計 px)，於是字圖被放大 1.28 倍畫 → 使用者回報「歌名變得很模糊」，而同一排的
    /// LV／時間還是舊的 fontSize 64(超取樣) → 兩種字重並排，style 不一致。
    ///
    /// 這支測試在同一次執行裡渲染兩次：新光柵(<see cref="NameplateMetrics.RasterPxForEm"/>) vs 舊光柵
    /// (fontSize 64 寫死 / FontPxFor)，量同一塊區域的邊緣梯度能量，並斷言：
    ///   1) 新版比舊版銳(梯度能量更高)；
    ///   2) 字的位置/大小沒被改到(白字覆蓋的 bounding box 幾乎一樣)。
    /// 兩張裁切圖也會落地(hud-text-*.png)供人眼複查。
    /// Run: -runTests -testPlatform PlayMode -screen-width 1920 -screen-height 1080
    /// </summary>
    public class HudTextSharpnessTest
    {
        private const string OutDir = "H:/65_remake";

        [UnityTearDown]
        public IEnumerator TearDown() => GameplayBoot.Teardown();

        [UnityTest]
        public IEnumerator Bottom_Row_Is_Sharper_Than_The_Old_Raster()
        {
            ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g);
            yield return new WaitForSecondsRealtime(3.0f);   // 讓 READY/GO 過去、底列都上了字

            int w = Mathf.Max(Screen.width, 640), h = Mathf.Max(Screen.height, 480);
            var now = Render(w, h, OutDir + "/hud-text-new.png");

            // ---- 換回舊光柵：fontSize 64 寫死(LV/時間) / FontPxFor(歌名)，characterSize 用當年的配對 ----
            float sy = NameplateMetrics.ScaleY(Screen.height, AspectController.ContentRect);
            int oldSongPx = NameplateMetrics.FontPxFor(11f, sy);
            foreach (var tm in Object.FindObjectsByType<TextMesh>(FindObjectsSortMode.None))
            {
                if (!IsBottomRow(tm)) continue;
                bool isSongTitle = tm.transform.parent != null && tm.transform.parent.name == "MusicName";
                int px = isSongTitle ? oldSongPx : NameplateMetrics.CalibrationFontPx;
                tm.fontSize = px;
                tm.characterSize = NameplateMetrics.CharacterSizeFor(11f, px, NameplateMetrics.PxToCharSizeLegacyAt64);
            }
            yield return null;   // legacy TextMesh 的 mesh 隔一幀才重建
            yield return null;
            var before = Render(w, h, OutDir + "/hud-text-old.png");

            Debug.Log($"[HudText] sy={sy} newRaster={NameplateMetrics.RasterPxForEm(11f, sy, NameplateMetrics.PxToCharSizeLegacyAt64)} " +
                      $"oldSongRaster={oldSongPx} | grad new={now.Gradient:F2} old={before.Gradient:F2} | " +
                      $"ink new={now.InkX0}..{now.InkX1} old={before.InkX0}..{before.InkX1}");

            Assert.Greater(now.Gradient, before.Gradient * 1.05f,
                $"新光柵沒有比舊的銳：new={now.Gradient:F2} old={before.Gradient:F2}");
            // 只准變清楚，不准變大小/位移：白字墨水的水平範圍要對得上(容 2 個實體 px)
            Assert.AreEqual(before.InkX0, now.InkX0, 2, "歌名左緣位移了");
            Assert.AreEqual(before.InkX1, now.InkX1, 2, "歌名右緣位移了(字被改大或改小)");
        }

        /// <summary>底列(design y 575..597)的白字：歌名的每個字(父物件 MusicName)、LV 值、時間三欄。</summary>
        private static bool IsBottomRow(TextMesh tm)
        {
            string n = tm.transform.parent != null ? tm.transform.parent.name : tm.name;
            return n == "MusicName" || tm.name == "MusicLev" || tm.name.StartsWith("MusicTime");
        }

        private struct Metrics { public float Gradient; public int InkX0, InkX1; }

        /// <summary>渲染主相機到螢幕大小的 RT，存下底列裁切圖，並回傳該區域的銳利度指標。</summary>
        private static Metrics Render(int w, int h, string path)
        {
            var main = Camera.main;
            var rt = new RenderTexture(w, h, 24);
            foreach (var c in Camera.allCameras)
                if (c != main && c.targetTexture != null) c.Render();
            main.targetTexture = rt; main.Render(); main.targetTexture = null;

            RenderTexture.active = rt;
            var full = new Texture2D(w, h, TextureFormat.RGBA32, false);
            full.ReadPixels(new Rect(0, 0, w, h), 0, 0); full.Apply();
            RenderTexture.active = null;

            // 底列在設計座標 y 575..597；ReadPixels 原點在左下 → 從畫面底部往上量。x 只取歌名欄(80..260)。
            float sy = h / 600f, sx = w / 800f;
            int y0 = Mathf.Clamp(Mathf.RoundToInt((600f - 597f) * sy), 0, h - 2);
            int y1 = Mathf.Clamp(Mathf.RoundToInt((600f - 575f) * sy), y0 + 2, h);
            int x0 = Mathf.Clamp(Mathf.RoundToInt(80f * sx), 0, w - 2);
            int x1 = Mathf.Clamp(Mathf.RoundToInt(260f * sx), x0 + 2, w);

            var crop = new Texture2D(x1 - x0, y1 - y0, TextureFormat.RGB24, false);
            crop.SetPixels(full.GetPixels(x0, y0, x1 - x0, y1 - y0)); crop.Apply(false);
            File.WriteAllBytes(path, crop.EncodeToPNG());

            var m = Measure(crop);
            Object.Destroy(crop); Object.Destroy(full); Object.Destroy(rt);
            Debug.Log("[HudText] saved " + path);
            return m;
        }

        /// <summary>邊緣能量(相鄰像素亮度差的平均)＋白字墨水的水平範圍。放大取樣會把邊緣抹平 → 梯度下降。</summary>
        private static Metrics Measure(Texture2D t)
        {
            var px = t.GetPixels();
            int w = t.width, h = t.height;
            float sum = 0f; int n = 0, x0 = w, x1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w - 1; x++)
                {
                    float a = Lum(px[y * w + x]), b = Lum(px[y * w + x + 1]);
                    sum += Mathf.Abs(a - b); n++;
                    if (a > 0.80f) { if (x < x0) x0 = x; if (x > x1) x1 = x; }   // 白字(底是深藍木紋)
                }
            return new Metrics { Gradient = n > 0 ? sum / n * 255f : 0f, InkX0 = x0, InkX1 = x1 };
        }

        private static float Lum(Color c) => 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
    }
}
