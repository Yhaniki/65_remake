using System.Linq;
using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    public class FavoritesTests
    {
        // Favorites holds static state; reset (and detach the file path) before each case.
        [SetUp]
        public void Reset() => Favorites.ResetForTests();

        [Test]
        public void Key_TakesFilename_Lowercase()
        {
            Assert.AreEqual("sdom0001k.gn", Favorites.Key("SDOM0001K.GN"));
            Assert.AreEqual("sdom0001k.gn", Favorites.Key(@"C:\music\SDOM0001K.gn"));
            Assert.AreEqual("sdom0001k.gn", Favorites.Key("MUSIC/sdom0001k.gn"));
            Assert.AreEqual("", Favorites.Key(null));
            Assert.AreEqual("", Favorites.Key(""));
        }

        [Test]
        public void Add_Remove_Toggle_IsFav()
        {
            Assert.IsFalse(Favorites.IsFav("sdom1.gn"));
            Assert.IsTrue(Favorites.Add("sdom1.gn"));
            Assert.IsTrue(Favorites.IsFav("SDOM1.GN"));    // key is case-insensitive
            Assert.IsFalse(Favorites.Add("sdom1.gn"));     // duplicate → no change
            Assert.AreEqual(1, Favorites.Count);

            Assert.IsFalse(Favorites.Toggle("sdom1.gn"));  // toggle off → returns now-favorited=false
            Assert.IsFalse(Favorites.IsFav("sdom1.gn"));
            Assert.IsTrue(Favorites.Toggle("sdom1.gn"));   // toggle on
            Assert.IsTrue(Favorites.IsFav("sdom1.gn"));

            Assert.IsTrue(Favorites.Remove("sdom1.gn"));
            Assert.IsFalse(Favorites.Remove("sdom1.gn"));  // already gone
            Assert.AreEqual(0, Favorites.Count);
        }

        [Test]
        public void Serialize_Then_Parse_RoundTrips_Deduped_OrderPreserved()
        {
            var json = Favorites.Serialize(new[] { "sdom3.gn", "sdom1.gn", "sdom1.gn", "" });
            var keys = Favorites.Parse(json).ToList();
            CollectionAssert.AreEqual(new[] { "sdom3.gn", "sdom1.gn" }, keys);   // 加入順序保留（不排序）+ 去重 + 去空
        }

        [Test]
        public void NewestFirst_PutsMostRecentlyAddedOnTop()
        {
            Favorites.Add("sdom1.gn");
            Favorites.Add("sdom2.gn");
            Favorites.Add("sdom3.gn");
            CollectionAssert.AreEqual(new[] { "sdom3.gn", "sdom2.gn", "sdom1.gn" }, Favorites.NewestFirst().ToList());
            CollectionAssert.AreEqual(new[] { "sdom1.gn", "sdom2.gn", "sdom3.gn" }, Favorites.Ordered.ToList());

            // 移除中間再重加 → 重加的視為最新，排到最前。
            Favorites.Remove("sdom2.gn");
            Favorites.Add("sdom2.gn");
            CollectionAssert.AreEqual(new[] { "sdom2.gn", "sdom3.gn", "sdom1.gn" }, Favorites.NewestFirst().ToList());
        }

        [Test]
        public void Parse_BadOrEmptyJson_ReturnsEmpty()
        {
            Assert.IsEmpty(Favorites.Parse(null));
            Assert.IsEmpty(Favorites.Parse(""));
            Assert.IsEmpty(Favorites.Parse("{ not json"));
            Assert.IsEmpty(Favorites.Parse("{\"gns\":null}"));
        }
    }
}
