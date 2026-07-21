using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using Sdo.UI.Catalog;

namespace Sdo.Tests
{
    /// <summary>
    /// The 分類瀏覽 panel's grouping — a port of StepMania's section rules (SM-YHANIKI src/SongUtil.cpp).
    /// The reference values below are what the C++ produces for the same inputs.
    /// </summary>
    public class SongGroupingTests
    {
        private static SongCatalog.Entry Song(string gn, string title, string artist = "", float bpm = -1f, string group = "", bool external = true)
            => new SongCatalog.Entry
            {
                gn = gn, title = title, artist = artist, bpm = bpm, group = group, external = external,
                notesEasy = 100, notesNormal = 100, notesHard = 100,
            };

        // ---- MakeSortString (SM: upper-case, drop a leading '.', '~'-prefix anything not starting alphanumeric) ----

        [Test]
        public void MakeSortString_Uppercases()
            => Assert.AreEqual("BUTTERFLY", SongGrouping.MakeSortString("Butterfly"));

        [Test]
        public void MakeSortString_Drops_Leading_Dot()
            => Assert.AreEqual("59", SongGrouping.MakeSortString(".59"));

        [Test]
        public void MakeSortString_Tilde_Prefixes_NonAlphanumeric()
        {
            Assert.AreEqual("~!HELLO", SongGrouping.MakeSortString("!hello"));
            Assert.AreEqual("~危險的演出", SongGrouping.MakeSortString("危險的演出"));   // CJK is not A-Z → sorts last
        }

        [Test]
        public void MakeSortString_Trims_So_A_Stray_Space_Is_Not_OTHER()
            => Assert.AreEqual("BUTTERFLY", SongGrouping.MakeSortString("  Butterfly "));

        [Test]
        public void MakeSortString_Null_Or_Empty_Is_Empty()
        {
            Assert.AreEqual("", SongGrouping.MakeSortString(null));
            Assert.AreEqual("", SongGrouping.MakeSortString("   "));
        }

        // ---- letter sections (歌名 / 歌手) ----

        [Test]
        public void InitialSection_Letter_Is_The_Uppercase_Initial()
        {
            Assert.AreEqual("B", SongGrouping.InitialSection("Butterfly"));
            Assert.AreEqual("S", SongGrouping.InitialSection("sugar"));
        }

        [Test]
        public void InitialSection_Digits_Bucket_To_NUM()
        {
            Assert.AreEqual(SongGrouping.Num, SongGrouping.InitialSection("2 Unlimited"));
            Assert.AreEqual(SongGrouping.Num, SongGrouping.InitialSection(".59"));   // leading dot dropped first
        }

        [Test]
        public void InitialSection_Cjk_And_Symbols_And_Blank_Bucket_To_OTHER()
        {
            Assert.AreEqual(SongGrouping.Other, SongGrouping.InitialSection("危險的演出"));
            Assert.AreEqual(SongGrouping.Other, SongGrouping.InitialSection("!!!"));
            Assert.AreEqual(SongGrouping.Other, SongGrouping.InitialSection(""));
            Assert.AreEqual(SongGrouping.Other, SongGrouping.InitialSection(null));
        }

        // ---- BPM bands (50-wide, rounded up to the top of the band) ----

        [Test]
        public void BpmSection_Bands_Are_50_Wide_And_Zero_Padded()
        {
            Assert.AreEqual("100-149", SongGrouping.BpmSection(100));
            Assert.AreEqual("100-149", SongGrouping.BpmSection(145.9));
            Assert.AreEqual("100-149", SongGrouping.BpmSection(149));
            Assert.AreEqual("150-199", SongGrouping.BpmSection(150));
            Assert.AreEqual("050-099", SongGrouping.BpmSection(50));
            Assert.AreEqual("000-049", SongGrouping.BpmSection(49));
            Assert.AreEqual("000-049", SongGrouping.BpmSection(20));
        }

