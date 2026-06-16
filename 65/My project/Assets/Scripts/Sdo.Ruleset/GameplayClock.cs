namespace Sdo.Ruleset
{
    /// <summary>
    /// Authoritative gameplay time in milliseconds, driven by the audio playback position.
    /// A user/global offset (ms) is added when comparing against note times.
    /// </summary>
    public sealed class GameplayClock
    {
        /// <summary>Global timing offset in ms (positive = notes judged later).</summary>
        public double OffsetMs { get; set; }

        /// <summary>Current gameplay time in ms (audio position + offset).</summary>
        public double CurrentMs { get; private set; }

        public void SetAudioSeconds(double audioSeconds)
        {
            CurrentMs = audioSeconds * 1000.0 + OffsetMs;
        }
    }
}
