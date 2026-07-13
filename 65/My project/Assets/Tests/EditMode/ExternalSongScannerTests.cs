using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// The scanner is the one IO-bound class in Sdo.Osu, so unlike the rest of the suite these tests build a real
    /// folder tree under the temp dir and delete it again. They cover what the pure helpers can't: how a folder is
    /// split into songs, and how deeply nested song folders are found.
    /// </summary>
    public class ExternalSongScannerTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "sdo_scan_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* temp dir */ }
        }

        // ---- fixtures ----

        private string Dir(params string[] parts)
        {
            var path = _root;
            foreach (var p in parts) path = Path.Combine(path, p);
            Directory.CreateDirectory(path);
            return path;
        }

        private static void Osu(string dir, string file, string audio, string title, int notes,
            string version = "Hard", string bg = "", int keys = 4, int mode = 3, int setId = -1)
        {
            var sb = new StringBuilder();
            sb.Append("osu file format v14\n\n[General]\n");
            sb.Append("AudioFilename: ").Append(audio).Append('\n');
            sb.Append("PreviewTime: 1000\n");
            sb.Append("Mode: ").Append(mode).Append("\n\n[Metadata]\n");
            sb.Append("Title:").Append(title).Append('\n');
            sb.Append("Artist:Tester\n");
            sb.Append("Version:").Append(version).Append('\n');
            sb.Append("BeatmapSetID:").Append(setId).Append("\n\n[Difficulty]\n");
            sb.Append("CircleSize:").Append(keys).Append("\n\n[Events]\n");
            if (bg.Length > 0) sb.Append("0,0,\"").Append(bg).Append("\",0,0\n");
            sb.Append("\n[TimingPoints]\n0,500,4,2,0,100,1,0\n\n[HitObjects]\n");
            for (int i = 0; i < notes; i++)
                sb.Append(64 + (i % 4) * 128).Append(",192,").Append(500 + i * 250).Append(",1,0,0:0:0:0:\n");
            File.WriteAllText(Path.Combine(dir, file), sb.ToString());
        }

        private static void Sm(string dir, string file, string music, string title)
        {
            File.WriteAllText(Path.Combine(dir, file),
                "#TITLE:" + title + ";\n#ARTIST:Tester;\n#MUSIC:" + music + ";\n#OFFSET:0.000;\n" +
                "#BPMS:0.000=120.000;\n#NOTES:\n     dance-single:\n     :\n     Hard:\n     8:\n" +
                "     0,0,0,0,0:\n1000\n0100\n0010\n0001\n;\n");
        }

        private static void Audio(string dir, string file) => File.WriteAllBytes(Path.Combine(dir, file), new byte[] { 0 });

        // ---- one folder, several songs ----

        [Test]
        public void Two_Sets_In_One_Folder_Are_Two_Songs_Each_With_Its_Own_Top_Three()
        {
            var dir = Dir("pack", "mixed");
            Audio(dir, "a.mp3"); Audio(dir, "b.mp3");
            Osu(dir, "a1.osu", "a.mp3", "Song A", 100, "Easy");
            Osu(dir, "a2.osu", "a.mp3", "Song A", 400, "Normal");
            Osu(dir, "a3.osu", "a.mp3", "Song A", 900, "Hard");
            Osu(dir, "a4.osu", "a.mp3", "Song A", 50, "Beginner");   // 4th chart of A — dropped, not spilled into B
            Osu(dir, "b1.osu", "b.mp3", "Song B", 700, "Hard");
            Osu(dir, "b2.osu", "b.mp3", "Song B", 300, "Normal");

            var songs = ExternalSongScanner.LoadFolder("pack", dir);
            Assert.AreEqual(2, songs.Count);

            var a = songs.Find(s => s.Title == "Song A");
            var b = songs.Find(s => s.Title == "Song B");
            Assert.IsNotNull(a); Assert.IsNotNull(b);

            // A: its own three highest note counts (900/400/100), the 50-note chart dropped.
            Assert.AreEqual(900, a.Charts[2].NoteCount);
            Assert.AreEqual(400, a.Charts[1].NoteCount);
            Assert.AreEqual(100, a.Charts[0].NoteCount);
            Assert.AreEqual(Path.Combine(dir, "a.mp3"), a.AudioPath);

            // B: ranked among B's charts only — B's 700 is hard even though A has a 900.
            Assert.AreEqual(700, b.Charts[2].NoteCount);
            Assert.AreEqual(300, b.Charts[1].NoteCount);
            Assert.IsNull(b.Charts[0], "only two charts → easy stays empty (greyed row)");
            Assert.AreEqual(Path.Combine(dir, "b.mp3"), b.AudioPath);
        }

        [Test]
        public void Songs_In_One_Folder_Get_Distinct_Song_Keys()
        {
            var dir = Dir("pack", "mixed");
            Audio(dir, "a.mp3"); Audio(dir, "b.mp3");
            Osu(dir, "a1.osu", "a.mp3", "Song A", 100);
            Osu(dir, "b1.osu", "b.mp3", "Song B", 100);

            var songs = ExternalSongScanner.LoadFolder("pack", dir);
            Assert.AreEqual(2, songs.Count);
            Assert.AreEqual("audio:a.mp3", songs[0].SongKey);
            Assert.AreEqual("audio:b.mp3", songs[1].SongKey);
            Assert.AreNotEqual(songs[0].SongKey, songs[1].SongKey, "the gn hashes this — equal keys would drop a song");
        }

        [Test]
        public void Sole_Song_Folder_Keeps_An_Empty_Song_Key()
        {
            var dir = Dir("pack", "one");
            Audio(dir, "a.mp3");
            Osu(dir, "a1.osu", "a.mp3", "Song A", 100);
            Osu(dir, "a2.osu", "a.mp3", "Song A", 300);

            var songs = ExternalSongScanner.LoadFolder("pack", dir);
            Assert.AreEqual(1, songs.Count);
            Assert.AreEqual("", songs[0].SongKey, "one song per folder → gn stays the plain folder hash (favourites survive)");
        }

        [Test]
        public void A_Song_Never_Borrows_Another_Songs_Audio()
        {
            var dir = Dir("pack", "broken");
            Audio(dir, "a.mp3");                                   // b.mp3 is missing from disk
            Osu(dir, "a1.osu", "a.mp3", "Song A", 100);
            Osu(dir, "b1.osu", "b.mp3", "Song B", 100);

            var songs = ExternalSongScanner.LoadFolder("pack", dir);
            Assert.AreEqual(1, songs.Count, "Song B has no music of its own → dropped, never handed Song A's");
            Assert.AreEqual("Song A", songs[0].Title);
            Assert.AreEqual(Path.Combine(dir, "a.mp3"), songs[0].AudioPath);
        }

        [Test]
        public void A_Sole_Song_Still_Loads_When_Its_Chart_Names_The_Audio_Wrongly()
        {
            var dir = Dir("pack", "renamed");
            Audio(dir, "actual.ogg");
            Sm(dir, "song.sm", "old-name.ogg", "Song");   // the audio got renamed after the chart was written

            var songs = ExternalSongScanner.LoadFolder("pack", dir);
            Assert.AreEqual(1, songs.Count);
            Assert.AreEqual(Path.Combine(dir, "actual.ogg"), songs[0].AudioPath, "one song in the folder → the one audio file is unambiguous");
        }

        [Test]
        public void Each_Song_Gets_Its_Own_Cover()
        {
            var dir = Dir("pack", "covers");
            Audio(dir, "a.mp3"); Audio(dir, "b.mp3");
            File.WriteAllBytes(Path.Combine(dir, "bg_a.jpg"), new byte[] { 0 });
            File.WriteAllBytes(Path.Combine(dir, "bg_b.jpg"), new byte[] { 0 });
            Osu(dir, "a1.osu", "a.mp3", "Song A", 100, "Hard", "bg_a.jpg");
            Osu(dir, "b1.osu", "b.mp3", "Song B", 100, "Hard", "bg_b.jpg");

            var songs = ExternalSongScanner.LoadFolder("pack", dir);
            Assert.AreEqual(Path.Combine(dir, "bg_a.jpg"), songs.Find(s => s.Title == "Song A").ImagePath);
            Assert.AreEqual(Path.Combine(dir, "bg_b.jpg"), songs.Find(s => s.Title == "Song B").ImagePath);
        }

        [Test]
        public void Same_Titled_Songs_In_One_Folder_Are_Disambiguated()
        {
            var dir = Dir("pack", "nightcore");
            Audio(dir, "a.mp3"); Audio(dir, "a_sped.mp3");
            Osu(dir, "a1.osu", "a.mp3", "Same", 100, "Normal");
            Osu(dir, "a2.osu", "a_sped.mp3", "Same", 100, "Nightcore");

            var songs = ExternalSongScanner.LoadFolder("pack", dir);
            Assert.AreEqual(2, songs.Count);
            Assert.AreEqual("Same (Normal)", songs[0].Title);
            Assert.AreEqual("Same (Nightcore)", songs[1].Title);
        }

        // ---- StepMania ----

        [Test]
        public void Several_Sm_Files_In_One_Folder_Are_Several_Songs()
        {
            var dir = Dir("pack", "smpack");
            Audio(dir, "one.ogg"); Audio(dir, "two.ogg");
            Sm(dir, "one.sm", "one.ogg", "One");
            Sm(dir, "two.sm", "two.ogg", "Two");

            var songs = ExternalSongScanner.LoadFolder("pack", dir);
            Assert.AreEqual(2, songs.Count, "sm[0] used to win and the rest were dropped");
            Assert.AreEqual("One", songs[0].Title);
            Assert.AreEqual("Two", songs[1].Title);
            Assert.AreEqual("file:one.sm", songs[0].SongKey);
            Assert.AreEqual(Path.Combine(dir, "two.ogg"), songs[1].AudioPath);
        }

        [Test]
        public void An_Osu_Shadows_The_Sm_Of_The_Same_Song()
        {
            var dir = Dir("pack", "both");
            Audio(dir, "song.ogg");
            Osu(dir, "chart.osu", "song.ogg", "Song", 300);
            Sm(dir, "chart.sm", "song.ogg", "Song");

            var songs = ExternalSongScanner.LoadFolder("pack", dir);
            Assert.AreEqual(1, songs.Count, "same audio → one song; the .osu wins as before");
            Assert.AreEqual(SongFormat.Osu, songs[0].Format);
        }

        [Test]
        public void An_Sm_Of_A_Different_Song_Survives_Next_To_An_Osu()
        {
            var dir = Dir("pack", "both2");
            Audio(dir, "a.ogg"); Audio(dir, "b.ogg");
            Osu(dir, "a.osu", "a.ogg", "Song A", 300);
            Sm(dir, "b.sm", "b.ogg", "Song B");

            var songs = ExternalSongScanner.LoadFolder("pack", dir);
            Assert.AreEqual(2, songs.Count);
            Assert.AreEqual(SongFormat.Sm, songs.Find(s => s.Title == "Song B").Format);
        }

        // ---- discovery ----

        [Test]
        public void Song_Folders_Nested_Inside_A_Group_Are_Found()
        {
            var deep = Dir("MyPack", "4K Vol.1", "Song A");   // pack folders add a level (or two)
            Audio(deep, "a.mp3");
            Osu(deep, "a.osu", "a.mp3", "Deep Song", 100);
            var shallow = Dir("MyPack", "Song B");
            Audio(shallow, "b.mp3");
            Osu(shallow, "b.osu", "b.mp3", "Shallow Song", 100);

            var work = ExternalSongScanner.BuildWorklist(new List<string> { _root });
            Assert.AreEqual(2, work.Count);
            foreach (var w in work) Assert.AreEqual("MyPack", w.Group, "the group is the folder under the root, however deep the song sits");

            var songs = ExternalSongScanner.Scan(new List<string> { _root });
            Assert.AreEqual(2, songs.Count);
        }

        [Test]
        public void An_Editor_Backup_Subfolder_Is_Not_A_Song()
        {
            // StepMania/ArrowVortex autosave into <song>/FileBackup/ — dozens of .sm files with no audio next to them.
            var dir = Dir("Group", "Song");
            Audio(dir, "a.mp3");
            Sm(dir, "song.sm", "a.mp3", "Song");
            var backup = Dir("Group", "Song", "FileBackup");
            Sm(backup, "2024-05-29_214123.sm", "a.mp3", "Song");
            Sm(backup, "2024-05-29_214625.sm", "a.mp3", "Song");

            var work = ExternalSongScanner.BuildWorklist(new List<string> { _root });
            Assert.AreEqual(1, work.Count, "the folder holding the charts IS the song — its subfolders are assets");
            Assert.AreEqual(dir, work[0].Path);
            Assert.AreEqual(1, ExternalSongScanner.Scan(new List<string> { _root }).Count);
        }

        [Test]
        public void A_Stray_Chart_At_Pack_Level_Does_Not_Hide_The_Songs_Below_It()
        {
            // A chart file left lying in a pack folder must not make the whole pack "the song" and swallow its songs.
            var pack = Dir("Group", "Pack");
            Osu(pack, "stray.osu", "gone.mp3", "Stray", 100);      // orphan: its audio isn't there → not a song
            var one = Dir("Group", "Pack", "Song A");
            Audio(one, "a.mp3"); Osu(one, "a.osu", "a.mp3", "Song A", 100);
            var two = Dir("Group", "Pack", "Song B");
            Audio(two, "b.mp3"); Osu(two, "b.osu", "b.mp3", "Song B", 100);

            var songs = ExternalSongScanner.Scan(new List<string> { _root });
            Assert.AreEqual(2, songs.Count);
            Assert.AreEqual("Song A", songs[0].Title);
            Assert.AreEqual("Song B", songs[1].Title);
        }

        [Test]
        public void An_Audio_Named_With_A_Folder_Prefix_Still_Resolves()
        {
            var dir = Dir("Group", "Song");
            Audio(dir, "a.mp3");
            Osu(dir, "a.osu", "sb\\a.mp3", "Song", 100);   // some charts spell the audio with a path

            var songs = ExternalSongScanner.LoadFolder("Group", dir);
            Assert.AreEqual(1, songs.Count);
            Assert.AreEqual(Path.Combine(dir, "a.mp3"), songs[0].AudioPath);
        }

        [Test]
        public void Folders_Without_Charts_Are_Not_Song_Folders()
        {
            Dir("Empty", "no charts here");
            var dir = Dir("Group", "Song");
            Audio(dir, "a.mp3");
            Osu(dir, "a.osu", "a.mp3", "Song", 100);

            var work = ExternalSongScanner.BuildWorklist(new List<string> { _root });
            Assert.AreEqual(1, work.Count);
            Assert.AreEqual(dir, work[0].Path);
        }

        [Test]
        public void Non_4K_And_Non_Mania_Charts_Are_Ignored()
        {
            var dir = Dir("Group", "Song");
            Audio(dir, "a.mp3");
            Osu(dir, "std.osu", "a.mp3", "Std", 100, "Hard", "", 4, 0);        // Mode 0 = osu!standard
            Osu(dir, "7k.osu", "a.mp3", "7K", 100, "Hard", "", 7);             // 7 keys
            Osu(dir, "ok.osu", "a.mp3", "Mania 4K", 100);

            var songs = ExternalSongScanner.LoadFolder("Group", dir);
            Assert.AreEqual(1, songs.Count);
            Assert.AreEqual("Mania 4K", songs[0].Title);
        }

        // ---- the sdo.header sidecar: the CD disc is built once, then read back ----

        private static void Img(string dir, string file) => File.WriteAllBytes(Path.Combine(dir, file), new byte[] { 0 });

        private static void Header(string dir, string text)
            => File.WriteAllText(Path.Combine(dir, SongSidecar.FileName), text);

        [Test]
        public void A_Recorded_Disc_Is_Handed_Back_So_It_Is_Never_Rebuilt()
        {
            var dir = Dir("Group", "Song");
            Audio(dir, "a.mp3");
            Osu(dir, "a.osu", "a.mp3", "Song", 100, "Hard", "bg.jpg");
            Img(dir, "bg.jpg"); Img(dir, "cd.png");
            Header(dir, "#VERSION:1;\n#SONG:;\n#CDIMAGE:cd.png;\n");

            var songs = ExternalSongScanner.LoadFolder("Group", dir);

            Assert.AreEqual(Path.Combine(dir, "cd.png"), songs[0].CdImagePath);
            Assert.AreEqual(Path.Combine(dir, "bg.jpg"), songs[0].ImagePath, "the source cover is still tracked");
        }

        [Test]
        public void A_Recorded_Disc_Whose_File_Is_Gone_Is_Rebuilt()
        {
            var dir = Dir("Group", "Song");
            Audio(dir, "a.mp3");
            Osu(dir, "a.osu", "a.mp3", "Song", 100, "Hard", "bg.jpg");
            Img(dir, "bg.jpg");
            Header(dir, "#SONG:;\n#CDIMAGE:cd.png;\n");   // …but the player deleted cd.png

            var songs = ExternalSongScanner.LoadFolder("Group", dir);

            Assert.AreEqual("", songs[0].CdImagePath, "deleting the disc must be all it takes to have it rebuilt");
        }

        [Test]
        public void A_Generated_Disc_Is_Never_Mistaken_For_The_Songs_Cover()
        {
            // The disc lives in the song folder, so on the next scan it is just another image next to the cover. With a
            // cover whose name carries no hint, the picker's "any image left" rule would otherwise hand the song its own
            // disc back — and the disc would then be rebuilt FROM the disc.
            var dir = Dir("Group", "Song");
            Audio(dir, "a.mp3");
            Osu(dir, "a.osu", "a.mp3", "Song", 100);   // no [Events] background → the cover is guessed from the folder
            Img(dir, "cd.png");                        // sorts BEFORE the real cover
            Img(dir, "zz_artwork.jpg");

            var songs = ExternalSongScanner.LoadFolder("Group", dir);

            Assert.AreEqual(Path.Combine(dir, "zz_artwork.jpg"), songs[0].ImagePath);
        }

        [Test]
        public void Each_Song_Of_A_Multi_Song_Folder_Gets_Its_Own_Disc()
        {
            // One osu folder, two beatmap sets: each song's disc is recorded under its own key, and neither song is
            // handed the other's (nor its own disc as a cover).
            var dir = Dir("Group", "Two");
            Audio(dir, "a.mp3"); Audio(dir, "b.mp3");
            Osu(dir, "a.osu", "a.mp3", "Song A", 100, "Hard", "a_bg.jpg");
            Osu(dir, "b.osu", "b.mp3", "Song B", 100, "Hard", "b_bg.jpg");
            Img(dir, "a_bg.jpg"); Img(dir, "b_bg.jpg");

            string cdA = SongSidecar.CdFileName("audio:a.mp3");
            string cdB = SongSidecar.CdFileName("audio:b.mp3");
            Img(dir, cdA); Img(dir, cdB);
            Header(dir, SongSidecar.Write(new List<SongSidecarEntry>
            {
                new SongSidecarEntry { SongKey = "audio:a.mp3", CdImage = cdA },
                new SongSidecarEntry { SongKey = "audio:b.mp3", CdImage = cdB },
            }));

            var songs = ExternalSongScanner.LoadFolder("Group", dir);

            var a = songs.Find(s => s.Title == "Song A");
            var b = songs.Find(s => s.Title == "Song B");
            Assert.AreEqual(Path.Combine(dir, cdA), a.CdImagePath);
            Assert.AreEqual(Path.Combine(dir, cdB), b.CdImagePath);
            Assert.AreEqual(Path.Combine(dir, "a_bg.jpg"), a.ImagePath);
            Assert.AreEqual(Path.Combine(dir, "b_bg.jpg"), b.ImagePath);
        }

        [Test]
        public void Reserved_Mot_And_Camera_Tags_Are_Read()
        {
            var dir = Dir("Group", "Song");
            Audio(dir, "a.mp3");
            Osu(dir, "a.osu", "a.mp3", "Song", 100);
            File.WriteAllBytes(Path.Combine(dir, "dance.mot"), new byte[] { 0 });
            Header(dir, "#SONG:;\n#MOT:dance.mot;\n#CAMERA:missing.cdt;\n");

            var songs = ExternalSongScanner.LoadFolder("Group", dir);

            Assert.AreEqual(Path.Combine(dir, "dance.mot"), songs[0].MotPath);
            Assert.AreEqual("", songs[0].CameraPath, "a named file that isn't there counts as absent");
        }
    }
}
