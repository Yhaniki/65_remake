using NUnit.Framework;
using UnityEngine;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 使用者回報「OPTION 面板粉紅框外面有幾條白色線」。素材本身沒有白線 —— 框的外緣是「粉框身 → 白框線 →
    /// 深紫 drop-shadow 描邊(α=84/23/7)」,而描邊外面的空白區在 atlas 裡存的是 (255,255,255,0):**透明的白**。
    /// 把 800×600 設計拉到實際視窗時,bilinear 只對 RGB 與 α 各自內插,那片白 RGB 就被混進 α 很低的深紫描邊,
    /// 描邊於是從「幾乎看不見的暗邊」變成一條白霧線。修法是 <see cref="OptionDlgArt.CropBled"/>(自己的 texture +
    /// AlphaBleed),把描邊色 dilate 進透明區。
    ///
    /// 這裡驗三件事:①框外的 matte 不再是白 ②對照組(共用 atlas 的 Crop)確實是白,證明量測有鑑別力
    /// ③框本身的可見像素一個 byte 都沒動(AlphaBleed 只碰 α≤8 的像素,不能順手改到美術)。
    /// 沒有遊戲資料的環境跳過。
    /// </summary>
    public class OptionFrameFringeTests
    {
        // 四塊框在 OPTIONDLG.clean.png 的 atlas 矩形(和 OptionDlgArt 的 named crops 一致)
        private static readonly (string Name, int X, int Y, int W, int H)[] Quadrants =
        {
            ("FrameTL", 355, 0, 256, 256),
            ("FrameTR", 611, 0, 132, 256),
            ("FrameBL", 355, 256, 256, 120),
            ("FrameBR", 611, 256, 138, 120),
        };

        [Test]
        public void FrameQuadrants_MatteTouchingTheEdgeIsNotWhite()
        {
            // 實測(OPTIONDLG.clean.png):貼邊 matte 亮度 TL 樣本只有 20(框身填滿整塊,外圈沒有 matte 可量 → 跳過)、
            // TR 0.220(右上的外緣隔著一圈 α=7 的深紫,本來就不是白)、BL 0.944、BR 0.551 —— 白 matte 真正貼到
            // 框邊的是**左下/右下**兩塊。所以這裡不要求每塊都改善,而是:有 matte 可量的塊修完都不准是亮白,
            // 且至少要有一塊「修前是白、修後不是」,證明這個量測抓得到病。
            float worstBefore = 0f;
            int measured = 0;
            foreach (var q in Quadrants)
            {
                var bled = Fetch(q.Name);
                var raw = OptionDlgArt.Crop(q.X, q.Y, q.W, q.H);
                if (raw == null) Assert.Ignore("OPTIONDLG art not present in this environment.");
                float after = MatteRingLum(bled, out int samples);
                float before = MatteRingLum(raw, out _);
                if (samples < 100) continue;                 // 這塊沒有朝外的 matte(TL),量了沒意義
                measured++;
                worstBefore = Mathf.Max(worstBefore, before);
                Assert.Less(after, 0.35f,
                    $"{q.Name}:框邊外那圈透明 matte 亮度 {after:F3} —— 還是亮白,放大時會被 bilinear 拉進描邊");
                Assert.LessOrEqual(after, before + 0.01f, $"{q.Name}:AlphaBleed 反而把 matte 弄亮了");
            }
            Assert.GreaterOrEqual(measured, 3, "量到的框塊太少,這個測試沒對準素材");
            Assert.Greater(worstBefore, 0.5f, "對照組沒有任何一塊是白 matte,這個量測失去鑑別力了");
        }

        [Test]
        public void FrameQuadrants_CarryNoStrayFragmentFromNeighbouringAtlasArt()
        {
            // 框是一整片連通的美術。crop 若多切了一欄/一列,隔壁 sprite 的碎片就會變成畫在框「外面」的孤立雜訊
            // —— 原本 FrameTR 的第 133 欄踩進「保存」鈕,帶進 10 個 α≤238 的深紅像素(螢幕 x=608,y=141~150)。
            foreach (var q in Quadrants)
                Assert.AreEqual(1, BlobCount(Fetch(q.Name)),
                                $"{q.Name}:crop 裡有和框主體不相連的碎片 —— 又切到隔壁的 atlas 美術了");
        }

        [Test]
        public void OverwideTrCrop_HasTheStrayFragment_Control()
        {
            // 對照組:回到 .an 表寫的 133 欄寬,碎片必須量得回來,否則上面那個測試是為了錯誤的理由通過。
            var raw = OptionDlgArt.Crop(611, 0, 133, 256);
            if (raw == null) Assert.Ignore("OPTIONDLG art not present in this environment.");
            Assert.AreEqual(2, BlobCount(raw), "133 欄寬的 crop 應該還帶著保存鈕的碎片,這個量測失去鑑別力了");
        }

        /// <summary>α&gt;8 的像素有幾個 8-連通的區塊(框本身應該只有 1 塊)。</summary>
        private static int BlobCount(Sprite s)
        {
            var px = Pixels(s);
            int w = Mathf.RoundToInt(s.textureRect.width), h = Mathf.RoundToInt(s.textureRect.height);
            var seen = new bool[w * h];
            var stack = new System.Collections.Generic.Stack<int>();
            int blobs = 0;
            for (int i = 0; i < px.Length; i++)
            {
                if (seen[i] || px[i].a <= 8) continue;
                blobs++;
                stack.Push(i); seen[i] = true;
                while (stack.Count > 0)
                {
                    int p = stack.Pop(), px0 = p % w, py0 = p / w;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = px0 + dx, ny = py0 + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            int n = ny * w + nx;
                            if (seen[n] || px[n].a <= 8) continue;
                            seen[n] = true; stack.Push(n);
                        }
                }
            }
            return blobs;
        }

        [Test]
        public void FrameQuadrants_VisiblePixelsAreUntouched()
        {
            foreach (var q in Quadrants)
            {
                var bled = Fetch(q.Name);
                var raw = OptionDlgArt.Crop(q.X, q.Y, q.W, q.H);
                if (raw == null) Assert.Ignore("OPTIONDLG art not present in this environment.");
                var a = Pixels(bled);
                var b = Pixels(raw);
                Assert.AreEqual(b.Length, a.Length, $"{q.Name}:crop 尺寸變了");
                for (int i = 0; i < a.Length; i++)
                {
                    Assert.AreEqual(b[i].a, a[i].a, $"{q.Name}:第 {i} 個像素的 alpha 被改了");
                    if (b[i].a > 8)   // AlphaBleed 只准填 α≤8 的透明像素
                        Assert.IsTrue(a[i].r == b[i].r && a[i].g == b[i].g && a[i].b == b[i].b,
                                      $"{q.Name}:第 {i} 個可見像素的顏色被改了");
                }
            }
        }

        private static Sprite Fetch(string name)
        {
            var s = name == "FrameTL" ? OptionDlgArt.FrameTL
                  : name == "FrameTR" ? OptionDlgArt.FrameTR
                  : name == "FrameBL" ? OptionDlgArt.FrameBL
                  : OptionDlgArt.FrameBR;
            if (s == null) Assert.Ignore("OPTIONDLG art not present in this environment.");
            return s;
        }

        /// <summary>緊貼可見像素的第一圈透明 matte 的平均亮度 —— 那正是 bilinear 放大時會被混進框外描邊的顏色。
        /// (每塊框都朝外貼著這一圈:TL 是左/上、TR 是右/上…所以不必分邊處理。)</summary>
        private static float MatteRingLum(Sprite s, out int samples)
        {
            var px = Pixels(s);
            int w = Mathf.RoundToInt(s.textureRect.width), h = Mathf.RoundToInt(s.textureRect.height);
            float sum = 0f; samples = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (px[y * w + x].a > 8) continue;              // 只看透明的 matte
                    bool touchesArt = false;
                    for (int dy = -1; dy <= 1 && !touchesArt; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            if (px[ny * w + nx].a > 8) { touchesArt = true; break; }
                        }
                    if (!touchesArt) continue;
                    sum += Lum(px[y * w + x]); samples++;
                }
            return samples == 0 ? 0f : sum / samples;
        }

        private static Color32[] Pixels(Sprite s)
        {
            var r = s.textureRect;
            return s.texture.GetPixels32Block(r);
        }

        private static float Lum(Color32 c) => (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
    }

    internal static class SpriteBlockPixels
    {
        /// <summary>GetPixels 一塊矩形並轉成 Color32(Crop 的 sprite 指向整張共用 atlas,不能直接 GetPixels32 全圖比對)。</summary>
        public static Color32[] GetPixels32Block(this Texture2D tex, Rect r)
        {
            var block = tex.GetPixels(Mathf.RoundToInt(r.x), Mathf.RoundToInt(r.y),
                                      Mathf.RoundToInt(r.width), Mathf.RoundToInt(r.height));
            var outPx = new Color32[block.Length];
            for (int i = 0; i < block.Length; i++) outPx[i] = block[i];
            return outPx;
        }
    }
}
