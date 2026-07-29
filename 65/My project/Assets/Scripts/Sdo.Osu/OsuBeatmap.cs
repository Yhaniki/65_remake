using System.Collections.Generic;

namespace Sdo.Osu
{
    /// <summary>
    /// Minimal parsed osu! beatmap for Step 1: mania metadata + hit objects.
    /// Difficulty fields (OD/HP) are intentionally NOT exposed — judgment precision and
    /// health are system settings in this project, not per-chart.
    /// </summary>
    public sealed class OsuBeatmap
    {
        public string AudioFilename { get; set; } = "";
        /// <summary>
        /// osu! [General] SamplesMatchPlaybackRate. The file-format default is false: rate-changing mods alter
        /// trigger spacing, while each individual storyboard/custom sample keeps its original pitch and duration.
        /// </summary>
        public bool SamplesMatchPlaybackRate { get; set; }
        public string Title { get; set; } = "";
        public string Version { get; set; } = "";

        /// <summary>osu! mode: 3 = mania.</summary>
        public int Mode { get; set; }

        /// <summary>Key count for mania (derived from CircleSize).</summary>
        public int Keys { get; set; }

        /// <summary>Chart difficulty level (from the .gn StepFile header), for the LV label.</summary>
        public int Level { get; set; }

        /// <summary>
        /// First uninherited timing point's tempo (beats/min); 0 if none parsed.
        /// Drives the SDO tick-based judgement (windows scale as 1/BPM, like the original).
        /// </summary>
        public double Bpm { get; set; }

        /// <summary>
        /// Time (ms, in the note/beat clock) of the song's music-start marker — the first StepFile type-10
        /// (音樂起止) slot carrying value 1000 (the engine's music-start flag; the type-9 小節線 is a fallback
        /// for charts without one). The audio (and the dancer) should begin at this point — the leading measure
        /// before it is a silent count-in; 0 if the chart has no usable marker.
        /// </summary>
        public double MusicStartOffsetMs { get; set; }

        public List<OsuHitObject> HitObjects { get; } = new List<OsuHitObject>();

        /// <summary>Storyboard Sample events which play automatically, independently of player input.</summary>
        public List<OsuSampleEvent> SampleEvents { get; } = new List<OsuSampleEvent>();

        /// <summary>
        /// SDO online/NX frame_type 33 = 捲動速度 track (time ms → current scroll-speed multiplier), sorted by time.
        /// Unlike osu SV (which bakes each note's spacing over its whole flight via the scroll integral, so a note
        /// is drawn wide from the moment it enters), this is the CURRENT global scroll speed: it changes the instant
        /// the play head REACHES the event and rescales every on-screen note together (they jump), then holds until
        /// the next event. Base value 1000 = ×1.0 (see GnChart.Frame33SpeedBase). Empty for offline .gn (no type-33)
        /// → <see cref="CurrentScrollSpeed"/> is 1.0 everywhere.
        /// </summary>
        public List<OsuScrollSpeed> ScrollSpeeds { get; } = new List<OsuScrollSpeed>();

        /// <summary>
        /// The frame_type-33 scroll multiplier in effect at <paramref name="ms"/> = the last event at or before it
        /// (1.0 before the first event, or when there are none). Multiply a note's scroll distance by this to get the
        /// "speed changes when the play head arrives" behaviour. Events with <see cref="OsuScrollSpeed.RampMs"/> &gt; 0
        /// ramp LINEARLY from the previous multiplier to their own over that duration (the slot's high 16 bits, e.g.
        /// sdom2818 小節7 → gradual slow-down); RampMs == 0 is an instant step. Pure/testable; O(log n).
        /// </summary>
        public double CurrentScrollSpeed(double ms)
        {
            var ss = ScrollSpeeds;
            if (ss == null || ss.Count == 0) return 1.0;
            int lo = 0, hi = ss.Count - 1, s = -1;
            while (lo <= hi) { int mid = (lo + hi) >> 1; if (ss[mid].TimeMs <= ms) { s = mid; lo = mid + 1; } else hi = mid - 1; }
            if (s < 0) return 1.0;
            var ev = ss[s];
            if (ev.RampMs > 0.0 && ms < ev.TimeMs + ev.RampMs)
            {
                double prev = s > 0 ? ss[s - 1].Mult : 1.0;
                double t = (ms - ev.TimeMs) / ev.RampMs;   // 0..1 across the ramp
                return prev + (ev.Mult - prev) * t;
            }
            return ev.Mult;
        }

