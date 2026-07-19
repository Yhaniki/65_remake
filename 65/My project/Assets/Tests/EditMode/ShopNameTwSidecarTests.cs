using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Shop;

namespace Sdo.Tests
{
    public class ShopNameTwSidecarTests
    {
        [Test]
        public void Key_IsUniquePerCategoryAndModelId()
        {
            // same modelId in two slots/genders → distinct keys (this is the whole reason we key by category too).
            Assert.AreNotEqual(ShopNameTwSidecar.Key(2, 1277), ShopNameTwSidecar.Key(3, 1277));     // 上衣 vs 下裝
            Assert.AreNotEqual(ShopNameTwSidecar.Key(1, 32), ShopNameTwSidecar.Key(101, 32));        // 男 vs 女 髮型
            Assert.AreEqual(ShopNameTwSidecar.Key(101, 32), ShopNameTwSidecar.Key(101, 32));         // deterministic
        }

        [Test]
        public void Parse_ReadsCategoryModelIdNameTriples()
        {
            var map = ShopNameTwSidecar.Parse("1\t1\t小刺蝟\n101\t34\t輕舞飛揚直髮\n");
            Assert.AreEqual(2, map.Count);
            Assert.AreEqual("小刺蝟", map[ShopNameTwSidecar.Key(1, 1)]);
            Assert.AreEqual("輕舞飛揚直髮", map[ShopNameTwSidecar.Key(101, 34)]);
        }

        [Test]
        public void Parse_ToleratesCrlf_BlankLines_AndTrailingNewline()
        {
            var map = ShopNameTwSidecar.Parse("1\t1\ta\r\n\r\n2\t5\tb\r\n");
            Assert.AreEqual(2, map.Count);
            Assert.AreEqual("a", map[ShopNameTwSidecar.Key(1, 1)]);
            Assert.AreEqual("b", map[ShopNameTwSidecar.Key(2, 5)]);
        }

        [Test]
        public void Parse_KeepsNameContainingTabsAndSpaces_AfterSecondTab()
        {
            // everything after the SECOND tab is the name — a tab or spaces inside the name survive.
            var map = ShopNameTwSidecar.Parse("2\t45\t條紋 針織\tV領\n");
            Assert.AreEqual("條紋 針織\tV領", map[ShopNameTwSidecar.Key(2, 45)]);
        }

        [Test]
        public void Parse_SkipsMalformedRows()
        {
            var map = ShopNameTwSidecar.Parse(
                "noTabs\n" +                 // no tab at all
                "1\tonlyOneTab\n" +          // only one tab (no name column)
                "x\t1\tbadCat\n" +           // non-integer category
                "1\ty\tbadModel\n" +         // non-integer modelId
                "\t1\tleadingTab\n" +        // empty category column
                "1\t9\tok\n");               // the only valid row
            Assert.AreEqual(1, map.Count);
            Assert.AreEqual("ok", map[ShopNameTwSidecar.Key(1, 9)]);
        }

        [Test]
        public void Parse_AllowsEmptyName()
        {
            var map = ShopNameTwSidecar.Parse("8\t7\t\n");
            Assert.IsTrue(map.ContainsKey(ShopNameTwSidecar.Key(8, 7)));
            Assert.AreEqual("", map[ShopNameTwSidecar.Key(8, 7)]);
        }

        [Test]
        public void Parse_NullOrEmpty_ReturnsEmptyMap()
        {
            Assert.AreEqual(0, ShopNameTwSidecar.Parse(null).Count);
            Assert.AreEqual(0, ShopNameTwSidecar.Parse("").Count);
        }

        [Test]
        public void Format_OmitsEmptyNames_AndRoundTrips()
        {
            var src = new List<KeyValuePair<long, string>>
            {
                new KeyValuePair<long, string>(ShopNameTwSidecar.Key(1, 1), "小刺蝟"),
                new KeyValuePair<long, string>(ShopNameTwSidecar.Key(8, 99), ""),       // dropped
                new KeyValuePair<long, string>(ShopNameTwSidecar.Key(102, 251), "牛仔西裝外套"),
            };
            var text = ShopNameTwSidecar.Format(src);
            var map = ShopNameTwSidecar.Parse(text);
            Assert.AreEqual(2, map.Count);
            Assert.IsFalse(map.ContainsKey(ShopNameTwSidecar.Key(8, 99)));
            Assert.AreEqual("小刺蝟", map[ShopNameTwSidecar.Key(1, 1)]);
            Assert.AreEqual("牛仔西裝外套", map[ShopNameTwSidecar.Key(102, 251)]);
        }
    }
}
