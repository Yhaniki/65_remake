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
    /// 結算面板 Cool / Bad / Miss 三欄白色數字的起頭位置。使用者回報「Bad 底下的數字開頭太靠左,
    /// 要往右一點切齊上面 Bad 的黃色字」—— DDRSTATISTIC.XML 的 412 / 467 / 530 是官方 NumLabel
    /// 「靠右填格」的欄位左界,拿來當左靠起點就會比標題字頭少 4 / 11 / 3 px。
    /// 這裡的基準是烘在背景圖上那排黃色標題的**左緣**,直接從美術檔量回來。
    /// </summary>
    public class ResultStatColumnAlignTests
    {
        // 欄名、起始 x(ResultScreen 用的)、對應的黃色標題、標題在整排裡的序號
        // (由左而右:排名0 暱稱1 最高連擊2 Perfect3 Cool4 Bad5 Miss6 命中率7 總積分8 成績9)。
        private static readonly (string Name, float X, string Header, int HeaderIndex)[] Columns =
        {
            ("cool", ResultScreen.CoolX, "Cool", 4),
            ("bad",  ResultScreen.BadX,  "Bad",  5),
            ("miss", ResultScreen.MissX, "Miss", 6),
        };

        private const float Num3 = 8f;   // Num3.an 的字寬(8×11)—— 用來框出一欄最多 4 位數的範圍

        // 這一列刻意讓 Cool 兩位、Bad / Miss 各一位 —— 「開頭在哪」只有位數少的時候看得出來。
        private static readonly ResultScreen.Row SampleRow = new ResultScreen.Row
        {
            Rank = 1, DisplayRank = 1, Name = "me", IsLocal = true,
            MaxCombo = 953, Perfect = 938, Cool = 15, Bad = 2, Miss = 0, Score = 64654,
        };

        [Test]
        public void CoolBadMiss_DigitsStartAtTheirColumnX()
        {
            RunOnRow(row =>
            {
                foreach (var c in Columns)
                {
                    var digits = DigitsIn(row, c.X, c.X + 4f * Num3);
                    Assert.IsNotEmpty(digits, c.Name + " 欄一個數字都沒畫出來");
                    Assert.AreEqual(c.X, digits.Min(), 0.51f,
                        c.Name + " 欄的第一個數字沒有從 " + c.X + " 起頭 —— 對不上「" + c.Header + "」標題的字頭");
                }
            });
        }

        [Test]
        public void ColumnX_MatchTheBakedYellowHeaderLeftEdge()
        {
            // 直接量背景圖(Statis0..3 橫向拼成 800 寬,貼在 design y=115)那排黃字的左緣,
            // 證明上面那三個常數不是硬湊的 —— 它們就是標題字頭。
            var left = MeasureHeaderLeftEdges();
            if (left == null) Assert.Ignore("STATISTIC art not present in this environment.");
            Assert.AreEqual(10, left.Count,
                "黃色標題應該有 10 欄(排名/暱稱/最高連擊/Perfect/Cool/Bad/Miss/命中率/總積分/成績)");

            foreach (var c in Columns)
                Assert.AreEqual(left[c.HeaderIndex], c.X, 1f,
                    c.Name + " 的起始 x 沒對上「" + c.Header + "」標題的左緣");
        }

        // ---------------------------------------------------------------- helpers

        // rowRoot 底下所有名為 "d" 的數字,取落在 [x-1, rightEdge+1] 這一欄裡的,回傳它們的 design 左緣。
        private static List<float> DigitsIn(GameObject rowRoot, float x, float rightEdge)
        {
            var found = new List<float>();
            foreach (var sr in rowRoot.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr.name != "d" || sr.sprite == null) continue;
                float w = sr.sprite.bounds.size.x;
                // 子物件是以 worldPositionStays 掛上去的,rowRoot 掛的時候在原點 → localPosition 就是 design 世界座標。
                float leftPx = sr.transform.localPosition.x + SdoLayout.Width / 2f - w / 2f;
                if (leftPx >= x - 1f && leftPx + w <= rightEdge + 1f) found.Add(leftPx);
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

        // 找 design y 122..142 那條裡的黃色像素,回傳每一段(欄位標題)的左緣 x。
        private static List<float> MeasureHeaderLeftEdges()
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
            var edges = new List<float>();
            int start = -1, gap = 0;
            for (int x = 0; x <= yellow.Length; x++)
            {
                bool on = x < yellow.Length && yellow[x];
                if (on) { if (start < 0) { start = x; edges.Add(x); } gap = 0; continue; }
                if (start >= 0 && ++gap > 4) start = -1;
            }
            return edges;
        }
    }
}