        /// <summary>
        /// Time (ms, note/beat clock) of the earliest hit object — the first "downbeat" the player hits.
        /// The per-song DPS choreography spans this to the last note (its total ≈ last−first note), so the
        /// dancer holds its standby idle through the intro (which can be several measures AFTER the music-start
        /// marker — e.g. sdom1226 has the type-10 marker at beat 0 but the first note ~5.4 s in) and only begins
        /// the DPS at this beat. Anchoring the dance here (not on <see cref="MusicStartOffsetMs"/>) keeps the
        /// choreography from leading the song. Negative-guarded to 0 for empty charts. HitObjects are kept sorted.
        /// </summary>
        public double FirstNoteMs => HitObjects.Count > 0 ? HitObjects[0].StartTimeMs : 0.0;

        /// <summary>
        /// Scroll/timing control points, in MILLISECONDS, sorted by time. Used to drive the note scroll
        /// (BPM-change segments + osu! SV) the way osu!mania does — see <see cref="ManiaScroll"/>.
        /// For single-BPM charts this is left empty (the scroll then runs at a constant base velocity).
        /// .gn charts emit one uninherited point per BPM segment; .osu charts emit every timing + SV point.
        /// </summary>
        public List<OsuTimingPoint> TimingPoints { get; } = new List<OsuTimingPoint>();

        /// <summary>The sample volume (0..100) in effect at a chart time; 100 before the first timing point.</summary>
        public int SampleVolumeAt(double ms)
        {
            for (int i = TimingPoints.Count - 1; i >= 0; i--)
            {
                if (TimingPoints[i].TimeMs <= ms) return TimingPoints[i].SampleVolume;
            }
            return 100;
        }

        /// <summary>
        /// StepMania #STOPS/#FREEZES, in MILLISECONDS on the note/beat clock, sorted by time. Each is a window
        /// during which the note highway FREEZES: the audio keeps playing but every note holds its on-screen
        /// position for <see cref="ScrollStop.DurationMs"/>, then resumes. StepMania folds the stop into note
        /// timing (a note after a stop is hit that much later — <c>TimingData::GetElapsedTimeFromBeat</c>), which
        /// <see cref="SmChart"/> already bakes into the hit-object times; this list additionally drives the visual
        /// freeze via <see cref="ManiaScroll"/>. Empty for .gn/.osu and stop-free .sm charts (→ no freeze, no
        /// behaviour change).
        /// </summary>
        public List<ScrollStop> Stops { get; } = new List<ScrollStop>();

        /// <summary>
        /// 編輯器格線用的「拍 ↔ **真實**歌曲時間」（<see cref="BeatGrid"/>）。空 = 沒有，格線退回從
        /// <see cref="TimingPoints"/> 反推（.gn/.osu 走這條，行為不變）。
        ///
        /// 為什麼不能只靠 <see cref="TimingPoints"/>：那是**捲動**用的顯示時間軸 ——
        /// StepMania 的負 BPM(warp)在上面被壓成 1ms 的超高速窗，而 <see cref="Stops"/> 的定格時間根本不在
        /// 裡面。BeatGrid 是拿「ms 差 ÷ beatLength」反推 beat 編號的，於是每經過一個停拍就多算
        /// 停拍長度 ÷ 一拍的拍數，warp 後面那段更是把定格的 200 多 ms 當成超高速段（一拍 0.125ms）換算 →
        /// 一次多算上千拍。engine[Blue] 實測第 48 小節起就開始歪，第 64 小節差 6.6 秒、之後格線整個卡住。
        /// </summary>
        public List<GridSegment> GridSegments { get; } = new List<GridSegment>();

