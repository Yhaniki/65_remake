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
                HitObjects[i] = new OsuHitObject(h.Lane, h.StartTimeMs + leadInMs,
                    h.EndTimeMs.HasValue ? h.EndTimeMs.Value + leadInMs : (int?)null, h.IsBomb);   // 炸彈旗標要跟著搬,不然平移完就變普通 note
            }
            for (int i = 0; i < TimingPoints.Count; i++)
            {
                var t = TimingPoints[i];
                TimingPoints[i] = new OsuTimingPoint(t.TimeMs + leadInMs, t.BeatLength);
            }
            for (int i = 0; i < Stops.Count; i++)
            {
                var s = Stops[i];
                Stops[i] = new ScrollStop(s.TimeMs + leadInMs, s.DurationMs);
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
        /// 把「無理的短 long note」原地改成一般 note（清掉 EndTimeMs，只留頭部判定）：長度 &lt;
        /// <paramref name="maxHoldMs"/> 的長條 → tap。回傳被轉換的顆數。純函式（只動 HitObjects，不碰時間/BPM/timing
        /// points），呼叫端用開關 gating（見 GameplaySettings.collapseShortHolds）。長度剛好等於門檻的長條保留。
        /// </summary>
        public int CollapseShortHolds(double maxHoldMs = ShortHoldMaxMs)
        {
            double cutoff = maxHoldMs - ShortHoldRoundingMs;
            int n = 0;
            for (int i = 0; i < HitObjects.Count; i++)
            {
                var h = HitObjects[i];
                if (!h.IsHold) continue;
                double dur = h.EndTimeMs.Value - h.StartTimeMs;
                if (dur >= cutoff) continue;
                HitObjects[i] = new OsuHitObject(h.Lane, h.StartTimeMs);   // 長條 → 一般 note
                n++;
            }
            return n;
        }

        /// <summary>Total judged events = taps + holdHeads + holdReleases. 炸彈不算 —— 它永遠不會被判定
        /// (踩到只扣血)，算進來的話滿分就永遠打不到。</summary>
        public int TotalNotes
        {
            get
            {
                int total = 0;
                foreach (var h in HitObjects)
                {
                    if (h.IsBomb) continue;
                    total += h.IsHold ? 2 : 1;
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

        public OsuTimingPoint(double timeMs, double beatLength)
        {
            TimeMs = timeMs;
            BeatLength = beatLength;
        }

        /// <summary>True for a tempo (BPM) point; false for an osu! SV (green) line.</summary>
        public bool Uninherited => BeatLength > 0.0;

        /// <summary>osu! scroll-velocity multiplier: 1.0 for tempo points, -100/BeatLength for green lines.</summary>
        public double SpeedMultiplier => BeatLength < 0.0 ? -100.0 / BeatLength : 1.0;
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
}
