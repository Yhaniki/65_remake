using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// REAL-DATA regression table for garment transparency, driven by THE OFFICIAL RULE. Every garment a user has ever
    /// reported as rendering wrong is pinned here against the ACTUAL shipped .msh + .dds, through the production chain:
    /// <see cref="MshLoader.ReadMaterialTable"/> (material name + official flags at record +0x194) →
    /// <see cref="DdsLoader.Analyze"/> → <see cref="SdoAvatarBuilder.OfficialAlphaMode"/>.
    /// Motivation (使用者): 「修一件衣服壞另一件的事情不能再發生 — 所有有問題的衣服都列下來，每次改就必須全部跑 test」。
    ///
    /// WHY THE EXPECTATIONS ARE NOW MECHANICAL. Transparency is NOT a property of the texture — the artist marked it
    /// per material in the mesh and the retail engine (H:\sdo_cn\sdo.bin.c) just obeys: flags &amp; 0x3f non-zero →
    /// deferred (alpha-blended) batch, zero → opaque batch with the alpha channel ignored. So each row's expected mode is simply what the shipped
    /// file says, and the suite's job is to prove our loader keeps reading those bytes and routing them correctly. This
    /// replaced a hand-tuned texture-histogram heuristic whose thresholds caused the repeated "fix one garment, break
    /// another" seesaw (corpus-wide it agreed with the official flags on barely a third of cloth textures).
    ///
    /// RULES for this table:
    ///   • Rows below cite the user report that put each garment here. When a NEW garment is reported broken: fix it,
    ///     then ADD its row.
    ///   • An expected mode that DISAGREES with the mesh's official flag needs an explicit comment saying why (a
    ///     deliberate deviation we own), never a silent edit.
    ///   • ANY change to the flag decode, OfficialAlphaMode, DdsLoader alpha analysis, or ResolveDds/FindDdsPath →
    ///     run the FULL EditMode suite before shipping.
    ///
    /// Non-alpha garment issues keep their own guards (do not fold them in here): 000004 亂碼材質 →
    /// AvatarItemCatalog.ClothTextureResolvable;070025/070030 模板id → SwapLeadingId/PreferOwnIdTexture;烈酒清茶
    /// 零蒙皮接縫 → seam weld;025149 縮放骨 bind → ScanBonePalette;draw ORDER between transparent pieces →
    /// TransparentGarmentQueue.
    /// </summary>
    public class GarmentAlphaRealDataTests
    {
        /// <summary>One user-reported garment material: which mesh, which material (texture) name inside it, and the
        /// alpha mode the official flag dictates.</summary>
        public struct Row
        {
            public string Msh; public string Dds; public DdsAlphaMode Expected; public string Why;
            public override string ToString() => Msh + ":" + Dds;   // NUnit test-case name
        }

        // ─── The verified table (Expected = the shipped mesh's official flag) ─────────────────────────────────────
        private static readonly Row[] Rows =
        {
            // Flower Lace Dress 024976 (item 1224976) — 第一/二輪報告「透明度沒做出來」「比官方的透明」。官方做法在這裡一目
            // 了然:蕾絲外層 _A/_B 是 BLEND、同一張圖的內襯 1_/2_ 是 OPAQUE。密度來自內襯,不是 shader 的 density hack。
            new Row { Msh = "024976_WOMAN_ONE.MSH", Dds = "024976_woman_one_a.dds",  Expected = DdsAlphaMode.Blend,  Why = "蕾絲外層(官方旗標 2 = blend bit)" },
            new Row { Msh = "024976_WOMAN_ONE.MSH", Dds = "024976_woman_one_b.dds",  Expected = DdsAlphaMode.Blend,  Why = "蕾絲外層背面(官方旗標 2 = blend bit)" },
            new Row { Msh = "024976_WOMAN_ONE.MSH", Dds = "024976_woman_one1_.dds",  Expected = DdsAlphaMode.Blend,  Why = "內襯層(官方旗標 1,也是透明批) — 判 Opaque 會把這張近全黑貼圖實心貼滿=使用者「比官方的黑」" },
            new Row { Msh = "024976_WOMAN_ONE.MSH", Dds = "024976_woman_one2_.dds",  Expected = DdsAlphaMode.Blend,  Why = "內襯層(官方旗標 1);貼圖不透明區平均 RGB 只有 9,9,9 —— 實心畫=整件黑" },
            new Row { Msh = "024976_WOMAN_ONE.MSH", Dds = "024977_woman_shoes.dds",  Expected = DdsAlphaMode.Opaque, Why = "配套鞋(官方 0,且 DXT1 無 alpha)" },
            new Row { Msh = "024976_WOMAN_ONE.MSH", Dds = "W_Basic_Coat2.dds",       Expected = DdsAlphaMode.Opaque, Why = "共用皮膚底圖 — 判半透明整個人會透" },
            // 037000 牛奶裝 — 第四輪報告「會透過胸部看到另一邊的袖子」(那是深度/排序問題,分類本來就對)。
            new Row { Msh = "037000_WOMAN_COAT.MSH", Dds = "037000_woman_coat.dds",  Expected = DdsAlphaMode.Blend,  Why = "紗袖/紗裙(官方旗標 2)" },
            // 002178 艳红 京族新娘装 — 使用者報告「褲子一直閃爍」。官方外層紗褲根本 NOT blended:官方版本從來沒有這個閃爍。
            new Row { Msh = "002178_WOMAN_ONE.MSH", Dds = "002178_woman_coat.dds",   Expected = DdsAlphaMode.Cutout, Why = "京族新娘 上身(官方旗標 2 透明,但 translucent 0.005=去背剪影非紗)→ Cutout 實心" },
            new Row { Msh = "002178_WOMAN_ONE.MSH", Dds = "002178_woman_pant_.dds",  Expected = DdsAlphaMode.Blend,  Why = "外層紗褲(官方旗標 1);閃爍改由 TransparentGarmentQueue 固定順序+ZWrite 解決,不是靠判它不透明" },
            // 003136/003137 紅色不羈牛仔 (id 173136) — 第五輪報告「是透明的」。官方上衣不 blend = 實心,正是使用者要的。
            new Row { Msh = "003136_WOMAN_COAT.MSH", Dds = "003136_woman_coat_.dds", Expected = DdsAlphaMode.Cutout, Why = "紅色不羈牛仔(旗標 1,translucent 0.153<0.21=去背非紗)→ Cutout;裁白底、紅流蘇實心=使用者要的實心" },
            new Row { Msh = "003137_WOMAN_PANT.MSH", Dds = "003137_woman_pant.dds",  Expected = DdsAlphaMode.Cutout, Why = "紅色不羈牛仔褲(旗標 2,translucent 0.010=去背)→ Cutout 實心" },
            // 003834 黑色魅力娃娃 — 使用者:「裙子中間層有一段透明,那段貼圖不應該透明」。旗標 2 透明,但 translucent
            // 0.005/0.016=去背剪影(荷葉邊輪廓),blend 會讓每層荷葉的抗鋸齒邊 ~45% 透 → 中間透明帶;Cutout 裁邊留實心。
            new Row { Msh = "003834_WOMAN_ONE.MSH", Dds = "003834_woman_coat.dds",  Expected = DdsAlphaMode.Cutout, Why = "娃娃裝上身(旗標 2,translucent 0.005=去背)→ Cutout 實心" },
            new Row { Msh = "003834_WOMAN_ONE.MSH", Dds = "003834_woman_pant.dds",  Expected = DdsAlphaMode.Cutout, Why = "娃娃裝荷葉裙(旗標 2,translucent 0.016=去背荷葉輪廓)→ Cutout,邊緣不透" },
            // 001934 Purple Dress 1 (id 1201934) — 使用者報告「上半身完全透明」。官方上衣 blend、兩層裙 opaque;貼圖 75%
            // 全不透明,直式 alpha blend 不會隱形(舊路徑是三層全判紗+關 prepass 才洗白)。
            new Row { Msh = "001934_WOMAN_ONE.MSH", Dds = "001934_woman_coat.dds",   Expected = DdsAlphaMode.Cutout, Why = "Purple Dress 上衣(旗標 2,translucent 0.164<0.21=去背)→ Cutout 實心(使用者曾報「上半身完全透明」)" },
            new Row { Msh = "001934_WOMAN_ONE.MSH", Dds = "001934_woman_pant_.dds",  Expected = DdsAlphaMode.Blend,  Why = "裙(旗標 1,translucent 0.243≥0.21=真紗)→ Blend" },
            new Row { Msh = "001934_WOMAN_ONE.MSH", Dds = "001934_woman_pant1_.dds", Expected = DdsAlphaMode.Blend,  Why = "裙內層(旗標 1,translucent 0.243≥0.21=紗)→ Blend" },
            // 001766 Skirt Suit (id 1201766) — 使用者報告「領口後面應該是有衣服的,但框裡面變成透明透過後面」。分類本來
            // 就對(官方旗標 2 = blend,貼圖 93~95% 完全不透明,alpha 只去背輪廓);破的是商城卡片的「單面 + 藏身體」,
            // 修在 ShopCardSolidGarmentTests / SdoAvatarBuilder.IsSheerFabric。這兩列釘住分類本身別再被改掉。
            new Row { Msh = "001766_WOMAN_ONE.MSH", Dds = "001766_woman_coat.dds",   Expected = DdsAlphaMode.Cutout, Why = "Skirt Suit 外套(旗標 2,translucent 0.008=去背)→ Cutout 實心" },
            new Row { Msh = "001766_WOMAN_ONE.MSH", Dds = "001766_woman_pant.dds",   Expected = DdsAlphaMode.Cutout, Why = "Skirt Suit 裙(旗標 2,translucent 0.008=去背)→ Cutout" },
            // 對照:官方 blend 的實心/去背衣(舊啟發式判 Cutout,官方沒有 cutout 這種模式 — 引擎全無 alpha test)。
            new Row { Msh = "037888_WOMAN_ONE.MSH", Dds = "037888_woman_one.dds",    Expected = DdsAlphaMode.Cutout, Why = "眉画犹思(旗標 2,translucent 0.092=實心+硬洞)→ Cutout 裁洞留實心" },
            new Row { Msh = "001839_WOMAN_COAT.MSH", Dds = "001839_woman_coat.dds",  Expected = DdsAlphaMode.Cutout, Why = "我的帥氣鋼琴外套(旗標 2,translucent 0.000+13%洞)→ Cutout 真去背孔洞(第三輪 room 沒去背)" },
            new Row { Msh = "000558_MAN_COAT.MSH",  Dds = "000558_man_coat.dds",     Expected = DdsAlphaMode.Cutout, Why = "至尊王者无敌 刺青(旗標 2,28%洞 translucent 0.026)→ Cutout 裁透明處" },
            // 壞 alpha:官方旗標=0 → 引擎完全忽略 alpha 畫實心。我們以前要靠「>70% 洞」guard 猜出同樣結果。
            new Row { Msh = "036009_MAN_PANT.MSH",  Dds = "036009_man_pant.dds",     Expected = DdsAlphaMode.Opaque, Why = "璀璨繁星 男裤 alpha 整片壞(官方 0 → 忽略)" },
            new Row { Msh = "023425_WOMAN_COAT.MSH", Dds = "023425_woman_coat.dds",  Expected = DdsAlphaMode.Opaque, Why = "78% 洞的邊界件(官方 0 → 忽略)" },
        };

        private static IEnumerable<Row> AllRows() => Rows;

        /// <summary>The real AVATAR data dir, resolved exactly like the runtime (data_root.txt → Root, dev Datas
        /// fallback). Null when no data is present — rows then Ignore, not fail.</summary>
        private static string AvatarDir()
        {
            var probe = SdoAvatarBuilder.ResolveAvatarFile("AVATAR/" + Rows[0].Msh);
            if (string.IsNullOrEmpty(probe) || !File.Exists(probe)) return null;
            return Path.GetDirectoryName(probe);
        }

        private static bool NameMatches(string materialName, string wanted)
        {
            if (string.IsNullOrEmpty(materialName)) return false;
            string a = Path.GetFileName(materialName.Replace('\\', '/'));
            int at = a.LastIndexOf('@');                       // material names ship as "L@name.dds"
            if (at >= 0) a = a.Substring(at + 1);
            return string.Equals(a, wanted, System.StringComparison.OrdinalIgnoreCase);
        }

        [Test, TestCaseSource(nameof(AllRows))]
        public void ReportedGarment_UsesOfficialAlphaMode(Row row)
        {
            var dir = AvatarDir();
            if (dir == null) Assert.Ignore("AVATAR data root not found — real-data rows need the game data (data_root.txt)");

            string mshPath = Path.Combine(dir, row.Msh);
            Assert.IsTrue(File.Exists(mshPath), $"{row.Msh}: mesh missing from {dir} ({row.Why})");
            var table = MshLoader.ReadMaterialTable(File.ReadAllBytes(mshPath));
            Assert.IsNotEmpty(table, $"{row.Msh}: no material table parsed — the +0x194 flag walk broke");

            uint? flags = null;
            foreach (var m in table) if (NameMatches(m.Name, row.Dds)) { flags = m.Flags; break; }
            Assert.IsTrue(flags.HasValue, $"{row.Msh}: material '{row.Dds}' not in the mesh's material table ({row.Why})");

            string ddsPath = SdoAvatarBuilder.FindDdsPath(dir, row.Dds);
            Assert.IsNotNull(ddsPath, $"{row.Dds}: texture not found in {dir} ({row.Why})");
            var st = DdsLoader.Analyze(File.ReadAllBytes(ddsPath));

            var am = SdoAvatarBuilder.OfficialAlphaMode(flags.Value, st.HasAlpha, st.Translucent);   // production 同一條(旗標+直方圖)
            Assert.AreEqual(row.Expected, am,
                $"{row.Msh}:{row.Dds} 透明度跑掉了 — {row.Why}\n" +
                $"official flags=0x{flags.Value:x8} (blend bit {( (flags.Value & MshLoader.MatFlagTransparentMask) != 0 ? "SET" : "clear")}), " +
                $"texture hasAlpha={st.HasAlpha} → {am}, expected {row.Expected}");
        }

        /// <summary>紅色不羈牛仔's mesh asks for '003136_woman_coat.dds' but the file on disk is
        /// '003136_WOMAN_COAT_.DDS' (trailing underscore) — the fuzzy stem match must keep bridging that, or the
        /// garment silently falls back to a flat colour.</summary>
        [Test]
        public void FuzzyResolution_TrailingUnderscoreVariant_StillFound()
        {
            var dir = AvatarDir();
            if (dir == null) Assert.Ignore("AVATAR data root not found");
            string hit = SdoAvatarBuilder.FindDdsPath(dir, "003136_woman_coat.dds");
            Assert.IsNotNull(hit, "003136_woman_coat.dds (mesh material name) no longer resolves");
            StringAssert.Contains("003136", Path.GetFileName(hit));
        }

        /// <summary>The mesh material walk must stay in sync between the name-only scan and the flag-carrying one:
        /// same records, same order. A drift would silently pair a garment with a NEIGHBOUR material's flags — the
        /// worst possible failure mode, since every garment would still render, just with the wrong transparency.</summary>
        [Test]
        public void MaterialTable_MatchesNameOnlyScan()
        {
            var dir = AvatarDir();
            if (dir == null) Assert.Ignore("AVATAR data root not found");
            foreach (var msh in new[] { "024976_WOMAN_ONE.MSH", "002178_WOMAN_ONE.MSH", "001934_WOMAN_ONE.MSH", "037000_WOMAN_COAT.MSH" })
            {
                var bytes = File.ReadAllBytes(Path.Combine(dir, msh));
                var names = MshLoader.ReadMaterialNames(bytes);
                var table = MshLoader.ReadMaterialTable(bytes);
                Assert.AreEqual(names.Count, table.Count, msh + ": flag walk visited a different number of materials");
                for (int i = 0; i < names.Count; i++)
                    Assert.AreEqual(names[i], table[i].Name, $"{msh}: material #{i} name differs between the two walks");
            }
        }

        /// <summary>The loaded SubMesh must carry the same flags the header scan reports for the material it picked —
        /// this is the seam the avatar builders actually read (<see cref="MshLoader.SubMesh.DdsFlags"/>).</summary>
        [Test]
        public void SubMeshDdsFlags_MatchHeaderTable()
        {
            var dir = AvatarDir();
            if (dir == null) Assert.Ignore("AVATAR data root not found");
            var bytes = File.ReadAllBytes(Path.Combine(dir, "037000_WOMAN_COAT.MSH"));
            var table = MshLoader.ReadMaterialTable(bytes);
            var res = MshLoader.Load(bytes);
            Assert.IsNotNull(res, "037000_WOMAN_COAT.MSH failed to load");
            foreach (var sub in res.Submeshes)
            {
                if (sub.MatFlags == null || sub.DdsNames == null) continue;
                Assert.AreEqual(sub.DdsNames.Length, sub.MatFlags.Length, "MatFlags must be parallel to DdsNames");
                if (string.IsNullOrEmpty(sub.Dds)) continue;
                foreach (var m in table)
                    if (m.Name == sub.Dds) { Assert.AreEqual(m.Flags, sub.DdsFlags, "picked material's flags differ from the header table"); break; }
            }
        }
    }
}
