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

        // The sidecar's per-song offset (ExternalSong.OffsetMs, read from sdo.header) must reach the catalog entry —
        // that's the field FrontendApp feeds into gameplay's songOffsetMs. Out-of-range values are clamped, not trusted.
        [Test]
        public void ToEntry_CarriesSidecarOffset_IntoCatalog_Clamped()
        {
            var song = new ExternalSong { FolderPath = "C:/x", Title = "t", OffsetMs = 57f };
            Assert.AreEqual(57f, ExternalSongLibrary.ToEntry(song, 0).offsetMs, 1e-3f);

            var wild = new ExternalSong { FolderPath = "C:/x", Title = "t", OffsetMs = 999999f };
            Assert.AreEqual(SongCatalog.MaxOffsetMs, ExternalSongLibrary.ToEntry(wild, 0).offsetMs, 1e-3f);

            var none = new ExternalSong { FolderPath = "C:/x", Title = "t" };
            Assert.AreEqual(0f, ExternalSongLibrary.ToEntry(none, 0).offsetMs);
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

        // 外部譜 lead-in 只在正式遊玩套用；編輯器回 0（音符留在真實音檔時間上），這是「打譜看到的秒數＝StepMania 的秒數」的關鍵。
        [Test]
        public void ExternalLeadIn_Skipped_In_Editor()
        {
            // gameplay：把第一顆音符推到 2000ms（count-in），好從邊緣捲進來。
            Assert.AreEqual(2000 - 537, ScreenGameplay.ExternalLeadInMsFor(false, 537));
            // editor：0 → 音符不動，第一顆留在 537ms（＝.sm 的 beat4 @ 140BPM, OFFSET 1.177）。
            Assert.AreEqual(0, ScreenGameplay.ExternalLeadInMsFor(true, 537));
            // 第一顆音符本來就晚於 lead-in → 兩種模式都不推（clamp 到 0，不會變成負的）。
            Assert.AreEqual(0, ScreenGameplay.ExternalLeadInMsFor(false, 3000));
            Assert.AreEqual(0, ScreenGameplay.ExternalLeadInMsFor(true, 3000));
        }

        // 端對端（純解析）：Be Crazy For Me 的 SM 標頭數字 → 第一顆音符必須落在 0.537s，套上「編輯器不 lead-in」後仍是 0.537s。
        // 這就是使用者回報「轉出來變 2.0s」的重現：gameplay 會 +1463ms → 2.0s；editor 不套 → 維持 0.537s。
        [Test]
        public void SmFirstNote_RealTime_Matches_StepMania_In_Editor()
        {
            // #OFFSET:1.177; #BPMS:0=140; 第一顆在 beat 4（第一個小節結尾）→ 4×(60/140)−1.177 = 0.537286s。
            // NOTES 欄位：StepsType:Description:Difficulty:Meter:Radar:NoteData（Description 空 → 只有一個冒號）。
            const string sm =
                "#OFFSET:1.177;\n#BPMS:0.000=140.000;\n" +
                "#NOTES:dance-single::Challenge:15:0,0,0,0,0:\n" +
                "0000\n0000\n0000\n0000\n,\n1000\n0000\n0000\n0000\n;";
            var map = SmChart.ToBeatmap(SmChart.Parse(sm), 0);
            Assert.AreEqual(1, map.HitObjects.Count);
            Assert.AreEqual(537, map.HitObjects[0].StartTimeMs);            // 真實音檔時間（≈0.537s）

            int firstMs = (int)map.FirstNoteMs;
            // 編輯器：lead-in = 0 → 音符停在 537ms（打譜看到的秒數＝StepMania）。
            Assert.AreEqual(0, ScreenGameplay.ExternalLeadInMsFor(true, firstMs));
            // 正式遊玩：lead-in > 0 → 套上後第一顆到 2000ms（＝使用者回報的 2.0s，遊玩本來就要的 count-in）。
            int gameLead = ScreenGameplay.ExternalLeadInMsFor(false, firstMs);
            Assert.AreEqual(2000, firstMs + gameLead);
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
        public void ToEntry_Time_Column_Is_The_Music_Files_Length()
        {
            // 時間 = how long the TRACK plays, not where the chart stops — so every difficulty of one song shows the
            // same time, even though their charts end at different points.
            var song = new ExternalSong { FolderPath = "C:/x", Title = "t", AudioDurationSec = 214 };
            song.Charts[0] = new ExternalChart { NoteCount = 10, DurationSec = 95 };
            song.Charts[2] = new ExternalChart { NoteCount = 40, DurationSec = 128 };
            var e = ExternalSongLibrary.ToEntry(song, 0);
            Assert.AreEqual(214, e.DurationSec(0));
            Assert.AreEqual(0, e.DurationSec(1), "empty slot stays blank");
            Assert.AreEqual(214, e.DurationSec(2));
        }

        [Test]
        public void ToEntry_Falls_Back_To_The_Chart_Length_When_The_Audio_Cant_Be_Measured()
        {
            var song = new ExternalSong { FolderPath = "C:/x", Title = "t", AudioDurationSec = 0 };   // unreadable/odd format
            song.Charts[2] = new ExternalChart { NoteCount = 40, DurationSec = 128 };
            Assert.AreEqual(128, ExternalSongLibrary.ToEntry(song, 0).DurationSec(2));
        }

        [Test]
        public void StatsOf_Duration_Is_The_Last_Notes_Time_In_Seconds()
        {
            var bm = new OsuBeatmap { Keys = 4 };
            bm.HitObjects.Add(new OsuHitObject(0, 1000));
            bm.HitObjects.Add(new OsuHitObject(1, 100_400, 125_600));   // hold tail = the real end of the chart
            Assert.AreEqual(126, ExternalSongScanner.StatsOf(bm, 0).DurationSec);
            Assert.AreEqual(7, ExternalSongScanner.StatsOf(null, 7).Level, "unparseable chart → fallback level, no duration");
            Assert.AreEqual(0, ExternalSongScanner.StatsOf(null, 7).DurationSec);
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

        // ---- what the 分類瀏覽 panel browses: the external pool, folder-grouped (SongGrouping owns the rules) ----

        private static SongCatalog.Entry Ext(string gn, string group, string title)
            => new SongCatalog.Entry { gn = gn, external = true, group = group, title = title, notesHard = 1 };

        private static List<SongBucket> FolderBuckets(List<SongCatalog.Entry> list)
            => SongGrouping.Build(SongListModel.Externals(list), SongGroupMode.Folder);

        [Test]
        public void Folder_Buckets_Are_Distinct_And_Sorted()
        {
            var list = new List<SongCatalog.Entry>
            {
                Ext("ext_1k.gn", "Beta", "t1"), Ext("ext_2k.gn", "Alpha", "t2"),
                Ext("ext_3k.gn", "Beta", "t3"), new SongCatalog.Entry { gn = "sdom1k.gn", title = "official" },
            };
            Assert.AreEqual(new[] { "Alpha", "Beta" }, FolderBuckets(list).ConvertAll(b => b.Key).ToArray());
        }

        [Test]
        public void Folder_Bucket_Holds_Its_Songs_Sorted_By_Title()
        {
            var list = new List<SongCatalog.Entry>
            {
                Ext("ext_1k.gn", "Beta", "Zeta"), Ext("ext_2k.gn", "Beta", "Alpha"), Ext("ext_3k.gn", "Alpha", "x"),
            };
            var beta = FolderBuckets(list)[1];
            Assert.AreEqual("Beta", beta.Key);
            Assert.AreEqual(2, beta.Count);
            Assert.AreEqual("Alpha", beta.Songs[0].title);
            Assert.AreEqual("Zeta", beta.Songs[1].title);
        }

        [Test]
        public void Non_External_Entries_Are_Not_Grouped()
        {
            var list = new List<SongCatalog.Entry>
            {
                new SongCatalog.Entry { gn = "sdom1k.gn", group = "X", title = "official" },   // group set but external=false
            };
            Assert.AreEqual(0, FolderBuckets(list).Count);   // official songs live in 全部, never in the panel
        }
    }
}
