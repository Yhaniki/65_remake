namespace Sdo.Ruleset
{
    /// <summary>
    /// 4-tier hit windows (ms): Perfect / Cool / Bad / Miss-boundary.
    /// Use <see cref="FromSdoBpm"/> for the ORIGINAL SDO behaviour (tick-based, BPM-dependent —
    /// what the game uses). The OD constructor is the legacy osu!mania mapping, kept for reference.
    /// </summary>
    public sealed class JudgmentWindows
    {
        // (min@OD0, mid@OD5, max@OD10)
        private static readonly double[] PerfectRange = { 22.4, 19.4, 13.9 };
        private static readonly double[] CoolRange = { 64, 49, 34 };   // osu Great
        private static readonly double[] BadRange = { 97, 82, 67 };    // osu Good
        private static readonly double[] MissRange = { 151, 136, 121 }; // osu Meh = miss boundary

        public double Perfect { get; }
        public double Cool { get; }
        public double Bad { get; }
        public double MissBoundary { get; }

        public JudgmentWindows(double overallDifficulty)
        {
            Perfect = DifficultyRange(overallDifficulty, PerfectRange);
            Cool = DifficultyRange(overallDifficulty, CoolRange);
            Bad = DifficultyRange(overallDifficulty, BadRange);
            MissBoundary = DifficultyRange(overallDifficulty, MissRange);
        }

        private JudgmentWindows(double perfect, double cool, double bad, double miss)
        {
            Perfect = perfect; Cool = cool; Bad = bad; MissBoundary = miss;
        }

        // SDO original timing windows are in CHART TICKS (1/192 bar), NOT ms (verified at the
        // judge caller ~0x497a60: e = abs(playhead - notePos), notePos = bar*192 + tick).
        // Normal: Perfect<=6, Cool<=15, Bad<=20, miss-register<=25 ticks; narrow/practice: 5/10/16/20.
        private const double NormalPerfectT = 6, NormalCoolT = 15, NormalBadT = 20, NormalMissT = 25;
        private const double NarrowPerfectT = 5, NarrowCoolT = 10, NarrowBadT = 16, NarrowMissT = 20;

        /// <summary>
        /// SDO original judgement windows for a given song tempo. A tick is 1/192 bar, so
        /// 1 tick = 1250/BPM ms (ticks advance at BPM*0.8 per second). Faster song ⇒ tighter ms
        /// windows ⇒ stricter — exactly the original's BPM-dependent feel.
        /// </summary>
        public static JudgmentWindows FromSdoBpm(double bpm, bool narrow = false)
        {
            if (bpm <= 0.0) bpm = 120.0;          // safe fallback when a chart has no tempo
            double msPerTick = 1250.0 / bpm;       // 1000 / (BPM * 0.8)
            return narrow
                ? new JudgmentWindows(NarrowPerfectT * msPerTick, NarrowCoolT * msPerTick,
                                      NarrowBadT * msPerTick, NarrowMissT * msPerTick)
                : new JudgmentWindows(NormalPerfectT * msPerTick, NormalCoolT * msPerTick,
                                      NormalBadT * msPerTick, NormalMissT * msPerTick);
        }

        /// <summary>
        /// Judge an absolute timing error (ms).
        /// Returns null when the press is outside the miss boundary (too early — ignore the press).
        /// Inside the miss boundary but past Bad counts as a (combo-breaking) Miss that consumes the note.
        /// </summary>
        public Judgment? Judge(double absDeltaMs)
        {
            if (absDeltaMs < 0) absDeltaMs = -absDeltaMs;
            if (absDeltaMs <= Perfect) return Judgment.Perfect;
            if (absDeltaMs <= Cool) return Judgment.Cool;
            if (absDeltaMs <= Bad) return Judgment.Bad;
            if (absDeltaMs <= MissBoundary) return Judgment.Miss;
            return null;
        }

        /// <summary>osu! piecewise-linear difficulty interpolation. range = (min, mid, max).</summary>
        public static double DifficultyRange(double difficulty, double[] range)
        {
            double min = range[0], mid = range[1], max = range[2];
            if (difficulty > 5) return mid + (max - mid) * (difficulty - 5) / 5;
            if (difficulty < 5) return mid - (mid - min) * (5 - difficulty) / 5;
            return mid;
        }
    }
}
