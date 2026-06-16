namespace Sdo.Osu
{
    /// <summary>A single mania note. Tap has no end time; Hold carries an end time.</summary>
    public readonly struct OsuHitObject
    {
        /// <summary>Lane index, 0-based (0..keys-1).</summary>
        public int Lane { get; }

        /// <summary>Judgment time of the note (head time for holds), in milliseconds.</summary>
        public int StartTimeMs { get; }

        /// <summary>End time for hold notes, in milliseconds; null for taps.</summary>
        public int? EndTimeMs { get; }

        public bool IsHold => EndTimeMs.HasValue;

        public OsuHitObject(int lane, int startTimeMs, int? endTimeMs = null)
        {
            Lane = lane;
            StartTimeMs = startTimeMs;
            EndTimeMs = endTimeMs;
        }
    }
}