        /// <summary>
        /// Shift every note + timing point LATER by <paramref name="leadInMs"/> and add the same to
        /// <see cref="MusicStartOffsetMs"/> (which the engine treats as a silent count-in that delays the audio).
        /// External osu/StepMania charts carry no count-in, so an early first note would pop in mid-highway; a lead-in
        /// makes the first note scroll in from the spawn edge while keeping note↔audio sync (audio is delayed by the
        /// same amount). No-op for leadInMs &lt;= 0. Note/TimingPoint are readonly structs → rebuilt in place.
        /// </summary>
        public void ApplyLeadIn(int leadInMs)
        {
            if (leadInMs <= 0) return;
            for (int i = 0; i < HitObjects.Count; i++)
            {
                var h = HitObjects[i];
                // 炸彈/warp 旗標與顯示用時間都要跟著搬,不然平移完炸彈變普通 note、warp 音符的位置會脫節。
                HitObjects[i] = new OsuHitObject(h.Lane, h.StartTimeMs + leadInMs,
                    h.EndTimeMs.HasValue ? h.EndTimeMs.Value + leadInMs : (int?)null, h.IsBomb,
                    h.IsFake, h.ScrollTimeMs + leadInMs, h.ScrollEndTimeMs + leadInMs, h.IsFakeTail,
                    h.CustomSampleFilename, h.CustomSampleVolume);
            }
            for (int i = 0; i < TimingPoints.Count; i++)
            {
                var t = TimingPoints[i];
                TimingPoints[i] = new OsuTimingPoint(t.TimeMs + leadInMs, t.BeatLength, t.SampleVolume);
            }
            for (int i = 0; i < SampleEvents.Count; i++)
            {
                var s = SampleEvents[i];
                SampleEvents[i] = new OsuSampleEvent(s.TimeMs + leadInMs, s.Layer, s.Filename, s.Volume);
            }
            for (int i = 0; i < Stops.Count; i++)
            {
                var s = Stops[i];
                Stops[i] = new ScrollStop(s.TimeMs + leadInMs, s.DurationMs);
            }
            for (int i = 0; i < GridSegments.Count; i++)
            {
                var g = GridSegments[i];
                GridSegments[i] = new GridSegment(g.StartBeat, g.StartMs + leadInMs, g.StopMs, g.BeatLengthMs);
            }
            MusicStartOffsetMs += leadInMs;
        }

        /// <summary>
        /// Time (ms, note/beat clock) the chart's last note ENDS at (a hold's tail, else its hit time); 0 for an empty
        /// chart. With <see cref="FirstNoteMs"/> this is the dance span — what a generated choreography has to fill
        /// (see <see cref="RandomDps"/>). HitObjects are sorted by start, but a hold can outlast a later tap → scan.
        /// </summary>
        public double LastNoteMs
        {
            get
            {
                double last = 0.0;
                foreach (var h in HitObjects)
                {
                    double t = h.EndTimeMs ?? h.StartTimeMs;
                    if (t > last) last = t;
                }
                return last;
            }
        }

        /// <summary>
        /// 「無理短 long note」的長度上限（ms）＝ 180 BPM 的 16 分音符 (60000/180/4 ≈ 83.3 ms)。短於此的長條在遊戲裡
        /// 按不出來（頭尾兩次判定擠在一個判定窗內），多半是 osu/StepMania 譜面把裝飾音寫成極短 hold。這是絕對時間門檻，
        /// 與歌曲自身 BPM 無關：220 BPM 的 16 分 (68.2 ms) 比它短 → 收掉；150 BPM 的 16 分 (100 ms) 比它長 → 留著。
        /// </summary>
        public const double ShortHoldMaxMs = 60000.0 / 180.0 / 4.0;

        /// <summary>剛好一個 16 分音符的長條「不算」無理（規格：16 分以下、不含 16 分）。譜面時間是整數 ms，180 BPM
        /// 的 16 分 83.33 ms 會被存成 83，直接跟門檻比會被誤收 → 留 1 ms 的取整容差。</summary>
        private const double ShortHoldRoundingMs = 1.0;

        /// <summary>
        /// 這個格式的譜「可不可以」套 <see cref="CollapseShortHolds"/>。**只有從別的遊戲轉過來的譜**
        /// (osu / StepMania / Malody) 可以：極短 hold 是它們的裝飾音寫法，搬到這個引擎按不出來。
        /// SDO 原生的 .gn 一律不動——官方 k.gn (<see cref="SongFormat.None"/>，DATA/MUSIC) 與 Songs/ 底下的
        /// .gn 歌曲包 (<see cref="SongFormat.Gn"/>，[NX]Patch 轉出來的官方譜) 都是照這個引擎的判定打的，
        /// 譜上多短的長條都是作者的原意，改了就是改官方譜。純函式。
        /// </summary>
        public static bool AllowsShortHoldCollapse(SongFormat format)
            => format == SongFormat.Osu || format == SongFormat.Sm || format == SongFormat.Malody;

