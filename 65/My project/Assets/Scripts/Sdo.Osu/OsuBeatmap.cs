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
                    h.EndTimeMs.HasValue ? h.EndTimeMs.Value + leadInMs : (int?)null);
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
