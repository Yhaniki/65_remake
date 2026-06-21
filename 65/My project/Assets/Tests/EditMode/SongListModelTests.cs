using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using Sdo.UI.Catalog;

namespace Sdo.Tests
{
    public class SongListModelTests
    {
        private static List<SongCatalog.Entry> Sample() => new List<SongCatalog.Entry>
        {
            new SongCatalog.Entry { gn = "a.gn", fileId = 1, title = "Butterfly", artist = "Smile", diffEasy = 3, diffNormal = 6, diffHard = 9 },
            new SongCatalog.Entry { gn = "b.gn", fileId = 2, title = "危險的演出", artist = "蔡妍" },
            new SongCatalog.Entry { gn = "c.gn", fileId = 3, title = "Sugar", artist = "Maroon" },
        };

        [Test]
        public void Filter_Empty_Returns_All()
        {
            var m = new SongListModel(Sample());
            Assert.AreEqual(3, m.Filter("").Count);
            Assert.AreEqual(3, m.Filter(null).Count);
        }

        [Test]
        public void Filter_By_Title_CaseInsensitive()
        {
            var r = new SongListModel(Sample()).Filter("butter");
            Assert.AreEqual(1, r.Count);
            Assert.AreEqual(1, r[0].fileId);
        }

        [Test]
        public void Filter_By_Artist_Cjk()
            => Assert.AreEqual(1, new SongListModel(Sample()).Filter("蔡妍").Count);

        [Test]
        public void PickRandom_Is_Deterministic_And_InRange()
        {
            var m = new SongListModel(Sample());
            var a = m.PickRandom(42);
            var b = m.PickRandom(42);
            Assert.AreSame(a, b);
            Assert.IsTrue(m.Filter("").Contains(a));
        }

        [Test]
        public void PickRandom_Empty_Is_Null()
            => Assert.IsNull(new SongListModel(new List<SongCatalog.Entry>()).PickRandom(1));

        [Test]
        public void Entry_Diff_Selects_Correct_Field()
        {
            var e = Sample()[0];
            Assert.AreEqual(3, e.Diff(0));
            Assert.AreEqual(6, e.Diff(1));
            Assert.AreEqual(9, e.Diff(2));
        }

        [Test]
        public void Entry_Missing_Bpm_Defaults_Negative()
            => Assert.Less(Sample()[1].bpm, 0f);

        // ---- Curate: the browse list shows only the 'k' chart of each sdomNNNNk/t.gn pair ----

        private static List<SongCatalog.Entry> Paired() => new List<SongCatalog.Entry>
        {
            new SongCatalog.Entry { gn = "sdom1197k.gn", fileId = 11197, title = "危險的演出" },
            new SongCatalog.Entry { gn = "sdom1197t.gn", fileId = 1197,  title = "危險的演出" },
            new SongCatalog.Entry { gn = "sdom1198k.gn", fileId = 11198, title = "Cross" },
            new SongCatalog.Entry { gn = "sdom1198t.gn", fileId = 1198,  title = "Cross" },
        };

        [Test]
        public void Curate_Drops_T_Variant_Keeps_K()
        {
            var r = SongListModel.Curate(Paired());
            Assert.AreEqual(2, r.Count);
            CollectionAssert.AreEquivalent(new[] { "sdom1198k.gn", "sdom1197k.gn" }, r.ConvertAll(e => e.gn));
        }

        [Test]
        public void Curate_Orders_By_FileId_Descending()
        {
            var r = SongListModel.Curate(Paired());
            Assert.AreEqual(11198, r[0].fileId);
            Assert.AreEqual(11197, r[1].fileId);
        }

        [Test]
        public void Curate_Null_Safe()
            => Assert.AreEqual(0, SongListModel.Curate(null).Count);
    }
}
