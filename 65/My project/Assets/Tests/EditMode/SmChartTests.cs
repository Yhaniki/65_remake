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

        // Real file: "(Barry) M@GIC☆.sm" (IDOLM@STER CINDERELLA GIRLS pack) drops the ';' after #TITLE and after
        // #SUBTITLE, so a ';'-only tokenizer glues all three header lines into the title and the song list overflows
        // with "M@GIC☆ #SUBTITLE:... #ARTIST:...". A '#' starting a line ends the previous value (as StepMania does).
        [Test]
        public void Missing_Semicolon_Ends_Tag_At_Next_Line_Leading_Hash()
        {
            var song = SmChart.Parse(
                "#TITLE:M@GIC☆\r\n" +
                "#SUBTITLE:THE IDOLM@STER CINDERELLA GIRLS ANIMATION PROJECT 2nd Season 07 M@GIC☆\r\n" +
                "#ARTIST:CINDERELLA PROJECT;\r\n" +
                "#MUSIC:M@GIC☆.mp3;\r\n" +
                "#OFFSET:0.100;\r\n");
            Assert.AreEqual("M@GIC☆", song.Title);
            Assert.AreEqual("CINDERELLA PROJECT", song.Artist);
            Assert.AreEqual("M@GIC☆.mp3", song.Music);
            Assert.AreEqual(0.100, song.Offset, 1e-9);
        }

        // The implicit cut is only for a '#' at the start of a line — one inside a value is an ordinary character.
        [Test]
        public void Hash_Inside_A_Value_Is_Literal()
        {
            Assert.AreEqual("C# Sharp #1", SmChart.Parse("#TITLE:C# Sharp #1;\n#ARTIST:A;\n").Title);
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

        // ---------------------------------------------------------------------------------------------------
        // 負 BPM (warp)。StepMania 的 #BPMS 允許負值:那一段的經過時間是負的(時間倒退),要等後面**同樣時間長度**
        // 的正 BPM 把它加回來,播放頭才回到原本的時刻 —— 中間那整段拍子是一瞬間跳過去的
        // (TimingData::GetBeatAndBPSFromElapsedTime),裡面的音符看得到但連一幀判定機會都沒有。
        // ---------------------------------------------------------------------------------------------------

        // 120 BPM(1 拍 = 500ms)。beats 4..8 是 -120(倒退 2000ms),beats 8..12 是 120(補回 2000ms)→
        // 播放頭在 2000ms 這一瞬間從 beat 4 直接跳到 beat 12。每拍一顆 note,共 4 小節(beats 0..15)。
        private const string Warp =
            "#TITLE:W;\n#OFFSET:0;\n#BPMS:0=120,4=-120,8=120;\n" +
            "#NOTES:\n     dance-single:\n     :\n     Easy:\n     1:\n     0,0,0,0,0:\n" +
            "1000\n1000\n1000\n1000\n,\n" +   // beats 0,1,2,3
            "1000\n1000\n1000\n1000\n,\n" +   // beats 4,5,6,7   ← 負 BPM 段
            "1000\n1000\n1000\n1000\n,\n" +   // beats 8,9,10,11 ← 被拿去抵銷的正 BPM 段
            "1000\n1000\n1000\n1000\n;\n";    // beats 12..15    ← 譜面從這裡照原時間接上

        [Test]
        public void Parses_Negative_Bpm()
        {
            var s = SmChart.Parse(Warp);
            Assert.AreEqual(3, s.BpmValues.Count);
            Assert.AreEqual(-120.0, s.BpmValues[1], 1e-9, "負 BPM 不能被當成壞資料丟掉");
            Assert.IsTrue(s.HasNegativeBpm);
            Assert.AreEqual(120.0, s.FirstPositiveBpm, 1e-9);
        }

        [Test]
        public void Warp_Spans_The_Negative_Segment_Plus_The_Positive_Span_That_Cancels_It()
        {
            var warps = SmChart.Warps(SmChart.Parse(Warp), 16);
            Assert.AreEqual(1, warps.Count);
            Assert.AreEqual(4.0, warps[0].StartBeat, 1e-9);    // 起跳 = 負 BPM 開始的那一拍
            Assert.AreEqual(12.0, warps[0].EndBeat, 1e-9);     // 落地 = 倒退的時間被補回來的那一拍
            Assert.AreEqual(8.0, warps[0].Beats, 1e-9);        // 負 4 拍 + 抵銷用的正 4 拍
            Assert.AreEqual(2000.0, warps[0].TimeMs, 1e-6);    // 起跳與落地是**同一個**歌曲時刻
            Assert.IsTrue(warps[0].Contains(7.0));
            Assert.IsFalse(warps[0].Contains(4.0), "起跳那一拍打得到");
            Assert.IsFalse(warps[0].Contains(12.0), "落地那一拍打得到");
        }

        [Test]
        public void Warped_Notes_Are_Fake_And_The_Chart_Resumes_At_The_Same_Song_Time()
        {
            var map = SmChart.ToBeatmap(SmChart.Parse(Warp), 0);
            Assert.AreEqual(16, map.HitObjects.Count, "音符一顆都不會消失 —— 看得到,只是打不到");
            Assert.AreEqual(120.0, map.Bpm, 1e-9);

            // beats 0..3:一般音符
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(i * 500, map.HitObjects[i].StartTimeMs);
                Assert.IsFalse(map.HitObjects[i].IsFake);
            }
            // beat 4(起跳)、beats 5..11(被跳過)、beat 12(落地) 判定時刻全部是 2000ms
            for (int i = 4; i <= 12; i++) Assert.AreEqual(2000, map.HitObjects[i].StartTimeMs);
            Assert.IsFalse(map.HitObjects[4].IsFake, "起跳那一拍打得到");
            for (int i = 5; i <= 11; i++) Assert.IsTrue(map.HitObjects[i].IsFake, $"beat {i} 被 warp 掃掉");
            Assert.IsFalse(map.HitObjects[12].IsFake, "落地那一拍打得到");

            // 後面的譜照原本的時間接上 —— beat 13/14/15 = 2500/3000/3500ms
            Assert.AreEqual(2500, map.HitObjects[13].StartTimeMs);
            Assert.AreEqual(3000, map.HitObjects[14].StartTimeMs);
            Assert.AreEqual(3500, map.HitObjects[15].StartTimeMs);
        }

        [Test]
        public void Warped_Notes_Do_Not_Count_Towards_The_Total()
        {
            var song = SmChart.Parse(Warp);
            // 16 顆裡有 7 顆在 warp 內 → 實際要打的只有 9 顆。
            // (StepMania 3.9 其實會把那 7 顆算進 note 總數,但它們永遠打不到 —— 這裡刻意不算。)
            Assert.AreEqual(9, SmChart.ToBeatmap(song, 0).TotalNotes);
            Assert.AreEqual(9, SmChart.PlayableNoteCount(song, 0));
            Assert.AreEqual(16, SmChart.NoteCount(song.Charts[0].NoteData), "逐字掃 note body 的顆數不變");
        }

        // warp 在時間軸上沒有厚度,但畫面上仍然要照拍子鋪開(StepMania 3.9 用 beat spacing 擺音符),
        // 所以顯示用時間 (ScrollTimeMs) 會和判定時間分家:整段被跳過的拍子攤在判定時刻前 1ms 的窗裡。
        [Test]
        public void Warped_Notes_Keep_Beat_Spacing_On_The_Highway()
        {
            var map = SmChart.ToBeatmap(SmChart.Parse(Warp), 0);
            for (int i = 0; i < 3; i++)
                Assert.AreEqual(map.HitObjects[i].StartTimeMs, map.HitObjects[i].ScrollTimeMs, 1e-9,
                    "warp 以外的音符,顯示時間就是判定時間");

            // 起跳拍 → 窗頭;warp 內第 k 拍 → 窗內第 k/8;落地拍 → 窗尾(= warp 的時刻)
            double win = 2000.0 - SmChart.WarpDisplayMs;
            for (int i = 4; i <= 12; i++)
                Assert.AreEqual(win + SmChart.WarpDisplayMs * (i - 4) / 8.0, map.HitObjects[i].ScrollTimeMs, 1e-9);

            // 位置照拍子等距展開:warp 前 1.5ms(播放頭還沒跳)時,相鄰兩顆的距離都等於「一拍」。
            var scroll = ManiaScroll.Build(map, 1.0);
            double now = win - 0.5;
            double oneBeat = scroll.PixelDistance(now, 500) - scroll.PixelDistance(now, 0);   // 120bpm 的一拍
            Assert.Greater(oneBeat, 0.0);
            for (int i = 4; i < 12; i++)
            {
                double d = scroll.PixelDistance(now, map.HitObjects[i + 1].ScrollTimeMs)
                         - scroll.PixelDistance(now, map.HitObjects[i].ScrollTimeMs);
                Assert.AreEqual(oneBeat, d, oneBeat * 1e-6, $"beat {i}→{i + 1} 在畫面上就是一拍的距離");
            }

            // 播放頭掃過那 1ms 之後,整批 warp 音符瞬間到判定線後方,落地那一拍剛好落在判定線上。
            Assert.AreEqual(0.0, scroll.PixelDistance(2000.0, map.HitObjects[12].ScrollTimeMs), 1e-6);
            for (int i = 4; i <= 11; i++)
                Assert.Less(scroll.PixelDistance(2000.0, map.HitObjects[i].ScrollTimeMs), 0.0);
        }

        [Test]
        public void Warped_Mine_Is_Fake_Too()
        {
            // beat 5(warp 內)放一顆 mine:看得到,但播放頭是瞬間跳過的 → 踩不到,也不該引爆。
            const string sm =
                "#TITLE:WM;\n#OFFSET:0;\n#BPMS:0=120,4=-120,8=120;\n" +
                "#NOTES:\n     dance-single:\n     :\n     Easy:\n     1:\n     0,0,0,0,0:\n" +
                "0000\n0000\n0000\n0000\n,\n0000\nM000\n0000\n0000\n,\n0000\n0000\n0000\n0000\n,\n1000\n;\n";
            var map = SmChart.ToBeatmap(SmChart.Parse(sm), 0);
            var mine = map.HitObjects.Find(h => h.IsBomb);
            Assert.IsTrue(mine.IsBomb);
            Assert.IsTrue(mine.IsFake, "warp 裡的炸彈也是裝飾");
            Assert.AreEqual(1, map.TotalNotes, "beat 12 的那顆 tap;炸彈與 warp 音符都不算");
        }

        [Test]
        public void Unterminated_Negative_Bpm_Warps_To_The_End_Of_The_Chart()
        {
            // beat 8 之後一路負 BPM,永遠沒有正 BPM 來抵銷 → 後面整段都是打不到的裝飾音。
            const string sm =
                "#TITLE:U;\n#OFFSET:0;\n#BPMS:0=120,8=-120;\n" +
                "#NOTES:\n     dance-single:\n     :\n     Easy:\n     1:\n     0,0,0,0,0:\n" +
                "1000\n1000\n1000\n1000\n,\n1000\n1000\n1000\n1000\n,\n1000\n1000\n1000\n1000\n;\n";
            var map = SmChart.ToBeatmap(SmChart.Parse(sm), 0);
            Assert.AreEqual(12, map.HitObjects.Count);
            for (int i = 0; i <= 8; i++) Assert.IsFalse(map.HitObjects[i].IsFake, $"beat {i} 還打得到");
            for (int i = 9; i < 12; i++) Assert.IsTrue(map.HitObjects[i].IsFake, $"beat {i} 被 warp 掃掉");
            Assert.AreEqual(9, map.TotalNotes);
        }

        [Test]
        public void Negative_Bpm_At_Beat_Zero_Still_Reports_A_Positive_Header_Bpm()
        {
            // 表頭 BPM 是判定窗(依 BPM 換算 tick)與選歌畫面的來源,拿到負數整個會壞掉 → 取第一個正的。
            var s = SmChart.Parse("#TITLE:N;\n#OFFSET:0;\n#BPMS:0=-200,1=150;\n");
            Assert.AreEqual(-200.0, s.FirstBpm, 1e-9);
            Assert.AreEqual(150.0, s.FirstPositiveBpm, 1e-9);
        }

        // ---------------------------------------------------------------------------------------------------
        // 負 BPM 中間夾 #STOPS —— gimmick 譜的正戲(engine[Blue] 那種:一連串 4 拍負 / 4 拍正,接縫上各放一個
        // 停拍)。StepMania 在停拍那一拍**定格**(GetBeatAndBPSFromElapsedTime 走到停拍時 bFreezeOut=true),
        // 玩家看得到「停住的那一瞬間」;停完才一口氣跳過下一段拍子。畫面上就是一格一格的定格動畫。
        // ---------------------------------------------------------------------------------------------------

        // 120 BPM。beats 4..8 負、8..12 正抵銷(warp 1);beat 12 停 0.5 秒;beats 12..16 負、16..20 正(warp 2)。
        private const string WarpStopBody =
            "1000\n1000\n1000\n1000\n,\n" +   // 0..3
            "1000\n1000\n1000\n1000\n,\n" +   // 4..7    ← 負 BPM(warp 1)
            "1000\n1000\n1000\n1000\n,\n" +   // 8..11   ← 抵銷 warp 1 的正 BPM
            "1000\n1000\n1000\n1000\n,\n" +   // 12..15  ← 定格在 beat 12,然後又是負 BPM(warp 2)
            "1000\n1000\n1000\n1000\n,\n" +   // 16..19  ← 抵銷 warp 2 的正 BPM
            "1000\n1000\n1000\n1000\n;\n";    // 20..23  ← 照原時間接上

        private const string WarpStopHead =
            "#NOTES:\n     dance-single:\n     :\n     Easy:\n     1:\n     0,0,0,0,0:\n";

        private const string WarpStop =
            "#TITLE:WS;\n#OFFSET:0;\n#BPMS:0=120,4=-120,8=120,12=-120,16=120;\n#STOPS:12.000=0.500;\n" +
            WarpStopHead + WarpStopBody;

        // 真實譜的寫法:停拍被寫在負 BPM 段起拍**之後**零點幾拍(engine[Blue] 是 #BPMS 204.667=-174 配
        // #STOPS 204.668),好讓 StepMania 認定它落在負段裡。作者要的是「停拍與負 BPM 同時」,那零點幾拍
        // 純粹是精度 —— 兩種寫法的畫面必須一模一樣。
        private const string WarpStopOffByAHair =
            "#TITLE:WS2;\n#OFFSET:0;\n#BPMS:0=120,4=-120,8=120,12=-120,16=120;\n#STOPS:12.001=0.500;\n" +
            WarpStopHead + WarpStopBody;

        [Test]
        public void Stop_On_A_Negative_Bpm_Beat_Freezes_The_Highway_And_Leaves_The_Warp_Window_Alone()
        {
            var map = SmChart.ToBeatmap(SmChart.Parse(WarpStop), 0);
            Assert.AreEqual(1, map.Stops.Count);
            Assert.AreEqual(2000.0, map.Stops[0].TimeMs, 1e-6, "定格從 warp 落地的那一刻開始");
            Assert.AreEqual(500.0 - SmChart.WarpDisplayMs, map.Stops[0].DurationMs, 1e-6,
                "最後 1ms 要讓給下一段 warp 的超高速窗 —— 蓋住的話那段音符會全部疊在判定線上,定格畫面等於空白");
        }

        [Test]
        public void The_Beat_The_Chart_Freezes_On_Is_Still_Hittable()
        {
            var map = SmChart.ToBeatmap(SmChart.Parse(WarpStop), 0);
            Assert.AreEqual(24, map.HitObjects.Count);
            Assert.AreEqual(2000, map.HitObjects[12].StartTimeMs);
            Assert.AreEqual(2000.0, map.HitObjects[12].ScrollTimeMs, 1e-6);
            Assert.IsFalse(map.HitObjects[12].IsFake, "定格時播放頭就停在這一拍上 —— 打得到");
            for (int i = 13; i <= 19; i++) Assert.IsTrue(map.HitObjects[i].IsFake, $"beat {i} 被第二段 warp 掃掉");
            Assert.IsFalse(map.HitObjects[20].IsFake, "第二段 warp 的落地拍");
            Assert.AreEqual(3000, map.HitObjects[21].StartTimeMs, "停完之後照原時間接上(2500 + 停的 500)");
        }

        // 這一題就是使用者回報的畫面:定格的那 0.5 秒,畫面上要看得到「停住的那一瞬間」——
        // 判定線上是定格的那一拍,後面被 warp 掃掉的拍子照拍距排在上方,而且完全不動。
        [Test]
        public void Frozen_Frame_Shows_The_Warped_Notes_Spread_By_Beat_And_Standing_Still()
        {
            var map = SmChart.ToBeatmap(SmChart.Parse(WarpStop), 0);
            var scroll = ManiaScroll.Build(map, 1.0);
            double oneBeat = scroll.PixelDistance(0.0, 500.0);   // 120bpm 的一拍
            Assert.Greater(oneBeat, 0.0);

            foreach (double now in new[] { 2000.0, 2000.5, 2100.0, 2400.0, 2498.0 })
            {
                Assert.AreEqual(0.0, scroll.PixelDistance(now, map.HitObjects[12].ScrollTimeMs), 1e-6,
                    $"now={now}:定格的那一拍停在判定線上");
                for (int i = 13; i <= 19; i++)
                    Assert.AreEqual(oneBeat * (i - 12),
                        scroll.PixelDistance(now, map.HitObjects[i].ScrollTimeMs), oneBeat * 1e-6,
                        $"now={now}:beat {i} 停在判定線上方 {i - 12} 拍");
            }

            // 定格結束、第二段 warp 刷過去之後:整批到判定線後方,落地那一拍剛好在判定線上。
            Assert.AreEqual(0.0, scroll.PixelDistance(2500.0, map.HitObjects[20].ScrollTimeMs), 1e-6);
            for (int i = 13; i <= 19; i++)
                Assert.Less(scroll.PixelDistance(2500.0, map.HitObjects[i].ScrollTimeMs), 0.0);
        }

        [Test]
        public void A_Stop_Written_A_Hair_After_The_Negative_Bpm_Freezes_The_Same_Frame()
        {
            var map = SmChart.ToBeatmap(SmChart.Parse(WarpStopOffByAHair), 0);
            var scroll = ManiaScroll.Build(map, 1.0);
            double oneBeat = scroll.PixelDistance(0.0, 500.0);

            Assert.IsFalse(map.HitObjects[12].IsFake, "停拍晚寫那 0.001 拍,不能讓這一拍變成打不到的裝飾音");
            Assert.AreEqual(2000.0, map.HitObjects[12].ScrollTimeMs, 1e-6, "定格那一拍對齊 warp 落地的時刻");
            Assert.AreEqual(2000.0, map.Stops[0].TimeMs, 1e-6);

            // 定格中的畫面與「停拍寫在同一拍」完全一樣(容差取一拍的 0.2% —— 就是那 0.001 拍的精度)。
            foreach (double now in new[] { 2100.0, 2400.0 })
            {
                Assert.AreEqual(0.0, scroll.PixelDistance(now, map.HitObjects[12].ScrollTimeMs), 1e-6);
                for (int i = 13; i <= 19; i++)
                    Assert.AreEqual(oneBeat * (i - 12),
                        scroll.PixelDistance(now, map.HitObjects[i].ScrollTimeMs), oneBeat * 0.002,
                        $"now={now}:beat {i} 停在判定線上方 {i - 12} 拍");
            }
        }

        [Test]
        public void A_Stop_Buried_Inside_A_Warp_Does_Not_Freeze_The_Highway()
        {
            // beat 6 的停拍埋在 warp(4→11.8)裡面:播放頭是瞬間跳過那段拍子的,StepMania 只把它的秒數
            // 扣掉(負段算出來的 fFreezeStartSecond 是負的 → 定格條件不成立),畫面不會停。
            const string sm =
                "#TITLE:WB;\n#OFFSET:0;\n#BPMS:0=120,4=-120,8=120;\n#STOPS:6.000=0.100;\n" +
                WarpStopHead + "1000\n1000\n1000\n1000\n,\n1000\n1000\n1000\n1000\n,\n" +
                "1000\n1000\n1000\n1000\n,\n1000\n1000\n1000\n1000\n;\n";
            var map = SmChart.ToBeatmap(SmChart.Parse(sm), 0);
            Assert.AreEqual(0, map.Stops.Count, "warp 內部的停拍不定格");
            var warps = SmChart.Warps(SmChart.Parse(sm), 16);
            Assert.AreEqual(1, warps.Count);
            Assert.AreEqual(11.8, warps[0].EndBeat, 1e-9, "那 100ms 讓 warp 晚 0.2 拍才落地");
        }

        [Test]
        public void Charts_Without_Negative_Bpm_Have_No_Warps_And_No_Fake_Notes()
        {
            var song = SmChart.Parse(Sample);
            Assert.IsFalse(song.HasNegativeBpm);
            Assert.AreEqual(0, SmChart.Warps(song, 4).Count);
            var map = SmChart.ToBeatmap(song, 0);
            foreach (var h in map.HitObjects)
            {
                Assert.IsFalse(h.IsFake);
                Assert.AreEqual(h.StartTimeMs, h.ScrollTimeMs, 1e-9);
            }
            Assert.AreEqual(1, map.TimingPoints.Count, "沒有 warp 就不會多送 timing point");
        }
    }
}
