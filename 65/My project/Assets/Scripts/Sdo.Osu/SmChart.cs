using System;
using System.Collections.Generic;
using System.Globalization;

namespace Sdo.Osu
{
    /// <summary>
    /// StepMania <c>.sm</c> (simfile) reader → <see cref="OsuBeatmap"/>, so external StepMania songs play through
    /// the same gameplay path as .gn / .osu. Only the 4-panel <c>dance-single</c> charts are used (the game highway
    /// is hardwired to 4K — see ScreenGameplay); other StepsType blocks are ignored.
    ///
    /// Format (verified against assets/SM-YHANIKI-master, StepMania 3.9):
    ///   MSD tags <c>#TAG:v1:v2:...;</c>. Header tags: #TITLE #ARTIST #MUSIC #BANNER #BACKGROUND #CDTITLE #OFFSET #BPMS.
    ///   #NOTES has 6 fields then the note body: StepsType:Description:Difficulty:Meter:Radar:NoteData .
    ///   Note body: measures separated by ',', each measure = N rows (one line each), one char per column
    ///   (dance-single order = Left,Down,Up,Right → lanes 0..3). Char: 0 empty, 1 tap, 2 hold-head, 3 hold/roll-tail,
    ///   4 roll-head (treated as hold), M mine (ignored). Row beat = (measureIndex + row/rowsInMeasure)*4.
    ///   Timing: note seconds = beatToSeconds(beat, #BPMS) − #OFFSET + Σ(stops strictly before the note's beat).
    ///   (StepMania TimingData.GetElapsedTimeFromBeat: subtracts m_fBeat0OffsetInSeconds, then adds every
    ///   #STOPS/#FREEZES whose start beat is < the note's beat — a stop exactly AT the beat comes before the stop,
    ///   so it is NOT added; verified NotesLoaderSM.cpp:141 + TimingData.cpp:162-174.) A stop also freezes the
    ///   highway for its duration — carried on <see cref="OsuBeatmap.Stops"/> and applied by <see cref="ManiaScroll"/>.
    ///
    /// 負 BPM (warp) —— 見 <see cref="Warps"/>:StepMania 的 #BPMS 允許負值,那一段的「經過時間」是**負的**,
    /// 所以歌曲時間會倒退;要等後面同樣長度的正 BPM 把它加回來,播放頭才回到原本的時刻,後面的譜就照原時間接上。
    /// 這中間的拍子播放頭是**一瞬間跳過去**的(TimingData::GetBeatAndBPSFromElapsedTime 在負段
    /// `fSecondsInThisSegment` 為負 → 條件不成立 → 直接 `fElapsedTime -= fSecondsInThisSegment` 把時間加回去,
    /// 一口氣跳到後面),裡面的音符看得到卻連一幀判定機會都沒有 → 標成 <see cref="OsuHitObject.IsFake"/>。
    /// See doc/SM_GN_NOTE_FORMAT.md.
    /// </summary>
    public static class SmChart
    {
        public sealed class SmNotes
        {
            public string StepsType = "";
            public string Difficulty = "";
            public int Meter;
            public string NoteData = "";
        }

        public sealed class SmSong
        {
            public string Title = "";
            public string Artist = "";
            public string Music = "";
            public string Banner = "";
            public string Background = "";
            public string CdTitle = "";
            public double Offset;
            public double SampleStart = -1.0;   // #SAMPLESTART 秒；-1 = 未指定 → 試聽退回中段
            public double SampleLength = -1.0;  // #SAMPLELENGTH 秒；-1/0 = 未指定 → 用預設長度
            public readonly List<double> BpmBeats = new List<double>();
            public readonly List<double> BpmValues = new List<double>();
            // #STOPS / #FREEZES (freeze) segments: parallel lists of start beat + freeze seconds.
            public readonly List<double> StopBeats = new List<double>();
            public readonly List<double> StopSeconds = new List<double>();
            public readonly List<SmNotes> Charts = new List<SmNotes>();

            public double FirstBpm => BpmValues.Count > 0 ? BpmValues[0] : 0.0;

            /// <summary>第一個**正的** BPM —— 選歌畫面顯示、判定窗換算都要用它。首段就是負 BPM (warp) 的譜
            /// 若直接用 <see cref="FirstBpm"/> 會拿到負數,判定窗會整個壞掉。全負/空 → 0。</summary>
            public double FirstPositiveBpm
            {
                get
                {
                    for (int i = 0; i < BpmValues.Count; i++) if (BpmValues[i] > 0.0) return BpmValues[i];
                    return 0.0;
                }
            }

            /// <summary>這首有沒有負 BPM(要走 warp 那條路)。</summary>
            public bool HasNegativeBpm
            {
                get
                {
                    for (int i = 0; i < BpmValues.Count; i++) if (BpmValues[i] < 0.0) return true;
                    return false;
                }
            }
        }

        /// <summary>
        /// 一段 warp:歌曲時間走到 <see cref="TimeMs"/> 的那一瞬間,播放頭的拍子從 <see cref="StartBeat"/>
        /// 直接跳到 <see cref="EndBeat"/>(中間不花任何時間)。負 BPM 段先讓時間倒退,後面的正 BPM 段再把它加回來,
        /// 「倒退 + 加回來」這整段拍子就是被跳過的範圍 —— 使用者說的「要有一段正 BPM 同樣時間長度的才能抵銷」。
        /// </summary>
        public readonly struct SmWarp
        {
            /// <summary>開始跳過的拍(這一拍本身還打得到 —— 播放頭正是在這裡起跳的)。</summary>
            public double StartBeat { get; }
            /// <summary>落地的拍(這一拍打得到,後面的譜從這裡照原時間接上)。</summary>
            public double EndBeat { get; }
            /// <summary>起跳/落地共用的歌曲時刻 (ms, note clock)。</summary>
            public double TimeMs { get; }

            public SmWarp(double startBeat, double endBeat, double timeMs)
            { StartBeat = startBeat; EndBeat = endBeat; TimeMs = timeMs; }

            /// <summary>被跳過的拍數(= 畫面上要瞬間刷過去的距離)。</summary>
            public double Beats => EndBeat - StartBeat;

            /// <summary>這一拍是不是落在 warp 內部(頭尾兩拍不算 —— 那兩拍打得到)。</summary>
            public bool Contains(double beat) => beat > StartBeat && beat < EndBeat;
        }

