using System.Collections;
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
    /// 順便存一張 PNG 給人眼複核。跑法:-runTests -testPlatform PlayMode -testFilter Sdo.Tests.SongTitleTrackingCaptureTest
    /// </summary>
    public class SongTitleTrackingCaptureTest
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
            TextTracking.ApplyTightening(safe, TextStyles.SongTitleTrackEm, TextStyles.MinInkGapEm);   // 現在的行為
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
            int blocksOld = InkBlocks(px, rt.width, yMid + (int)(22f * Zoom) - band, yMid + (int)(22f * Zoom) + band);
            int blocksSafe = InkBlocks(px, rt.width, yMid - band, yMid + band);
            int blocksNat = InkBlocks(px, rt.width, yMid - (int)(22f * Zoom) - band, yMid - (int)(22f * Zoom) + band);
            Debug.Log($"[SongTitleTracking] 墨塊數 固定收緊={blocksOld} 安全收緊={blocksSafe} 自然字距={blocksNat}" +
                      $" / characterSpacing 固定={tight.characterSpacing:0.###} 安全={safe.characterSpacing:0.###}" +
                      $" → {Path.Combine(OutDir, "songtitle-tracking.png")}");

            Object.Destroy(shot); Object.Destroy(canvasGo); Object.Destroy(camGo);
            cam.targetTexture = null; rt.Release();

            Assert.GreaterOrEqual(blocksNat, 14, "自然字距下應該量得到 15 個字母各自分開(渲染沒出來就不用比了)");
            Assert.AreEqual(blocksNat, blocksSafe, "安全收緊後字母必須跟自然字距一樣各自分開(沒有任何兩個字黏成一塊)");
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

        /// <summary>一條水平帶裡的「墨塊數」:一整直行只要有一個像素夠亮就算有墨,連續有墨的直行算一塊。
        /// 兩個字母之間留得住空隙就會被算成兩塊,黏在一起就併成一塊。</summary>
        private static int InkBlocks(Color32[] px, int w, int yFrom, int yTo)
        {
            int blocks = 0; bool inBlock = false;
            for (int x = 0; x < w; x++)
            {
                bool ink = false;
                for (int y = yFrom; y <= yTo && !ink; y++)
                {
                    var c = px[y * w + x];
                    if ((c.r + c.g + c.b) / 3f / 255f > Thr) ink = true;
                }
                if (ink && !inBlock) { blocks++; inBlock = true; }
                else if (!ink) inBlock = false;
            }
            return blocks;
        }
    }
}
