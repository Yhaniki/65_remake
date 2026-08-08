using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// DpsLoader.Load 的位元組解析 —— 特別是 DATA/DANCE 裡的**兩個版本**。
    ///
    /// 2445 支是 PAS00003，最早那 44 首（10001..10056，例如 10003）是 PAS00002。官方 exe 兩種都讀
    /// （sdo_stand_alone FUN_00407a30 每個 magic 一個分支），兩個 sub-item 迴圈只差 v3 最後多讀一個 byte
    /// 進 +0x3c（v2 直接寫 0），所以 v2 的 row 剛好短一個 byte、名字與 mid 的 244/248/252 偏移完全一樣。
    /// 以前只認 PAS00003 → 那 44 首整首沒有編舞，舞者站著不動。
    /// </summary>
    public class DpsLoaderVersionTests
    {
        private const int HeaderSize = 8 + 4 + 256 + 4 + 4;    // magic + type + chart name + -1 + rowCount
        private const int MidV3 = 289, MidV2 = 288;

        private struct Slice { public string Mot; public int S, E; public float Dur; public bool NewItem; }

        private static Slice Row(string mot, int s, int e, float dur, bool newItem = true)
            => new Slice { Mot = mot, S = s, E = e, Dur = dur, NewItem = newItem };

        /// <summary>
        /// 組一支合成 .dps。<paramref name="mid"/> 決定版本（289 = PAS00003、288 = PAS00002）；
        /// NewItem=false 的 row 是同一個 item 的下一個 sub-item，前面沒有 12 bytes 的 item 表頭
        /// （所以跟前一個 row 的間距是短的那一種），跟官方檔一模一樣。
        /// </summary>
        private static byte[] Build(string magic, int mid, params Slice[] rows)
        {
            var buf = new List<byte>();
            buf.AddRange(Encoding.ASCII.GetBytes(magic));
            buf.AddRange(BitConverter.GetBytes(4));
            buf.AddRange(new byte[256]);                        // chart name（留空，免得裡面出現 ".mot"）
            buf.AddRange(BitConverter.GetBytes(-1));
            buf.AddRange(BitConverter.GetBytes(rows.Length));
            Assert.AreEqual(HeaderSize, buf.Count);

            int beat = 0;
            foreach (var r in rows)
            {
                if (r.NewItem)
                {
                    buf.AddRange(BitConverter.GetBytes(beat));  // pre_a：本段開始 tick
                    buf.AddRange(BitConverter.GetBytes(8));     // pre_b：長度 tick
                    buf.AddRange(BitConverter.GetBytes(1));     // sub_item_count
                    beat += 8;
                }
                var name = new byte[16];
                Encoding.ASCII.GetBytes(r.Mot).CopyTo(name, 0);
                buf.AddRange(name);
                var m = new byte[mid];
                BitConverter.GetBytes(r.S).CopyTo(m, 244);
                BitConverter.GetBytes(r.E).CopyTo(m, 248);
                BitConverter.GetBytes(r.Dur).CopyTo(m, 252);
                buf.AddRange(m);
            }
            return buf.ToArray();
        }

        private static Slice[] ThreeSlices() => new[]
        {
            Row("wdance0058.mot", 156, 285, 3.486f),
            Row("wdance0058.mot", 284, 413, 3.486f),
            Row("wdance0055.mot", 185, 305, 2.823f),
        };

        private static void AssertParsedThreeSlices(DpsLoader dps)
        {
            Assert.IsNotNull(dps);
            Assert.AreEqual(3, dps.Rows.Length);
            Assert.AreEqual("wdance0058.mot", dps.Rows[0].Mot);
            Assert.AreEqual(156, dps.Rows[0].StartF);
            Assert.AreEqual(285, dps.Rows[0].EndF);
            Assert.AreEqual(3.486f, dps.Rows[0].Dur, 1e-4f);
            Assert.AreEqual(0f, dps.Rows[0].TStart, 1e-4f);
            Assert.AreEqual("wdance0055.mot", dps.Rows[2].Mot);
            Assert.AreEqual(185, dps.Rows[2].StartF);
            Assert.AreEqual(3.486f + 3.486f, dps.Rows[2].TStart, 1e-4f);
            Assert.AreEqual(3.486f + 3.486f + 2.823f, dps.Total, 1e-4f);
        }

        [Test]
        public void Load_ReadsPas00003()
        {
            AssertParsedThreeSlices(DpsLoader.Load(Build("PAS00003", MidV3, ThreeSlices())));
        }

        [Test]
        public void Load_ReadsPas00002()
        {
            // 這就是 10003.DPS 那一類的檔：row 少一個 byte，其餘完全一樣。
            AssertParsedThreeSlices(DpsLoader.Load(Build("PAS00002", MidV2, ThreeSlices())));
        }

        [Test]
        public void Load_ReadsExtraSubItemsOfOneItem()
        {
            // 官方檔常常一個 item 掛好幾個 sub-item（間距是短的那一種：v3 305 / v2 304）。
            var rows = new[]
            {
                Row("wdance0058.mot", 0, 40, 1f),
                Row("wdance0058.mot", 41, 80, 1f, newItem: false),
                Row("wdance0058.mot", 81, 120, 1f, newItem: false),
            };
            Assert.AreEqual(3, DpsLoader.Load(Build("PAS00003", MidV3, rows)).Rows.Length);
            Assert.AreEqual(3, DpsLoader.Load(Build("PAS00002", MidV2, rows)).Rows.Length);
        }

        [Test]
        public void Load_StrideIsPerVersion_SoAWronglyLabelledFileStopsAfterTheFirstRow()
        {
            // 用 v3 的 magic 貼 v2 的排版（或反過來）：間距對不上，第一個 row 之後全被當成 mid 裡的雜訊丟掉。
            // 這正是「兩組間距不能混在一起認」的理由 —— 混認會把 mid 裡剛好出現的 ".mot" 收成假 row。
            Assert.AreEqual(1, DpsLoader.Load(Build("PAS00003", MidV2, ThreeSlices())).Rows.Length);
            Assert.AreEqual(1, DpsLoader.Load(Build("PAS00002", MidV3, ThreeSlices())).Rows.Length);
        }

        [Test]
        public void Load_RejectsOtherMagics()
        {
            Assert.IsNull(DpsLoader.Load(Build("PAS00001", MidV3, ThreeSlices())), "更舊的版本結構不同，不能照這套讀");
            Assert.IsNull(DpsLoader.Load(Build("PAS00000", MidV3, ThreeSlices())));
            Assert.IsNull(DpsLoader.Load(null));
            Assert.IsNull(DpsLoader.Load(new byte[4]));
        }

        // ---- 真資料：那 44 支 PAS00002 全部都要讀得出編舞（沒有資料樹就 Ignore，不是 fail）----

        [Test]
        public void Load_EveryLegacyChartInTheDataTreeParses()
        {
            string dir = Path.Combine(SdoExtracted.Root, "DANCE");
            if (!Directory.Exists(dir)) Assert.Ignore("找不到資料樹的 DANCE/（data_root.txt / SDO_DATA_ROOT）");

            int legacy = 0;
            var broken = new List<string>();
            foreach (var f in Directory.GetFiles(dir, "*.DPS"))
            {
                var bytes = File.ReadAllBytes(f);
                if (bytes.Length < 8 || Encoding.ASCII.GetString(bytes, 0, 8) != "PAS00002") continue;
                legacy++;
                var dps = DpsLoader.Load(bytes);
                if (dps == null || dps.Rows.Length < 2 || dps.Total < 10f)
                    broken.Add(Path.GetFileName(f) + (dps == null ? "：null" : $"：rows={dps.Rows.Length} total={dps.Total:F1}s"));
            }
            if (legacy == 0) Assert.Ignore("這棵資料樹沒有 PAS00002 舊檔");
            Assert.IsEmpty(broken, "PAS00002 舊譜解不出編舞（舞者會站著不動）：" + string.Join("、", broken));
        }
    }
}
