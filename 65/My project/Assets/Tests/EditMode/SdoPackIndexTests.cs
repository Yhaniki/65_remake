using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// The <c>sdo_pack.tsv</c> reader (tools/nx/nx_to_gn.py writes it, the scanner consumes it). What matters is that
    /// it stays readable across versions: columns are matched BY NAME, so re-ordering them or adding new ones must not
    /// break an older game, and a row with junk in one cell must degrade rather than disappear.
    /// </summary>
    public class SdoPackIndexTests
    {
        private const string Header =
            "gn\tseed\tfileId\tbpm\tlvE\tlvN\tlvH\tnotesE\tnotesN\tnotesH\tdurE\tdurN\tdurH\taudio\tcd\tpreview\tdps\ttitle\tartist";

        private static string Pack(params string[] rows)
        {
            var text = "#sdo-pack/1\n# a comment\n\n" + Header + "\n";
            foreach (var r in rows) text += r + "\n";
            return text;
        }

        [Test]
        public void ParsesAFullRow()
        {
            var rows = SdoPackIndex.Parse(Pack(
                "sdom0040K.gn\t15860471\t10040\t137.7\t1\t4\t5\t141\t318\t357\t129\t129\t129\t" +
                "sdom0040.mp3\t../ICONS/10040.PNG\texper/10040.ogg\t../DANCE/10040.DPS\tSuper dancer\tSDO"));

            Assert.AreEqual(1, rows.Count);
            var s = rows[0];
            Assert.AreEqual("sdom0040K.gn", s.Gn);
            Assert.AreEqual(15860471u, s.Seed);
            Assert.AreEqual(10040, s.FileId);
            Assert.AreEqual(137.7, s.Bpm, 1e-6);
            CollectionAssert.AreEqual(new[] { 1, 4, 5 }, s.Levels);
            CollectionAssert.AreEqual(new[] { 141, 318, 357 }, s.Notes);
            CollectionAssert.AreEqual(new[] { 129, 129, 129 }, s.Durations);
            Assert.AreEqual("sdom0040.mp3", s.Audio);
            Assert.AreEqual("../ICONS/10040.PNG", s.Cd);
            Assert.AreEqual("exper/10040.ogg", s.Preview);
            Assert.AreEqual("../DANCE/10040.DPS", s.Dps);
            Assert.AreEqual("Super dancer", s.Title);
            Assert.AreEqual("SDO", s.Artist);
        }

        [Test]
        public void ColumnsAreMatchedByNameNotPosition()
        {
            // A future tool re-orders the columns and inserts one this build has never heard of.
            var text = "#sdo-pack/1\ntitle\tgn\twhatsThis\tseed\nHello\tsdom0001K.gn\tjunk\t42\n";
            var rows = SdoPackIndex.Parse(text);
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("sdom0001K.gn", rows[0].Gn);
            Assert.AreEqual("Hello", rows[0].Title);
            Assert.AreEqual(42u, rows[0].Seed);
        }

        [Test]
        public void SeedsAboveIntMaxSurvive()
        {
            // The LCG seed is a uint32 and routinely exceeds int.MaxValue — parsing it as int would wrap it to garbage
            // and the chart would silently fail to decrypt.
            var rows = SdoPackIndex.Parse("#p\ngn\tseed\nsdom0001K.gn\t4000000000\n");
            Assert.AreEqual(4000000000u, rows[0].Seed);
        }

        [Test]
        public void BadCellsFallBackInsteadOfDroppingTheRow()
        {
            var rows = SdoPackIndex.Parse("#p\ngn\tseed\tbpm\tnotesH\nsdom0001K.gn\tnope\t\t-5\n");
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(0u, rows[0].Seed);
            Assert.AreEqual(0.0, rows[0].Bpm);
            Assert.AreEqual(0, rows[0].Notes[2]);
        }

        [Test]
        public void RowsWithoutAChartAreSkipped()
        {
            var rows = SdoPackIndex.Parse("#p\ngn\tseed\n\t7\nsdom0001K.gn\t7\n");
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("sdom0001K.gn", rows[0].Gn);
        }

        [Test]
        public void GarbageInputIsAnEmptyList()
        {
            Assert.AreEqual(0, SdoPackIndex.Parse(null).Count);
            Assert.AreEqual(0, SdoPackIndex.Parse("").Count);
            Assert.AreEqual(0, SdoPackIndex.Parse("# only comments\n\n").Count);
        }

        [Test]
        public void ByGnLooksUpCaseInsensitively()
        {
            var map = SdoPackIndex.ByGn(SdoPackIndex.Parse(Pack(
                "sdom0040K.gn\t1\t10040\t120\t1\t2\t3\t10\t20\t30\t60\t60\t60\ta.mp3\t\t\t\tT\tA")));
            Assert.IsTrue(map.ContainsKey("SDOM0040k.GN"));
            Assert.AreEqual(10040, map["sdom0040k.gn"].FileId);
        }
    }
}
