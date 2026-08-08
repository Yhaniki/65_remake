using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    public class ExternalSongGrouperTests
    {
        private static OsuMeta Meta(string audio, string title, int notes, int setId = -1, string artist = "")
            => new OsuMeta { AudioFilename = audio, Title = title, Artist = artist, BeatmapSetId = setId, NoteCount = notes };

        [Test]
        public void One_Audio_File_Is_One_Song()
        {
            var metas = new List<OsuMeta> { Meta("a.mp3", "T", 500), Meta("a.mp3", "T", 900), Meta("a.mp3", "T", 200) };
            var groups = ExternalSongGrouper.GroupOsu(metas, new List<string> { "e.osu", "h.osu", "n.osu" });
            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual(new[] { 1, 0, 2 }, groups[0].Charts.ToArray(), "charts come out hardest-first");
        }

        [Test]
        public void Two_Audio_Files_In_One_Folder_Are_Two_Songs()
        {
            var metas = new List<OsuMeta> { Meta("a.mp3", "A", 500), Meta("b.mp3", "B", 700), Meta("a.mp3", "A", 900) };
            var groups = ExternalSongGrouper.GroupOsu(metas, new List<string> { "1.osu", "2.osu", "3.osu" });
            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual("audio:a.mp3", groups[0].Key);
            Assert.AreEqual(new[] { 2, 0 }, groups[0].Charts.ToArray());
            Assert.AreEqual("audio:b.mp3", groups[1].Key);
            Assert.AreEqual(new[] { 1 }, groups[1].Charts.ToArray());
        }

        [Test]
        public void Audio_Name_Matching_Ignores_Case_And_Folders()
        {
            var metas = new List<OsuMeta> { Meta("Audio.mp3", "T", 100), Meta("sb\\audio.MP3", "T", 200) };
            var groups = ExternalSongGrouper.GroupOsu(metas, new List<string> { "1.osu", "2.osu" });
            Assert.AreEqual(1, groups.Count, "same file, different spelling → same song");
        }

        [Test]
        public void Key_Falls_Back_SetId_Then_Metadata_Then_Filename()
        {
            Assert.AreEqual("audio:a.mp3", ExternalSongGrouper.KeyOf(Meta("a.mp3", "T", 1, 42), "x.osu"));
            Assert.AreEqual("set:42", ExternalSongGrouper.KeyOf(Meta("", "T", 1, 42), "x.osu"));
            Assert.AreEqual("meta:art|t", ExternalSongGrouper.KeyOf(Meta("", "T", 1, -1, "Art"), "x.osu"));
            Assert.AreEqual("file:x.osu", ExternalSongGrouper.KeyOf(Meta("", "", 1), "x.osu"),
                "no audio, no set id, no metadata → each chart is its own song rather than a bogus merge");
        }

        [Test]
        public void Charts_With_No_Audio_But_Different_Set_Ids_Do_Not_Merge()
        {
            var metas = new List<OsuMeta> { Meta("", "T", 100, 1), Meta("", "T", 200, 2) };
            var groups = ExternalSongGrouper.GroupOsu(metas, new List<string> { "1.osu", "2.osu" });
            Assert.AreEqual(2, groups.Count);
        }

        [Test]
        public void AudioNameOf_Reads_Back_Only_Audio_Keys()
        {
            Assert.AreEqual("a.mp3", ExternalSongGrouper.AudioNameOf("audio:a.mp3"));
            Assert.AreEqual("", ExternalSongGrouper.AudioNameOf("set:42"));
            Assert.AreEqual("", ExternalSongGrouper.AudioNameOf("file:x.osu"));
        }

        [Test]
        public void Sm_Key_Is_The_File()
        {
            Assert.AreEqual("file:song.sm", ExternalSongGrouper.SmKeyOf("Song.SM"));
        }

        // ---- 純 keysound 合輯:一個 beatmap set 裝了好幾首不同曲子(osu 的「鋼琴精選集」那種) ----

        private static OsuMeta Keysound(string version, int lastNoteMs, int notes = 500, int setId = 332673)
            => new OsuMeta
            {
                AudioFilename = "virtual", Title = "Piano Beatmap Set", Artist = "CircusGalop",
                BeatmapSetId = setId, NoteCount = notes, LastNoteMs = lastNoteMs, Version = version,
            };

        [Test]
        public void Keysound_Compilation_Splits_Into_One_Song_Per_Track()
        {
            var metas = new List<OsuMeta>
            {
                Keysound("Canon - 4K EX", 210_000),
                Keysound("Turkish March - 4K EX", 95_000),
                Keysound("Black Key - 4K EX", 42_000),
            };
            var files = new List<string> { "canon.osu", "turkish.osu", "black.osu" };
            var groups = ExternalSongGrouper.GroupOsu(metas, files);

            Assert.AreEqual(3, groups.Count, "全部 virtual + 同一個 setId,只能靠譜長分辨是三首不同曲子");
            var names = new List<string>();
            foreach (var g in groups) names.Add(g.SongName);
            Assert.Contains("Canon", names);
            Assert.Contains("Turkish March", names);
            Assert.Contains("Black Key", names);
            foreach (var g in groups) Assert.AreEqual(1, g.Charts.Count);
        }

        [Test]
        public void Keysound_Compilation_Keys_Are_Distinct_And_Content_Derived()
        {
            var metas = new List<OsuMeta> { Keysound("Canon - 4K EX", 210_000), Keysound("Black Key - 4K EX", 42_000) };
            var files = new List<string> { "canon.osu", "black.osu" };
            var groups = ExternalSongGrouper.GroupOsu(metas, files);

            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual("set:332673|song:canon", groups[0].Key, "照第一次出現的順序");
            Assert.AreEqual("set:332673|song:black key", groups[1].Key);

            // 同一份內容、不同掃描順序 → 同一組 key(收藏與缺歌傳檔都靠它比對)
            var swapped = ExternalSongGrouper.GroupOsu(
                new List<OsuMeta> { metas[1], metas[0] }, new List<string> { files[1], files[0] });
            var keys = new List<string>();
            foreach (var g in swapped) keys.Add(g.Key);
            Assert.Contains("set:332673|song:canon", keys);
            Assert.Contains("set:332673|song:black key", keys);
        }

        [Test]
        public void Keysound_Difficulties_Of_One_Track_Stay_One_Song()
        {
            // 同一首曲子的三個難度:命名沒有規律(BMS 移植常見),但長度一樣 → 不能拆
            var metas = new List<OsuMeta>
            {
                Keysound("SP ANOTHER", 120_200, notes: 1800),
                Keysound("SP HYPER", 120_100, notes: 1200),
                Keysound("SP NORMAL", 120_000, notes: 700),
            };
            var groups = ExternalSongGrouper.GroupOsu(metas, new List<string> { "a.osu", "h.osu", "n.osu" });
            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual("set:332673", groups[0].Key, "沒拆 → key 與以前完全一樣(舊收藏不會失效)");
            Assert.AreEqual(new[] { 0, 1, 2 }, groups[0].Charts.ToArray(), "音符多的先出來");
        }

        [Test]
        public void Keysound_Same_Track_Multiple_Difficulties_Split_By_Track()
        {
            // 合輯裡的曲子各自又有兩個 4K 難度 → 兩首歌,每首兩張譜
            var metas = new List<OsuMeta>
            {
                Keysound("Canon - 4K EX", 210_000, notes: 1500),
                Keysound("Canon - 4K NM", 209_000, notes: 800),
                Keysound("Black Key - 4K EX", 42_000, notes: 900),
                Keysound("Black Key - 4K NM", 42_400, notes: 400),
            };
            var groups = ExternalSongGrouper.GroupOsu(metas,
                new List<string> { "c-ex.osu", "c-nm.osu", "b-ex.osu", "b-nm.osu" });

            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual("Canon", groups[0].SongName);
            Assert.AreEqual(new[] { 0, 1 }, groups[0].Charts.ToArray());
            Assert.AreEqual("Black Key", groups[1].SongName);
            Assert.AreEqual(new[] { 2, 3 }, groups[1].Charts.ToArray());
        }

        [Test]
        public void Compilation_Tracks_That_Are_Nearly_The_Same_Length_Still_Split()
        {
            // 真實的 332673「Piano Beatmap Set」量出來的四首長曲:最接近的兩首只差 770 ms ——
            // 純靠長度分群(容差 ±3%)會把它們併掉,曲名才分得開。
            var metas = new List<OsuMeta>
            {
                Keysound("Chopin Nocturne - 4K Full", 225_066),
                Keysound("Hungarian Rhapsody - 4K Full", 228_866),
                Keysound("Grandes Etudes No.6 - 4K Full", 248_130),
                Keysound("La Campanella - 4K Full", 248_900),
            };
            var groups = ExternalSongGrouper.GroupOsu(metas,
                new List<string> { "1.osu", "2.osu", "3.osu", "4.osu" });

            Assert.AreEqual(4, groups.Count);
            Assert.AreEqual("Grandes Etudes No.6", groups[2].SongName);
            Assert.AreEqual("La Campanella", groups[3].SongName, "差 770 ms 的兩首也要各自成一首歌");
        }

        [Test]
        public void Compilation_Split_Falls_Back_To_Chart_File_When_Names_Are_Difficulty_Labels()
        {
            // 難度名沒帶曲名 → 分組照譜長照樣要拆(不然那首歌整個消失),但沒有曲名可顯示
            var metas = new List<OsuMeta> { Keysound("4K EX", 210_000), Keysound("4K EX", 42_000) };
            var groups = ExternalSongGrouper.GroupOsu(metas, new List<string> { "long.osu", "short.osu" });

            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual("", groups[0].SongName);
            Assert.AreEqual("set:332673|song:long.osu", groups[0].Key);
            Assert.AreEqual("set:332673|song:short.osu", groups[1].Key);
        }

        [Test]
        public void Real_Audio_Still_Groups_By_Audio_Whatever_The_Lengths()
        {
            // 有真的音檔 → 音檔就是身分,譜長差多少都不切(短難度譜不是另一首歌)
            var metas = new List<OsuMeta>
            {
                new OsuMeta { AudioFilename = "a.mp3", Title = "T", NoteCount = 900, LastNoteMs = 200_000, Version = "Insane" },
                new OsuMeta { AudioFilename = "a.mp3", Title = "T", NoteCount = 200, LastNoteMs = 40_000, Version = "Easy" },
            };
            var groups = ExternalSongGrouper.GroupOsu(metas, new List<string> { "1.osu", "2.osu" });
            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual("audio:a.mp3", groups[0].Key);
        }

        [Test]
        public void Unmeasurable_Lengths_Never_Split_When_There_Are_No_Names_Either()
        {
            // 難度名沒帶曲名(純難度標籤)+ 量不到譜長 → 兩條線索都沒了,維持現況不拆
            var metas = new List<OsuMeta> { Keysound("4K EX", 0), Keysound("4K NM", 42_000) };
            var groups = ExternalSongGrouper.GroupOsu(metas, new List<string> { "1.osu", "2.osu" });
            Assert.AreEqual(1, groups.Count, "沒有證據 → 寧可維持現況也不要照壞資料亂拆");
            Assert.AreEqual("set:332673", groups[0].Key);
        }

        [Test]
        public void Names_Still_Split_When_A_Length_Cannot_Be_Measured()
        {
            // 長度只是用來擋「名字不同其實同曲」的反例;量不到就是沒有反例,照曲名拆
            var metas = new List<OsuMeta> { Keysound("Canon - 4K EX", 0), Keysound("Black Key - 4K EX", 42_000) };
            var groups = ExternalSongGrouper.GroupOsu(metas, new List<string> { "1.osu", "2.osu" });
            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual("Canon", groups[0].SongName);
            Assert.AreEqual("Black Key", groups[1].SongName);
        }

        [Test]
        public void SongNameOf_Cuts_At_The_Last_Separator()
        {
            Assert.AreEqual("Canon", ExternalSongGrouper.SongNameOf("Canon - 4K EX"));
            Assert.AreEqual("Pathetique (Medley ver.)",
                ExternalSongGrouper.SongNameOf("Pathetique (Medley ver.) - 4K EX"));
            Assert.AreEqual("Hungarian Rhapsody - No.2",
                ExternalSongGrouper.SongNameOf("Hungarian Rhapsody - No.2 - 4K Full"));
            Assert.AreEqual("4K HELL CIRCUS", ExternalSongGrouper.SongNameOf("4K HELL CIRCUS"),
                "沒有分隔符 → 整串就是曲名");
            Assert.AreEqual("", ExternalSongGrouper.SongNameOf(null));
        }
    }
}
