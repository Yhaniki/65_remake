using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// The boot-scan cache: its signature (what makes a folder's parse result stale), the ExternalSong ⇄ record
    /// mapping (empty difficulty slots must survive), and the on-disk round-trip.
    /// </summary>
    public class ExternalScanCacheTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sdo_cache_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        private void Write(string name, string content) => File.WriteAllText(Path.Combine(_dir, name), content);

        // ---- signature ----

        [Test]
        public void Signature_Is_Stable_For_Unchanged_Source_Files()
        {
            Write("a.osu", "chart");
            Write("a.mp3", "audio");
            var sig = ExternalScanCache.Signature(_dir);
            Assert.IsNotEmpty(sig);
            Assert.AreEqual(sig, ExternalScanCache.Signature(_dir), "same files → same token");
        }

        [Test]
        public void Signature_Changes_When_A_Chart_Changes()
        {
            Write("a.osu", "chart");
            Write("a.mp3", "audio");
            var before = ExternalScanCache.Signature(_dir);
            Write("a.osu", "chart EDITED — now longer");   // size differs → token differs even at the same mtime
            Assert.AreNotEqual(before, ExternalScanCache.Signature(_dir));
        }

        [Test]
        public void Generated_Files_Do_Not_Invalidate_The_Signature()
        {
            Write("a.osu", "chart");
            Write("a.mp3", "audio");
            var clean = ExternalScanCache.Signature(_dir);

            // composing the disc / building the dance drops these into the SAME folder — they must be ignored, or the
            // very song you just played would re-parse on the next boot.
            Write(SongSidecar.FileName, "#CDIMAGE:cd.png;");
            Write("cd.png", "disc");
            Write("cd_slug_deadbeef.png", "multi-song disc");
            Write("dance.dps", "choreo");
            Write("dance_slug_deadbeef.dps", "choreo2");
            Assert.AreEqual(clean, ExternalScanCache.Signature(_dir));
        }

        [Test]
        public void Signature_Is_Empty_For_A_Folder_With_No_Scannable_Files()
        {
            Assert.AreEqual("", ExternalScanCache.Signature(_dir));
            Assert.AreEqual("", ExternalScanCache.Signature(Path.Combine(_dir, "does_not_exist")));
        }

        // ---- ExternalSong ⇄ record round-trip ----

        [Test]
        public void ToFolder_FromFolder_Preserves_Songs_And_Empty_Slots()
        {
            var song = new ExternalSong
            {
                Group = "MyGroup", FolderPath = _dir, SongKey = "audio:a.mp3",
                Title = "Track", Artist = "Tester", Bpm = 175.5, Format = SongFormat.Osu,
                AudioPath = Path.Combine(_dir, "a.mp3"), AudioDurationSec = 0, ImagePath = Path.Combine(_dir, "bg.jpg"),
                CdImagePath = Path.Combine(_dir, "cd.png"), PreviewStartMs = 1234, PreviewLengthMs = 20000,
            };
            song.Charts[0] = new ExternalChart { FilePath = "e.osu", ChartIndex = 0, NoteCount = 100, Level = 8, DurationSec = 90 };
            song.Charts[2] = new ExternalChart { FilePath = "h.osu", ChartIndex = 0, NoteCount = 900, Level = 22, DurationSec = 128 };
            // Charts[1] stays null (empty normal slot)

            var folder = ExternalScanCache.ToFolder(_dir, "SIG", new List<ExternalSong> { song });
            Assert.AreEqual("MyGroup", folder.group, "group is taken from the songs");

            var back = ExternalScanCache.FromFolder(folder);
            Assert.AreEqual(1, back.Count);
            var s = back[0];
            Assert.AreEqual("MyGroup", s.Group);
            Assert.AreEqual("Track", s.Title);
            Assert.AreEqual(175.5, s.Bpm, 1e-9);
            Assert.AreEqual(SongFormat.Osu, s.Format);
            Assert.AreEqual(1234, s.PreviewStartMs);
            Assert.AreEqual(Path.Combine(_dir, "cd.png"), s.CdImagePath);

            Assert.IsNotNull(s.Charts[0]);
            Assert.AreEqual(900, s.Charts[2].NoteCount);
            Assert.AreEqual(22, s.Charts[2].Level);
            Assert.IsNull(s.Charts[1], "an empty difficulty slot must round-trip as null, not a zero-note chart");
        }

        // ---- on-disk round-trip ----

        [Test]
        public void Save_Then_Load_Round_Trips_The_Cache_File()
        {
            var folder = ExternalScanCache.ToFolder("C:/lib/song", "SIGVAL", new List<ExternalSong>
            {
                new ExternalSong { Group = "G", FolderPath = "C:/lib/song", Title = "T" },
            });
            var path = Path.Combine(_dir, "cache.json");
            ExternalScanCache.Save(path, new List<ExternalScanCache.Folder> { folder }, "osu");

            var map = ExternalScanCache.Load(path, "osu");
            Assert.IsTrue(map.ContainsKey("C:/lib/song"));
            Assert.AreEqual("SIGVAL", map["C:/lib/song"].sig);
            Assert.AreEqual("G", map["C:/lib/song"].group);
            Assert.AreEqual("T", map["C:/lib/song"].songs[0].title);
        }

        [Test]
        public void Load_Of_A_Missing_Or_Corrupt_File_Is_Empty_Not_A_Throw()
        {
            Assert.AreEqual(0, ExternalScanCache.Load(Path.Combine(_dir, "nope.json"), "osu").Count);
            var bad = Path.Combine(_dir, "bad.json");
            File.WriteAllText(bad, "{ this is not valid json ][");
            Assert.AreEqual(0, ExternalScanCache.Load(bad, "osu").Count);
        }

        [Test]
        public void Load_Is_Cold_When_The_Difficulty_Calc_Changed()
        {
            // 分槽是掃描期用當時那套算法決定的（哪三張譜留下、誰是困難）→ 換一套，整份快取就不能用了。
            // 重掃只有換算法後那一次；沒換就照常命中。
            var folder = ExternalScanCache.ToFolder("C:/lib/song", "SIGVAL", new List<ExternalSong>
            {
                new ExternalSong { Group = "G", FolderPath = "C:/lib/song", Title = "T" },
            });
            var path = Path.Combine(_dir, "calc.json");
            ExternalScanCache.Save(path, new List<ExternalScanCache.Folder> { folder }, "osu");

            Assert.AreEqual(1, ExternalScanCache.Load(path, "osu").Count, "同一套算法 → 快取照用");
            Assert.AreEqual(0, ExternalScanCache.Load(path, "minacalc").Count, "換算法 → 冷快取重掃一次");

            ExternalScanCache.Save(path, new List<ExternalScanCache.Folder> { folder }, "minacalc");
            Assert.AreEqual(1, ExternalScanCache.Load(path, "minacalc").Count, "重掃後就換這套命中");
            Assert.AreEqual(0, ExternalScanCache.Load(path, "osu").Count);
        }

        [Test]
        public void Load_Of_A_Pre_Calc_Cache_File_Is_Cold()
        {
            // 這版之前寫的快取檔沒有 calc 欄位 → JsonUtility 讀成 ""，跟任何一套都不相等 → 重掃一次補上。
            var path = Path.Combine(_dir, "legacy.json");
            File.WriteAllText(path, "{\"version\":" + ExternalScanCache.Version +
                                    ",\"folders\":[{\"path\":\"C:/lib/song\",\"sig\":\"S\",\"group\":\"G\",\"songs\":[]}]}");
            Assert.AreEqual(0, ExternalScanCache.Load(path, "osu").Count);
        }
    }
}
