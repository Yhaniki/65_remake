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

        // ---- RandomCandidates: 隨機難度 searches easy/normal/hard together, keyed on each qualifying chart ----

        // easy3/normal6/hard9, all playable (notes>0); "hi" is hard-only level 27 with notes only on hard.
        private static List<SongCatalog.Entry> Levelled() => new List<SongCatalog.Entry>
        {
            new SongCatalog.Entry { gn = "a.gn", fileId = 1, diffEasy = 3, diffNormal = 6, diffHard = 9,
                                    notesEasy = 100, notesNormal = 200, notesHard = 300 },
            new SongCatalog.Entry { gn = "hi.gn", fileId = 2, diffEasy = 3, diffNormal = 12, diffHard = 27,
                                    notesEasy = 0, notesNormal = 150, notesHard = 250 },   // easy empty -> not a candidate
        };

        private const int RngAll = 3, Rng1to5 = 0, Rng5to9 = 2, Rng9up = 5, Rng25up = 8;

        [Test]
        public void RandomCandidates_Searches_All_Three_Difficulties()
        {
            // "a" qualifies at easy(3) in 1-5; "hi" easy is empty. Only one candidate.
            var c15 = SongListModel.RandomCandidates(Levelled(), Rng1to5);
            Assert.AreEqual(1, c15.Count);
            Assert.AreEqual("a.gn", c15[0].Song.gn);
            Assert.AreEqual(0, c15[0].Difficulty);   // the matched difficulty is easy

            // 5-9 catches a.normal(6) and a.hard(9) — same song, two candidates at different difficulties.
            var c59 = SongListModel.RandomCandidates(Levelled(), Rng5to9);
            Assert.AreEqual(2, c59.Count);
            CollectionAssert.AreEquivalent(new[] { 1, 2 }, c59.ConvertAll(x => x.Difficulty));
        }

        [Test]
        public void RandomCandidates_High_Range_Uses_A_Different_Songs_Hard()
        {
            // 25以上: only hi.hard(27) qualifies — proves the pool isn't limited to the active/easy difficulty.
            var c = SongListModel.RandomCandidates(Levelled(), Rng25up);
            Assert.AreEqual(1, c.Count);
            Assert.AreEqual("hi.gn", c[0].Song.gn);
            Assert.AreEqual(2, c[0].Difficulty);
        }

        [Test]
        public void RandomCandidates_9up_Spans_Both_Songs()
        {
            // 9級以上: a.hard(9), hi.normal(12), hi.hard(27) — 3 candidates across both songs.
            var c = SongListModel.RandomCandidates(Levelled(), Rng9up);
            Assert.AreEqual(3, c.Count);
        }

        [Test]
        public void RandomCandidates_All_Includes_Every_Playable_Chart()
        {
            // 全部: a has 3 playable charts, hi has 2 (easy empty) -> 5 candidates.
            Assert.AreEqual(5, SongListModel.RandomCandidates(Levelled(), RngAll).Count);
        }

        [Test]
        public void RandomCandidates_Clamps_Range_And_Is_Null_Safe()
        {
            Assert.AreEqual(0, SongListModel.RandomCandidates(null, RngAll).Count);
            // out-of-bounds index clamps into the table instead of throwing.
            Assert.DoesNotThrow(() => SongListModel.RandomCandidates(Levelled(), -5));
            Assert.DoesNotThrow(() => SongListModel.RandomCandidates(Levelled(), 999));
        }

        // ---- HasChart / FirstPlayableIndex: a difficulty with 0 notes is empty (row greyed, non-selectable) ----
        // Mirrors the real 動畫歌曲串燒 entry (sdom2140k): easy has notes, normal/hard are empty (level 0, not -1).

        private static SongCatalog.Entry EasyOnly() => new SongCatalog.Entry
        {
            gn = "sdom2140k.gn", fileId = 12140, title = "动画歌曲串烧",
            diffEasy = 26, diffNormal = 0, diffHard = 0,
            notesEasy = 3417, notesNormal = 0, notesHard = 0,
        };

        [Test]
        public void HasChart_Uses_NoteCount_Not_Level()
        {
            var e = EasyOnly();
            Assert.IsTrue(e.HasChart(0), "easy has 3417 notes");
            Assert.IsFalse(e.HasChart(1), "normal has 0 notes (even though level field is 0, not -1)");
            Assert.IsFalse(e.HasChart(2), "hard has 0 notes");
        }

        private static List<SongCatalog.Entry> MixedList() => new List<SongCatalog.Entry>
        {
            new SongCatalog.Entry { gn = "0.gn", notesEasy = 10, notesNormal = 10, notesHard = 10 }, // full
            EasyOnly(),                                                                              // easy only
            new SongCatalog.Entry { gn = "2.gn", notesEasy = 10, notesNormal = 10, notesHard = 10 }, // full
        };

        [Test]
        public void FirstPlayable_Returns_Same_Index_When_Playable()
            => Assert.AreEqual(0, SongListModel.FirstPlayableIndex(MixedList(), 2, 0));   // index0 has a hard chart

        [Test]
        public void FirstPlayable_Skips_Empty_Difficulty_Rows()
        {
            // On HARD, index1 (動畫歌曲串燒) is empty -> from index1 the first playable hard chart is index2.
            Assert.AreEqual(2, SongListModel.FirstPlayableIndex(MixedList(), 2, 1));
        }

        [Test]
        public void FirstPlayable_Wraps_To_Start()
        {
            // Only index0 has a hard chart among {0:full, 1:easyOnly}; searching from index1 wraps to 0.
            var list = new List<SongCatalog.Entry> { MixedList()[0], EasyOnly() };
            Assert.AreEqual(0, SongListModel.FirstPlayableIndex(list, 2, 1));
        }

        [Test]
        public void FirstPlayable_None_Returns_Negative()
        {
            var list = new List<SongCatalog.Entry> { EasyOnly(), EasyOnly() };   // none has a hard chart
            Assert.AreEqual(-1, SongListModel.FirstPlayableIndex(list, 2, 0));
        }

        [Test]
        public void FirstPlayable_Null_Or_Empty_Safe()
        {
            Assert.AreEqual(-1, SongListModel.FirstPlayableIndex(null, 0, 0));
            Assert.AreEqual(-1, SongListModel.FirstPlayableIndex(new List<SongCatalog.Entry>(), 0, 0));
        }

        // ---- Externals: the pool the 分類瀏覽 panel groups (user Songs/ songs only, never the official .gn ones) ----

        [Test]
        public void Externals_Keeps_Only_External_Songs()
        {
            var list = new List<SongCatalog.Entry>
            {
                new SongCatalog.Entry { gn = "sdom0001k.gn", title = "official" },
                new SongCatalog.Entry { gn = "ext_aaaak.gn", title = "user", external = true, group = "Anime" },
            };
            var r = SongListModel.Externals(list);
            Assert.AreEqual(1, r.Count);
            Assert.AreEqual("user", r[0].title);
        }

        [Test]
        public void Externals_Null_Safe()
            => Assert.AreEqual(0, SongListModel.Externals(null).Count);
    }
}