        /// <summary>True for a 4-panel single chart (the only kind the 4K highway can play).</summary>
        public static bool IsDanceSingle(SmNotes n)
            => n != null && n.StepsType != null &&
               n.StepsType.Trim().Equals("dance-single", StringComparison.OrdinalIgnoreCase);

        /// <summary>Parse a .sm file's text into a <see cref="SmSong"/> (metadata + raw #NOTES blocks). Pure/testable.</summary>
        public static SmSong Parse(string text)
        {
            var song = new SmSong();
            if (string.IsNullOrEmpty(text)) return song;

            foreach (var stmt in Statements(text))
            {
                // stmt[0] = tag name (already upper-cased, no '#'); stmt[1..] = colon-separated params.
                if (stmt.Length == 0) continue;
                // A tag's value is everything after the first ':' up to ';'. Because Statements() split the whole
                // statement on ':', a value that itself contains ':' (a title, or an "MM:SS" sample time) was split
                // across parts — Rest() rejoins parts[1..] to recover it. NOTES sub-fields (StepsType/Difficulty/Meter)
                // never contain ':', so they keep using Val() (single part).
                switch (stmt[0])
                {
                    case "TITLE":      song.Title = Rest(stmt, 1); break;
                    case "ARTIST":     song.Artist = Rest(stmt, 1); break;
                    case "MUSIC":      song.Music = Rest(stmt, 1); break;
                    case "BANNER":     song.Banner = Rest(stmt, 1); break;
                    case "BACKGROUND": song.Background = Rest(stmt, 1); break;
                    case "CDTITLE":    song.CdTitle = Rest(stmt, 1); break;
                    case "OFFSET":       song.Offset = ParseDouble(Rest(stmt, 1)); break;
                    case "SAMPLESTART":  song.SampleStart = ParseSmTime(Rest(stmt, 1)); break;
                    case "SAMPLELENGTH": song.SampleLength = ParseSmTime(Rest(stmt, 1)); break;
                    case "BPMS":         ParseBpms(Rest(stmt, 1), song); break;
                    // #STOPS and its older alias #FREEZES are the same thing (NotesLoaderSM.cpp:141).
                    case "STOPS":
                    case "FREEZES":      ParseStops(Rest(stmt, 1), song); break;
                    case "NOTES":
                        // NOTES:StepsType:Description:Difficulty:Meter:Radar:NoteData(:Attacks)
                        if (stmt.Length >= 7)
                            song.Charts.Add(new SmNotes
                            {
                                StepsType = Val(stmt, 1),
                                Difficulty = Val(stmt, 3),
                                Meter = ParseInt(Val(stmt, 4)),
                                NoteData = stmt.Length > 6 ? stmt[6] : "",   // keep raw (newlines/commas preserved)
                            });
                        break;
                }
            }
            return song;
        }

        /// <summary>Number of judged objects in a dance-single note body (taps + hold-heads). Pure/testable —
        /// used to rank difficulties by note count (a hold counts once, like an osu! [HitObjects] line).
        /// Mines ('M') are NOT counted: they are never judged, so they must not inflate a difficulty's rank.
        /// 落單的 '3'(hold cap 沒有配對的頭)算一顆 —— StepMania 把它當一般箭頭,見 <see cref="ToBeatmap"/>。</summary>
        public static int NoteCount(string noteData)
        {
            if (string.IsNullOrEmpty(noteData)) return 0;
            int count = 0;
            var open = new bool[4];   // 該軌有沒有還沒收尾的 '2'/'4' 頭 —— 用來分辨「配對的 cap」與「落單的 cap」
            foreach (var line in noteData.Replace("\r", "").Split('\n'))
            {
                var row = line.Trim();
                if (row.Length == 0 || row[0] == ',') continue;
                int cols = Math.Min(4, row.Length);
                for (int c = 0; c < cols; c++)
                {
                    char ch = row[c];
                    if (ch == '1') count++;                                   // tap
                    else if (ch == '2' || ch == '4') { count++; open[c] = true; }   // hold-head / roll-head
                    else if (ch == '3') { if (open[c]) open[c] = false; else count++; }   // 配對的 cap 不另計;落單的 = 一顆 tap
                }
            }
            return count;
        }

        /// <summary>
        /// 這張譜**實際打得到**的音符顆數(炸彈與 warp 內的裝飾音都不算)——選歌畫面顯示的 note 數、難度排序都用它。
        /// 沒有負 BPM 的譜直接走便宜的 <see cref="NoteCount(string)"/>(逐字掃 note body,不必建時間軸)。
        /// </summary>
        public static int PlayableNoteCount(SmSong song, int chartIndex)
        {
            if (song == null || chartIndex < 0 || chartIndex >= song.Charts.Count) return 0;
            if (!song.HasNegativeBpm) return NoteCount(song.Charts[chartIndex].NoteData);
            int n = 0;
            foreach (var h in ToBeatmap(song, chartIndex).HitObjects)
                if (!h.IsBomb && !h.IsFake) n++;
            return n;
        }

        /// <summary>
        /// 這首歌的 warp 清單(依時間遞增);沒有負 BPM → 空清單。<paramref name="lastBeat"/> 是譜面最後一拍,
        /// 只有「負 BPM 一路到譜尾、永遠沒被抵銷」時才用得到(那種 warp 收在譜尾)。純函式,給測試/工具用。
        /// </summary>
        public static List<SmWarp> Warps(SmSong song, double lastBeat = 0.0)
        {
            var list = new List<SmWarp>();
            if (song == null) return list;
            double headerBpm = song.FirstPositiveBpm > 0 ? song.FirstPositiveBpm : 120.0;
            list.AddRange(Timeline.Build(song, headerBpm, lastBeat).Warps);
            return list;
        }

