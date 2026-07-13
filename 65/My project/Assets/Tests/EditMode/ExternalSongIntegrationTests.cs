using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Osu;
using Sdo.UI.Catalog;

namespace Sdo.Tests
{
    public class ExternalSongIntegrationTests
    {
        // ---- ExternalSongLibrary.ToEntry (ExternalSong → SongCatalog.Entry) ----

        [Test]
        public void ToEntry_Sets_External_Fields_And_Slots()
        {
            var song = new ExternalSong
            {
                Group = "G", FolderPath = "C:/Songs/G/Foo", Title = "Foo", Artist = "Bar",
                Bpm = 140, Format = SongFormat.Osu, AudioPath = "C:/Songs/G/Foo/a.mp3", ImagePath = "C:/Songs/G/Foo/bg.jpg",
                PreviewStartMs = 45000, PreviewLengthMs = 0,
            };
            song.Charts[2] = new ExternalChart { FilePath = "hard.osu", ChartIndex = 0, NoteCount = 1000, Level = 0 };
            song.Charts[1] = new ExternalChart { FilePath = "norm.osu", ChartIndex = 0, NoteCount = 600, Level = 0 };

            var e = ExternalSongLibrary.ToEntry(song, 0);
            Assert.IsTrue(e.external);
            Assert.IsTrue(e.gn.EndsWith("k.gn"), "gn must end 'k.gn' to survive list curation");
            Assert.Less(e.fileId, 0, "external fileId must be negative (excluded from NEW/jacket loader)");
            Assert.AreEqual("G", e.group);
            Assert.AreEqual("Foo", e.title);
            Assert.AreEqual(1, e.chartFormat);
            Assert.AreEqual("C:/Songs/G/Foo/a.mp3", e.audioPath);
            Assert.AreEqual("C:/Songs/G/Foo/bg.jpg", e.imagePath);
            Assert.AreEqual(45000, e.previewStartMs);
            Assert.AreEqual(0, e.previewLengthMs);
            // slots: hard(1000) + normal(600) filled, easy empty → greyed.
            Assert.AreEqual(1000, e.NoteCount(2));
            Assert.AreEqual(600, e.NoteCount(1));
            Assert.AreEqual(0, e.NoteCount(0));
            Assert.IsTrue(e.HasChart(2));
            Assert.IsTrue(e.HasChart(1));
            Assert.IsFalse(e.HasChart(0));
            Assert.AreEqual("hard.osu", e.ChartPath(2));
            Assert.AreEqual("norm.osu", e.ChartPath(1));
            Assert.AreEqual("", e.ChartPath(0));
        }

        [Test]
        public void ApplyLeadIn_Shifts_Notes_Timing_And_Offset()
        {
            var bm = new OsuBeatmap { Keys = 4 };
            bm.HitObjects.Add(new OsuHitObject(0, 540));
            bm.HitObjects.Add(new OsuHitObject(1, 1000, 1500));
            bm.TimingPoints.Add(new OsuTimingPoint(0, 400));
            bm.ApplyLeadIn(1460);
            Assert.AreEqual(2000, bm.HitObjects[0].StartTimeMs);
            Assert.AreEqual(2460, bm.HitObjects[1].StartTimeMs);
            Assert.AreEqual(2960, bm.HitObjects[1].EndTimeMs);
            Assert.AreEqual(1460.0, bm.TimingPoints[0].TimeMs, 1e-9);
            Assert.AreEqual(1460.0, bm.MusicStartOffsetMs, 1e-9);
            bm.ApplyLeadIn(0);   // no-op
            Assert.AreEqual(2000, bm.HitObjects[0].StartTimeMs);
        }

        [Test]
        public void LastNoteMs_Is_The_End_Of_The_Last_Note()
        {
            // 生成舞蹈的區間 = 第一個 note → 最後一個 note（長條算到尾巴，即使它比後面的 tap 還晚結束）。
            var bm = new OsuBeatmap { Keys = 4 };
            Assert.AreEqual(0.0, bm.LastNoteMs, 1e-9, "空譜 → 0");
            bm.HitObjects.Add(new OsuHitObject(0, 1000));
            bm.HitObjects.Add(new OsuHitObject(1, 2000, 9000));   // 長條尾巴比最後一顆 tap 還晚
            bm.HitObjects.Add(new OsuHitObject(2, 5000));
            Assert.AreEqual(9000.0, bm.LastNoteMs, 1e-9);
            Assert.AreEqual(1000.0, bm.FirstNoteMs, 1e-9);
        }

