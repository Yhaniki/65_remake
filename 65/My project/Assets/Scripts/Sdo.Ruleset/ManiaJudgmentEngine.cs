namespace Sdo.Ruleset
{
    /// <summary>
    /// Stateless timing judge over <see cref="JudgmentWindows"/>.
    /// Tap and hold-head use the same window; hold-tail is judged on release timing.
    /// Combo/score/health side effects live in their own processors — this only maps time -> Judgment.
    /// </summary>
    public sealed class ManiaJudgmentEngine
    {
        /// <summary>
        /// Releasing a long note precisely at its end is harder than tapping a note, so the hold-tail
        /// release is judged against windows widened by this factor (tail window = press window × HoldTailWindowScale).
        /// </summary>
        public const double HoldTailWindowScale = 1.2;

        public JudgmentWindows Windows { get; }

        /// <summary>Press windows widened by <see cref="HoldTailWindowScale"/> — the hold-tail release timing windows.</summary>
        public JudgmentWindows HoldTailWindows { get; }

        public ManiaJudgmentEngine(double overallDifficulty)
            : this(new JudgmentWindows(overallDifficulty)) { }

        public ManiaJudgmentEngine(JudgmentWindows windows)
        {
            Windows = windows;
            HoldTailWindows = windows.Scaled(HoldTailWindowScale);
        }

        /// <summary>
        /// Judge a press against a note time. Returns null when the press is outside the
        /// miss boundary (too early / not this note's press).
        /// </summary>
        public Judgment? JudgeHit(double noteTimeMs, double hitTimeMs)
            => Windows.Judge(hitTimeMs - noteTimeMs);

        /// <summary>Judge a hold release against the hold's end time, using the widened tail windows.</summary>
        public Judgment? JudgeHoldTail(double endTimeMs, double releaseTimeMs)
            => HoldTailWindows.Judge(releaseTimeMs - endTimeMs);

        /// <summary>
        /// True once the current time has passed the note beyond the miss boundary with no hit,
        /// i.e. the note should be auto-missed.
        /// </summary>
        public bool HasPassed(double noteTimeMs, double currentTimeMs)
            => currentTimeMs - noteTimeMs > Windows.MissBoundary;

        /// <summary>
        /// True once a still-held hold has run past its end beyond the (widened) tail miss boundary
        /// with no release — the tail can no longer earn a grade and must be auto-missed. This gate
        /// must track the tail window, not the press window, or a note held into the extra tail
        /// leniency would be force-missed while a release there would still score.
        /// </summary>
        public bool HoldTailHasPassed(double endTimeMs, double currentTimeMs)
            => currentTimeMs - endTimeMs > HoldTailWindows.MissBoundary;
    }
}
