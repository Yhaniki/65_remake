using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    public class SmChartTests
    {
        // One dance-single chart: 1 measure of 4 quarter-note taps L,D,U,R (columns 0..3). 120 BPM → 1 beat = 500ms.
        private const string Sample =
            "#TITLE:Test Song;\n" +
            "#ARTIST:Tester;\n" +
            "#MUSIC:song.ogg;\n" +
            "#BANNER:bn.png;\n" +
            "#BACKGROUND:bg.png;\n" +
            "#CDTITLE:cd.png;\n" +
            "#OFFSET:0.000;\n" +
            "#SAMPLESTART:12.500;\n" +
            "#SAMPLELENGTH:20.000;\n" +
            "#BPMS:0.000=120.000;\n" +
            "#NOTES:\n" +
            "     dance-single:\n" +
            "     :\n" +
            "     Hard:\n" +
            "     8:\n" +
            "     0,0,0,0,0:\n" +
            "1000\n" +
            "0100\n" +
            "0010\n" +
            "0001\n" +
            ";\n";

        [Test]
        public void Parses_Header_Metadata()
        {
            var s = SmChart.Parse(Sample);
            Assert.AreEqual("Test Song", s.Title);
            Assert.AreEqual("Tester", s.Artist);
            Assert.AreEqual("song.ogg", s.Music);
            Assert.AreEqual("bn.png", s.Banner);
            Assert.AreEqual("bg.png", s.Background);
            Assert.AreEqual("cd.png", s.CdTitle);
            Assert.AreEqual(120.0, s.FirstBpm, 1e-6);
            Assert.AreEqual(12.5, s.SampleStart, 1e-6);
            Assert.AreEqual(20.0, s.SampleLength, 1e-6);
            Assert.AreEqual(1, s.Charts.Count);
            Assert.AreEqual("dance-single", s.Charts[0].StepsType);
            Assert.AreEqual("Hard", s.Charts[0].Difficulty);
            Assert.AreEqual(8, s.Charts[0].Meter);
        }

        [Test]
        public void NoteCount_Counts_Taps()
        {
            var s = SmChart.Parse(Sample);
            Assert.AreEqual(4, SmChart.NoteCount(s.Charts[0].NoteData));
        }

        [Test]
        public void ToBeatmap_Maps_Lanes_And_Times()
        {
            var map = SmChart.ToBeatmap(SmChart.Parse(Sample), 0);
            Assert.AreEqual(4, map.Keys);
            Assert.AreEqual(8, map.Level);
            Assert.AreEqual(120.0, map.Bpm, 1e-6);
            Assert.AreEqual(4, map.HitObjects.Count);
            // columns 0..3 in file order = L,D,U,R; quarter notes at 0/500/1000/1500 ms.
            Assert.AreEqual(0, map.HitObjects[0].Lane); Assert.AreEqual(0, map.HitObjects[0].StartTimeMs);
            Assert.AreEqual(1, map.HitObjects[1].Lane); Assert.AreEqual(500, map.HitObjects[1].StartTimeMs);
            Assert.AreEqual(2, map.HitObjects[2].Lane); Assert.AreEqual(1000, map.HitObjects[2].StartTimeMs);
            Assert.AreEqual(3, map.HitObjects[3].Lane); Assert.AreEqual(1500, map.HitObjects[3].StartTimeMs);
            Assert.IsFalse(map.HitObjects[0].IsHold);
        }

        [Test]
        public void ToBeatmap_Pairs_Holds()
        {
            // hold head '2' at beat 0, tail '3' at beat 3 on lane 0 (4 rows = quarter notes).
            const string hold =
                "#TITLE:H;\n#OFFSET:0;\n#BPMS:0=120;\n#NOTES:\n     dance-single:\n     :\n     Easy:\n     1:\n     0,0,0,0,0:\n" +
                "2000\n0000\n0000\n3000\n;\n";
            var map = SmChart.ToBeatmap(SmChart.Parse(hold), 0);
            Assert.AreEqual(1, map.HitObjects.Count);
            Assert.IsTrue(map.HitObjects[0].IsHold);
            Assert.AreEqual(0, map.HitObjects[0].Lane);
            Assert.AreEqual(0, map.HitObjects[0].StartTimeMs);
            Assert.AreEqual(1500, map.HitObjects[0].EndTimeMs);
        }

        [Test]
        public void ToBeatmap_Applies_Negative_Offset_As_Positive_Shift()
        {
            // note at beat 4 (= 2000ms @120bpm). OFFSET −0.2 → StepMania subtracts offset → +200ms → 2200ms.
            const string off =
                "#TITLE:O;\n#OFFSET:-0.200;\n#BPMS:0=120;\n#NOTES:\n     dance-single:\n     :\n     Easy:\n     1:\n" +
                "     0,0,0,0,0:\n0000\n0000\n0000\n0000\n,\n1000\n;\n";
            var map = SmChart.ToBeatmap(SmChart.Parse(off), 0);
            Assert.AreEqual(1, map.HitObjects.Count);
            Assert.AreEqual(0, map.HitObjects[0].Lane);
            Assert.AreEqual(2200, map.HitObjects[0].StartTimeMs);
        }

        [Test]
        public void Sample_Time_Accepts_Colon_And_Defaults_Unspecified()
        {
            Assert.AreEqual(83.0, SmChart.Parse("#SAMPLESTART:1:23;\n").SampleStart, 1e-6);   // MM:SS
            Assert.AreEqual(-1.0, SmChart.Parse("#TITLE:x;\n").SampleStart, 1e-6);            // absent → -1
        }

        [Test]
        public void Title_With_Colon_Is_Preserved()
        {
            Assert.AreEqual("Song: The Remix", SmChart.Parse("#TITLE:Song: The Remix;\n").Title);
        }

        [Test]
        public void IsDanceSingle_Rejects_Double()
        {
            var single = new SmChart.SmNotes { StepsType = "dance-single" };
            var dbl = new SmChart.SmNotes { StepsType = "dance-double" };
            Assert.IsTrue(SmChart.IsDanceSingle(single));
            Assert.IsFalse(SmChart.IsDanceSingle(dbl));
        }

        [Test]
        public void Parses_Stops_And_Freezes_Alias()
        {
            var s = SmChart.Parse("#STOPS:4.000=1.500,8.000=0.250;\n");
            Assert.AreEqual(2, s.StopBeats.Count);
            Assert.AreEqual(4.0, s.StopBeats[0], 1e-6); Assert.AreEqual(1.5, s.StopSeconds[0], 1e-6);
            Assert.AreEqual(8.0, s.StopBeats[1], 1e-6); Assert.AreEqual(0.25, s.StopSeconds[1], 1e-6);

            // #FREEZES is the legacy alias for the same tag.
            var f = SmChart.Parse("#FREEZES:2.000=0.500;\n");
            Assert.AreEqual(1, f.StopBeats.Count);
            Assert.AreEqual(2.0, f.StopBeats[0], 1e-6); Assert.AreEqual(0.5, f.StopSeconds[0], 1e-6);
        }

        [Test]
        public void ToBeatmap_Stop_Shifts_Later_Notes_But_Not_The_Note_On_The_Stop_Beat()
        {
            // 120bpm (1 beat = 500ms). A 1.0s freeze at beat 4. Notes at beat 0, beat 4, beat 5.
            // StepMania folds stops into note times: beat 4 (== stop beat) is hit BEFORE the freeze → 2000ms;
            // beat 5 (after the freeze) is pushed +1000ms → 2500+1000 = 3500ms.
            const string sm =
                "#TITLE:S;\n#OFFSET:0;\n#BPMS:0=120;\n#STOPS:4=1.0;\n" +
                "#NOTES:\n     dance-single:\n     :\n     Easy:\n     1:\n     0,0,0,0,0:\n" +
                "1000\n0000\n0000\n0000\n,\n1000\n1000\n0000\n0000\n;\n";
            var map = SmChart.ToBeatmap(SmChart.Parse(sm), 0);

            Assert.AreEqual(3, map.HitObjects.Count);
            Assert.AreEqual(0, map.HitObjects[0].StartTimeMs);      // beat 0 — before the stop
            Assert.AreEqual(2000, map.HitObjects[1].StartTimeMs);   // beat 4 — on the stop beat, not shifted
            Assert.AreEqual(3500, map.HitObjects[2].StartTimeMs);   // beat 5 — after the stop, +1000ms

            // the freeze window is exposed for the highway scroll: starts as the beat-4 note is hit, lasts 1s.
            Assert.AreEqual(1, map.Stops.Count);
            Assert.AreEqual(2000.0, map.Stops[0].TimeMs, 1e-6);
            Assert.AreEqual(1000.0, map.Stops[0].DurationMs, 1e-6);
        }

        [Test]
        public void ToBeatmap_Hold_Spanning_A_Stop_Is_Lengthened()
        {
            // hold head beat 0, tail beat 3 @120bpm (base end 1500ms); a 0.5s freeze at beat 2 (between them)
            // pushes the tail +500ms → 2000ms. The head (beat 0, before the stop) stays at 0.
            const string sm =
                "#TITLE:H;\n#OFFSET:0;\n#BPMS:0=120;\n#STOPS:2=0.5;\n" +
                "#NOTES:\n     dance-single:\n     :\n     Easy:\n     1:\n     0,0,0,0,0:\n" +
                "2000\n0000\n0000\n3000\n;\n";
            var map = SmChart.ToBeatmap(SmChart.Parse(sm), 0);

            Assert.AreEqual(1, map.HitObjects.Count);
            Assert.IsTrue(map.HitObjects[0].IsHold);
            Assert.AreEqual(0, map.HitObjects[0].StartTimeMs);
            Assert.AreEqual(2000, map.HitObjects[0].EndTimeMs);
        }

        [Test]
        public void ToBeatmap_No_Stops_Leaves_The_Freeze_List_Empty()
        {
            var map = SmChart.ToBeatmap(SmChart.Parse(Sample), 0);
            Assert.AreEqual(0, map.Stops.Count);
        }

        // StepMania 原生的炸彈 = 'M' (mine)。它要跟 .gn 的 note_type 1 走同一條路(IsBomb),才會用 ZD00..ZD03 顯示、
        // 踩到才引爆。'M' 永遠不是長條,也不能被算成一次判定。
        private const string Mines =
            "#TITLE:M;\n#OFFSET:0;\n#BPMS:0=120;\n#NOTES:\n     dance-single:\n     :\n     Easy:\n     1:\n" +
            "     0,0,0,0,0:\n1000\n0M00\n00M0\n0001\n;\n";

        [Test]
        public void ToBeatmap_Mine_Becomes_Bomb()
        {
            var map = SmChart.ToBeatmap(SmChart.Parse(Mines), 0);
            Assert.AreEqual(4, map.HitObjects.Count);   // 2 taps + 2 mines

            var bombs = map.HitObjects.FindAll(h => h.IsBomb);
            Assert.AreEqual(2, bombs.Count, "'M' → 炸彈");
            Assert.AreEqual(1, bombs[0].Lane); Assert.AreEqual(500, bombs[0].StartTimeMs);
            Assert.AreEqual(2, bombs[1].Lane); Assert.AreEqual(1000, bombs[1].StartTimeMs);
            foreach (var b in bombs) Assert.IsFalse(b.IsHold, "炸彈永遠不是長條");

            // 一般 note 不受影響。
            foreach (var h in map.HitObjects.FindAll(x => !x.IsBomb)) Assert.IsFalse(h.IsBomb);
        }

        [Test]
        public void Mines_Are_Not_Judged_Notes()
        {
            var s = SmChart.Parse(Mines);
            Assert.AreEqual(2, SmChart.NoteCount(s.Charts[0].NoteData), "難度排名只看可打的音符");
            Assert.AreEqual(2, SmChart.ToBeatmap(s, 0).TotalNotes, "炸彈不進滿分分母");
        }

        [Test]
        public void Lowercase_Mine_Also_Becomes_Bomb()
        {
            const string sm =
                "#TITLE:m;\n#OFFSET:0;\n#BPMS:0=120;\n#NOTES:\n     dance-single:\n     :\n     Easy:\n     1:\n" +
                "     0,0,0,0,0:\n000m\n;\n";
            var map = SmChart.ToBeatmap(SmChart.Parse(sm), 0);
            Assert.AreEqual(1, map.HitObjects.Count);
            Assert.IsTrue(map.HitObjects[0].IsBomb);
            Assert.AreEqual(3, map.HitObjects[0].Lane);
        }
    }
}
