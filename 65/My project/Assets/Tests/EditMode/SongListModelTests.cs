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
        public void Curate_Orders_By_Gn_Filename_Descending()
        {
            // Browse list is ordered by the on-disk chart filename (sdomNNNNk.gn) descending —
            // highest number first / at the top of the list.
            var r = SongListModel.Curate(Paired());
            Assert.AreEqual("sdom1198k.gn", r[0].gn);
            Assert.AreEqual("sdom1197k.gn", r[1].gn);
        }

        [Test]
        public void Curate_Null_Safe()
            => Assert.AreEqual(0, SongListModel.Curate(null).Count);

        // ---- static Filter over an arbitrary subset (category + search) ----

        [Test]
        public void StaticFilter_Searches_Within_Subset()
        {
            var subset = new List<SongCatalog.Entry> { Sample()[0], Sample()[2] };   // Butterfly, Sugar
            Assert.AreEqual(2, SongListModel.Filter(subset, "").Count);
            Assert.AreEqual(1, SongListModel.Filter(subset, "sugar").Count);
            Assert.AreEqual(0, SongListModel.Filter(subset, "蔡妍").Count);   // not in the subset
        }

        [Test]
        public void StaticFilter_Null_Safe()
            => Assert.AreEqual(0, SongListModel.Filter(null, "x").Count);

        // ---- InLevelRange: the 隨機 difficulty-range pool ----

        [Test]
        public void InLevelRange_Filters_By_Current_Difficulty_Level()
        {
            // Sample()[0] is easy3/normal6/hard9; the others have no level data (Diff -> -1).
            Assert.AreEqual(1, SongListModel.InLevelRange(Sample(), 0, 1, 5).Count);   // easy 3 in 1-5
            Assert.AreEqual(0, SongListModel.InLevelRange(Sample(), 0, 5, 9).Count);   // easy 3 not in 5-9
            Assert.AreEqual(1, SongListModel.InLevelRange(Sample(), 2, 9, 99).Count);  // hard 9 >= 9
        }

        [Test]
        public void InLevelRange_All_Includes_Unknown_Levels()
            => Assert.AreEqual(3, SongListModel.InLevelRange(Sample(), 1, 0, 99).Count);

        [Test]
        public void InLevelRange_Null_Safe()
            => Assert.AreEqual(0, SongListModel.InLevelRange(null, 0, 1, 5).Count);
    }
}
