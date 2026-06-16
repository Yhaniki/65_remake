namespace Sdo.Ruleset
{
    /// <summary>
    /// System-level (not per-chart) gameplay precision and health difficulty.
    /// The .osu file's OverallDifficulty / HPDrainRate are intentionally ignored.
    /// </summary>
    public sealed class SdoSystemSettings
    {
        /// <summary>Judgment precision, 0..10. Higher = tighter windows.</summary>
        public double OverallDifficulty { get; set; } = 8.0;

        /// <summary>Health drain difficulty, 0..10. Higher = harsher.</summary>
        public double HpDrainRate { get; set; } = 8.0;
    }
}