        /// <summary>Convert one dance-single chart to a playable <see cref="OsuBeatmap"/> (4 lanes).</summary>
        public static OsuBeatmap ToBeatmap(SmSong song, int chartIndex)
        {
            var map = new OsuBeatmap { Keys = 4 };
            if (song == null || chartIndex < 0 || chartIndex >= song.Charts.Count) return map;
            var chart = song.Charts[chartIndex];

            // 表頭 BPM 取第一個**正的**值:負 BPM 開頭的譜若拿到負數,判定窗(依 BPM 換算 tick)會整個壞掉。
            double headerBpm = song.FirstPositiveBpm > 0 ? song.FirstPositiveBpm : 120.0;
            map.Bpm = headerBpm;
            map.Level = chart.Meter;
            map.Title = song.Title;
            map.MusicStartOffsetMs = 0.0;   // audio starts at note-clock 0; the OFFSET is folded into each note's ms.

            var measures = SplitMeasures(chart.NoteData);
            var tl = Timeline.Build(song, headerBpm, measures.Count * 4.0);

            // timing points (ms): one uninherited point per BPM segment (drives ManiaScroll BPM-change scrolling),
            // shifted by the cumulative freeze at each segment's start beat so a BPM change after a stop lands at its
            // real song time. Warps add their own super-fast 1ms segment — see Timeline.FillTimingPoints.
            tl.FillTimingPoints(map);

            // --- parse the note body: measures split on ',', rows split on '\n' ---
            var openHead = new double[4]; for (int i = 0; i < 4; i++) openHead[i] = -1.0;
            for (int m = 0; m < measures.Count; m++)
            {
                var rows = measures[m];
                int lpm = rows.Count;
                if (lpm == 0) continue;
                for (int j = 0; j < lpm; j++)
                {
                    var row = rows[j];
                    double beat = (m + (double)j / lpm) * 4.0;
                    int cols = Math.Min(4, row.Length);
                    for (int c = 0; c < cols; c++)
                    {
                        char ch = row[c];
                        if (ch == '1')
                            map.HitObjects.Add(tl.Tap(c, beat));
                        else if (ch == '2' || ch == '4')
                            openHead[c] = beat;
                        else if (ch == '3')
                        {
                            if (openHead[c] >= 0.0)
                            {
                                map.HitObjects.Add(tl.Hold(c, openHead[c], beat));
                                openHead[c] = -1.0;
                            }
                            else
                                // **落單的 '3'**(hold cap 沒有配對的 '2'/'4' 頭)不能丟掉 —— StepMania 會把它當一顆
                                // 普通箭頭:NoteData::Convert2sAnd3sToHoldNotes 只把「有頭的那一對」清成 TAP_EMPTY,
                                // 落單的 '3' 原封不動留在譜上(type = TapNote::hold_tail),NoteField.cpp:645 的 DrawTap
                                // 不看 hold_tail(只分 mine/attack)→ 畫成一般箭頭,GetNumTapNotes 也照樣算它一顆。
                                // 這是 .dwi 轉檔常見的裝飾音寫法:deadsoul[Blue](Blue's 6th step)整段負 BPM 的
                                // 假 note 全是落單 '3'(964 顆),丟掉的話那段 gimmick 在畫面上完全是空的。
                                map.HitObjects.Add(tl.Tap(c, beat));
                        }
                        else if (ch == 'M' || ch == 'm')
                            // 'M' = mine，StepMania 原生的炸彈：要避開、踩到才有事，和 .gn 的 note_type 1 同一種東西，
                            // 所以走同一條 IsBomb 路徑(ZD00..ZD03 外觀 + TickBombs 引爆)。永遠不是長條。
                            map.HitObjects.Add(tl.Tap(c, beat, isBomb: true));
                        // '0' 與其他字元(lift 'L'、fake 'F'…) → 無音符。
                    }
                }
            }
            // 判定時間相同時(warp 內的音符全部落在同一個瞬間)再比顯示時間 —— ScreenGameplay 的「後面的都還沒進場
            // 就 break」提早結束是靠這個順序,排錯會讓 warp 那批音符少畫幾顆。
            map.HitObjects.Sort((a, b) =>
            {
                int c = a.StartTimeMs.CompareTo(b.StartTimeMs);
                return c != 0 ? c : a.ScrollTimeMs.CompareTo(b.ScrollTimeMs);
            });

            // Freeze windows for the highway (ManiaScroll): each stop begins at its own note-clock time — the
            // cumulative freeze BEFORE it (a stop's own duration is not yet applied at its start beat) — and lasts
            // its freeze duration. A note sitting exactly on the stop beat is hit right as the freeze begins.
            // 負 BPM 的譜還要多兩件事(warp 內的 stop 不凍結、warp 的顯示窗要讓出來)—— 見 Timeline.DisplayStops。
            map.Stops.AddRange(tl.DisplayStops());
            // 編輯器格線走「真實時間軸」,不能拿上面那些捲動用的 timing point 反推(warp 壓成 1ms、停拍不在裡面)。
            tl.FillGridSegments(map);
            return map;
        }

        /// <summary>
        /// warp 在**畫面**上被壓成多短的一個「超高速捲動窗」(ms)。warp 在時間軸上沒有厚度(播放頭瞬間跳過),
        /// 但 StepMania 3.9 的音符位置是 beat spacing(ArrowEffects::ArrowGetYOffset),warp 內的音符進場時
        /// 仍然照拍子一顆顆排開往下捲、到了那個瞬間整批刷過判定線。要在「用時間定位音符」的引擎裡重現這件事,
        /// 就把整段被跳過的拍數塞進判定時刻前這麼短的一個窗裡:1ms 短到播放頭幾乎不可能停在裡面(一幀 ~16ms),
        /// 看起來就是瞬間跳過,又足以把窗內的音符按拍子分開來擺。
        /// </summary>
        public const double WarpDisplayMs = 1.0;

