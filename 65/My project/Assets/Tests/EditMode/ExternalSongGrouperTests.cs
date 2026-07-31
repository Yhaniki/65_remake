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
    }
}
