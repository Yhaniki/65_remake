using System.IO;
using System.Text;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// Scanning a converted SDO song pack — the [NX]Patch layout after tools/nx/nx_to_gn.py has run over it:
    /// <code>
    ///   &lt;pack&gt;/patch music/sdom0040K.gn      charts (one file = all three difficulties)
    ///   &lt;pack&gt;/patch music/sdom0040.mp3      the track
    ///   &lt;pack&gt;/patch music/exper/10040.ogg   官方試聽短檔
    ///   &lt;pack&gt;/patch music/sdo_pack.tsv      seeds + UTF-8 歌名 + 上面那些東西在哪
    ///   &lt;pack&gt;/patch Datas/UI/MUSIC/ICONS/10040.PNG   CD 圖
    /// </code>
    /// These build that tree for real (the scanner is the one IO-bound class here) and check the three things a pack
    /// gives you that a bare chart folder can't: the CD art, the preview clip, and the decryption key.
    /// </summary>
    public class SdoPackScanTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "sdo_pack_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* temp dir */ }
        }

        private string Dir(params string[] parts)
        {
            var path = _root;
            foreach (var p in parts) path = Path.Combine(path, p);
            Directory.CreateDirectory(path);
            return path;
        }

        private static void Blob(string path, int bytes = 16)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[bytes]);
        }

        /// <summary>Build the pack tree; returns its music folder. <paramref name="withSidecar"/> false = a pack the
        /// converter never ran on (bare .gn + music), which must still scan.</summary>
        private string Pack(bool withSidecar = true, bool withIcon = true, bool withPreview = true, bool withDps = true)
        {
            string music = Dir("patch music");
            File.WriteAllBytes(Path.Combine(music, "sdom0040K.gn"),
                GnHeaderTests.Gn(fileId: 10040, bpm: 137.7f, levels: new[] { 1, 4, 5 }, notes: new[] { 141, 318, 357 }));
            Blob(Path.Combine(music, "sdom0040.mp3"), 2048);
            if (withPreview) Blob(Path.Combine(Dir("patch music", "exper"), "10040.ogg"));
            if (withIcon) Blob(Path.Combine(Dir("patch Datas", "UI", "MUSIC", "ICONS"), "10040.PNG"));
            if (withDps) Blob(Path.Combine(Dir("patch Datas", "DANCE"), "10040.DPS"));
            if (withSidecar)
            {
                var sb = new StringBuilder();
                sb.Append("#sdo-pack/1\n");
                sb.Append("gn\tseed\tfileId\tbpm\tlvE\tlvN\tlvH\tnotesE\tnotesN\tnotesH\tdurE\tdurN\tdurH")
                  .Append("\taudio\tcd\tpreview\tdps\ttitle\tartist\n");
                sb.Append("sdom0040K.gn\t15860471\t10040\t137.7\t1\t4\t5\t141\t318\t357\t129\t129\t129\tsdom0040.mp3\t")
                  .Append(withIcon ? "../patch Datas/UI/MUSIC/ICONS/10040.PNG" : "").Append('\t')
                  .Append(withPreview ? "exper/10040.ogg" : "").Append('\t')
                  .Append(withDps ? "../patch Datas/DANCE/10040.DPS" : "").Append('\t')
                  .Append("Super dancer\tSDO\n");
                File.WriteAllText(Path.Combine(music, SdoPackIndex.FileName), sb.ToString(), Encoding.UTF8);
            }
            return music;
        }

        [Test]
        public void PackSongCarriesItsKeyArtPreviewAndDance()
        {
            var songs = ExternalSongScanner.LoadFolder("NX Patch", Pack());

            Assert.AreEqual(1, songs.Count);
            var s = songs[0];
            Assert.AreEqual(SongFormat.Gn, s.Format);
            Assert.AreEqual("Super dancer", s.Title);       // UTF-8 from the sidecar — the .gn's own title is GB2312
            Assert.AreEqual("SDO", s.Artist);
            Assert.AreEqual(10040, s.FileId);
            Assert.AreEqual(15860471u, s.GnSeed);           // without this the chart can't be decrypted at all
            Assert.AreEqual(137.7, s.Bpm, 1e-3);
            StringAssert.EndsWith("sdom0040.mp3", s.AudioPath);
            StringAssert.EndsWith("10040.PNG", s.CdImagePath);
            StringAssert.EndsWith("10040.ogg", s.PreviewAudioPath);
            StringAssert.EndsWith("10040.DPS", s.DpsPath);
        }

        [Test]
        public void OneChartFillsAllThreeDifficultySlots()
        {
            var s = ExternalSongScanner.LoadFolder("NX Patch", Pack())[0];
            for (int d = 0; d < 3; d++)
            {
                Assert.IsNotNull(s.Charts[d], "slot " + d);
                StringAssert.EndsWith("sdom0040K.gn", s.Charts[d].FilePath);
                Assert.AreEqual(d, s.Charts[d].ChartIndex, "the difficulty IS the chart index for .gn");
            }
            Assert.AreEqual(141, s.Charts[0].NoteCount);
            Assert.AreEqual(318, s.Charts[1].NoteCount);
            Assert.AreEqual(357, s.Charts[2].NoteCount);
            CollectionAssert.AreEqual(new[] { 1, 4, 5 }, new[] { s.Charts[0].Level, s.Charts[1].Level, s.Charts[2].Level });
        }

        [Test]
        public void EmptyDifficultiesLeaveTheirSlotEmpty()
        {
            // Several official songs really do ship a difficulty with no notes — that row must grey out, not play silence.
            string music = Dir("patch music");
            File.WriteAllBytes(Path.Combine(music, "sdom2140K.gn"),
                GnHeaderTests.Gn(fileId: 12140, notes: new[] { 3417, 0, 0 }));
            Blob(Path.Combine(music, "sdom2140.mp3"), 2048);

            var s = ExternalSongScanner.LoadFolder("NX Patch", music)[0];
            Assert.IsNotNull(s.Charts[0]);
            Assert.IsNull(s.Charts[1]);
            Assert.IsNull(s.Charts[2]);
        }

        [Test]
        public void ABarePackWithoutTheSidecarStillScans()
        {
            // No sdo_pack.tsv: the numbers come from the .gn's own header and the track is found by the engine's
            // chart↔music naming rule (sdom0040K.gn → sdom0040.*), even though the pack ships .mp3 not .ogg.
            var songs = ExternalSongScanner.LoadFolder("NX Patch", Pack(withSidecar: false));

            Assert.AreEqual(1, songs.Count);
            var s = songs[0];
            Assert.AreEqual(SongFormat.Gn, s.Format);
            Assert.AreEqual("sdom0040K", s.Title);          // no UTF-8 title available → the chart's own name
            Assert.AreEqual(10040, s.FileId);
            Assert.AreEqual(0u, s.GnSeed);                  // unknown → the runtime falls back to the shared seed pool
            StringAssert.EndsWith("sdom0040.mp3", s.AudioPath);
            Assert.AreEqual(357, s.Charts[2].NoteCount);
        }

        [Test]
        public void AChartWithNoTrackIsNotASong()
        {
            string music = Dir("patch music");
            File.WriteAllBytes(Path.Combine(music, "sdom0040K.gn"), GnHeaderTests.Gn());
            Assert.AreEqual(0, ExternalSongScanner.LoadFolder("NX Patch", music).Count);
        }

        [Test]
        public void PackKeepsTheGroupItWasFoundUnderNotItsMusicFolderName()
        {
            // A pack's music folder is multi-song by construction and named nothing useful ("patch music"), so the
            // browse tab must stay the pack the player dropped in — unlike an osu multi-set folder, which renames.
            string music = Pack();
            File.WriteAllBytes(Path.Combine(music, "sdom0063K.gn"), GnHeaderTests.Gn(fileId: 10063));
            File.WriteAllBytes(Path.Combine(music, "sdom0063.mp3"), new byte[2048]);

            var songs = ExternalSongScanner.LoadFolder("NX Patch", music);
            Assert.AreEqual(2, songs.Count);
            foreach (var s in songs) Assert.AreEqual("NX Patch", s.Group);
        }

        [Test]
        public void AbsolutePathsInTheSidecarResolveToo()
        {
            // Art/dance the pack didn't ship gets filled in from the game's own DATA tree, which usually sits nowhere
            // near the Songs folder — the converter writes those as ABSOLUTE paths (a ../../../../.. chain would break
            // the moment Songs/ moved). Path.Combine must let the absolute one win.
            string music = Dir("patch music");
            File.WriteAllBytes(Path.Combine(music, "sdom0040K.gn"), GnHeaderTests.Gn());
            Blob(Path.Combine(music, "sdom0040.mp3"), 2048);
            string far = Dir("elsewhere", "ICONS");
            Blob(Path.Combine(far, "10040.PNG"));
            File.WriteAllText(Path.Combine(music, SdoPackIndex.FileName),
                "#sdo-pack/1\ngn\tfileId\tnotesH\taudio\tcd\n" +
                "sdom0040K.gn\t10040\t357\tsdom0040.mp3\t" + Path.Combine(far, "10040.PNG").Replace('\\', '/') + "\n",
                Encoding.UTF8);

            var s = ExternalSongScanner.LoadFolder("NX Patch", music)[0];
            Assert.AreEqual(Path.Combine(far, "10040.PNG"), s.CdImagePath);
        }

        [Test]
        public void ReapplySidecarKeepsThePacksOwnJacket()
        {
            // Cache-hit path: the CD art comes from sdo_pack.tsv, not from sdo.header, and must survive the refresh
            // — otherwise every re-boot would show the whole pack with the NONE disc.
            var s = ExternalSongScanner.LoadFolder("NX Patch", Pack())[0];
            string jacket = s.CdImagePath;
            Assert.IsNotEmpty(jacket);

            ExternalSongScanner.ReapplySidecar(s);
            Assert.AreEqual(jacket, s.CdImagePath);
        }
    }
}
