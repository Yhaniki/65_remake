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

        /// <summary>Total judged events = taps + holdHeads + holdReleases.</summary>
        public int TotalNotes
        {
            get
            {
                int total = 0;
                foreach (var h in HitObjects)
                    total += h.IsHold ? 2 : 1;
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
}