        /// <summary>時間比較的容差 (ms) —— 判斷「負 BPM 倒退的時間有沒有被加回來」用。</summary>
        private const double TimeEps = 1e-6;

        /// <summary>拍數比較的容差 —— 判斷某一拍是不是正好落在 warp 的頭/尾上。</summary>
        private const double BeatEps = 1e-9;

        /// <summary>
        /// 一張 .sm 的時間軸:拍 → 時間,含 #BPMS(可為負)、#STOPS,以及由負 BPM 推導出來的 warp。
        /// 三種「時間」要分清楚:
        ///   <see cref="RawMs"/>     = StepMania TimingData::GetElapsedTimeFromBeat 原式(負 BPM 會讓它倒退);
        ///   <see cref="PlayMs"/>    = 播放頭**真的**經過那一拍的時刻(warp 內一律等於 warp 那一瞬間)→ 判定時間;
        ///   <see cref="DisplayMs"/> = 畫面定位用的時間(warp 內攤在 <see cref="WarpDisplayMs"/> 的窗裡)。
        /// </summary>
        private sealed class Timeline
        {
            public readonly double[] SegBeat, SegBpm, SegMs;   // BPM 段起拍 / BPM(可為負) / 該起拍的累計 ms
            public readonly double[] StopBeat, StopMs;         // #STOPS,依拍遞增
            public readonly double OffMs;                      // #OFFSET × 1000(StepMania 是「減」)
            public readonly SmWarp[] Warps;                    // 依 StartBeat 遞增
            public readonly double[] WinStartMs;               // 每個 warp 的顯示窗起點(窗尾 = warp.TimeMs)

            public static Timeline Build(SmSong song, double headerBpm, double lastBeat)
            {
                BuildBpmSegments(headerBpm, song.BpmBeats, song.BpmValues,
                    out double[] segBeat, out double[] segBpm, out double[] segMs);
                BuildStops(song.StopBeats, song.StopSeconds, out double[] stopBeat, out double[] stopMs);
                return new Timeline(segBeat, segBpm, segMs, stopBeat, stopMs, song.Offset * 1000.0, lastBeat);
            }

            private Timeline(double[] segBeat, double[] segBpm, double[] segMs,
                double[] stopBeat, double[] stopMs, double offMs, double lastBeat)
            {
                SegBeat = segBeat; SegBpm = segBpm; SegMs = segMs;
                StopBeat = stopBeat; StopMs = stopMs; OffMs = offMs;
                Warps = DetectWarps(lastBeat);      // 只用 Seg*/Stop*/OffMs,不碰下面兩個欄位
                WinStartMs = BuildWindows(Warps);
            }

            /// <summary>StepMania 的原式:beat → 秒(TimingData.cpp:162)。負 BPM 段的貢獻是負的,所以會倒退。</summary>
            public double RawMs(double beat)
            {
                int s = SegIndex(beat);
                return SegMs[s] + (beat - SegBeat[s]) * 60000.0 / SegBpm[s] - OffMs + StopSum(beat, false);
            }

            /// <summary>播放頭經過這一拍的時刻。warp 內的拍子播放頭是一瞬間跳過的 → 全部等於 warp 的時刻。</summary>
            public double PlayMs(double beat)
            {
                for (int i = 0; i < Warps.Length; i++)
                {
                    if (beat <= Warps[i].StartBeat) break;
                    if (beat < Warps[i].EndBeat) return Warps[i].TimeMs;
                }
                return RawMs(beat);
            }

            /// <summary>畫面定位用的時間:warp 內依拍數等比攤在 [WinStart, warp.TimeMs) 這個超短窗裡。</summary>
            public double DisplayMs(double beat)
            {
                for (int i = 0; i < Warps.Length; i++)
                {
                    var w = Warps[i];
                    // warp 起跳拍(含)之前:照原時間,但不得踩進窗裡(不然會跟窗內的音符位置對調)。
                    if (beat <= w.StartBeat) return Math.Min(RawMs(beat), WinStartMs[i]);
                    if (beat < w.EndBeat - BeatEps)
                    {
                        double span = w.Beats;
                        double f = span > 0.0 ? (beat - w.StartBeat) / span : 0.0;
                        return WinStartMs[i] + (w.TimeMs - WinStartMs[i]) * f;
                    }
                    // 落地拍一律對齊窗尾(= warp 的時刻)。這一拍的 RawMs 有可能還差那麼一點點:譜面把 stop
                    // 寫在負 BPM 段起拍之後零點幾拍(#BPMS 204.667=-174 配 #STOPS 204.668,作者要的是「同時」),
                    // 那零點幾拍的負 BPM 讓 RawMs 比落地時刻早零點幾 ms。照 RawMs 擺的話,這一拍的音符會掉進
                    // 上一段 warp 的超高速窗裡 —— 定格時它不是停在判定線上,而是被那零點幾 ms × 超高速甩到
                    // 判定線下方好幾拍。stop 的凍結窗也是從這個時刻起算(見 DisplayStops)。
                    if (beat <= w.EndBeat + BeatEps) return w.TimeMs;
                }
                return RawMs(beat);
            }

