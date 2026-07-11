namespace Sdo.Ruleset
{
    /// <summary>
    /// Authoritative gameplay time (ms) that drives note scrolling and judging.
    ///
    /// The note timeline and the song audio run on TWO independent clocks: Unity's frame/wall clock
    /// (Time.timeAsDouble) and the sound device's DSP clock (AudioSettings.dspTime). They are anchored
    /// together at song start, then drift apart — a few tens of ms over a 3-minute song from crystal-rate
    /// mismatch, plus the occasional audio-buffer stall. Left unchecked the notes and the music separate.
    ///
    /// This clock ADVANCES smoothly on the per-frame wall delta (so there is no visible jitter) while gently
    /// pulling itself back toward the true audio playback position every frame — a slew-rate-limited
    /// complementary filter: the wall delta is the high-frequency estimate, the audio position is the
    /// low-frequency truth. Small drift is corrected invisibly; a gap too large to slew (a stall / seek) is
    /// snapped. When the audio position is unknown (the READY/GO lead-in, the silent count-in, or after the
    /// song ends) it free-runs on the wall clock, exactly as the old clock did.
    ///
    /// Pure logic, no engine dependency (Sdo.Ruleset is noEngineReferences) — see GameplayClockTests.
    /// </summary>
    public sealed class GameplayClock
    {
        /// <summary>Global timing offset in ms (positive = notes judged later). Audio-latency calibration hook.</summary>
        public double OffsetMs { get; set; }

        /// <summary>Current gameplay time in ms (smoothed audio position + offset). Monotonic except on a stall snap.</summary>
        public double CurrentMs { get; private set; }

        /// <summary>Gap (ms) beyond which the clock hard-snaps onto the audio instead of slewing (a stall / seek).</summary>
        public double SnapThresholdMs { get; set; } = 50.0;

        /// <summary>Max share of a frame's wall-time spendable on catch-up (0.05 = the apparent rate stays within ±5%).</summary>
        public double MaxSlewRate { get; set; } = 0.05;

        private double _estMs;      // smoothed chart time BEFORE the offset, ms
        private double _lastWallMs; // previous frame's wall reading, ms
        private bool _primed;       // false until the first Tick seeds the estimate

        /// <summary>Smoothed chart time before the offset is applied (ms). Exposed for tests / telemetry.</summary>
        public double EstimatedMs => _estMs;

        /// <summary>Re-arm for a new song: the next Tick re-seeds the estimate from scratch.</summary>
        public void Reset()
        {
            _primed = false;
            _estMs = 0.0;
            _lastWallMs = 0.0;
            CurrentMs = 0.0;
        }

        /// <summary>Back-compat: advance on the wall clock only (audio position unknown).</summary>
        public void SetAudioSeconds(double wallSeconds) => Tick(wallSeconds, null);

        /// <param name="wallSeconds">
        ///   Smooth per-frame chart time from the wall clock (Time.timeAsDouble - clockStart), in seconds.
        /// </param>
        /// <param name="audioSeconds">
        ///   Authoritative chart time from the audio DSP playback position, in seconds, or null when the audio
        ///   is not playing (the lead-in, the silent count-in, or after the song has finished).
        /// </param>
        public void Tick(double wallSeconds, double? audioSeconds)
        {
            double wallMs = wallSeconds * 1000.0;

            if (!_primed)
            {
                // Seed on the audio truth if we already have it, otherwise on the wall clock.
                _estMs = audioSeconds.HasValue ? audioSeconds.Value * 1000.0 : wallMs;
                _lastWallMs = wallMs;
                _primed = true;
                CurrentMs = _estMs + OffsetMs;
                return;
            }

            double dtWall = wallMs - _lastWallMs;
            _lastWallMs = wallMs;
            if (dtWall < 0.0) dtWall = 0.0;   // never run backward if the wall reading dips

            _estMs += dtWall;                 // smooth advance on the frame delta

            if (audioSeconds.HasValue)
            {
                double audioMs = audioSeconds.Value * 1000.0;
                double err = audioMs - _estMs;
                if (err > SnapThresholdMs || err < -SnapThresholdMs)
                {
                    _estMs = audioMs;         // too far off to slew (stall / seek) — snap onto the audio
                }
                else
                {
                    // Pull a slew-limited fraction of the gap. Capped at MaxSlewRate*dtWall so the apparent rate
                    // stays within ±MaxSlewRate and the net motion (dtWall + corr) stays forward — no rewind, no
                    // audible/visible speed jump, and the per-buffer staircase of the (steppy) audio reading is
                    // low-passed away while a persistent drift is corrected over ~1s.
                    double maxCorr = MaxSlewRate * dtWall;
                    double corr = err > maxCorr ? maxCorr : (err < -maxCorr ? -maxCorr : err);
                    _estMs += corr;
                }
            }

            CurrentMs = _estMs + OffsetMs;
        }
    }
}
