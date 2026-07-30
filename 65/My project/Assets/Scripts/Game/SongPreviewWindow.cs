using System;

namespace Sdo.Game
{
    /// <summary>Resolves the loop window used when song select previews a full song.</summary>
    public static class SongPreviewWindow
    {
        public const float LegacyAutomaticStartRatio = 0.4f;
        public const float OsuAutomaticStartRatio = 0.5f;

        public static float AutomaticStartRatio(int chartFormat)
        {
            return chartFormat == (int)Sdo.Osu.SongFormat.Osu
                ? OsuAutomaticStartRatio
                : LegacyAutomaticStartRatio;
        }
        /// <summary>
        /// osu PreviewTime:0 is treated like an absent preview point. Other formats keep zero as an explicit start
        /// (notably StepMania #SAMPLESTART:0).
        /// </summary>
        public static int NormalizeStart(int chartFormat, int previewStartMs)
        {
            return chartFormat == (int)Sdo.Osu.SongFormat.Osu && previewStartMs <= 0
                ? -1
                : previewStartMs;
        }

        /// <summary>
        /// A non-negative source preview point is honoured. A negative point uses
        /// <paramref name="automaticStartRatio"/>, then clamps so the requested window remains inside the available
        /// timeline. Call <see cref="NormalizeStart"/> first when applying format-specific metadata.
        /// </summary>
        public static double ResolveStart(
            double requestedStart, double duration, double windowLength,
            double automaticStartRatio = LegacyAutomaticStartRatio)
        {
            if (!(duration > 0.0)) return 0.0;

            double window = windowLength > 0.0 ? windowLength : duration;
            double ratio = Math.Max(0.0, Math.Min(1.0, automaticStartRatio));
            double start = requestedStart >= 0.0 && requestedStart < duration
                ? requestedStart
                : duration * ratio;
            return Math.Max(0.0,
                Math.Min(start, Math.Max(0.0, duration - window)));
        }
    }
}
