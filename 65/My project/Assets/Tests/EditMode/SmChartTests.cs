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

        // ---------------------------------------------------------------------------------------------------
        // 落單的 '3'(hold cap 沒有配對的 '2'/'4' 頭)。StepMania **不會**丟掉它:
        // NoteData::Convert2sAnd3sToHoldNotes 只把「有頭的那一對」清成 TAP_EMPTY,落單的 '3' 原封不動留在譜上
        // (type = TapNote::hold_tail),NoteField.cpp:645 的 DrawTap 只分 mine/attack → 畫成一般箭頭,
        // GetNumTapNotes 也照樣算它一顆。這是 .dwi 轉檔常見的裝飾音寫法:deadsoul[Blue](Blue's 6th step)
        // 整段負 BPM 的假 note 全是落單 '3'(964 顆),丟掉的話那段 gimmick 在畫面上完全是空的。
        // ---------------------------------------------------------------------------------------------------

        // 16 分格線的一小節(16 行);第 row 行放 cells,其餘空白。row < 0 → 整節空白。
        private static string Measure16(int row, string cells)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 16; i++) sb.Append(i == row ? cells : "0000").Append('\n');
            return sb.ToString();
        }

        private const string Head =
            "#NOTES:\n     dance-single:\n     :\n     Easy:\n     1:\n     0,0,0,0,0:\n";

        [Test]
        public void Orphan_Hold_Cap_Is_A_Normal_Tap()
        {
            // lane 0:beat 0 一顆 tap、beat 2 一顆**沒有頭**的 cap。120 BPM → 一拍 500ms。
            const string sm = "#TITLE:OC;\n#OFFSET:0;\n#BPMS:0=120;\n" + Head + "1000\n0000\n3000\n0000\n;\n";
            var song = SmChart.Parse(sm);
            var map = SmChart.ToBeatmap(song, 0);
            Assert.AreEqual(2, map.HitObjects.Count, "落單的 cap 不能被丟掉");
            Assert.IsFalse(map.HitObjects[1].IsHold, "沒有配對的頭 → 就是一顆一般 note");
            Assert.AreEqual(0, map.HitObjects[1].Lane);
            Assert.AreEqual(1000, map.HitObjects[1].StartTimeMs);
            Assert.IsFalse(map.HitObjects[1].IsFake, "不在 warp 裡 → 打得到");
            Assert.AreEqual(2, map.TotalNotes);
            Assert.AreEqual(2, SmChart.NoteCount(song.Charts[0].NoteData));
            Assert.AreEqual(2, SmChart.PlayableNoteCount(song, 0));
        }

        [Test]
        public void NoteCount_Counts_An_Orphan_Cap_But_Not_A_Paired_One()
        {
            // lane 0:beat 0 '2' → beat 2 '3'(配對 = 一個長條,算一顆);lane 1:beat 3 落單的 '3'(算一顆)
            const string sm = "#TITLE:PC;\n#OFFSET:0;\n#BPMS:0=120;\n" + Head + "2000\n0000\n3000\n0300\n;\n";
            var song = SmChart.Parse(sm);
            Assert.AreEqual(2, SmChart.NoteCount(song.Charts[0].NoteData));
            var map = SmChart.ToBeatmap(song, 0);
            Assert.AreEqual(2, map.HitObjects.Count);
            Assert.IsTrue(map.HitObjects[0].IsHold, "配對的那一組 = 長條");
            Assert.IsFalse(map.HitObjects[1].IsHold, "落單的那一顆 = tap");
            Assert.AreEqual(1, map.HitObjects[1].Lane);
            Assert.AreEqual(1500, map.HitObjects[1].StartTimeMs);
        }

        [Test]
        public void Orphan_Hold_Caps_Inside_A_Warp_Are_The_Fake_Notes_You_See_But_Cannot_Hit()
        {
            // deadsoul[Blue](Blue's 6th step)的寫法:負 BPM 那一段的裝飾音全是落單的 '3'。
            // 時序與 Warp 一樣:beats 4..12 一瞬間跳過(2000ms)。
            const string sm =
                "#TITLE:OW;\n#OFFSET:0;\n#BPMS:0=120,4=-120,8=120;\n" + Head +
                "0000\n0000\n0000\n0000\n,\n" +   // beats 0..3
                "3000\n3000\n3000\n3000\n,\n" +   // beats 4..7   ← 負 BPM 段的裝飾音
                "3000\n3000\n3000\n3000\n,\n" +   // beats 8..11  ← 抵銷段,也是裝飾音
                "1000\n0000\n0000\n0000\n;\n";    // beat 12      ← 落地,打得到
            var song = SmChart.Parse(sm);
            var map = SmChart.ToBeatmap(song, 0);
            Assert.AreEqual(9, map.HitObjects.Count, "8 顆落單 cap + beat 12 的 tap,一顆都不能少");
            foreach (var h in map.HitObjects)
            {
                Assert.IsFalse(h.IsHold, "沒有頭的 cap 不是長條");
                Assert.AreEqual(2000, h.StartTimeMs, "warp 內的判定時刻全部是同一瞬間");
            }
            Assert.IsFalse(map.HitObjects[0].IsFake, "beat 4 是起跳拍,打得到");
            for (int i = 1; i <= 7; i++) Assert.IsTrue(map.HitObjects[i].IsFake, $"beat {i + 4} 被 warp 掃掉");
            Assert.IsFalse(map.HitObjects[8].IsFake, "beat 12 是落地拍,打得到");
            Assert.AreEqual(2, map.TotalNotes, "起跳拍 + 落地拍;warp 內那 7 顆是裝飾");

            // 畫面上照拍子鋪開(和一般 warp 音符同一條規則)
            double win = 2000.0 - SmChart.WarpDisplayMs;
            for (int i = 0; i <= 8; i++)
                Assert.AreEqual(win + SmChart.WarpDisplayMs * i / 8.0, map.HitObjects[i].ScrollTimeMs, 1e-9);
        }

        // ---------------------------------------------------------------------------------------------------
        // 長條的 cap(放開點)落在 warp 裡:頭部在真實時間上打得到,但播放頭是一瞬間跳過那段拍子的 ——
        // 玩家永遠碰不到「該放開」的時刻,所以結尾不判定(StepMania Player.cpp:407 直接給 HNS_OK)。
        // 整條仍然照 beat 間距畫出來。deadsoul[Blue] 有 62 條這種。
        // ---------------------------------------------------------------------------------------------------

        [Test]
        public void A_Hold_Whose_Cap_Is_Warped_Keeps_Its_Bar_And_Needs_No_Release()
        {
            // 頭在 beat 3(1500ms,打得到)、cap 在 beat 6(warp 內)。
            const string sm =
                "#TITLE:HW;\n#OFFSET:0;\n#BPMS:0=120,4=-120,8=120;\n" + Head +
                "0000\n0000\n0000\n2000\n,\n" +   // beat 3  = 長條頭
                "0000\n0000\n3000\n0000\n,\n" +   // beat 6  = cap(warp 內)
                "0000\n0000\n0000\n0000\n,\n" +
                "1000\n0000\n0000\n0000\n;\n";    // beat 12 = 落地拍的 tap
            var map = SmChart.ToBeatmap(SmChart.Parse(sm), 0);
            Assert.AreEqual(2, map.HitObjects.Count);
            var hold = map.HitObjects[0];
            Assert.IsTrue(hold.IsHold, "整條長條還在 —— 看得到");
            Assert.IsFalse(hold.IsFake, "頭部在真實時間上,打得到");
            Assert.IsTrue(hold.IsFakeTail, "cap 被 warp 掃掉 → 結尾不用放開");
            Assert.AreEqual(1500, hold.StartTimeMs);
            Assert.AreEqual(2000, hold.EndTimeMs.Value, "判定上的尾端 = warp 那一瞬間(只用來決定何時從畫面收掉)");
            Assert.AreEqual(1500.0, hold.ScrollTimeMs, 1e-9);
            Assert.AreEqual(2000.0 - SmChart.WarpDisplayMs + SmChart.WarpDisplayMs * (6.0 - 4.0) / 8.0,
                hold.ScrollEndTimeMs, 1e-9, "cap 照拍子擺在 warp 的顯示窗裡");
            Assert.AreEqual(2, map.TotalNotes, "長條只算頭部一個判定 + beat 12 的 tap");
        }

        [Test]
        public void A_Warped_Cap_Hold_Is_Not_Collapsed_By_The_Short_Hold_Rule()
        {
            // 480 BPM(一拍 125ms):頭在 beat 3.75 (469ms)、cap 在 beat 6(warp 內 → 判定尾端 = 500ms)。
            // 判定長度只有 31ms,遠低於「無理短長條」門檻 —— 但它短只是因為尾端被夾到 warp 那一瞬間,
            // 拍子上整條是 2.25 拍;收成 tap 就把整條裝飾長條從畫面上抹掉了。
            string sm =
                "#TITLE:HS;\n#OFFSET:0;\n#BPMS:0=480,4=-480,8=480;\n" + Head +
                Measure16(15, "2000") + ",\n" +   // beat 3.75 = 長條頭
                Measure16(8, "3000") + ",\n" +    // beat 6    = cap(warp 內)
                Measure16(-1, "") + ",\n" +
                "1000\n0000\n0000\n0000\n;\n";    // beat 12   = 落地拍的 tap
            var map = SmChart.ToBeatmap(SmChart.Parse(sm), 0);
            var hold = map.HitObjects[0];
            Assert.IsTrue(hold.IsHold);
            Assert.IsTrue(hold.IsFakeTail);
            Assert.Less(hold.EndTimeMs.Value - hold.StartTimeMs, OsuBeatmap.ShortHoldMaxMs,
                "判定長度確實短到會踩進「無理短長條」門檻");
            Assert.AreEqual(0, map.CollapseShortHolds(), "cap 被 warp 掃掉的長條不收");
            Assert.IsTrue(map.HitObjects[0].IsHold, "整條還在");
        }

        // ---------------------------------------------------------------------------------------------------
        // warp 的顯示窗速率。兩個坑都來自同一件事:60000/BPM 大多不能精確表示(4224 → 14.204545…),所以
        // 「-N 拍再 +N 拍抵銷」算出來的落地拍常常比譜面的切點差幾個 ULP,而 FillTimingPoints 的去重
        // (<= 1e-9)會把精確的那個切點吃掉,只留下差幾個 ULP 的那個 —— 於是速率被讀成「切點左邊」那一段。
        //   坑 1:兩個 warp 首尾相接(±BPM 乒乓 + 中間夾 stop)→ 後一個 warp 的**窗首沒有 timing point**,
        //         前半窗沿用上一點的速率 → 窗內音符被壓成一疊(使用者看到的「炸彈被壓扁」)。
        //   坑 2:warp 落地在一個 BPM 變化上 → 落地後整段用**warp 前**的 BPM 捲動(實測快 10 倍)。
        // ---------------------------------------------------------------------------------------------------

        // 5 小節四分音符,每拍換一軌 —— warp 窗裡才會有好幾顆可以量間距的音符。
        private const string Body20 =
            "1000\n0100\n0010\n0001\n,\n" +
            "1000\n0100\n0010\n0001\n,\n" +
            "1000\n0100\n0010\n0001\n,\n" +
            "1000\n0100\n0010\n0001\n,\n" +
            "1000\n0100\n0010\n0001\n;\n";

        // 157 BPM(一拍 382.1656ms,60000/157 不能精確表示)。beats 12..14.5 與 14.5..17 是兩個首尾相接的
        // warp(-157 走 1.25 拍、+157 補 1.25 拍),接縫 beat 14.5 上放一個 0.5 秒的 stop 把兩個 warp 在
        // 時間上分開 —— 和 deadsoul[Blue] 的 -4224 段同一個寫法。落地拍會算出 14.499999999999998。
        private const string ChainedWarps =
            "#TITLE:CW;\n#OFFSET:0;\n#BPMS:0=157,12=-157,13.25=157,14.5=-157,15.75=157,17=157;\n" +
            "#STOPS:14.500=0.500;\n" + Head + Body20;

        // 195 BPM 主速度;beats 16..18 是一個 warp(-1950 走 1 拍、+1950 補 1 拍),落地拍 18 正好是
        // 「BPM 回到 195」的切點。落地拍會算出 17.99999999999999。
        private const string WarpLandingOnBpmChange =
            "#TITLE:PW;\n#OFFSET:0;\n#BPMS:0=195,16=-1950,17=1950,18=195;\n" + Head + Body20;

        // 一拍在畫面上的距離(px)。基準速度 = ManiaScroll 解出來的 base beat length,和 warp 無關。
        private static double OneBeatPx(OsuBeatmap map, ManiaScroll scroll)
        {
            var noteMs = new System.Collections.Generic.List<double>();
            double last = 0.0;
            foreach (var h in map.HitObjects)
            {
                noteMs.Add(h.StartTimeMs);
                double t = h.EndTimeMs ?? h.StartTimeMs;
                if (t > last) last = t;
            }
            return scroll.BaseVelocity / 1000.0
                 * ManiaScroll.BaseBeatLength(map.TimingPoints, noteMs, last, map.Bpm);
        }

        [Test]
        public void Chained_Warps_Each_Keep_Their_Own_Full_Speed_Display_Window()
        {
            var song = SmChart.Parse(ChainedWarps);
            var warps = SmChart.Warps(song, 20.0);
            Assert.AreEqual(2, warps.Count);
            Assert.AreEqual(14.5, warps[0].EndBeat, 1e-9);
            Assert.AreEqual(14.5, warps[1].StartBeat, 1e-9, "兩個 warp 首尾相接(接縫在 beat 14.5)");

            var map = SmChart.ToBeatmap(song, 0);
            var scroll = ManiaScroll.Build(map, 1.0);
            double oneBeat = OneBeatPx(map, scroll);
            Assert.Greater(oneBeat, 0.0);

            // 每個 warp 的整個顯示窗都要照拍子鋪滿 —— 而且是**線性**的(四等分各佔四分之一)。
            // 修好之前:第二個 warp 的前半窗只有 0.05px(該有 49.68px),整窗 99.5px(該有 198.7px)。
            for (int i = 0; i < warps.Count; i++)
            {
                double we = warps[i].TimeMs, ws = we - SmChart.WarpDisplayMs;
                double want = warps[i].Beats * oneBeat;
                Assert.AreEqual(want, scroll.PixelDistance(ws, we), want * 1e-6,
                    $"warp[{i}] 的顯示窗要剛好等於 {warps[i].Beats} 拍");
                for (int k = 0; k < 4; k++)
                    Assert.AreEqual(want / 4.0,
                        scroll.PixelDistance(ws + (we - ws) * k / 4.0, ws + (we - ws) * (k + 1) / 4.0),
                        want * 1e-6, $"warp[{i}] 窗內第 {k + 1}/4 段:速率不能中途變(變了就是一半被壓扁)");
            }
        }

        [Test]
        public void Every_Warp_Window_Gets_Its_Own_Timing_Point()
        {
            // 窗首一定要有自己的 timing point(值 = 整段拍數壓進 WarpDisplayMs)。接縫那一拍的 DisplayMs
            // 指向的是**上一個** warp 的落地時刻,所以靠切點是拿不到窗首的。
            var song = SmChart.Parse(ChainedWarps);
            var warps = SmChart.Warps(song, 20.0);
            var map = SmChart.ToBeatmap(song, 0);
            foreach (var w in warps)
            {
                double ws = w.TimeMs - SmChart.WarpDisplayMs;
                double wantBl = SmChart.WarpDisplayMs / w.Beats;
                bool found = false;
                foreach (var tp in map.TimingPoints)
                    if (System.Math.Abs(tp.TimeMs - ws) <= 1e-9 &&
                        System.Math.Abs(tp.BeatLength - wantBl) <= wantBl * 1e-9)
                        found = true;
                Assert.IsTrue(found, $"warp {w.StartBeat}→{w.EndBeat} 的窗首 {ws} 少了 beatLength {wantBl} 的 timing point");
            }
        }

        [Test]
        public void The_Editor_Grid_Line_On_A_Chained_Seam_Sits_At_The_Start_Of_Its_Freeze()
        {
            // 同一個浮點刀鋒也會打壞**編輯器格線**,症狀完全不同:不是速率,是格線整體晚了一整個 #STOPS。
            // BeatGrid.BeatToMs 判斷「站在停拍上」用的是沒有容差的 `beat > _segBeat[s]`(BeatGrid.cs),
            // 所以 GridSegment.StartBeat 只要比切點小幾個 ULP,問 BeatToMs(切點) 就會把定格加進去 ——
            // 小節線被畫到定格**結束**的位置,離它自己的音符整整一個停拍。實測出貨譜 3 首共 22 條線會歪
            // (deadsoul[Blue] 是 228/228/228/228/223 ms)。
            var song = SmChart.Parse(ChainedWarps);
            var warps = SmChart.Warps(song, 20.0);
            var map = SmChart.ToBeatmap(song, 0);

            GridSegment seam = default(GridSegment);
            bool found = false;
            foreach (var g in map.GridSegments)
                if (g.StopMs > 0.0) { seam = g; found = true; break; }
            Assert.IsTrue(found, "接縫上的停拍要有自己的 grid segment");
            Assert.AreEqual(14.5, seam.StartBeat, 0.0, "grid segment 的 StartBeat 要**精確**落在譜面的切點上");

            var grid = new BeatGrid(map.GridSegments);
            Assert.AreEqual(warps[0].TimeMs, grid.BeatToMs(14.5), 1e-9,
                "beat 14.5 的小節線要畫在定格**開始**(= 前一個 warp 落地那一刻),不是定格結束");
            Assert.AreEqual(warps[1].TimeMs, grid.BeatToMs(15.0), 1e-9,
                "定格之後的拍子才落在定格結束之後");
        }

        [Test]
        public void A_Warp_Landing_On_A_Bpm_Change_Resumes_At_The_New_Tempo()
        {
            var song = SmChart.Parse(WarpLandingOnBpmChange);
            var warps = SmChart.Warps(song, 20.0);
            Assert.AreEqual(1, warps.Count);
            Assert.AreEqual(16.0, warps[0].StartBeat, 1e-9);
            Assert.AreEqual(18.0, warps[0].EndBeat, 0.0,
                "落地拍要**精確**貼在譜面的切點上 —— 差幾個 ULP 就會讀到切點左邊那一段的速度");

            var map = SmChart.ToBeatmap(song, 0);
            var scroll = ManiaScroll.Build(map, 1.0);
            double oneBeat = OneBeatPx(map, scroll);
            double beatMs = 60000.0 / 195.0;   // 落地之後回到 195 BPM

            // 落地後一拍的距離就是「一拍」。修好之前這裡是 10 倍(拿到 warp 內 1950 BPM 那段的速率)。
            double t0 = warps[0].TimeMs + 0.01;
            Assert.AreEqual(oneBeat, scroll.PixelDistance(t0, t0 + beatMs), oneBeat * 1e-6,
                "warp 落地之後要用落地拍那一段的 BPM 捲動,不是 warp 裡面那個超快 BPM");
        }

        [Test]
        public void A_Hold_Entirely_Inside_A_Warp_Is_Plain_Decoration()
        {
            // 頭在 beat 5、cap 在 beat 7,兩端都在 warp 內:判定時刻一樣(都是 warp 那一瞬間)→ 沒有長度可言,
            // 整顆就是打不到的裝飾(IsFake)。IsFakeTail 是給「頭打得到、只有結尾被掃掉」用的,這裡不標。
            const string sm =
                "#TITLE:HI;\n#OFFSET:0;\n#BPMS:0=120,4=-120,8=120;\n" + Head +
                "0000\n0000\n0000\n0000\n,\n" +
                "0000\n2000\n0000\n3000\n,\n" +   // beat 5 = 頭, beat 7 = cap(都在 warp 內)
                "0000\n0000\n0000\n0000\n,\n" +
                "1000\n0000\n0000\n0000\n;\n";
            var map = SmChart.ToBeatmap(SmChart.Parse(sm), 0);
            Assert.AreEqual(2, map.HitObjects.Count);
            Assert.IsTrue(map.HitObjects[0].IsFake);
            Assert.IsFalse(map.HitObjects[0].IsFakeTail, "整條都打不到 → 走 IsFake,不重複標");
            Assert.IsFalse(map.HitObjects[0].IsHold, "頭尾判定時刻相同 → 沒有長條可判定");
            Assert.AreEqual(1, map.TotalNotes, "只有 beat 12 那顆 tap");
        }
    }
}
