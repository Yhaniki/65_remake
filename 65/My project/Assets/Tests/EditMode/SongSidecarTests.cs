using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// The sdo.header sidecar: the file that makes a song's CD disc a build-once asset (and will carry its .mot /
    /// camera files next). What matters is that it survives a rewrite — a multi-song folder rewrites one song's block
    /// while the others (and any hand-written tags) must come out unchanged.
    /// </summary>
    public class SongSidecarTests
    {
        [Test]
        public void SingleSong_FolderWritesAnEmptyKeyBlock()
        {
            var text = SongSidecar.Write(new List<SongSidecarEntry>
            {
                new SongSidecarEntry { SongKey = "", CdImage = "cd.png" },
            });

            var back = SongSidecar.Parse(text);
            Assert.AreEqual(1, back.Count);
            Assert.AreEqual("", back[0].SongKey);
            Assert.AreEqual("cd.png", back[0].CdImage);
        }

        [Test]
        public void Parse_TagsBeforeAnySongBlockBelongToTheOnlySong()
        {
            // A hand-written single-song file needs no #SONG line at all.
            var e = SongSidecar.Parse("// mine\n#VERSION:1;\n#CDIMAGE:disc.png;\n#MOT:dance.mot;\n");

            Assert.AreEqual(1, e.Count);
            Assert.AreEqual("", e[0].SongKey);
            Assert.AreEqual("disc.png", e[0].CdImage);
            Assert.AreEqual("dance.mot", e[0].Mot);
        }

        [Test]
        public void Parse_MultiSongFolderKeepsOneBlockPerSong()
        {
            var text =
                "#VERSION:1;\n" +
                "#SONG:audio:a.mp3;\n#CDIMAGE:cd_audio_a_mp3.png;\n" +
                "#SONG:audio:b.mp3;\n#CDIMAGE:cd_audio_b_mp3.png;\n#CAMERA:b.cam;\n";

            var e = SongSidecar.Parse(text);

            Assert.AreEqual(2, e.Count);
            Assert.AreEqual("cd_audio_a_mp3.png", SongSidecar.Find(e, "audio:a.mp3").CdImage);
            Assert.AreEqual("cd_audio_b_mp3.png", SongSidecar.Find(e, "audio:b.mp3").CdImage);
            Assert.AreEqual("b.cam", SongSidecar.Find(e, "audio:b.mp3").Camera);
            Assert.AreEqual("", SongSidecar.Find(e, "audio:a.mp3").Camera);
            Assert.IsNull(SongSidecar.Find(e, "audio:missing.mp3"));
        }

        [Test]
        public void SetCdImage_TouchesOnlyThatSongsDisc()
        {
            var text = SongSidecar.Write(new List<SongSidecarEntry>
            {
                new SongSidecarEntry { SongKey = "audio:a.mp3", CdImage = "cd_audio_a_mp3.png", Mot = "a.mot" },
                new SongSidecarEntry { SongKey = "audio:b.mp3" },
            });

            var e = SongSidecar.Parse(SongSidecar.SetCdImage(text, "audio:b.mp3", "cd_audio_b_mp3.png"));

            Assert.AreEqual(2, e.Count);
            Assert.AreEqual("cd_audio_b_mp3.png", SongSidecar.Find(e, "audio:b.mp3").CdImage);
            Assert.AreEqual("cd_audio_a_mp3.png", SongSidecar.Find(e, "audio:a.mp3").CdImage, "other song's disc changed");
            Assert.AreEqual("a.mot", SongSidecar.Find(e, "audio:a.mp3").Mot, "other song's reserved tags were eaten");
        }

        [Test]
        public void SetCdImage_OnAnEmptyFolderCreatesTheBlock()
        {
            var e = SongSidecar.Parse(SongSidecar.SetCdImage("", "", "cd.png"));

            Assert.AreEqual(1, e.Count);
            Assert.AreEqual("cd.png", e[0].CdImage);
        }

        [Test]
        public void SetDps_RecordsTheGeneratedDanceBesideTheDisc()
        {
            // 生成的舞蹈跟 CD 圖記在同一份 sidecar：兩者互不覆蓋，別首歌的區塊也不能被動到。
            var text = SongSidecar.SetCdImage("", "audio:a.mp3", "cd_a.png");
            text = SongSidecar.SetDps(text, "audio:a.mp3", "dance_a.dps");
            text = SongSidecar.SetDps(text, "audio:b.mp3", "dance_b.dps");

            var e = SongSidecar.Parse(text);
            Assert.AreEqual(2, e.Count);
            Assert.AreEqual("dance_a.dps", SongSidecar.Find(e, "audio:a.mp3").Dps);
            Assert.AreEqual("cd_a.png", SongSidecar.Find(e, "audio:a.mp3").CdImage, "寫舞蹈把 CD 圖蓋掉了");
            Assert.AreEqual("dance_b.dps", SongSidecar.Find(e, "audio:b.mp3").Dps);
        }

        [Test]
        public void SetDps_StampsTheGeneratorSoAnOldDanceGetsRebuilt()
        {
            // 生成器改了（開場改成照抄官方 row）之後，舊版生的舞蹈必須重生一次 —— 靠 #DPSVER 認。
            var e = SongSidecar.Parse(SongSidecar.SetDps("", "", "dance.dps"));
            Assert.AreEqual(SongSidecar.DpsGenerator, e[0].DpsVersion);

            var legacy = SongSidecar.Parse("#DPS:dance.dps;\n");   // 舊檔沒有 #DPSVER
            Assert.AreEqual(0, legacy[0].DpsVersion);
            Assert.Less(legacy[0].DpsVersion, SongSidecar.DpsGenerator, "沒版號 → 視為過期 → 重生");
        }

        [Test]
        public void DpsFileName_FollowsTheDiscNamingScheme()
        {
            Assert.AreEqual("dance.dps", SongSidecar.DpsFileName(""), "資料夾只有一首歌 → 就叫 dance.dps");
            StringAssert.StartsWith("dance_audio_my_song_mp3_", SongSidecar.DpsFileName("audio:my song.mp3"));
            Assert.AreNotEqual(SongSidecar.DpsFileName("audio:恋.mp3"), SongSidecar.DpsFileName("audio:愛.mp3"),
                "同資料夾的兩首歌不能撞檔名");
        }

        [Test]
        public void Rewrite_KeepsTagsWeDontKnowAbout()
        {
            var e = SongSidecar.Parse("#SONG:;\n#CDIMAGE:cd.png;\n#OFFSET:0.123;\n");
            var back = SongSidecar.Parse(SongSidecar.Write(e));

            Assert.AreEqual(1, back[0].Extra.Count);
            Assert.AreEqual("OFFSET", back[0].Extra[0].Key);
            Assert.AreEqual("0.123", back[0].Extra[0].Value);
        }

        [Test]
        public void Parse_SurvivesJunk()
        {
            Assert.AreEqual(0, SongSidecar.Parse(null).Count);
            Assert.AreEqual(0, SongSidecar.Parse("").Count);
            Assert.AreEqual(0, SongSidecar.Parse("not a sidecar at all\n").Count);
            // A missing ';' still ends at the line break rather than swallowing the rest of the file.
            var e = SongSidecar.Parse("#SONG:;\n#CDIMAGE:cd.png\n#MOT:d.mot;\n");
            Assert.AreEqual("cd.png", e[0].CdImage);
            Assert.AreEqual("d.mot", e[0].Mot);
        }

        [Test]
        public void CdFileName_OneSongFolderIsPlainCdPng()
        {
            Assert.AreEqual("cd.png", SongSidecar.CdFileName(""));
            Assert.AreEqual("cd.png", SongSidecar.CdFileName(null));
        }

        [Test]
        public void CdFileName_MultiSongFolderNamesTheDiscAfterTheSong()
        {
            // Readable AND unique: the slug says which song it is, the key's hash keeps two songs apart.
            StringAssert.StartsWith("cd_audio_my_song_mp3_", SongSidecar.CdFileName("audio:my song.mp3"));
            StringAssert.StartsWith("cd_file_chart_sm_", SongSidecar.CdFileName("file:chart.sm"));
            StringAssert.StartsWith("cd_set_12345_", SongSidecar.CdFileName("set:12345"));
            StringAssert.EndsWith(".png", SongSidecar.CdFileName("audio:a.mp3"));
            Assert.AreNotEqual(SongSidecar.CdFileName("audio:a.mp3"), SongSidecar.CdFileName("audio:b.mp3"));
            Assert.AreEqual(SongSidecar.CdFileName("audio:a.mp3"), SongSidecar.CdFileName("audio:a.mp3"), "must be stable across runs");
        }

        [Test]
        public void CdFileName_NonLatinAndOverlongKeysStayUniqueAndSafe()
        {
            // Two CJK tracks in one folder: the slug alone can't tell "恋.mp3" from "愛.mp3" once the separators are
            // stripped — the hash is what stops the second song from overwriting the first one's disc.
            var jp = SongSidecar.CdFileName("audio:恋.mp3");
            var cn = SongSidecar.CdFileName("audio:愛.mp3");
            Assert.AreNotEqual(jp, cn);

            // A key that slugs to nothing at all still yields a usable name.
            var empty = SongSidecar.CdFileName("::");
            StringAssert.StartsWith("cd_", empty);
            StringAssert.EndsWith(".png", empty);

            var longKey = "audio:" + new string('x', 200) + ".mp3";
            var name = SongSidecar.CdFileName(longKey);
            Assert.Less(name.Length, 64, "filename must stay well inside the path limit");
            Assert.AreNotEqual(name, SongSidecar.CdFileName("audio:" + new string('x', 200) + "2.mp3"),
                "two long keys with a common prefix must not share a disc");

            foreach (var n in new[] { jp, cn, empty, name })
                foreach (char c in n)
                    Assert.IsFalse(":\\/*?\"<>| ".IndexOf(c) >= 0, "unsafe char in " + n);
        }
    }
}