        [Test]
        public void BpmSection_Missing_Bpm_Is_Unknown()
        {
            Assert.AreEqual(SongGrouping.UnknownBpm, SongGrouping.BpmSection(0));
            Assert.AreEqual(SongGrouping.UnknownBpm, SongGrouping.BpmSection(-1));   // catalog's "no bpm" value
        }

        // ---- SectionName per mode ----

        [Test]
        public void SectionName_Folder_Is_The_Group_Folder()
        {
            Assert.AreEqual("Anime", SongGrouping.SectionName(Song("a.gn", "x", group: "Anime"), SongGroupMode.Folder));
            Assert.AreEqual(SongGrouping.Ungrouped, SongGrouping.SectionName(Song("a.gn", "x"), SongGroupMode.Folder));
        }

        [Test]
        public void SectionName_Uses_Title_Artist_Or_Bpm_Per_Mode()
        {
            var e = Song("a.gn", "Butterfly", "Smile.dk", 138f, "Anime");
            Assert.AreEqual("B", SongGrouping.SectionName(e, SongGroupMode.Title));
            Assert.AreEqual("S", SongGrouping.SectionName(e, SongGroupMode.Artist));
            Assert.AreEqual("100-149", SongGrouping.SectionName(e, SongGroupMode.Bpm));
        }

        // ---- Build: buckets, counts, ordering ----

        private static List<SongCatalog.Entry> Library() => new List<SongCatalog.Entry>
        {
            Song("s1.gn", "Butterfly",   "Smile.dk", 138f, "Anime"),
            Song("s2.gn", "Cross",       "Smile.dk", 145f, "Anime"),
            Song("s3.gn", "2 Unlimited", "Zed",      160f, "K-POP"),
            Song("s4.gn", "危險的演出",   "蔡妍",     145f, "K-POP"),
            Song("s5.gn", "Apple",       "Zed",      -1f,  "K-POP"),
        };

        [Test]
        public void Build_Folder_Buckets_Carry_Their_Songs_And_Counts()
        {
            var b = SongGrouping.Build(Library(), SongGroupMode.Folder);
            Assert.AreEqual(2, b.Count);
            Assert.AreEqual("Anime", b[0].Key);
            Assert.AreEqual(2, b[0].Count);
            Assert.AreEqual("K-POP", b[1].Key);
            Assert.AreEqual(3, b[1].Count);
        }

        [Test]
        public void Build_Folder_Merges_Case_Different_Folder_Names()
        {
            var list = new List<SongCatalog.Entry> { Song("a.gn", "A", group: "Anime"), Song("b.gn", "B", group: "anime") };
            var b = SongGrouping.Build(list, SongGroupMode.Folder);
            Assert.AreEqual(1, b.Count);
            Assert.AreEqual(2, b[0].Count);
        }

        [Test]
        public void Build_Folder_Puts_An_Unnamed_Folder_Last()
        {
            var list = new List<SongCatalog.Entry> { Song("a.gn", "A"), Song("b.gn", "B", group: "Zoo") };
            var b = SongGrouping.Build(list, SongGroupMode.Folder);
            Assert.AreEqual("Zoo", b[0].Key);
            Assert.AreEqual(SongGrouping.Ungrouped, b[1].Key);
        }

        [Test]
        public void Build_Title_Sections_Order_Num_First_Then_Letters_Then_Other()
        {
            var b = SongGrouping.Build(Library(), SongGroupMode.Title);
            CollectionAssert.AreEqual(
                new[] { SongGrouping.Num, "A", "B", "C", SongGrouping.Other },
                b.ConvertAll(x => x.Key));
            Assert.AreEqual(1, b[0].Count);                 // "2 Unlimited"
            Assert.AreEqual("危險的演出", b[4].Songs[0].title);   // CJK → OTHER, last
        }