            /// <summary>
            /// 填 <see cref="OsuBeatmap.GridSegments"/> —— 編輯器格線用的「拍 ↔ **真實**歌曲時間」。
            /// 切點 = BPM 段起拍 ∪ #STOPS 拍 ∪ warp 頭尾;每一段的斜率直接用**播放頭**時刻(<see cref="PlayMs"/>)
            /// 相減算出來,所以負 BPM 被跳過的那段自動變成 `BeatLengthMs = 0`(拍子繼續走、時間不動),
            /// 停拍則落在 `StopMs` 上。這條時間軸和音符的判定時刻是同一個,格線才會真的長在音符上。
            /// </summary>
            public void FillGridSegments(OsuBeatmap map)
            {
                var knots = new List<double>(SegBeat.Length + StopBeat.Length + Warps.Length * 2);
                for (int i = 0; i < SegBeat.Length; i++) knots.Add(SegBeat[i]);
                for (int i = 0; i < StopBeat.Length; i++) knots.Add(StopBeat[i]);
                for (int i = 0; i < Warps.Length; i++) { knots.Add(Warps[i].StartBeat); knots.Add(Warps[i].EndBeat); }
                knots.Sort();

                for (int i = 0; i < knots.Count; i++)
                {
                    double b = knots[i];
                    if (b < 0.0) continue;
                    if (i > 0 && b - knots[i - 1] <= BeatEps) continue;              // 去重
                    double startMs = PlayMs(b);
                    // warp **內部**的停拍不定格(播放頭是瞬間跳過那段拍子的)—— 和 DisplayStops 同一條規則。
                    double stop = IsWarped(b) ? 0.0 : StopAt(b);
                    double bl;
                    int next = i + 1;
                    while (next < knots.Count && knots[next] - b <= BeatEps) next++;
                    if (next < knots.Count)
                    {
                        bl = (PlayMs(knots[next]) - startMs - stop) / (knots[next] - b);
                        if (bl < 0.0) bl = 0.0;   // 落地拍的 RawMs 可能比 warp 的時刻早零點幾 ms(見 DisplayMs)
                    }
                    else
                    {
                        double bpm = SegBpm[SegIndex(b)];
                        bl = bpm > 0.0 ? 60000.0 / bpm : 0.0;   // 收在譜尾的 warp(負 BPM 沒被抵銷)→ 時間不再前進
                    }
                    map.GridSegments.Add(new GridSegment(b, startMs, stop, bl));
                }
            }

            /// <summary>
            /// 高速公路要凍結的窗(**顯示**時間軸)。一段 #STOPS 就是播放頭在那一拍上停住 —— 畫面定格,
            /// 玩家看得到「停住的那一瞬間」;負 BPM 段中間夾 stop 的譜(engine[Blue] 那種)整段 gimmick
            /// 就是這樣一格一格看出來的。
            ///
            /// 兩件事和單純的「stop → 凍結」不一樣:
            ///  • warp **內部**的 stop 不凍結。那段拍子播放頭是瞬間跳過的,StepMania 也只是把它的秒數扣掉
            ///    (GetBeatAndBPSFromElapsedTime 在負段算出來的 fFreezeStartSecond 是負的 → 凍結條件不成立);
            ///    只有 warp 頭尾那一拍上的 stop 才會定格 —— 而那正是「負 BPM 被 stop 切成兩段」的接縫。
            ///  • 每個 warp 的顯示窗(<see cref="WarpDisplayMs"/>)要從凍結窗裡**挖掉**。那 1 ms 是「整段被
            ///    跳過的拍子瞬間刷過畫面」用的超高速捲動;被凍結蓋住的話那段捲動就沒了,窗內的音符會全部
            ///    疊在判定線上(定格時畫面等於空白),停完才在下一段 warp 一次冒出來。
            /// </summary>
            public List<ScrollStop> DisplayStops()
            {
                var list = new List<ScrollStop>();
                for (int i = 0; i < StopBeat.Length; i++)
                {
                    if (IsWarped(StopBeat[i])) continue;
                    double s = DisplayMs(StopBeat[i]);
                    if (s < 0.0) s = 0.0;
                    AddFreeze(list, s, s + StopMs[i]);
                }
                return list;
            }

            // 把 [s, e) 這段凍結加進 list,跳過每一個 warp 的顯示窗。warp 依時刻遞增,窗尾也遞增,
            // 所以一次線性掃描就夠(窗互相重疊也沒關係 —— s 只會往前走)。
            private void AddFreeze(List<ScrollStop> list, double s, double e)
            {
                for (int i = 0; i < Warps.Length && s < e; i++)
                {
                    double ws = WinStartMs[i], we = Warps[i].TimeMs;
                    if (we <= s || ws >= e) continue;
                    if (ws > s) AddSpan(list, s, ws);
                    s = we;
                }
                AddSpan(list, s, e);
            }

            private static void AddSpan(List<ScrollStop> list, double s, double e)
            {
                if (e - s > 1e-9) list.Add(new ScrollStop(s, e - s));
            }

            /// <summary>這一拍是否被 warp 掃掉(看得到、打不到)。</summary>
            public bool IsWarped(double beat)
            {
                for (int i = 0; i < Warps.Length; i++)
                {
                    if (beat <= Warps[i].StartBeat) break;
                    if (beat < Warps[i].EndBeat) return true;
                }
                return false;
            }

            public OsuHitObject Tap(int lane, double beat, bool isBomb = false)
                => new OsuHitObject(lane, JudgeMs(beat), null, isBomb, IsWarped(beat), ShowMs(beat), ShowMs(beat));

            public OsuHitObject Hold(int lane, double headBeat, double tailBeat)
            {
                int start = JudgeMs(headBeat), end = JudgeMs(tailBeat);
                bool bar = end > start;
                bool headFake = IsWarped(headBeat);
                // cap(放開點)落在 warp 內:頭部在真實時間上打得到,但播放頭是一瞬間跳過那段拍子的 —— 玩家永遠
                // 碰不到「該放開」的時刻,所以結尾不判定(見 OsuHitObject.IsFakeTail)。StepMania 也是這樣:
                // 播放頭 warp 過 iEndRow 時 Player.cpp:407 直接給 HNS_OK(踩到頭 + 血還沒歸零就算完成),
                // 放不放開根本不影響。整條仍照 beat 間距畫出來(ShowMs 已經把 cap 擺進 warp 的顯示窗)。
                // 頭部本身就在 warp 裡的話整條都是裝飾 → 走 IsFake,不再標這個。
                bool tailFake = bar && !headFake && IsWarped(tailBeat);
                return new OsuHitObject(lane, start, bar ? end : (int?)null, false, headFake,
                    ShowMs(headBeat), bar ? ShowMs(tailBeat) : ShowMs(headBeat), tailFake);
            }

