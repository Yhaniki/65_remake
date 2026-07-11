using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Shop;

namespace Sdo.Tests
{
    public class ShopNameSidecarTests
    {
        [Test]
        public void Parse_ReadsIdNamePairs()
        {
            var map = ShopNameSidecar.Parse("13457\t黄帽 文静女孩\n13458\t男生层次短发\n");
            Assert.AreEqual(2, map.Count);
            Assert.AreEqual("黄帽 文静女孩", map[13457]);
            Assert.AreEqual("男生层次短发", map[13458]);
        }

        [Test]
        public void Parse_ToleratesCrlf_BlankLines_AndTrailingNewline()
        {
            var map = ShopNameSidecar.Parse("1\ta\r\n\r\n2\tb\r\n");
            Assert.AreEqual(2, map.Count);
            Assert.AreEqual("a", map[1]);
            Assert.AreEqual("b", map[2]);
        }

        [Test]
        public void Parse_KeepsNameContainingSpaces_ButNotTheTabDelimiter()
        {
            // only the FIRST tab splits id from name; the name itself has no tab, so spaces survive intact.
            var map = ShopNameSidecar.Parse("14104\t充气奶牛锤（男）\n");
            Assert.AreEqual("充气奶牛锤（男）", map[14104]);
        }

        [Test]
        public void Parse_SkipsMalformedRows()
        {
            var map = ShopNameSidecar.Parse("noTabHere\n\t leadingTab\nabc\tnotAnInt\n42\tok\n");
            Assert.AreEqual(1, map.Count);            // only "42\tok" is valid
            Assert.AreEqual("ok", map[42]);
        }

        [Test]
        public void Parse_AllowsEmptyName()
        {
            var map = ShopNameSidecar.Parse("7\t\n");
            Assert.IsTrue(map.ContainsKey(7));
            Assert.AreEqual("", map[7]);
        }

        [Test]
        public void Parse_NullOrEmpty_ReturnsEmptyMap()
        {
            Assert.AreEqual(0, ShopNameSidecar.Parse(null).Count);
            Assert.AreEqual(0, ShopNameSidecar.Parse("").Count);
        }

        [Test]
        public void Format_OmitsEmptyNames_AndRoundTrips()
        {
            var src = new List<KeyValuePair<int, string>>
            {
                new KeyValuePair<int, string>(13457, "黄帽 文静女孩"),
                new KeyValuePair<int, string>(999, ""),          // dropped
                new KeyValuePair<int, string>(14104, "充气奶牛锤（男）"),
            };
            var text = ShopNameSidecar.Format(src);
            var map = ShopNameSidecar.Parse(text);
            Assert.AreEqual(2, map.Count);
            Assert.IsFalse(map.ContainsKey(999));
            Assert.AreEqual("黄帽 文静女孩", map[13457]);
            Assert.AreEqual("充气奶牛锤（男）", map[14104]);
        }
    }
}
