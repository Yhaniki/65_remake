using NUnit.Framework;
using Sdo.Shop;

namespace Sdo.Tests
{
    /// <summary>DRESS.TXT / PETDRESS.TXT 的解析 —— 官方「modelId → 資源檔名」對照表 (非衣服商品的圖示/模型全靠它)。
    /// 樣本行全部照抄實檔。</summary>
    public class DressTableTests
    {
        [Test]
        public void Parse_TabSeparated_IdToResource()
        {
            var m = DressTable.Parse(
                "100000\t100000_xiaolaba.an\n" +      // 小喇叭 → 2D 圖示
                "100400\t100400_lihe.msh\n" +         // 禮盒 → 3D
                "200000\t200000_charback0.an\n" +     // 背景卡
                "1000001\t1000001.an\n");             // 寵物 (UI/MATCHITEMS)
            Assert.AreEqual(4, m.Count);
            Assert.AreEqual("100000_xiaolaba.an", m[100000]);
            Assert.AreEqual("100400_lihe.msh", m[100400]);
            Assert.AreEqual("1000001.an", m[1000001]);
        }

        [Test]
        public void Parse_SpaceSeparated_AndOverlay()
        {
            // PETDRESS.TXT 是空白分隔,而且會疊在 DRESS.TXT 之上 (同 id 後者勝)
            var m = DressTable.Parse("1030000 1030000_all_headear.msh\n1040000 1040000_all_coat.dds\n");
            DressTable.Parse("1030000 override.msh\n", m);
            Assert.AreEqual("override.msh", m[1030000]);
            Assert.AreEqual("1040000_all_coat.dds", m[1040000]);
        }

        [Test]
        public void Parse_SkipsBlankAndNonNumericLines()
        {
            var m = DressTable.Parse("\n  \n# comment\nnot a row\n100000\t100000_xiaolaba.an\n");
            Assert.AreEqual(1, m.Count);
        }

        [Test]
        public void KindOf_ByExtension()
        {
            Assert.AreEqual(DressResourceKind.Icon, DressTable.KindOf("100000_xiaolaba.an"));
            Assert.AreEqual(DressResourceKind.Mesh, DressTable.KindOf("100400_LIHE.MSH"));      // 大小寫不敏感
            Assert.AreEqual(DressResourceKind.Texture, DressTable.KindOf("1040000_all_coat.dds"));
            Assert.AreEqual(DressResourceKind.Unknown, DressTable.KindOf("something.txt"));
            Assert.AreEqual(DressResourceKind.Unknown, DressTable.KindOf(null));
        }

        [Test]
        public void Potion_BorrowsBianxingshuiArt_FromTheTableItself()
        {
            // 官方就是這樣:22000 藥水在 DRESS.TXT 直接指到變形水/換膚水的美術 (不是我們自己編的 fallback)。
            var m = DressTable.Parse("100031\t100202_bianxingshui.an\n");
            Assert.AreEqual("100202_bianxingshui.an", m[100031]);
        }

        [Test]
        public void GiftPackProxy_ByModelIdRange()
        {
            Assert.AreEqual(100400, DressTable.GiftPackProxyModelId(600001));   // 禮盒
            Assert.AreEqual(100400, DressTable.GiftPackProxyModelId(619999));
            Assert.AreEqual(100796, DressTable.GiftPackProxyModelId(620000));   // 紅包
            Assert.AreEqual(100798, DressTable.GiftPackProxyModelId(630500));
            Assert.AreEqual(100881, DressTable.GiftPackProxyModelId(640001));   // 過年大禮包
            Assert.AreEqual(-1, DressTable.GiftPackProxyModelId(100000));       // 一般道具不借
            Assert.AreEqual(-1, DressTable.GiftPackProxyModelId(1000000));      // 寵物不借
        }

    }
}