            // 判定/顯示時間都夾在 0 以上（#OFFSET 為正時開頭幾拍會落在 0 之前,沿用既有行為）。
            private int JudgeMs(double beat) { double ms = PlayMs(beat); return (int)Math.Round(ms < 0.0 ? 0.0 : ms); }
            private double ShowMs(double beat) { double ms = DisplayMs(beat); return ms < 0.0 ? 0.0 : ms; }

            /// <summary>
            /// 產生 ManiaScroll 用的 timing point。切點 = BPM 段起拍 ∪ warp 頭尾;每一段的 ms/beat 由
            /// <see cref="BeatLengthAt"/> 決定(warp 段 = 整段拍數壓進顯示窗 → 超短 beat length = 超快捲動)。
            /// 沒有 warp 時輸出與舊版逐段送 timing point 完全相同。
            ///
            /// **每個 warp 的顯示窗起點一定要自己送一個 timing point**,不能靠「起跳拍那個切點」代勞:
            /// 兩個 warp 首尾相接時(±BPM 乒乓,中間夾一個 stop —— deadsoul[Blue] 的 -4224 段就是這樣),
            /// 接縫那一拍的 <see cref="DisplayMs"/> 走的是「上一個 warp 的落地時刻」那條分支(見 DisplayMs
            /// 的第三個 if),永遠到不了下一個 warp 的「窗首」分支 → 窗首沒有任何 timing point,前半窗就沿用
            /// 上一點的速率。實測 deadsoul[Blue] beats 1116→1120 的窗前半只跑 mult 32(該是 1818),
            /// 1/6 拍的炸彈間距 39.4px 被壓成 0.69px(1/57),看起來就是一疊擠在判定線上的炸彈。
            /// </summary>
            public void FillTimingPoints(OsuBeatmap map)
            {
                var cuts = new List<double>(SegBeat.Length + Warps.Length * 2);
                for (int i = 0; i < SegBeat.Length; i++) cuts.Add(SegBeat[i]);
                for (int i = 0; i < Warps.Length; i++) { cuts.Add(Warps[i].StartBeat); cuts.Add(Warps[i].EndBeat); }
                cuts.Sort();
                for (int i = 0; i < cuts.Count; i++)
                {
                    if (i > 0 && cuts[i] - cuts[i - 1] <= 1e-9) continue;    // 去重
                    double bl = BeatLengthAt(cuts[i]);
                    if (bl > 0.0) map.TimingPoints.Add(new OsuTimingPoint(DisplayMs(cuts[i]), bl));
                }
                // 窗首的點放在最後 append:ManiaScroll.BuildMultiplierPoints 同時刻取**後面**那個,所以窗首
                // 一定由 warp 自己的速率作主。沒有接縫問題的 warp 本來就有一個同時刻同值的點,這裡只是重複一次
                // (BuildMultiplierPoints 會併掉),行為不變。
                for (int i = 0; i < Warps.Length; i++)
                {
                    double beats = Warps[i].Beats;
                    if (beats <= 0.0) continue;
                    map.TimingPoints.Add(new OsuTimingPoint(WinStartMs[i], (Warps[i].TimeMs - WinStartMs[i]) / beats));
                }
            }

            // 從這一拍起算,那一段在**顯示時間軸**上的 ms/beat。
            private double BeatLengthAt(double beat)
            {
                for (int i = 0; i < Warps.Length; i++)
                {
                    var w = Warps[i];
                    if (beat < w.StartBeat) break;
                    if (beat < w.EndBeat && w.Beats > 0.0) return (w.TimeMs - WinStartMs[i]) / w.Beats;
                }
                double bpm = SegBpm[SegIndex(beat)];
                return bpm > 0.0 ? 60000.0 / bpm : 0.0;   // 負 BPM 段一定在 warp 內,不會走到這裡
            }

            /// <summary>
            /// 找出所有 warp。走訪「斜率會變(BPM 段起拍)或時間會跳(stop 起拍)」的拍點,把譜面切成一段段線性:
            ///   斜率為負 → 時間開始倒退,warp 從這一拍起跳(記下起跳時刻 wMs);
            ///   之後第一次回到 wMs 的那一拍 → 落地,warp 結束(= 使用者說的「同樣時間長度的正 BPM 抵銷掉」)。
            /// 收不回來(負 BPM 一路到譜尾)就收在譜尾,後面全部是打不到的裝飾音。
            /// </summary>
            private SmWarp[] DetectWarps(double lastBeat)
            {
                var knots = new List<double>(SegBeat.Length + StopBeat.Length + 1);
                for (int i = 0; i < SegBeat.Length; i++) knots.Add(SegBeat[i]);
                for (int i = 0; i < StopBeat.Length; i++) knots.Add(StopBeat[i]);
                double end = Math.Max(lastBeat, SegBeat[SegBeat.Length - 1]) + 4.0;
                knots.Add(end);
                knots.Sort();

                var warps = new List<SmWarp>();
                bool inWarp = false; double wStart = 0.0, wMs = 0.0;
                for (int k = 0; k + 1 < knots.Count; k++)
                {
                    double b0 = knots[k], b1 = knots[k + 1];
                    if (b1 - b0 <= 1e-9) continue;                  // 同一拍上的重複切點
                    double v0 = RawMs(b0) + StopAt(b0);             // 含這一拍上的 stop(往上跳,只會提早結束 warp)
                    if (inWarp && v0 >= wMs - TimeEps) { Close(warps, wStart, b0, wMs); inWarp = false; }

                    double slope = 60000.0 / SegBpm[SegIndex(b0)];  // ms/beat;負 BPM → 負斜率 = 時間倒退
                    double v1 = v0 + (b1 - b0) * slope;
                    if (inWarp)
                    {
                        if (slope > 0.0 && v1 >= wMs - TimeEps)     // 這一段裡把倒退的時間補回來了 → 落地
                        {
                            double bEnd = b0 + (wMs - v0) / slope;
                            if (bEnd < wStart) bEnd = wStart;
                            if (bEnd > b1) bEnd = b1;
                            // 落地拍幾乎正好落在下一個切點上時**貼上去**。60000/BPM 大多不能精確表示
                            // (4224 → 14.204545…),所以「-N 拍再 +N 拍抵銷」算出來的落地拍常常比切點差幾個
                            // ULP(實測 1119.9999999999998 vs 1116+4)。差這幾個 ULP 會讓後面兩件事讀到**切點
                            // 左邊**那一段:(1) FillTimingPoints 的去重留下小的那個拍,BeatLengthAt 在
                            // `beat < w.StartBeat` 提早 break → 落地後的捲動速率抓成 warp 前的 BPM
                            // (實測 Ops Code-Rapture- 落地後整段快 10 倍);(2) 反方向 round 的話 IsWarped(接縫)
                            // 會變 true,DisplayStops 就把接縫上的 #STOPS 當成 warp 內部丟掉,窗前冒出幾百 ms
                            // 的超高速區。貼齊切點把這個浮點刀鋒整個拿掉。
                            if (b1 - bEnd <= BeatEps) bEnd = b1;
                            Close(warps, wStart, bEnd, wMs);
                            inWarp = false;
                        }
                    }
                    else if (slope < 0.0) { inWarp = true; wStart = b0; wMs = v0; }
                }
                if (inWarp) Close(warps, wStart, end, wMs);
                return warps.ToArray();
            }