        [Test]
        public void Build_Artist_Buckets_By_Artist_Initial()
        {
            var b = SongGrouping.Build(Library(), SongGroupMode.Artist);
            CollectionAssert.AreEqual(new[] { "S", "Z", SongGrouping.Other }, b.ConvertAll(x => x.Key));
            Assert.AreEqual(2, b[0].Count);   // Smile.dk ×2
            Assert.AreEqual(2, b[1].Count);   // Zed ×2
            Assert.AreEqual(1, b[2].Count);   // 蔡妍
        }

        [Test]
        public void Build_Artist_Bucket_Sorts_Songs_By_Title()
        {
            var b = SongGrouping.Build(Library(), SongGroupMode.Artist);
            var zed = b[1];   // Zed: "2 Unlimited" (NUM sorts before letters) then "Apple"
            CollectionAssert.AreEqual(new[] { "2 Unlimited", "Apple" }, zed.Songs.ConvertAll(s => s.title));
        }

        [Test]
        public void Build_Bpm_Bands_Ascend_With_Unknown_Last()
        {
            var b = SongGrouping.Build(Library(), SongGroupMode.Bpm);
            CollectionAssert.AreEqual(
                new[] { "100-149", "150-199", SongGrouping.UnknownBpm },
                b.ConvertAll(x => x.Key));
            Assert.AreEqual(3, b[0].Count);   // 138 + 145 ×2 all fall in the one 100-149 band
            Assert.AreEqual(1, b[2].Count);   // Apple has no bpm → UnknownBpm, last
        }

        [Test]
        public void Build_Bpm_Bucket_Sorts_Songs_By_Bpm_Then_Title()
        {
            var list = new List<SongCatalog.Entry>
            {
                Song("a.gn", "Zebra", bpm: 145f),   // all three land in the one 100-149 band
                Song("b.gn", "Alpha", bpm: 141f),
                Song("c.gn", "Beta",  bpm: 141f),
            };
            var b = SongGrouping.Build(list, SongGroupMode.Bpm);
            Assert.AreEqual(1, b.Count);
            // within the band: by BPM then title → 141 Alpha, 141 Beta (tie → title), 145 Zebra
            CollectionAssert.AreEqual(new[] { "Alpha", "Beta", "Zebra" }, b[0].Songs.ConvertAll(s => s.title));
        }

        [Test]
        public void Build_Bpm_Bands_Order_Numerically_Not_Lexically()
        {
            // A 4–5 digit BPM band must sort ABOVE the small bands, not wedge in after "100-…" because '1' < '2'.
            var list = new List<SongCatalog.Entry>
            {
                Song("a.gn", "Slow",   bpm: 100f),     // 100-149
                Song("b.gn", "Mid",    bpm: 200f),     // 200-249
                Song("c.gn", "Broken", bpm: 10000f),   // 10000-10049 — must come LAST, not between 100 and 200
            };
            var b = SongGrouping.Build(list, SongGroupMode.Bpm);
            CollectionAssert.AreEqual(
                new[] { "100-149", "200-249", "10000-10049" }, b.ConvertAll(x => x.Key));
        }

        // ---- 歌包自帶 serverconfig 時，資料夾模式照包自己的順序（見 SDO_SERVERCONFIG.md / SdoServerConfig）----

        private static SongCatalog.Entry PackSong(string gn, string title, string group, int packOrder)
        {
            var e = Song(gn, title, group: group);
            e.packOrder = packOrder;
            return e;
        }

        [Test]
        public void Build_Folder_Bucket_Uses_The_Packs_Own_Order()
        {
            // 官方選單是反序畫的：serverconfig 表的最後一列在最上面 → 列號降冪，跟歌名字母序無關。
            var list = new List<SongCatalog.Entry>
            {
                PackSong("ext_ak.gn", "Aloha", "NX", 3),
                PackSong("ext_bk.gn", "Zebra", "NX", 9),
                PackSong("ext_ck.gn", "Melody", "NX", 5),
            };
            var b = SongGrouping.Build(list, SongGroupMode.Folder);
            CollectionAssert.AreEqual(new[] { "Zebra", "Melody", "Aloha" }, b[0].Songs.ConvertAll(x => x.title));
        }

