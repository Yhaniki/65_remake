using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 使用者回報「OPTION 音效頁的 slider 把手外圍有超大一圈灰色」。素材本身是乾淨的(把手 crop = 43×23 的粉膠囊、
    /// OptVolumeBoard 上只有 baked 的 MIN…MAX 線),所以只有實際渲染能證明那圈灰是哪來的:把 OptionDlgModal 的
    /// 音效 slider 結構(透明拖曳區 + 透明 fill + 官方把手 sprite)照原樣搭在官方板上,用放大的正交相機拍下來,
    /// 量把手 box 外面那一圈相對板底色的亮度差。EditMode 就能跑(Camera.Render 不需要 play mode);沒有遊戲資料時跳過。
    /// SDO_SHOT_DIR 有設就順手把照片 dump 出來人眼複驗。
    /// </summary>
    public class OptionSliderRenderTests
    {
        private const int Zoom = 8;                 // design px → render px（灰暈只有放大才看得清）

        // OptionDlgModal.BuildAudio 的實測座標
        private const float TrackX = 315f, TrackW = 188f, HandleCy = 282.5f;
        private static readonly Vector2 BoardAt = new Vector2(236f, 225f);

        private static Vector3 DesignToWorld(float x, float y) => new Vector3(x - 400f, 300f - y, 0f);

        [Test]
        public void AudioSliderHandle_EdgeDoesNotBleedIntoTheBoard()
            => AssertHaloWidth(OptionDlgArt.SliderHandle, "option-slider.png",
                               // 實測:銳化後 2.50 px(= 深紫描邊本身的寬度 + 1px AA,已是乾淨的下限);
                               // 原本的共用-atlas crop 是 4.50 px(描邊 + 2.5px 灰漸層)。
                               w => Assert.Less(w, 3.0f,
                                   $"把手邊緣到板底色的過渡有 {w:F2} design px — 又糊成一圈灰暈了"));

        [Test]
        public void RawAtlasCrop_StillBleeds_Control()
            // 對照組:同一顆把手走原本的 Crop(保留 3px 半透明深紫外圈)必須量得到明顯更寬的過渡,
            // 否則上面那個測試就是為了錯誤的理由通過(探針沒對準、或根本沒畫出來)。
            => AssertHaloWidth(OptionDlgArt.Crop(951, 0, 43, 23), "option-slider-raw.png",
                               w => Assert.Greater(w, 4.0f,
                                   $"對照組的過渡只有 {w:F2} design px — 這個量測失去鑑別力了"));

        private static void AssertHaloWidth(Sprite handle, string dumpName, System.Action<float> check)
        {
            if (handle == null || OptionDlgArt.AudioBoard == null)
                Assert.Ignore("OPTIONDLG art not present in this environment.");

            GameObject canvasGo = null, camGo = null;
            Texture2D shot = null;
            try
            {
                // 手搭一個和 UIKit.CreateWorldCanvas 同規格的 world canvas(1 design px = 1 world unit,置中),
                // 但不走 UIKit —— 它會 AspectController.Register,而 AspectController 用 DontDestroyOnLoad(EditMode 禁用)。
                canvasGo = new GameObject("SliderProbe", typeof(Canvas), typeof(CanvasScaler));
                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                var root = (RectTransform)canvas.transform;
                root.pivot = new Vector2(0.5f, 0.5f);
                root.sizeDelta = new Vector2(800f, 600f);
                root.position = Vector3.zero;
                root.localScale = Vector3.one;
                var scaler = canvasGo.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.dynamicPixelsPerUnit = 3f;

                UIKit.AddSprite(root, "AudioBoard", OptionDlgArt.AudioBoard, BoardAt.x, BoardAt.y);
                BuildBareSlider(root, handle, HandleCy, 0.5f);
                Canvas.ForceUpdateCanvases();

                // 把手中心 = fill 右緣 = track 起點 + 50% 寬
                float hx = TrackX + TrackW * 0.5f;
                var view = new Rect(hx - 45f, HandleCy - 30f, 90f, 60f);
                (shot, camGo) = Photograph(view, dumpName);

                // 板底色 = 同一列、把手左邊遠處的空白板(在 view 內,且不在 baked pill / 軌線上)
                float bgLum = SampleDesignAvg(shot, view, hx - 40f, HandleCy - 10f);

                // 灰暈 = 把手邊緣「化開」的寬度:沿把手正上方的中線往上掃,量最暗的描邊到「回到板底色」之間有多遠。
                // 實心描邊 → 1px 就回到板色;半透明外圈被放大 → 拖出好幾 px 的灰漸層。
                float width = HaloWidth(shot, view, hx, HandleCy, bgLum);
                TestContext.Out.WriteLine($"bg={bgLum:F4} 過渡寬度={width:F2} design px");
                check(width);
            }
            finally
            {
                if (shot != null) Object.DestroyImmediate(shot);
                if (camGo != null) Object.DestroyImmediate(camGo);
                if (canvasGo != null) Object.DestroyImmediate(canvasGo);
            }
        }

        /// <summary>沿把手正上方中線:最暗的描邊像素 → 第一個「回到板底色」的像素,兩者相距幾 design px。</summary>
        private static float HaloWidth(Texture2D shot, Rect view, float hx, float cy, float bgLum)
        {
            const float Step = 1f / Zoom;
            float darkestY = cy - 2f, darkest = 2f;
            for (float y = cy - 2f; y >= cy - 18f; y -= Step)
            {
                float l = SampleDesign(shot, view, hx, y);
                if (l < darkest) { darkest = l; darkestY = y; }
            }
            for (float y = darkestY; y >= cy - 22f; y -= Step)
                if (Mathf.Abs(SampleDesign(shot, view, hx, y) - bgLum) < 0.03f)
                    return darkestY - y;
            return 99f;   // 一路到板外都沒回到底色
        }

        private static float SampleDesign(Texture2D shot, Rect view, float dx, float dy)
        {
            int px = Mathf.Clamp(Mathf.RoundToInt((dx - view.xMin) * Zoom), 0, shot.width - 1);
            int py = Mathf.Clamp(shot.height - 1 - Mathf.RoundToInt((dy - view.yMin) * Zoom), 0, shot.height - 1);
            return Lum(shot.GetPixel(px, py));
        }

        // OptionDlgModal.AddSlider 的結構,原樣重搭(透明拖曳區 + 透明 fill + 官方把手騎在 fill 右緣)
        private static void BuildBareSlider(RectTransform p, Sprite handleSprite, float cy, float value)
        {
            const float H = 20f;
            var rt = UIKit.NewRect(p, "slider");
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(TrackX, -(cy - H / 2f)); rt.sizeDelta = new Vector2(TrackW, H);
            var bg = rt.gameObject.AddComponent<Image>(); bg.color = new Color(1f, 1f, 1f, 0f); bg.raycastTarget = true;
            var slider = rt.gameObject.AddComponent<Slider>();

            var fillArea = UIKit.NewRect(rt, "FillArea");
            fillArea.anchorMin = Vector2.zero; fillArea.anchorMax = Vector2.one;
            fillArea.offsetMin = new Vector2(1, 1); fillArea.offsetMax = new Vector2(-1, -1);
            var fill = UIKit.AddImage(fillArea, "Fill", new Color(1f, 1f, 1f, 0f)).rectTransform;
            fill.anchorMin = Vector2.zero; fill.anchorMax = Vector2.one; fill.sizeDelta = Vector2.zero;

            var handle = UIKit.AddImage(fill, "Handle", Color.white);
            handle.sprite = handleSprite; handle.raycastTarget = false;
            handle.rectTransform.anchorMin = handle.rectTransform.anchorMax = new Vector2(1f, 0.5f);
            handle.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            handle.rectTransform.sizeDelta = handleSprite.rect.size;
            handle.rectTransform.anchoredPosition = Vector2.zero;

            slider.fillRect = fill; slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0; slider.maxValue = 1; slider.targetGraphic = bg;
            slider.value = value;
        }

        private static (Texture2D, GameObject) Photograph(Rect view, string dumpName)
        {
            int rw = Mathf.RoundToInt(view.width) * Zoom, rh = Mathf.RoundToInt(view.height) * Zoom;
            var rt = new RenderTexture(rw, rh, 16, RenderTextureFormat.ARGB32);
            var camGo = new GameObject("SliderProbeCam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = view.height / 2f;
            var c = DesignToWorld(view.center.x, view.center.y);
            cam.transform.position = new Vector3(c.x, c.y, -100f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.targetTexture = rt;
            cam.Render();

            var prev = RenderTexture.active; RenderTexture.active = rt;
            var shot = new Texture2D(rw, rh, TextureFormat.RGBA32, false);
            shot.ReadPixels(new Rect(0, 0, rw, rh), 0, 0); shot.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null; rt.Release(); Object.DestroyImmediate(rt);

            var dump = System.Environment.GetEnvironmentVariable("SDO_SHOT_DIR");
            if (dumpName != null && !string.IsNullOrEmpty(dump) && Directory.Exists(dump))
                File.WriteAllBytes(Path.Combine(dump, dumpName), shot.EncodeToPNG());
            return (shot, camGo);
        }

        // design 座標 → 照片像素(view 左上為原點,照片是 y-up);取 1 design px 見方的平均以免踩到單一取樣雜訊
        private static float SampleDesignAvg(Texture2D shot, Rect view, float dx, float dy)
        {
            int cx = Mathf.RoundToInt((dx - view.xMin) * Zoom);
            int cy = shot.height - 1 - Mathf.RoundToInt((dy - view.yMin) * Zoom);
            int r = Mathf.Max(1, Zoom / 2);
            float sum = 0f; int n = 0;
            for (int y = cy - r; y <= cy + r; y++)
                for (int x = cx - r; x <= cx + r; x++)
                {
                    if (x < 0 || y < 0 || x >= shot.width || y >= shot.height) continue;
                    sum += Lum(shot.GetPixel(x, y)); n++;
                }
            return n == 0 ? 0f : sum / n;
        }

        private static float Lum(Color c) => 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
    }
}