            // 接在上一段 warp 尾巴、時刻又相同的兩段(例:負 BPM 段起拍之後零點幾拍才寫 stop —— 中間那
            // 零點幾拍會自成一段極短的 warp)**不併回去**:併了之後接縫那一拍就變成 warp 的**內部**,
            // 那一拍上的音符會被誤標成打不到的裝飾音,而 StepMania 是打得到的(定格時播放頭就停在那裡,
            // 判定窗涵蓋得到)。窗會不會重疊由 BuildWindows 收尾。
            private static void Close(List<SmWarp> warps, double startBeat, double endBeat, double timeMs)
            {
                if (endBeat <= startBeat) return;
                warps.Add(new SmWarp(startBeat, endBeat, timeMs));
            }

            // 每個 warp 的顯示窗 = [TimeMs − ε, TimeMs)。ε 預設 WarpDisplayMs,但兩個 warp 靠得太近時縮到一半的
            // 間距,窗才不會互相重疊(重疊 → timing point 時間倒退 → 捲動整個亂掉)。
            private static double[] BuildWindows(SmWarp[] warps)
            {
                var win = new double[warps.Length];
                for (int i = 0; i < warps.Length; i++)
                {
                    double eps = WarpDisplayMs;
                    if (i > 0)
                    {
                        double room = warps[i].TimeMs - warps[i - 1].TimeMs;
                        if (room < 2.0 * eps) eps = Math.Max(1e-4, room * 0.5);
                    }
                    win[i] = warps[i].TimeMs - eps;
                }
                return win;
            }

            private int SegIndex(double beat)
            {
                int lo = 0, hi = SegBeat.Length - 1, s = 0;
                while (lo <= hi) { int mid = (lo + hi) >> 1; if (SegBeat[mid] <= beat) { s = mid; lo = mid + 1; } else hi = mid - 1; }
                return s;
            }

            // 這一拍之前的 #STOPS 總和 (ms)。StepMania 在 `stopBeat >= beat` 就 break(TimingData.cpp:171):
            // 正好落在 stop 拍上的音符是「停之前」打的,所以不含自己那一拍 —— inclusive=true 才把它算進去。
            private double StopSum(double beat, bool inclusive)
            {
                double acc = 0.0;
                for (int i = 0; i < StopBeat.Length; i++)
                {
                    if (inclusive ? StopBeat[i] > beat : StopBeat[i] >= beat) break;
                    acc += StopMs[i];
                }
                return acc;
            }

            private double StopAt(double beat)
            {
                double acc = 0.0;
                for (int i = 0; i < StopBeat.Length; i++) if (Math.Abs(StopBeat[i] - beat) <= 1e-9) acc += StopMs[i];
                return acc;
            }
        }

        // Piecewise-constant BPM timeline that KEEPS negative BPMs (unlike GnChart.BuildBpmTimeline, whose domain —
        // .gn charts — has none and clamps to ≥1). Parallel arrays sorted by start beat; segMs = cumulative ms at
        // that start beat, which is NOT monotonic once a negative segment shows up (that's the whole point).
        private static void BuildBpmSegments(double headerBpm, List<double> beats, List<double> bpms,
            out double[] segBeat, out double[] segBpm, out double[] segMs)
        {
            int n = beats?.Count ?? 0;
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, (a, b) =>
            {
                int c = beats[a].CompareTo(beats[b]);
                return c != 0 ? c : a.CompareTo(b);
            });

            var sb = new List<double> { 0.0 };
            var sp = new List<double> { ClampBpm(headerBpm) };      // leading segment = header BPM
            for (int j = 0; j < n; j++)
            {
                double b = beats[idx[j]];
                double v = ClampBpm(bpms[idx[j]]);
                if (b <= 0.0) { sp[0] = v; continue; }               // event at/before 0 overrides the lead
                if (b == sb[sb.Count - 1]) sp[sp.Count - 1] = v;     // same beat as last → keep latest
                else { sb.Add(b); sp.Add(v); }
            }

            segBeat = sb.ToArray();
            segBpm = sp.ToArray();
            segMs = new double[segBeat.Length];
            for (int s = 1; s < segBeat.Length; s++)
                segMs[s] = segMs[s - 1] + (segBeat[s] - segBeat[s - 1]) * 60000.0 / segBpm[s - 1];
        }

        // |BPM| 至少 1（沿用 GnChart 對正 BPM 的下限，負的鏡像過去）。0 進不來（ParseBpms 已擋）。
        private static double ClampBpm(double bpm) => bpm >= 0.0 ? Math.Max(1.0, bpm) : Math.Min(-1.0, bpm);

