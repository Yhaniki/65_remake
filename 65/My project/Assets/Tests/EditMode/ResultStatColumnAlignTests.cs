using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// 結算面板成績五欄的對齊。官方 DDRSTATISTIC.XML 的 Rank1 把這幾欄寫成
    /// <c>NumLabel labelnum="4" hidezero="true"</c> —— 值填在最右邊的格子裡,也就是**靠右**排,
    /// 右緣固定在 x + 4×字寬。老截圖對得上:Cool 欄的 38 / 3 / 107 右緣切齊、Bad 欄的 0 / 1 與 15 / 16 右緣切齊。
    /// (曾經誤用左靠畫 —— 位數少就整欄往左縮,Bad 只有一位數時偏左快 20px,對不上黃色欄位標題。)
    /// </summary>
    public class ResultStatColumnAlignTests
    {
        private const float Num8 = 12f, Num3 = 8f;   // Num8.an 12×16 / Num3.an 8×11 的字寬 = NumLabel 的 cell pitch

        // 欄名、字寬、起始 x、黃色標題、標題在整排裡的序號
        // (由左而右:排名0 暱稱1 最高連擊2 Perfect3 Cool4 Bad5 Miss6 命中率7 總積分8 成績9)。
        private static readonly (string Name, float Pitch, float X, string Header, int HeaderIndex)[] Columns =
        {
            ("combo",   Num8, ResultScreen.ComboX,   "最高連擊", 2),
            ("perfect", Num8, ResultScreen.PerfectX, "Perfect",  3),
            ("cool",    Num3, ResultScreen.CoolX,    "Cool",     4),
            ("bad",     Num3, ResultScreen.BadX,     "Bad",      5),
            ("miss",    Num3, ResultScreen.MissX,    "Miss",     6),
        };

        // 這一列刻意用「不滿 4 位」的值 —— 左靠與右靠只有在位數不滿時才分得出來。
        private static readonly ResultScreen.Row SampleRow = new ResultScreen.Row
        {
            Rank = 1, DisplayRank = 1, Name = "me", IsLocal = true,
            MaxCombo = 953, Perfect = 938, Cool = 15, Bad = 2, Miss = 0, Score = 64654,
        };

        [Test]
        public void StatDigits_RightAlignedInTheirNumLabelField()
        {
            RunOnRow(row =>
            {
                foreach (var c in Columns)
                {
                    float want = c.X + ResultScreen.StatCells * c.Pitch;   // 欄位右緣 = 靠右排的基準
                    var digits = DigitsIn(row, c.X, want);
                    Assert.IsNotEmpty(digits, c.Name + " 欄一個數字都沒畫出來");
                    Assert.AreEqual(want, digits.Max(), 0.51f,
                        c.Name + " 欄沒有靠右排 —— 位數不滿 4 位時整欄會往左縮,對不上「" + c.Header + "」標題");
                }
            });
        }

        [Test]
        public void NumLabelFields_CentreOnTheirBakedYellowHeader()
        {
            // 量背景圖(Statis0..3 橫向拼成 800 寬,貼在 design y=115)上那排黃字的水平範圍,
            // 證明官方欄位框確實是對著標題置中的 —— 這是上面那個「靠右」要求的理由。
            var centres = MeasureHeaderCentres();
            if (centres == null) Assert.Ignore("STATISTIC art not present in this environment.");
            Assert.AreEqual(10, centres.Count,
                "黃色標題應該有 10 欄(排名/暱稱/最高連擊/Perfect/Cool/Bad/Miss/命中率/總積分/成績)");

            foreach (var c in Columns)
            {
                float field = c.X + ResultScreen.StatCells * c.Pitch / 2f;
                Assert.AreEqual(centres[c.HeaderIndex], field, 8f,
                    c.Name + " 的 NumLabel 欄位框沒對準「" + c.Header + "」標題");
            }
        }

        // ---------------------------------------------------------------- helpers

        // rowRoot 底下所有名為 "d" 的數字,取落在 [x-1, rightEdge+1] 這一欄裡的,回傳它們的 design 右緣。
        private static List<float> DigitsIn(GameObject rowRoot, float x, float rightEdge)
        {
            var found = new List<float>();
            foreach (var sr in rowRoot.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr.name != "d" || sr.sprite == null) continue;
                float w = sr.sprite.bounds.size.x;
                // 子物件是以 worldPositionStays 掛上去的,rowRoot 掛的時候在原點 → localPosition 就是 design 世界座標。
                float leftPx = sr.transform.localPosition.x + SdoLayout.Width / 2f - w / 2f;
                if (leftPx >= x - 1f && leftPx + w <= rightEdge + 1f) found.Add(leftPx + w);
            }
            return found;
        }

        private static void RunOnRow(System.Action<GameObject> body)
        {
            if (!File.Exists(Path.Combine(SdoExtracted.ResultStatisDir, "Statis25.an")))
                Assert.Ignore("STATISTIC art not present in this environment.");

            GameObject hudGo = null, root = null;
            try
            {
                hudGo = new GameObject("HudCamResultStatAlign");
                var hud = hudGo.AddComponent<Camera>();
                hud.enabled = false;

                var result = new ResultScreen();
                result.Build(hud);
                result.Show("song", "Lv 1", new[] { SampleRow }, localWon: true, expGained: 1, coinsGained: 0);
                root = (GameObject)typeof(ResultScreen)
                    .GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(result);

                var row = root.transform.Find("Row1");
                Assert.IsNotNull(row, "沒建出 Row1");
                body(row.gameObject);
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
                if (hudGo != null) Object.DestroyImmediate(hudGo);
            }
        }

        // 找 design y 122..142 那條裡的黃色像素,回傳每一段(欄位標題)的中心 x。
        private static List<float> MeasureHeaderCentres()
        {
            const int Tile = 256, BandTop = 7, BandBottom = 27;   // 背景貼在 design y=115 → local 7..27 = design 122..142
            string dir = SdoExtracted.ResultStatisDir;
            var yellow = new bool[(int)SdoLayout.Width];
            for (int i = 0; i < 4; i++)
            {
                string png = Path.Combine(dir, "Statis" + i + ".PNG");
                if (!File.Exists(png)) return null;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(File.ReadAllBytes(png))) { Object.DestroyImmediate(tex); return null; }
                var px = tex.GetPixels32();
                int w = tex.width, h = tex.height;
                for (int ly = BandTop; ly < BandBottom && ly < h; ly++)
                    for (int lx = 0; lx < w && i * Tile + lx < yellow.Length; lx++)
                    {
                        var c = px[(h - 1 - ly) * w + lx];        // Texture2D 由下往上
                        if (c.a > 100 && c.r > 150 && c.g > 110 && c.b < 120) yellow[i * Tile + lx] = true;
                    }
                Object.DestroyImmediate(tex);
            }

            // 連續的黃色像素算一段;中間空 4px 以內還算同一段(字與字之間的縫)。
            var centres = new List<float>();
            int start = -1, gap = 0;
            for (int x = 0; x <= yellow.Length; x++)
            {
                bool on = x < yellow.Length && yellow[x];
                if (on) { if (start < 0) start = x; gap = 0; continue; }
                if (start < 0) continue;
                if (++gap > 4) { centres.Add((start + x - gap) / 2f); start = -1; }
            }
            if (start >= 0) centres.Add((start + yellow.Length - 1) / 2f);
            return centres;
        }
    }
}