        /// <summary>
        /// 把「無理的短 long note」原地改成一般 note（清掉 EndTimeMs，只留頭部判定）：長度 &lt;
        /// <paramref name="maxHoldMs"/> 的長條 → tap。回傳被轉換的顆數。純函式（只動 HitObjects，不碰時間/BPM/timing
        /// points），呼叫端用開關 gating（見 GameplaySettings.collapseShortHolds）＋格式 gating
        /// （<see cref="AllowsShortHoldCollapse"/>：只有外部轉檔譜能套）。長度剛好等於門檻的長條保留。
        /// </summary>
        public int CollapseShortHolds(double maxHoldMs = ShortHoldMaxMs)
        {
            double cutoff = maxHoldMs - ShortHoldRoundingMs;
            int n = 0;
            for (int i = 0; i < HitObjects.Count; i++)
            {
                var h = HitObjects[i];
                if (!h.IsHold) continue;
                // cap 落在 StepMania warp 裡的長條**不收**:它的判定長度短只是因為尾端被夾到 warp 那一瞬間
                // (整條在拍子上是正常長度,StepMania 也照 beat 間距整條畫出來)。收成 tap 會把那一整條裝飾
                // 長條從畫面上抹掉 —— deadsoul[Blue] 有 64 條這種,判定長度只剩 ~57ms,一收就全沒了。
                if (h.IsFakeTail) continue;
                double dur = h.EndTimeMs.Value - h.StartTimeMs;
                if (dur >= cutoff) continue;
                // 長條 → 一般 note(頭部的判定/顯示時間原封不動,尾端跟著收回頭部)
                HitObjects[i] = new OsuHitObject(h.Lane, h.StartTimeMs, null, h.IsBomb, h.IsFake,
                    h.ScrollTimeMs, h.ScrollTimeMs, false, h.CustomSampleFilename, h.CustomSampleVolume);
                n++;
            }
            return n;
        }

        /// <summary>
        /// 把譜面上的炸彈(mine / avoid-note)整顆拿掉，回傳移除的顆數。給 OPTION 進階「歌曲炸彈」**關掉**時用
        /// （GameplaySettings.songBombs=false）：踩到炸彈只會扣血（不斷 combo）、
        /// 永遠不計分也不計 miss，所以移除它**不影響**滿分、TotalNotes 或任何判定數字（<see cref="TotalNotes"/>
        /// 本來就跳過炸彈）——差別只在「地上不再有雷」。純函式（只動 HitObjects，不碰時間/BPM/timing points），
        /// 呼叫端用開關 gating（見 ScreenGameplay.LoadChart）。
        /// </summary>
        public int RemoveBombs()
        {
            int n = 0;
            for (int i = HitObjects.Count - 1; i >= 0; i--)
            {
                if (!HitObjects[i].IsBomb) continue;
                HitObjects.RemoveAt(i);
                n++;
            }
            return n;
        }

        /// <summary>Total judged events = taps + holdHeads + holdReleases. 炸彈不算 —— 它永遠不會被判定
        /// (踩到只扣血)，算進來的話滿分就永遠打不到。StepMania warp (負 BPM) 掃過去的
        /// <see cref="OsuHitObject.IsFake"/> 音符同理:播放頭是瞬間跳過那一段的,玩家連按的機會都沒有。
        /// cap 被 warp 掃掉的長條(<see cref="OsuHitObject.IsFakeTail"/>)只算**頭部一個**判定:結尾不用放開,
        /// 算兩個的話滿分永遠差那一下。</summary>
        public int TotalNotes
        {
            get
            {
                int total = 0;
                foreach (var h in HitObjects)
                {
                    if (h.IsBomb || h.IsFake) continue;
                    total += (h.IsHold && !h.IsFakeTail) ? 2 : 1;
                }
                return total;
            }
        }
    }

