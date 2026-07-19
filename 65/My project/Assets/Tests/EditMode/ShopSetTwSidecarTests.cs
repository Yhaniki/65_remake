using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Shop;

namespace Sdo.Tests
{
    public class ShopSetTwSidecarTests
    {
        [Test]
        public void Parse_ReadsSetIdGenderCompsName()
        {
            var list = ShopSetTwSidecar.Parse("500001\tM\t3,46,89,131\t古惑仔\n550001\tF\t30,73,116,158\t優娜\n");
            Assert.AreEqual(2, list.Count);
            Assert.AreEqual(500001, list[0].SetId);
            Assert.IsTrue(list[0].Male);
            Assert.AreEqual("古惑仔", list[0].Name);
            CollectionAssert.AreEqual(new[] { 3, 46, 89, 131 }, list[0].Components);
            Assert.IsFalse(list[1].Male);
            Assert.AreEqual("優娜", list[1].Name);
        }

        [Test]
        public void Parse_ToleratesCrlf_BlankLines_AndTrailingNewline()
        {
            var list = ShopSetTwSidecar.Parse("500001\tM\t3,46\ta\r\n\r\n550002\tF\t7,8\tb\r\n");
            Assert.AreEqual(2, list.Count);
            Assert.AreEqual("a", list[0].Name);
            Assert.AreEqual("b", list[1].Name);
        }

        [Test]
        public void Parse_KeepsNameWithTab_AfterThirdTab()
        {
            var list = ShopSetTwSidecar.Parse("500008\tM\t407,437,467\tTekken\t復古套裝\n");
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("Tekken\t復古套裝", list[0].Name);
        }

        [Test]
        public void Parse_SkipsMalformedRows()
        {
            var list = ShopSetTwSidecar.Parse(
                "noTabs\n" +                    // no tabs
                "500001\tM\t3,46\n" +           // only two tabs (no name column)
                "x\tM\t3,46\tbadSetId\n" +      // non-integer setId
                "500002\tM\t\temptyComps\n" +   // no components
                "500003\tM\t9,10\t\n" +         // empty name
                "500004\tM\t11,12\tok\n");      // valid
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual(500004, list[0].SetId);
            Assert.AreEqual("ok", list[0].Name);
        }

        [Test]
        public void Parse_IgnoresNonIntegerAndZeroComponents()
        {
            var list = ShopSetTwSidecar.Parse("500001\tM\t3, xx ,0,131\t古惑仔\n");
            Assert.AreEqual(1, list.Count);
            CollectionAssert.AreEqual(new[] { 3, 131 }, list[0].Components);
        }

        [Test]
        public void Parse_NullOrEmpty_ReturnsEmptyList()
        {
            Assert.AreEqual(0, ShopSetTwSidecar.Parse(null).Count);
            Assert.AreEqual(0, ShopSetTwSidecar.Parse("").Count);
        }

        [Test]
        public void Format_RoundTrips_AndDropsInvalid()
        {
            var defs = new List<TwSetDef>
            {
                new TwSetDef { SetId = 500001, Male = true,  Name = "古惑仔", Components = new[] { 3, 46, 89, 131 } },
                new TwSetDef { SetId = 999,    Male = true,  Name = "",       Components = new[] { 1, 2 } },          // dropped (no name)
                new TwSetDef { SetId = 998,    Male = false, Name = "空",      Components = new int[0] },              // dropped (no comps)
                new TwSetDef { SetId = 550001, Male = false, Name = "優娜",    Components = new[] { 30, 73, 116, 158 } },
            };
            var round = ShopSetTwSidecar.Parse(ShopSetTwSidecar.Format(defs));
            Assert.AreEqual(2, round.Count);
            Assert.AreEqual(500001, round[0].SetId);
            Assert.IsTrue(round[0].Male);
            CollectionAssert.AreEqual(new[] { 3, 46, 89, 131 }, round[0].Components);
            Assert.AreEqual("優娜", round[1].Name);
            Assert.IsFalse(round[1].Male);
        }
    }
}
