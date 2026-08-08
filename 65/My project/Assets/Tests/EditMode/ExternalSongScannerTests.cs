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
            ExternalSongScanner.CollapseShortHolds = false;   // 靜態掃描設定：別漏給下一個測試
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

        // A real, readable WAV of `seconds` at 44.1k stereo 16-bit — so a test can prove the scanner deliberately does
        // NOT read it (deferred to song-select), as opposed to it merely being an unreadable stub.
        private static void ValidWav(string dir, string file, int seconds)
        {
            const uint byteRate = 176400u;   // 44100 * 2ch * 2bytes
            uint dataBytes = byteRate * (uint)seconds;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms, Encoding.ASCII);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36u + dataBytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16u);
            w.Write((ushort)1); w.Write((ushort)2); w.Write(44100u); w.Write(byteRate); w.Write((ushort)4); w.Write((ushort)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataBytes); w.Write(new byte[dataBytes]);
            w.Flush();
            File.WriteAllBytes(Path.Combine(dir, file), ms.ToArray());
        }

        // ---- the scan reads only .osu/.sm: audio length is left for song-select ----

        [Test]
        public void Scan_Does_Not_Read_Audio_Length_It_Is_Deferred_To_Song_Select()
        {
            var dir = Dir("pack", "song");
            ValidWav(dir, "track.wav", 7);                       // a real 7s wav — readable, has a length
            Osu(dir, "chart.osu", "track.wav", "Song", 200);

            var songs = ExternalSongScanner.LoadFolder("pack", dir);
            Assert.AreEqual(1, songs.Count);
            Assert.AreEqual(Path.Combine(dir, "track.wav"), songs[0].AudioPath);

            // The file's length is genuinely readable (7s) — proving the 0 below is a deliberate deferral, not a stub.
            Assert.AreEqual(7, AudioDuration.Seconds(Path.Combine(dir, "track.wav")));
            Assert.AreEqual(0, songs[0].AudioDurationSec, "scan must not decode audio — 時間 falls back to chart length");
        }

        [Test]
        public void Osu_PreviewTime_Zero_Is_Missing_And_Does_Not_Hide_A_Positive_Difficulty()
        {
            var zeroDir = Dir("pack", "zero");
            Audio(zeroDir, "track.mp3");
            Osu(zeroDir, "zero.osu", "track.mp3", "Zero", 200);
            string zeroPath = Path.Combine(zeroDir, "zero.osu");
            File.WriteAllText(zeroPath, File.ReadAllText(zeroPath)
                .Replace("PreviewTime: 1000", "PreviewTime: 0"));

            var zeroSongs = ExternalSongScanner.LoadFolder("pack", zeroDir);
            Assert.AreEqual(1, zeroSongs.Count);
            Assert.AreEqual(-1, zeroSongs[0].PreviewStartMs,
                "zero is normalized to the automatic midpoint sentinel");

            var mixedDir = Dir("pack", "mixed-preview");
            Audio(mixedDir, "track.mp3");
            Osu(mixedDir, "a-zero.osu", "track.mp3", "Mixed", 100, "Easy");
            Osu(mixedDir, "b-positive.osu", "track.mp3", "Mixed", 200, "Hard");
            string mixedZeroPath = Path.Combine(mixedDir, "a-zero.osu");
            string mixedPositivePath = Path.Combine(mixedDir, "b-positive.osu");
            File.WriteAllText(mixedZeroPath, File.ReadAllText(mixedZeroPath)
                .Replace("PreviewTime: 1000", "PreviewTime: 0"));
            File.WriteAllText(mixedPositivePath, File.ReadAllText(mixedPositivePath)
                .Replace("PreviewTime: 1000", "PreviewTime: 5000"));

            var mixedSongs = ExternalSongScanner.LoadFolder("pack", mixedDir);
            Assert.AreEqual(1, mixedSongs.Count);
            Assert.AreEqual(5000, mixedSongs[0].PreviewStartMs);
        }

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
        public void A_Multi_Song_Folder_Becomes_Its_Own_Pack()
        {
            // Several sets dropped flat in one folder → that folder is its OWN pack (named after itself), not dissolved
            // into the parent group's song list.
            var dir = Dir("pack", "mixed");
            Audio(dir, "a.mp3"); Audio(dir, "b.mp3");
            Osu(dir, "a1.osu", "a.mp3", "Song A", 100);
            Osu(dir, "b1.osu", "b.mp3", "Song B", 100);

            var songs = ExternalSongScanner.LoadFolder("pack", dir);
            Assert.AreEqual(2, songs.Count);
            foreach (var s in songs)
                Assert.AreEqual("mixed", s.Group, "a folder with several songs is grouped under the folder, not 'pack'");
        }

        [Test]
        public void A_Single_Song_Folder_Keeps_Its_Parent_Group()
        {
            var dir = Dir("pack", "one");
            Audio(dir, "a.mp3");
            Osu(dir, "a1.osu", "a.mp3", "Song A", 100);

            var songs = ExternalSongScanner.LoadFolder("pack", dir);
            Assert.AreEqual(1, songs.Count);
            Assert.AreEqual("pack", songs[0].Group, "one song → stays under the pack it was found in");
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

        [Test]
        public void Osu_Pack_Set_Shows_Song_Names_Not_The_Pack_Label()
        {
            // One osu beatmap set holding several DISTINCT songs (each its own audio): the shared Title is only the
            // pack label and each song's real name lives in its Version. The list must show "Aoi Shiori", not
            // "SDO Pack8 (Aoi Shiori)". Three+ songs under one title is the tell that the title is a pack label.
            var dir = Dir("osu", "SDO Pack8");
            Audio(dir, "aoi.mp3"); Audio(dir, "invoke.mp3"); Audio(dir, "shining.mp3");
            Osu(dir, "s1.osu", "aoi.mp3",     "SDO Pack8", 100, "Aoi Shiori");
            Osu(dir, "s2.osu", "invoke.mp3",  "SDO Pack8", 200, "INVOKE");
            Osu(dir, "s3.osu", "shining.mp3", "SDO Pack8", 300, "Shining Collection");

            var songs = ExternalSongScanner.LoadFolder("osu", dir);
            var titles = new List<string>();
            foreach (var s in songs) titles.Add(s.Title);
            Assert.AreEqual(3, songs.Count);
            CollectionAssert.AreEquivalent(
                new[] { "Aoi Shiori", "INVOKE", "Shining Collection" }, titles,
                "3+ same-titled songs = a pack → each row shows its own Version, not the shared pack label");
        }

        [Test]
        public void Osu_Pack_Song_Without_A_Version_Keeps_The_Unique_Pack_Label()
        {
            // A pack whose one song carries no Version can't be promoted; it stays the pack label — which is now
            // unique among the promoted siblings, so it still reads as a distinct row (no bogus duplicate).
            var dir = Dir("osu", "SDO Pack");
            Audio(dir, "a.mp3"); Audio(dir, "b.mp3"); Audio(dir, "c.mp3");
            Osu(dir, "s1.osu", "a.mp3", "Pack", 100, "Song A");
            Osu(dir, "s2.osu", "b.mp3", "Pack", 200, "Song B");
            Osu(dir, "s3.osu", "c.mp3", "Pack", 300, "");   // no Version → left as the (now unique) pack label

            var songs = ExternalSongScanner.LoadFolder("osu", dir);
            var titles = new List<string>();
            foreach (var s in songs) titles.Add(s.Title);
            Assert.AreEqual(3, songs.Count);
            CollectionAssert.AreEquivalent(new[] { "Song A", "Song B", "Pack" }, titles);
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
        public void Osu_Style_Singles_Share_One_Group_While_Packs_Get_Their_Own()
        {
            // osu! drops each song as its OWN folder directly under Songs/ (no group level). Those singles must not
            // each become a one-song browse tab — they share the root's group. A PACK (a folder that itself holds
            // several song folders) is still pulled out as its own group.
            var rootName = new DirectoryInfo(_root).Name;

            var a = Dir("Artist A - Track A");          // single, straight under the root
            Audio(a, "a.mp3"); Osu(a, "a.osu", "a.mp3", "Track A", 100);
            var b = Dir("Artist B - Track B");          // another single
            Audio(b, "b.mp3"); Osu(b, "b.osu", "b.mp3", "Track B", 100);
            var p1 = Dir("Cool Pack", "Song One");      // a pack: songs nested one level in
            Audio(p1, "1.mp3"); Osu(p1, "1.osu", "1.mp3", "Song One", 100);
            var p2 = Dir("Cool Pack", "Song Two");
            Audio(p2, "2.mp3"); Osu(p2, "2.osu", "2.mp3", "Song Two", 100);

            var work = ExternalSongScanner.BuildWorklist(new List<string> { _root });
            string GroupOf(string path) => work.Find(w => w.Path == path).Group;

            Assert.AreEqual(rootName, GroupOf(a), "single osu folders share the root group");
            Assert.AreEqual(rootName, GroupOf(b));
            Assert.AreEqual("Cool Pack", GroupOf(p1), "a pack is pulled out as its own group");
            Assert.AreEqual("Cool Pack", GroupOf(p2));

            // …and it carries through to the actual songs (a single keeps the root group; the pack keeps its own).
            var songs = ExternalSongScanner.Scan(new List<string> { _root });
            Assert.AreEqual(rootName, songs.Find(s => s.Title == "Track A").Group);
            Assert.AreEqual("Cool Pack", songs.Find(s => s.Title == "Song One").Group);
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

        // ---- the sdoinfo.dat sidecar: the CD disc is built once, then read back ----

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

        // ---- backward compat: the pre-rename sdo.header is still read; a write migrates it to sdoinfo.dat ----

        private static void LegacyHeader(string dir, string text)
            => File.WriteAllText(Path.Combine(dir, SongSidecar.LegacyFileName), text);

        [Test]
        public void A_Legacy_Sdo_Header_Is_Still_Read_When_No_New_Sidecar_Exists()
        {
            // A library scanned before the rename has its records in sdo.header only — the disc it built and the
            // offset the player calibrated must keep applying, or the rename would silently lose both.
            var dir = Dir("Group", "Song");
            Audio(dir, "a.mp3");
            Osu(dir, "a.osu", "a.mp3", "Song", 100, "Hard", "bg.jpg");
            Img(dir, "bg.jpg"); Img(dir, "cd.png");
            LegacyHeader(dir, "#VERSION:1;\n#SONG:;\n#CDIMAGE:cd.png;\n#OFFSETMS:-42;\n");

            var songs = ExternalSongScanner.LoadFolder("Group", dir);

            Assert.AreEqual(Path.Combine(dir, "cd.png"), songs[0].CdImagePath, "legacy sidecar's disc must still be honored");
            Assert.AreEqual(-42f, songs[0].OffsetMs, 1e-3f, "legacy sidecar's offset must still apply");
        }

        [Test]
        public void The_New_Sidecar_Wins_Over_A_Leftover_Legacy_One()
        {
            var dir = Dir("Group", "Song");
            Audio(dir, "a.mp3");
            Osu(dir, "a.osu", "a.mp3", "Song", 100, "Hard", "bg.jpg");
            Img(dir, "bg.jpg");
            LegacyHeader(dir, "#SONG:;\n#OFFSETMS:-42;\n");             // stale
            Header(dir, "#SONG:;\n#OFFSETMS:7;\n");                    // current sdoinfo.dat

            var songs = ExternalSongScanner.LoadFolder("Group", dir);

            Assert.AreEqual(7f, songs[0].OffsetMs, 1e-3f, "the current sdoinfo.dat must win over a leftover sdo.header");
        }

        [Test]
        public void WriteText_Migrates_A_Legacy_Sidecar_To_The_New_Name()
        {
            var dir = Dir("Group", "Song");
            LegacyHeader(dir, "#SONG:;\n#CDIMAGE:cd.png;\n");

            // ReadText sees the legacy file; WriteText persists under the new name AND removes the legacy one, so a
            // folder is never left carrying both.
            var text = SongSidecar.ReadText(dir);
            Assert.AreEqual("cd.png", SongSidecar.Parse(text)[0].CdImage, "the legacy file was read on the way in");

            SongSidecar.WriteText(dir, SongSidecar.SetOffset(text, "", 12f));

            Assert.IsTrue(File.Exists(Path.Combine(dir, SongSidecar.FileName)), "new sidecar written");
            Assert.IsFalse(File.Exists(Path.Combine(dir, SongSidecar.LegacyFileName)), "legacy sidecar removed after migration");
            var back = SongSidecar.Parse(SongSidecar.ReadText(dir));
            Assert.AreEqual(12f, back[0].OffsetMs, 1e-3f, "the migrated file carries the new offset");
            Assert.AreEqual("cd.png", back[0].CdImage, "migrating kept the recorded disc");
        }

        // ---- 選歌那欄的音符數 = 判定次數（＝全接的最大 combo）----

        // 一張 4K .sm：#BPMS 240 ＋ 每小節 16 行 → 一行 62.5 ms。
        //   tap ×2、正常長條 ×1（8 行 = 500 ms）、極短長條 ×1（1 行 = 62.5 ms < 83 ms 的收合門檻）
        // → 物件數 4、判定次數 6（長條的放開各算一次）、收合開著時 5。
        private static void SmWithHolds(string dir, string file, string music, string title)
        {
            var rows = new string[16];
            for (int i = 0; i < rows.Length; i++) rows[i] = "0000";
            rows[0] = "1000";                       // tap
            rows[1] = "0100";                       // tap
            rows[2] = "0010"; rows[10] = "0030";    // 長條 500 ms（row2 的 '2' → row10 的 '3'）
            rows[12] = "0002"; rows[13] = "0003";   // 極短長條 62.5 ms
            File.WriteAllText(Path.Combine(dir, file),
                "#TITLE:" + title + ";\n#ARTIST:Tester;\n#MUSIC:" + music + ";\n#OFFSET:0.000;\n" +
                "#BPMS:0.000=240.000;\n#NOTES:\n     dance-single:\n     :\n     Hard:\n     8:\n" +
                "     0,0,0,0,0:\n" + string.Join("\n", rows) + "\n;\n");
        }

        // 一張 4K osu!mania 譜：tap ×2 ＋ 長條 ×1（type 128，第一個 objectParam 是結束時間）。
        private static void OsuWithHold(string dir, string file, string audio, string title, int holdMs)
        {
            var sb = new StringBuilder();
            sb.Append("osu file format v14\n\n[General]\nAudioFilename: ").Append(audio).Append('\n');
            sb.Append("PreviewTime: 1000\nMode: 3\n\n[Metadata]\nTitle:").Append(title).Append('\n');
            sb.Append("Artist:Tester\nVersion:Hard\nBeatmapSetID:-1\n\n[Difficulty]\nCircleSize:4\n\n[Events]\n");
            sb.Append("\n[TimingPoints]\n0,500,4,2,0,100,1,0\n\n[HitObjects]\n");
            sb.Append("64,192,500,1,0,0:0:0:0:\n");                                  // tap
            sb.Append("192,192,750,1,0,0:0:0:0:\n");                                 // tap
            sb.Append("320,192,1000,128,0,").Append(1000 + holdMs).Append(":0:0:0:0:\n");   // 長條
            File.WriteAllText(Path.Combine(dir, file), sb.ToString());
        }

        [Test]
        public void Sm_Note_Count_Is_Judged_Events_So_It_Equals_The_Max_Combo()
        {
            // 選歌那欄和官方 .gn 表頭的 notes 是同一個語意：長條的頭與放開各算一次判定
            //（官方 25 首 × 3 難度實測 68/75 等於 OsuBeatmap.TotalNotes，其餘是沒長條、兩種算法同值的譜）。
            // 長條只算一顆的話,一張長條多的 .sm 打完會發現 combo 比選歌寫的多出幾十。
            var dir = Dir("pack", "sm");
            Audio(dir, "track.mp3");
            SmWithHolds(dir, "chart.sm", "track.mp3", "Holds");

            var songs = ExternalSongScanner.LoadFolder("pack", dir);

            Assert.AreEqual(1, songs.Count);
            Assert.AreEqual(6, songs[0].Charts[2].NoteCount, "2 taps + 2 holds × (頭 + 放開) = 6 次判定");
        }

        [Test]
        public void Note_Count_Follows_The_Short_Hold_Collapse_Setting()
        {
            // 「無理短長條→一般 note」開著時（預設）那條 62.5 ms 的長條會被收成 tap，少一次放開判定 ——
            // ScreenGameplay.LoadChart 就是這樣載譜的，所以選歌那欄要跟著少，數字才等於玩家打得到的 combo。
            var dir = Dir("pack", "sm");
            Audio(dir, "track.mp3");
            SmWithHolds(dir, "chart.sm", "track.mp3", "Holds");

            ExternalSongScanner.CollapseShortHolds = true;
            var songs = ExternalSongScanner.LoadFolder("pack", dir);

            Assert.AreEqual(5, songs[0].Charts[2].NoteCount, "短長條收成 tap → 少一次放開判定");
        }

        // ---- GroupWorklist（掃描的順序單位＝群組，批內才平行）----

        private static List<ExternalSongScanner.SongDir> Work(params string[] groups)
        {
            var w = new List<ExternalSongScanner.SongDir>();
            for (int i = 0; i < groups.Length; i++)
                w.Add(new ExternalSongScanner.SongDir { Group = groups[i], Path = "d" + i });
            return w;
        }

        [Test]
        public void GroupWorklist_Batches_By_Group_In_First_Seen_Order()
        {
            var b = ExternalSongScanner.GroupWorklist(Work("A", "A", "B"));

            Assert.AreEqual(2, b.Count);
            Assert.AreEqual("A", b[0].Group);
            CollectionAssert.AreEqual(new[] { 0, 1 }, b[0].Indices);
            Assert.AreEqual("B", b[1].Group);
            CollectionAssert.AreEqual(new[] { 2 }, b[1].Indices);
        }

        [Test]
        public void GroupWorklist_Gathers_A_Group_Whose_Folders_Are_Not_Adjacent()
        {
            // root 底下的單曲資料夾全掛 rootGroup，會跟各個 pack 在 worklist 裡交錯 —— 那些不相連的位置
            // 必須收攏成同一批，不然同一個群組名會在載入條上出現好幾次。
            var b = ExternalSongScanner.GroupWorklist(Work("Songs", "Pack", "Songs", "Pack"));

            Assert.AreEqual(2, b.Count);
            CollectionAssert.AreEqual(new[] { 0, 2 }, b[0].Indices);
            CollectionAssert.AreEqual(new[] { 1, 3 }, b[1].Indices);
        }

        [Test]
        public void GroupWorklist_Merges_Case_Variants_And_Covers_Every_Folder()
        {
            // 不同 root 可能把同一個群組寫成不同大小寫；顯示名取第一次出現的那個。
            var b = ExternalSongScanner.GroupWorklist(Work("Pack", "PACK", "", ""));

            Assert.AreEqual(2, b.Count);
            Assert.AreEqual("Pack", b[0].Group);
            CollectionAssert.AreEqual(new[] { 0, 1 }, b[0].Indices);
            Assert.AreEqual("", b[1].Group, "群組名讀不到的資料夾自成一批");

            int total = 0;
            foreach (var g in b) total += g.Indices.Count;
            Assert.AreEqual(4, total, "每個資料夾都要恰好被分到一批 —— 漏一個就等於漏掃一個資料夾");
        }

        [Test]
        public void GroupWorklist_Handles_An_Empty_Worklist()
        {
            Assert.AreEqual(0, ExternalSongScanner.GroupWorklist(null).Count);
            Assert.AreEqual(0, ExternalSongScanner.GroupWorklist(new List<ExternalSongScanner.SongDir>()).Count);
        }

        [Test]
        public void Keysound_Compilation_Set_Becomes_One_Song_Per_Track()
        {
            // osu 的「鋼琴精選集」:一個 beatmap set 裡好幾首**不同曲子**,全部純 keysound(沒有音檔)、
            // 共用一個 setId 與一個當標籤用的 Title。以前整包被併成一首歌 → 只剩三個難度槽,其餘整首選不到。
            var dir = Dir("pack", "piano");
            const string set = "Piano Beatmap Set";
            Osu(dir, "canon.osu", "virtual", set, 800, version: "Canon - 4K EX", setId: 332673);
            Osu(dir, "turkish.osu", "virtual", set, 600, version: "Turkish March - 4K EX", setId: 332673);
            Osu(dir, "william.osu", "virtual", set, 400, version: "William Tell - 4K EX", setId: 332673);
            Osu(dir, "circus.osu", "virtual", set, 1000, version: "4K HELL CIRCUS", setId: 332673);
            Osu(dir, "hell10k.osu", "virtual", set, 1000, version: "10K HELL CIRCUS", setId: 332673, keys: 10);

            var songs = ExternalSongScanner.LoadFolder("pack", dir);

            Assert.AreEqual(4, songs.Count, "四首 4K 曲子各自成一首歌(10K 那張照舊不算)");
            var titles = new List<string>();
            foreach (var s in songs)
            {
                titles.Add(s.Title);
                Assert.IsTrue(s.Playable);
                Assert.AreNotEqual("", s.SongKey, "同一個資料夾裡有好幾首歌 → 每首都要有自己的 songKey");
                Assert.AreEqual("", s.AudioPath, "純 keysound:音樂就是譜自己的取樣,沒有底軌");
            }
            CollectionAssert.AreEquivalent(
                new[] { "Canon", "Turkish March", "William Tell", "4K HELL CIRCUS" }, titles,
                "標題要是曲名(藏在難度名的前綴裡),不是整包的標籤");
        }

        [Test]
        public void Keysound_Difficulties_Of_One_Track_Remain_One_Song()
        {
            // 同一首純 keysound 曲子的三個難度(長度一樣)→ 還是一首歌三個難度槽,標題保持原樣。
            var dir = Dir("pack", "bms");
            Osu(dir, "another.osu", "virtual", "Only One Song", 400, version: "SP ANOTHER", setId: 55);
            Osu(dir, "hyper.osu", "virtual", "Only One Song", 399, version: "SP HYPER", setId: 55);
            Osu(dir, "normal.osu", "virtual", "Only One Song", 398, version: "SP NORMAL", setId: 55);

            var songs = ExternalSongScanner.LoadFolder("pack", dir);

            Assert.AreEqual(1, songs.Count);
            Assert.AreEqual("Only One Song", songs[0].Title);
            Assert.AreEqual("", songs[0].SongKey, "沒拆 → 維持「資料夾唯一一首」的身分,舊收藏不會失效");
            Assert.IsNotNull(songs[0].Charts[0]);
            Assert.IsNotNull(songs[0].Charts[1]);
            Assert.IsNotNull(songs[0].Charts[2]);
        }

        [Test]
        public void Osu_Note_Count_Counts_The_Hold_Release_Too()
        {
            // osu 那條路線本來是拿 [HitObjects] 的行數（長條算一顆）——和 .sm 一樣要改看判定次數。
            var dir = Dir("pack", "osu");
            Audio(dir, "track.mp3");
            OsuWithHold(dir, "chart.osu", "track.mp3", "Holds", 1000);

            var songs = ExternalSongScanner.LoadFolder("pack", dir);

            Assert.AreEqual(1, songs.Count);
            Assert.AreEqual(4, songs[0].Charts[2].NoteCount, "2 taps + 1 hold × (頭 + 放開) = 4 次判定");
        }
    }
}