        [Test]
        public void Build_Folder_PackOrdered_Songs_Come_Before_Unordered_Ones()
        {
            var list = new List<SongCatalog.Entry>
            {
                Song("ext_zk.gn", "Aaa", group: "NX"),          // packOrder 預設 -1
                PackSong("ext_ak.gn", "Zzz", "NX", 0),
                Song("ext_yk.gn", "Bbb", group: "NX"),
            };
            var b = SongGrouping.Build(list, SongGroupMode.Folder);
            CollectionAssert.AreEqual(new[] { "Zzz", "Aaa", "Bbb" }, b[0].Songs.ConvertAll(x => x.title));
        }

        [Test]
        public void Build_Title_Mode_Ignores_PackOrder()
        {
            // 使用者明確選「歌名」時就是歌名序 —— 包的順序只管 資料夾 模式。
            var list = new List<SongCatalog.Entry>
            {
                PackSong("ext_ak.gn", "Apple", "NX", 0),
                PackSong("ext_bk.gn", "Avocado", "NX", 9),
            };
            var b = SongGrouping.Build(list, SongGroupMode.Title);
            CollectionAssert.AreEqual(new[] { "Apple", "Avocado" }, b[0].Songs.ConvertAll(x => x.title));
        }

        [Test]
        public void Build_Folder_Bucket_Sorts_Songs_By_Title()
        {
            var b = SongGrouping.Build(Library(), SongGroupMode.Folder);
            CollectionAssert.AreEqual(new[] { "Butterfly", "Cross" }, b[0].Songs.ConvertAll(s => s.title));
            // K-POP: NUM before letters before CJK (OTHER)
            CollectionAssert.AreEqual(new[] { "2 Unlimited", "Apple", "危險的演出" }, b[1].Songs.ConvertAll(s => s.title));
        }

        [Test]
        public void Build_Null_Or_Empty_Is_Empty()
        {
            Assert.AreEqual(0, SongGrouping.Build(null, SongGroupMode.Folder).Count);
            Assert.AreEqual(0, SongGrouping.Build(new List<SongCatalog.Entry>(), SongGroupMode.Title).Count);
        }

        [Test]
        public void Build_Skips_Null_Entries()
        {
            var list = new List<SongCatalog.Entry> { null, Song("a.gn", "A", group: "G"), null };
            var b = SongGrouping.Build(list, SongGroupMode.Folder);
            Assert.AreEqual(1, b.Count);
            Assert.AreEqual(1, b[0].Count);
        }

        // ---- IndexOfKey: how the panel re-selects the bucket holding the last played song ----

        [Test]
        public void IndexOfKey_Finds_The_Bucket_Case_Insensitively()
        {
            var b = SongGrouping.Build(Library(), SongGroupMode.Folder);
            Assert.AreEqual(1, SongGrouping.IndexOfKey(b, "k-pop"));
            Assert.AreEqual(-1, SongGrouping.IndexOfKey(b, "Nope"));
            Assert.AreEqual(-1, SongGrouping.IndexOfKey(null, "Anime"));
        }

        [Test]
        public void IndexOfKey_Round_Trips_A_Songs_Own_Section()
        {
            var lib = Library();
            var song = lib[3];   // 危險的演出 (K-POP, artist 蔡妍, bpm 145)
            foreach (var mode in SongGrouping.Modes)
            {
                var buckets = SongGrouping.Build(lib, mode);
                int i = SongGrouping.IndexOfKey(buckets, SongGrouping.SectionName(song, mode));
                Assert.GreaterOrEqual(i, 0, "no bucket for mode " + mode);
                CollectionAssert.Contains(buckets[i].Songs, song, "song missing from its own bucket in mode " + mode);
            }
        }
    }
}