    /// <summary>
    /// One SDO frame_type-33 捲動速度 point: at <see cref="TimeMs"/> the current scroll multiplier heads to
    /// <see cref="Mult"/>. <see cref="RampMs"/> == 0 → instant step; &gt; 0 → linear ramp from the previous
    /// multiplier over that many ms (the slot's high-16-bits duration, 官方線性變速).
    /// </summary>
    public readonly struct OsuScrollSpeed
    {
        public double TimeMs { get; }
        public double Mult { get; }
        public double RampMs { get; }
        public OsuScrollSpeed(double timeMs, double mult, double rampMs = 0.0) { TimeMs = timeMs; Mult = mult; RampMs = rampMs; }
    }

    /// <summary>
    /// One scroll/timing control point (osu! semantics, time in ms):
    ///   <see cref="BeatLength"/> &gt; 0 — UNINHERITED point (a tempo / BPM change), ms-per-beat.
    ///   <see cref="BeatLength"/> &lt; 0 — INHERITED point (osu! green line), an SV multiplier of -100/BeatLength.
    /// .gn charts only ever produce uninherited points (BPM segments); .osu charts produce both.
    /// </summary>
    public readonly struct OsuTimingPoint
    {
        public double TimeMs { get; }
        public double BeatLength { get; }
        public int SampleVolume { get; }

        public OsuTimingPoint(double timeMs, double beatLength, int sampleVolume = 100)
        {
            TimeMs = timeMs;
            BeatLength = beatLength;
            SampleVolume = sampleVolume < 0 ? 0 : (sampleVolume > 100 ? 100 : sampleVolume);
        }

        /// <summary>True for a tempo (BPM) point; false for an osu! SV (green) line.</summary>
        public bool Uninherited => BeatLength > 0.0;

        /// <summary>osu! scroll-velocity multiplier: 1.0 for tempo points, -100/BeatLength for green lines.</summary>
        public double SpeedMultiplier => BeatLength < 0.0 ? -100.0 / BeatLength : 1.0;
    }

    /// <summary>An osu! storyboard Sample event: play one beatmap-relative audio file automatically at a chart time.</summary>
    public readonly struct OsuSampleEvent
    {
        public double TimeMs { get; }
        public int Layer { get; }
        public string Filename { get; }
        public int Volume { get; }

        public OsuSampleEvent(double timeMs, int layer, string filename, int volume = 100)
        {
            TimeMs = timeMs;
            Layer = layer;
            Filename = filename ?? "";
            Volume = volume < 0 ? 0 : (volume > 100 ? 100 : volume);
        }
    }

    /// <summary>
    /// One StepMania stop/freeze window (note/beat-clock ms): the highway holds all note positions for
    /// <see cref="DurationMs"/> starting at <see cref="TimeMs"/>. See <see cref="OsuBeatmap.Stops"/>.
    /// </summary>
    public readonly struct ScrollStop
    {
        public double TimeMs { get; }
        public double DurationMs { get; }

        public ScrollStop(double timeMs, double durationMs)
        {
            TimeMs = timeMs;
            DurationMs = durationMs;
        }
    }

    /// <summary>
    /// 一段「拍 ↔ 真實歌曲時間」（給編輯器格線用，見 <see cref="OsuBeatmap.GridSegments"/> 與 <see cref="BeatGrid"/>）。
    /// 語意就是 StepMania <c>TimingData::GetElapsedTimeFromBeat</c> 的分段形式：播放頭在 <see cref="StartMs"/>
    /// 到達 <see cref="StartBeat"/>，在那一拍上停 <see cref="StopMs"/>，之後每一拍走 <see cref="BeatLengthMs"/>。
    /// </summary>
    public readonly struct GridSegment
    {
        /// <summary>這一段從哪一拍開始。</summary>
        public double StartBeat { get; }
        /// <summary>播放頭**到達**那一拍的時刻（ms；停拍還沒開始）。</summary>
        public double StartMs { get; }
        /// <summary>這一拍上停多久（#STOPS 定格；沒有就 0）。</summary>
        public double StopMs { get; }
        /// <summary>停完之後每一拍幾 ms。**0 = warp**：拍子繼續走但時間不前進（負 BPM 被瞬間跳過的那段）。</summary>
        public double BeatLengthMs { get; }

        public GridSegment(double startBeat, double startMs, double stopMs, double beatLengthMs)
        {
            StartBeat = startBeat; StartMs = startMs; StopMs = stopMs; BeatLengthMs = beatLengthMs;
        }
    }
}