        [Test]
        public void ToEntry_Gn_Is_Stable_Per_Song()
        {
            var a = ExternalSongLibrary.ToEntry(new ExternalSong { FolderPath = "C:/x" }, 0);
            var b = ExternalSongLibrary.ToEntry(new ExternalSong { FolderPath = "C:/x" }, 5);
            Assert.AreEqual(a.gn, b.gn, "gn depends on the song's identity, not the scan index");
        }

        [Test]
        public void ToEntry_Gn_Differs_Per_Song_In_The_Same_Folder()
        {
            // SongCatalog.RegisterExternal silently skips duplicate gns — two songs in one folder sharing a gn would
            // mean the second one just never appears in the list.
            var a = ExternalSongLibrary.ToEntry(new ExternalSong { FolderPath = "C:/x", SongKey = "audio:a.mp3" }, 0);
            var b = ExternalSongLibrary.ToEntry(new ExternalSong { FolderPath = "C:/x", SongKey = "audio:b.mp3" }, 1);
            Assert.AreNotEqual(a.gn, b.gn);
            var again = ExternalSongLibrary.ToEntry(new ExternalSong { FolderPath = "C:/x", SongKey = "audio:B.MP3" }, 7);
            Assert.AreEqual(b.gn, again.gn, "same song, later rescan → same gn, so favourites stick");
        }

        [Test]
        public void ToEntry_Gn_Of_A_Sole_Song_Is_The_Plain_Folder_Hash()
        {
            var sole = ExternalSongLibrary.ToEntry(new ExternalSong { FolderPath = "C:/x", SongKey = "" }, 0);
            var legacy = ExternalSongLibrary.ToEntry(new ExternalSong { FolderPath = "C:/x" }, 0);
            Assert.AreEqual(legacy.gn, sole.gn, "one-song folders keep the gn they had before multi-song folders existed");
        }

        // ---- SongListModel group helpers ----

        private static SongCatalog.Entry Ext(string gn, string group, string title)
            => new SongCatalog.Entry { gn = gn, external = true, group = group, title = title, notesHard = 1 };

        [Test]
        public void ExternalGroups_Distinct_And_Sorted()
        {
            var list = new List<SongCatalog.Entry>
            {
                Ext("ext_1k.gn", "Beta", "t1"), Ext("ext_2k.gn", "Alpha", "t2"),
                Ext("ext_3k.gn", "Beta", "t3"), new SongCatalog.Entry { gn = "sdom1k.gn", title = "official" },
            };
            var g = SongListModel.ExternalGroups(list);
            Assert.AreEqual(new[] { "Alpha", "Beta" }, g.ToArray());
        }

        [Test]
        public void InGroup_Filters_And_Sorts_By_Title()
        {
            var list = new List<SongCatalog.Entry>
            {
                Ext("ext_1k.gn", "Beta", "Zeta"), Ext("ext_2k.gn", "Beta", "Alpha"), Ext("ext_3k.gn", "Alpha", "x"),
            };
            var songs = SongListModel.InGroup(list, "Beta");
            Assert.AreEqual(2, songs.Count);
            Assert.AreEqual("Alpha", songs[0].title);
            Assert.AreEqual("Zeta", songs[1].title);
        }

        [Test]
        public void Non_External_Entries_Are_Not_Grouped()
        {
            var list = new List<SongCatalog.Entry>
            {
                new SongCatalog.Entry { gn = "sdom1k.gn", group = "X", title = "official" },   // group set but external=false
            };
            Assert.AreEqual(0, SongListModel.ExternalGroups(list).Count);
            Assert.AreEqual(0, SongListModel.InGroup(list, "X").Count);
        }
    }
}
