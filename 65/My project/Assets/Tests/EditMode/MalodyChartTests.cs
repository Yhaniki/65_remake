using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    public class MalodyChartTests
    {
        // A minimal 4K Key-mode .mc: 120 BPM (1 beat = 500 ms), bgm offset 100 ms (SUBTRACTED from beat times).
        // Notes: tap col0 @ beat1, tap col1 @ beat1.5, hold col2 beat2→beat4; plus the bgm/sound note (audio + offset).
        private const string Sample = @"{
            ""meta"": {
                ""$ver"": 0,
                ""creator"": ""Lu-"",
                ""background"": ""bg.jpg"",
                ""version"": ""4K Hard"",
                ""preview"": 5000,
                ""mode"": 0,
                ""song"": { ""title"": ""Cutter"", ""artist"": ""EmoCosine"" },
                ""mode_ext"": { ""column"": 4 }
            },
            ""time"": [ { ""beat"": [0, 0, 1], ""bpm"": 120.0 } ],
            ""effect"": [],
            ""note"": [
                { ""beat"": [1, 0, 1], ""column"": 0 },
                { ""beat"": [1, 2, 4], ""column"": 1 },
                { ""beat"": [2, 0, 1], ""column"": 2, ""endbeat"": [4, 0, 1] },
                { ""beat"": [0, 0, 1], ""sound"": ""song.ogg"", ""vol"": 100, ""offset"": 100, ""type"": 1 }
            ]
        }";

        // ---- MiniJson ----

        [Test]
        public void MiniJson_Parses_Object_Array_Number_String()
        {
            var root = MiniJson.Parse(@"{ ""a"": 1, ""b"": ""x"", ""c"": [1, 2, 3], ""d"": true, ""e"": null }");
            Assert.IsNotNull(root);
            Assert.AreEqual(1.0, MiniJson.GetDouble(root, "a"));
            Assert.AreEqual("x", MiniJson.GetString(root, "b"));
            var arr = MiniJson.AsArray(MiniJson.Get(root, "c"));
            Assert.AreEqual(3, arr.Count);
            Assert.AreEqual(2.0, (double)arr[1]);
            Assert.AreEqual(true, MiniJson.Get(root, "d"));
            Assert.IsNull(MiniJson.Get(root, "e"));
        }

        [Test]
        public void MiniJson_Handles_Escapes_And_NegativeNumbers()
        {
            var root = MiniJson.Parse(@"{ ""s"": ""a\""b\n"", ""n"": -3.5, ""u"": ""A"" }");
            Assert.AreEqual("a\"b\n", MiniJson.GetString(root, "s"));
            Assert.AreEqual(-3.5, MiniJson.GetDouble(root, "n"));
            Assert.AreEqual("A", MiniJson.GetString(root, "u"));
        }

        [Test]
        public void MiniJson_Malformed_Returns_Null_Not_Throw()
        {
            Assert.IsNull(MiniJson.Parse(null));
            Assert.IsNull(MiniJson.Parse(""));
        }

        // ---- BeatOf ----

        [Test]
        public void BeatOf_Is_Bar_Plus_Fraction()
        {
            Assert.AreEqual(5.0, MalodyChart.BeatOf(MiniJson.Parse("[5,0,4]")), 1e-9);
            Assert.AreEqual(6.75, MalodyChart.BeatOf(MiniJson.Parse("[6,3,4]")), 1e-9);
            Assert.AreEqual(0.0, MalodyChart.BeatOf(MiniJson.Parse("[0,0,1]")), 1e-9);
            Assert.AreEqual(0.0, MalodyChart.BeatOf(null), 1e-9);          // malformed → 0
            Assert.AreEqual(2.0, MalodyChart.BeatOf(MiniJson.Parse("[2,1,0]")), 1e-9); // den 0 → 小數部分忽略，落整拍
        }

        // ---- Parse (metadata + notes) ----

        [Test]
        public void Parse_Reads_Metadata()
        {
            var s = MalodyChart.Parse(Sample);
            Assert.AreEqual(0, s.Mode);
            Assert.AreEqual(4, s.Column);
            Assert.AreEqual("Cutter", s.Title);
            Assert.AreEqual("EmoCosine", s.Artist);
            Assert.AreEqual("4K Hard", s.Version);
            Assert.AreEqual("bg.jpg", s.Background);
            Assert.AreEqual(5000, s.PreviewMs);
            Assert.AreEqual("song.ogg", s.AudioFile);
            Assert.AreEqual(100.0, s.AudioOffsetMs, 1e-9);
            Assert.AreEqual(120.0, s.FirstBpm, 1e-9);
        }

        [Test]
        public void Parse_Splits_Notes_And_Holds_From_Bgm()
        {
            var s = MalodyChart.Parse(Sample);
            Assert.AreEqual(3, s.Notes.Count);   // the bgm/sound note is NOT a playable note
            int holds = 0;
            foreach (var n in s.Notes) if (n.IsHold) holds++;
            Assert.AreEqual(1, holds);
        }

        [Test]
        public void Is4KKey_Requires_Mode0_And_4_Columns()
        {
            Assert.IsTrue(MalodyChart.Is4KKey(MalodyChart.Parse(Sample)));
            // wrong column count
            var c7 = Sample.Replace(@"""column"": 4", @"""column"": 7");
            Assert.IsFalse(MalodyChart.Is4KKey(MalodyChart.Parse(c7)));
            // wrong mode (not Key)
            var m5 = Sample.Replace(@"""mode"": 0", @"""mode"": 5");
            Assert.IsFalse(MalodyChart.Is4KKey(MalodyChart.Parse(m5)));
        }

        // ---- ToBeatmap: noteMs = beatToMs(beat) − offset (StepMania-style, empirically verified) ----

        [Test]
        public void ToBeatmap_Subtracts_The_Bgm_Offset()
        {
            var map = MalodyChart.ToBeatmap(MalodyChart.Parse(Sample));
            Assert.AreEqual(4, map.Keys);
            Assert.AreEqual(120.0, map.Bpm, 1e-9);
            Assert.AreEqual(3, map.HitObjects.Count);

            // sorted by start: beat1(500)-100=400 col0, beat1.5(750)-100=650 col1, beat2(1000)-100=900 col2 hold→beat4(2000)-100=1900
            Assert.AreEqual(400, map.HitObjects[0].StartTimeMs);
            Assert.AreEqual(0, map.HitObjects[0].Lane);
            Assert.AreEqual(650, map.HitObjects[1].StartTimeMs);
            Assert.AreEqual(1, map.HitObjects[1].Lane);
            Assert.AreEqual(900, map.HitObjects[2].StartTimeMs);
            Assert.AreEqual(2, map.HitObjects[2].Lane);
            Assert.IsTrue(map.HitObjects[2].IsHold);
            Assert.AreEqual(1900, map.HitObjects[2].EndTimeMs.Value);
        }

        [Test]
        public void ToBeatmap_Clamps_Negative_Note_Times_To_Zero()
        {
            // A tap on beat 0 with a positive offset would land at −offset → clamp to 0 (never a negative hit time).
            var s = MalodyChart.Parse(Sample);
            s.Notes.Clear();
            s.Notes.Add(new MalodyChart.McNote { Column = 0, Beat = 0.0 });
            var map = MalodyChart.ToBeatmap(s);
            Assert.AreEqual(1, map.HitObjects.Count);
            Assert.AreEqual(0, map.HitObjects[0].StartTimeMs);
        }

        // ---- GroupMalody: difficulties of one song share its audio ----

        [Test]
        public void GroupMalody_Groups_By_Audio_And_Ranks_By_NoteCount()
        {
            var audio = new List<string> { "a.ogg", "a.ogg", "b.ogg" };
            var counts = new List<int> { 200, 1500, 900 };
            var names = new List<string> { "easy.mc", "hard.mc", "other.mc" };
            var groups = ExternalSongGrouper.GroupMalody(audio, counts, names);

            Assert.AreEqual(2, groups.Count);                       // a.ogg (2 charts) + b.ogg (1 chart)
            Assert.AreEqual("audio:a.ogg", groups[0].Key);
            Assert.AreEqual(2, groups[0].Charts.Count);
            Assert.AreEqual(1, groups[0].Charts[0]);                // hardest (1500 notes) first
            Assert.AreEqual(0, groups[0].Charts[1]);
            Assert.AreEqual("audio:b.ogg", groups[1].Key);
        }

        [Test]
        public void GroupMalody_Falls_Back_To_Filename_When_No_Audio()
        {
            var groups = ExternalSongGrouper.GroupMalody(
                new List<string> { "" }, new List<int> { 100 }, new List<string> { "lone.mc" });
            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual("file:lone.mc", groups[0].Key);
        }
    }
}
