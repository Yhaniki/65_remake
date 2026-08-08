using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 「露出來的皮膚要畫成皮膚」—— <see cref="MshLoader.ResolveMeshDdsIndices"/> 的純邏輯 + 真實資料守門。
    ///
    /// 使用者回報「white heels 鞋子破圖」= <c>001337_WOMAN_SHOES</c>(iteminfo 1401337 "White Heels")。該 mesh 兩個
    /// submesh:#0 是鞋(單 range attrib→<c>001337_woman_shoes.dds</c>)、#1 是**露出來的小腿**(單 range
    /// attrib→<c>W_Basic_Pants2.dds</c>)。per-submesh 的挑選看不到別的 submesh,遇到 attrib 指向 Basic 一律 fall
    /// through 去挑布料 → 小腿被貼上鞋子的 atlas,腿上多出一截銀白色的破塊。
    ///
    /// 那個 fall through 不能直接拿掉:語料裡一大票**單 submesh** 的衣服 range table 也指著 Basic,順著它會把整件
    /// 衣服畫成裸體 (見 <see cref="MshLoader.PickSubmeshDdsIndex"/> 的註解)。所以判準是「這張布料是不是已經有
    /// **別的 submesh** 在畫」—— 有,那這塊 attrib 說自己是皮膚就是皮膚。
    /// </summary>
    public class MeshSkinRangeTests
    {
        private static IList<IList<string>> N(params string[][] rows)
        {
            var l = new List<IList<string>>();
            foreach (var r in rows) l.Add(r);
            return l;
        }

        private static IList<IList<(int, int, int)>> R(params (int, int, int)[][] rows)
        {
            var l = new List<IList<(int, int, int)>>();
            foreach (var r in rows) l.Add(r);
            return l;
        }

        /// <summary>白高跟鞋的形狀:鞋 submesh + 小腿 submesh,兩個都列同一組材質名。小腿的 range 指向 Basic
        /// → 交還膚色 (鞋 submesh 已經在畫 shoes.dds)。</summary>
        [Test]
        public void LegSubmesh_GetsItsSkinBack_WhenTheGarmentIsDrawnElsewhere()
        {
            var names = new[] { "001337_woman_shoes.dds", "W_Basic_Pants2.dds" };
            var picks = MshLoader.ResolveMeshDdsIndices(
                N(names, names),
                R(new[] { (0, 0, 376) }, new[] { (1, 0, 252) }));
            Assert.AreEqual(0, picks[0], "鞋子 submesh 應該畫鞋子貼圖");
            Assert.AreEqual(1, picks[1], "小腿 submesh 的 range 指向膚色 → 必須畫膚色,不能被 fallback 貼上鞋子 atlas");
        }

        /// <summary>材質順序相反 (膚色列在前) 也要一樣 —— 001328 "White Heels 1" 就是這種排法。</summary>
        [Test]
        public void SkinListedFirst_StillResolvesBothWays()
        {
            var names = new[] { "W_Basic_Pants2.dds", "001328_woman_shoes.dds" };
            var picks = MshLoader.ResolveMeshDdsIndices(
                N(names, names),
                R(new[] { (1, 0, 308) }, new[] { (0, 0, 214) }));
            Assert.AreEqual(1, picks[0]);
            Assert.AreEqual(0, picks[1]);
        }

        /// <summary>單 submesh 的衣服:range table 指著 Basic,但沒有別人在畫布料 → **維持舊行為**挑布料。
        /// 這條是防迴歸的核心 —— 順著 attrib 會讓 211 件衣服變裸體。</summary>
        [Test]
        public void LoneSubmesh_KeepsTheGarment_EvenWhenItsRangeSaysSkin()
        {
            var names = new[] { "W_Basic_Coat2.dds", "000719_woman_coat.dds" };
            var picks = MshLoader.ResolveMeshDdsIndices(N(names), R(new[] { (0, 0, 100) }));
            Assert.AreEqual(1, picks[0], "只有這一塊幾何 → 它就是衣服本體,不可以畫成裸體");
        }

        /// <summary>gate 也要認得「多 range」的鄰居:那種 submesh 走 per-range 材質分裂,它畫的是**每個 range 各自**
        /// 的貼圖 (不是 <c>Dds</c> 那一張)。布料由它畫出來時,旁邊那塊皮膚一樣要交還。</summary>
        [Test]
        public void MultiRangeNeighbour_CountsAsDrawingTheGarment()
        {
            var names = new[] { "sh8109_woman_shoes.dds", "W_Basic_Pants2.dds" };
            var picks = MshLoader.ResolveMeshDdsIndices(
                N(names, names),
                R(new[] { (0, 0, 10), (1, 10, 10) },      // 多 range → 兩張都畫
                  new[] { (1, 0, 20) }));
            Assert.AreEqual(1, picks[1], "布料已經被隔壁的多 range submesh 畫掉了 → 這塊交還膚色");
        }

        /// <summary>沒有任何 submesh 在畫那張布料時不交還 —— gate 真的有在擋。</summary>
        [Test]
        public void NoOtherSubmeshDrawsIt_NoHandback()
        {
            var picks = MshLoader.ResolveMeshDdsIndices(
                N(new[] { "someone_else_coat.dds", "W_Basic_Coat2.dds" },
                  new[] { "another_pant.dds" }),
                R(new[] { (1, 0, 10) }, new[] { (0, 0, 10) }));
            Assert.AreEqual(0, picks[0], "沒有別的 submesh 在畫 someone_else_coat.dds → 維持舊行為");
        }

        // ---------------- 真實資料 ----------------

        private static string AvatarDir()
        {
            var probe = SdoAvatarBuilder.ResolveAvatarFile("AVATAR/001337_WOMAN_SHOES.MSH");
            if (string.IsNullOrEmpty(probe) || !File.Exists(probe)) return null;
            return Path.GetDirectoryName(probe);
        }

        /// <summary>使用者回報的那雙鞋,直接從磁碟載進來看每個 submesh 最後挑到什麼貼圖。</summary>
        [TestCase("001337_WOMAN_SHOES.MSH", "W_Basic_Pants2")]   // White Heels
        [TestCase("001328_WOMAN_SHOES.MSH", "W_Basic_Pants2")]   // White Heels 1 (材質順序相反)
        [TestCase("000141_MAN_SHOES.MSH", "M_Basic_Pants")]      // 露腳趾的涼鞋
        public void RealShoe_HasExactlyOneSkinSubmesh(string msh, string skinStem)
        {
            var dir = AvatarDir();
            if (dir == null) Assert.Ignore("AVATAR data root not found — 需要遊戲資料 (data_root.txt)");
            var res = MshLoader.Load(File.ReadAllBytes(Path.Combine(dir, msh)));
            Assert.IsNotNull(res, msh + " 載入失敗");
            Assert.AreEqual(2, res.Submeshes.Count, msh + ": 這件是「鞋 + 露出的腿」兩塊幾何");
            int skin = 0, cloth = 0;
            foreach (var sm in res.Submeshes)
            {
                if (sm.Dds != null && sm.Dds.IndexOf(skinStem, System.StringComparison.OrdinalIgnoreCase) >= 0) skin++;
                else if (sm.Dds != null && sm.Dds.IndexOf("shoes", System.StringComparison.OrdinalIgnoreCase) >= 0) cloth++;
            }
            Assert.AreEqual(1, skin, msh + ": 露出來的那塊應該畫膚色 (被貼上鞋子 atlas = 使用者說的「破圖」)");
            Assert.AreEqual(1, cloth, msh + ": 鞋子本體那塊應該畫鞋子貼圖");
        }

        /// <summary>對照組:單 submesh、或沒有膚色 attrib 的一般衣物 —— 載進來每一塊都還是畫布料 (沒被規則誤傷)。</summary>
        [TestCase("037000_WOMAN_COAT.MSH")]
        [TestCase("024976_WOMAN_ONE.MSH")]
        public void OrdinaryGarment_StillDrawsCloth(string msh)
        {
            var dir = AvatarDir();
            if (dir == null) Assert.Ignore("AVATAR data root not found — 需要遊戲資料 (data_root.txt)");
            var res = MshLoader.Load(File.ReadAllBytes(Path.Combine(dir, msh)));
            Assert.IsNotNull(res, msh + " 載入失敗");
            bool anyCloth = false;
            foreach (var sm in res.Submeshes)
                if (sm.Dds != null && sm.Dds.IndexOf("Basic", System.StringComparison.OrdinalIgnoreCase) < 0) anyCloth = true;
            Assert.IsTrue(anyCloth, msh + ": 整件都被畫成膚色了 → 交還規則誤傷了正常衣物");
        }
    }
}