        // Sort the parsed stops by beat and convert seconds→ms into parallel ascending arrays.
        private static void BuildStops(List<double> beats, List<double> seconds,
            out double[] stopBeat, out double[] stopMs)
        {
            int n = beats?.Count ?? 0;
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, (a, b) =>
            {
                int c = beats[a].CompareTo(beats[b]);
                return c != 0 ? c : a.CompareTo(b);
            });
            stopBeat = new double[n];
            stopMs = new double[n];
            for (int i = 0; i < n; i++) { stopBeat[i] = beats[idx[i]]; stopMs[i] = seconds[idx[i]] * 1000.0; }
        }

        // ---- MSD tokenizer ----

        // Split a .sm into statements. Strips // line comments, then cuts on ';'. Each statement that begins with
        // '#' is returned as its ':'-separated parts with the tag name upper-cased in [0]. The note body (last part
        // of #NOTES) keeps its newlines/commas since only ':' and ';' are consumed here.
        private static IEnumerable<string[]> Statements(string text)
        {
            var noComments = StripComments(text);
            foreach (var chunk in SplitStatements(noComments))
            {
                int hash = chunk.IndexOf('#');
                if (hash < 0) continue;
                var body = chunk.Substring(hash + 1);
                var parts = body.Split(':');
                if (parts.Length == 0) continue;
                parts[0] = parts[0].Trim().ToUpperInvariant();
                yield return parts;
            }
        }

        // Cut the (comment-free) text into statements. A ';' ends a statement — but plenty of real simfiles drop the
        // terminator on a header tag, e.g. "#TITLE:M@GIC☆" followed on the next line by "#SUBTITLE:...". StepMania
        // treats a '#' that is the first non-blank character of a line as an implicit end of the value it is reading
        // ("Unfortunately, many of these files are missing ';'s" — MsdFile::ReadBuf), so we cut there too; otherwise
        // every following tag line gets swallowed into the title. A '#' in mid-line stays a literal character (titles
        // do contain them), and a note body never starts a line with '#', so #NOTES is unaffected.
        private static IEnumerable<string> SplitStatements(string text)
        {
            int start = 0;
            bool atLineStart = true;   // no non-blank character seen yet on the current line
            bool hasContent = false;   // the statement being read already holds a non-blank character
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == ';')
                {
                    yield return text.Substring(start, i - start);
                    start = i + 1; hasContent = false; atLineStart = false;
                }
                else if (c == '#' && atLineStart && hasContent)
                {
                    yield return text.Substring(start, i - start);
                    start = i; hasContent = true; atLineStart = false;
                }
                else if (c == '\n') atLineStart = true;
                else if (!char.IsWhiteSpace(c)) { atLineStart = false; hasContent = true; }
            }
            if (start < text.Length) yield return text.Substring(start);
        }

        private static string StripComments(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(comment >= 0 ? line.Substring(0, comment) : line).Append('\n');
            }
            return sb.ToString();
        }

        private static List<List<string>> SplitMeasures(string noteData)
        {
            var measures = new List<List<string>>();
            var current = new List<string>();
            if (string.IsNullOrEmpty(noteData)) return measures;
            foreach (var raw in noteData.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == ',') { measures.Add(current); current = new List<string>(); continue; }
                current.Add(line);
            }
            measures.Add(current);
            return measures;
        }

        // #BPMS:beat=bpm,... —— **負值要留著**:StepMania 用負 BPM 做 warp(那一段的經過時間是負的,等後面同樣
        // 時間長度的正 BPM 抵銷掉,播放頭就一瞬間跳過中間那段拍子)。見 SmWarp / Timeline.DetectWarps。
        // 0 沒有意義(除以 0),直接丟掉。
        private static void ParseBpms(string val, SmSong song)
        {
            if (string.IsNullOrEmpty(val)) return;
            foreach (var pair in val.Split(','))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (TryDouble(pair.Substring(0, eq), out double beat) &&
                    TryDouble(pair.Substring(eq + 1), out double bpm) && Math.Abs(bpm) > 1e-9)
                {
                    song.BpmBeats.Add(beat);
                    song.BpmValues.Add(bpm);
                }
            }
        }

        // #STOPS:beat=seconds,beat=seconds,...  (same grammar as #BPMS). A non-positive freeze is ignored
        // (StepMania keeps 0-length stops but they are a no-op for timing and would only add a dead scroll point).
        private static void ParseStops(string val, SmSong song)
        {
            if (string.IsNullOrEmpty(val)) return;
            foreach (var pair in val.Split(','))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (TryDouble(pair.Substring(0, eq), out double beat) &&
                    TryDouble(pair.Substring(eq + 1), out double sec) && sec > 0)
                {
                    song.StopBeats.Add(beat);
                    song.StopSeconds.Add(sec);
                }
            }
        }

        /// <summary>Parse a StepMania time value — plain seconds ("45.0") or colon time ("1:23" / "0:01:23"). −1 if empty/bad.</summary>
        private static double ParseSmTime(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return -1.0;
            if (s.IndexOf(':') >= 0)
            {
                double total = 0.0;
                foreach (var p in s.Split(':'))
                {
                    if (!TryDouble(p, out double v)) return -1.0;
                    total = total * 60.0 + v;
                }
                return total;
            }
            return TryDouble(s, out double d) ? d : -1.0;
        }

        private static string Val(string[] parts, int i) => i < parts.Length ? parts[i].Trim() : "";
        // Rejoin parts[from..] with ':' — recovers a tag value that contained ':' (title / MM:SS sample time).
        private static string Rest(string[] parts, int from)
            => (parts == null || from >= parts.Length) ? "" : string.Join(":", parts, from, parts.Length - from).Trim();
        private static bool TryDouble(string s, out double v)
            => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        private static double ParseDouble(string s) => TryDouble(s, out var v) ? v : 0.0;
        private static int ParseInt(string s)
            => int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
}
