using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 真的把歌名畫出來,證明「字有沒有黏在一起」—— 因為 TMP 的 faux bold 會讓墨水再脹一點,光算字型
    /// metrics 不夠。三行同一首歌名(SimSun / 14px / Bold,跟歌單一模一樣),分別用:
    ///   0 固定收緊(舊行為,"STATE" 的 T 和 A 會連成一塊)/ 1 逐字串安全收緊(現在的行為)/ 2 自然字距(對照組)。
    /// 判定用「墨塊數」:掃描每一直行有沒有墨,字與字之間有空隙就多一塊。安全收緊必須跟自然字距一樣多塊。
    /// 第二個測試同樣把遊戲內名牌(legacy <see cref="TextMesh"/> 逐字佈局的 <see cref="Label3D"/>,收得更狠的
    /// 0.1em)畫出來存檔,給人眼複核中文/西文名字現在的鬆緊。
    /// 順便存 PNG 給人眼複核。跑法:-runTests -testPlatform PlayMode -testFilter Sdo.Tests.TextTrackingCaptureTest
    /// </summary>
    public class TextTrackingCaptureTest
    {
        private const string Title = "SOLID STATE SQUAD";   // 使用者回報的那一首:STATE 的 TA 黏在一起
        private const float Zoom = 6f;                      // 放大渲染,字縫的有無看得出來(幾何比例不變)
        private const float Thr = 0.35f;                    // 亮度超過這個才算「有墨」(SDF 邊緣是灰的)
        private static string OutDir => System.Environment.GetEnvironmentVariable("SDO_SHOT_DIR") ?? "H:/65_remake";

        [UnityTest]
        public IEnumerator SafeTightening_KeepsLettersApart()
        {
            var rt = new RenderTexture(1600, 420, 24);
            var camGo = new GameObject("tt_cam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.targetTexture = rt;

            var canvasGo = new GameObject("tt_canvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 5f;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = Zoom;

            // 純黑底板：batchmode 配到的 RT 可能帶著別人用過的顯存殘影，墨塊判定要有乾淨的背景。
            var bg = new GameObject("backdrop").AddComponent<Image>();
            bg.transform.SetParent(canvas.transform, false);
            bg.color = Color.black;
            bg.rectTransform.anchorMin = Vector2.zero;
            bg.rectTransform.anchorMax = Vector2.one;
            bg.rectTransform.sizeDelta = Vector2.zero;

            var tight = MakeRow(canvas.transform, "old_fixed", 22f);
            tight.characterSpacing = -TextStyles.SongTitleTrackEm * 100f;              // 舊行為
            var safe = MakeRow(canvas.transform, "new_safe", 0f);
            // 現在的行為:characterSpacing 只收緊,撐開交給逐字對的 <cspace>(歌單走的就是這條)
            TextTracking.ApplyOpticalTracking(safe, Title, TextStyles.SongTitleTrackEm, TextStyles.MinInkGapEm,
                                              TextStyles.SongTitleOpticalGapPx);
            var natural = MakeRow(canvas.transform, "natural", -22f);
            natural.characterSpacing = 0f;                                              // 對照組

            if (tight.font == null) Assert.Ignore("沒有可用的 CJK 字型,跳過");

            Canvas.ForceUpdateCanvases();
            for (int i = 0; i < 3; i++) yield return null;
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var shot = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            shot.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); shot.Apply();
            RenderTexture.active = prev;
            Directory.CreateDirectory(OutDir);
            File.WriteAllBytes(Path.Combine(OutDir, "songtitle-tracking.png"), shot.EncodeToPNG());

            var px = shot.GetPixels32();
            int yMid = rt.height / 2, band = (int)(9f * Zoom);   // 大寫字高 ≈ 9px @ fontSize 14
            int blocksOld = InkBlocks(px, rt.width, yMid + (int)(22f * Zoom) - band, yMid + (int)(22f * Zoom) + band, out _);
            int blocksSafe = InkBlocks(px, rt.width, yMid - band, yMid + band, out int valleySafe);
            int blocksNat = InkBlocks(px, rt.width, yMid - (int)(22f * Zoom) - band, yMid - (int)(22f * Zoom) + band, out int valleyNat);
            Debug.Log($"[SongTitleTracking] 墨塊數 固定收緊={blocksOld} 安全字距={blocksSafe} 自然字距={blocksNat}" +
                      $" / 最窄字縫(px) 安全={valleySafe} 自然={valleyNat}" +
                      $" / characterSpacing 固定={tight.characterSpacing:0.###} 安全={safe.characterSpacing:0.###}" +
                      $" → {Path.Combine(OutDir, "songtitle-tracking.png")}");

            Object.Destroy(shot); Object.Destroy(canvasGo); Object.Destroy(camGo);
            cam.targetTexture = null; rt.Release();

            // 15 個字母(SOLID/STATE/SQUAD)—— 只要有任何一對黏成一塊就會少一塊,所以這裡要的是「剛好 15」。
            // 舊的 >=14 正好放行一對黏著,是這個 bug 當初測試綠燈卻沒修好的原因。
            Assert.AreEqual(15, blocksSafe, "安全字距下必須 15 個字母各自分開(少一塊 = 有兩個字黏在一起)");
            // 光是「跟自然字距一樣」不夠:SimSun 的 TA 天生只有 0.60px,自然字距下也只剩一個半亮的像素縫。
            Assert.Greater(valleySafe, valleyNat, "最窄的字縫必須比自然字距更寬(天生不夠的字縫要撐開,不是只有不收緊)");
        }

        /// <summary>遊戲內名牌那條路(legacy TextMesh 逐字佈局):把中文名和西文名各排一行拍下來。西文的字縫
        /// 不夠 0.1em 可收 → 退回自然字距;中文照收。存 PNG 供人眼複核鬆緊,並確認沒有排成反向(cell 亂序)。</summary>
        [UnityTest]
        public IEnumerator HeadNameLabel3D_Capture()
        {
            var font = TextStyles.CjkFont();
            if (font == null || UIFont.Cjk == null) Assert.Ignore("沒有可用的 CJK 字型,跳過");
            TextTracking.MeasureFont = UIFont.Cjk;   // runtime 由 UIFont 掛上,這裡明確設定不依賴測試順序

            const int Layer = 31;                    // 空 layer:相機只拍名牌,房間場景不會入鏡
            var rt = new RenderTexture(1200, 300, 24);
            var camGo = new GameObject("hn_cam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 60f;              // 1 world unit = 1 design px(Label3D 的慣例)
            cam.transform.position = new Vector3(90f, 0f, -10f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = 1 << Layer;
            cam.targetTexture = rt;

            var cjk = TextStyles.NewLabel("hn_cjk", TextStyles.Style.HeadName, 0, 22f, TextAnchor.MiddleLeft, Layer);
            cjk.Text = "戀愛達人的祕密";
            cjk.Position = new Vector3(0f, 24f, 0f);
            var latin = TextStyles.NewLabel("hn_latin", TextStyles.Style.HeadName, 0, 22f, TextAnchor.MiddleLeft, Layer);
            latin.Text = "SOLID STATE SQUAD";
            latin.Position = new Vector3(0f, -24f, 0f);

            for (int i = 0; i < 3; i++) yield return null;
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var shot = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            shot.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); shot.Apply();
            RenderTexture.active = prev;
            Directory.CreateDirectory(OutDir);
            string path = Path.Combine(OutDir, "headname-tracking.png");
            File.WriteAllBytes(path, shot.EncodeToPNG());

            // 名牌帶 16 向黑色描邊,字與字之間會被描邊填掉 → 墨塊數在這裡沒有判讀價值(那是歌名那條測試的手法)。
            // 這張圖是給人眼複核鬆緊的;自動斷言只確認兩行真的畫出來了(收緊量本身由 EditMode 的幾何測試鎖住)。
            var px = shot.GetPixels32();
            float pxPerUnit = rt.height / (2f * cam.orthographicSize);   // world y → 圖上的像素 y
            int cjkY = Mathf.RoundToInt(rt.height / 2f + 24f * pxPerUnit);
            int latinY = Mathf.RoundToInt(rt.height / 2f - 24f * pxPerUnit);
            int half = Mathf.RoundToInt(11f * pxPerUnit);                // 大寫字高的一半(pxSize 22)
            int cjkBlocks = CountInk(px, rt.width, cjkY - half, cjkY + half);
            int latinBlocks = CountInk(px, rt.width, latinY - half, latinY + half);
            Debug.Log($"[HeadNameTracking] 亮像素 中文={cjkBlocks} 西文={latinBlocks} → {path}");

            Object.Destroy(shot); Object.Destroy(cjk.root); Object.Destroy(latin.root); Object.Destroy(camGo);
            cam.targetTexture = null; rt.Release();

            Assert.Greater(cjkBlocks, 100, "中文名字沒畫出來");
            Assert.Greater(latinBlocks, 100, "西文名字沒畫出來");
        }

        private static TextMeshProUGUI MakeRow(Transform parent, string name, float y)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            var f = UIFont.Cjk; if (f != null) t.font = f;
            t.fontSize = 14;                       // 跟歌單同字級
            t.fontStyle = FontStyles.Bold;         // 歌單也開了 Bold(墨水會再脹一點)
            t.color = Color.white;
            t.alignment = TextAlignmentOptions.Left;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.rectTransform.sizeDelta = new Vector2(240f, 20f);
            t.rectTransform.anchoredPosition = new Vector2(0f, y);
            t.text = Title;
            t.ForceMeshUpdate();
            return t;
        }

        /// <summary>一條水平帶裡「夠亮」的像素數 —— 只用來確認東西真的畫出來了。</summary>
        private static int CountInk(Color32[] px, int w, int yFrom, int yTo)
        {
            int n = 0;
            for (int y = yFrom; y <= yTo; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = px[y * w + x];
                    if ((c.r + c.g + c.b) / 3f / 255f > Thr) n++;
                }
            return n;
        }

        /// <summary>一條水平帶裡的「墨塊數」:一整直行只要有一個像素夠亮就算有墨,連續有墨的直行算一塊。
        /// 兩個字母之間留得住空隙就會被算成兩塊,黏在一起就併成一塊。
        /// <paramref name="narrowestValley"/> 回傳最窄的那條字縫有幾個像素寬 —— 字母是不是「只差一點點沒黏到」
        /// 看的是這個數字(墨塊數只分黏 / 不黏,分不出 1 個半亮像素和 3 個全暗像素的差別)。</summary>
        private static int InkBlocks(Color32[] px, int w, int yFrom, int yTo, out int narrowestValley)
        {
            var runs = new List<int[]>();
            bool inBlock = false; int start = 0;
            for (int x = 0; x < w; x++)
            {
                bool ink = false;
                for (int y = yFrom; y <= yTo && !ink; y++)
                {
                    var c = px[y * w + x];
                    if ((c.r + c.g + c.b) / 3f / 255f > Thr) ink = true;
                }
                if (ink && !inBlock) { inBlock = true; start = x; }
                else if (!ink && inBlock) { inBlock = false; runs.Add(new[] { start, x - 1 }); }
            }
            if (inBlock) runs.Add(new[] { start, w - 1 });

            narrowestValley = int.MaxValue;
            for (int i = 0; i + 1 < runs.Count; i++)
            {
                int gap = runs[i + 1][0] - runs[i][1] - 1;
                if (gap > 0 && gap < narrowestValley) narrowestValley = gap;
            }
            if (narrowestValley == int.MaxValue) narrowestValley = 0;
            return runs.Count;
        }
    }
}
