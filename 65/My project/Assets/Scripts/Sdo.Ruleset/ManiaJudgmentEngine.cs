namespace Sdo.Ruleset
{
    /// <summary>
    /// Stateless timing judge over <see cref="JudgmentWindows"/>.
    /// Tap and hold-head use the same window; hold-tail is judged on release timing.
    /// Combo/score/health side effects live in their own processors — this only maps time -> Judgment.
    /// </summary>
    public sealed class ManiaJudgmentEngine
    {
        public JudgmentWindows Windows { get; }

        public ManiaJudgmentEngine(double overallDifficulty)
            : this(new JudgmentWindows(overallDifficulty)) { }

        public ManiaJudgmentEngine(JudgmentWindows windows)
        {
            Windows = windows;
        }

        /// <summary>
        /// Judge a press against a note time. Returns null when the press is outside the
        /// miss boundary (too early / not this note's press).
        /// </summary>
        public Judgment? JudgeHit(double noteTimeMs, double hitTimeMs)
            => Windows.Judge(hitTimeMs - noteTimeMs);

        /// <summary>Judge a hold release against the hold's end time.</summary>
        public Judgment? JudgeHoldTail(double endTimeMs, double releaseTimeMs)
            => Windows.Judge(releaseTimeMs - endTimeMs);

        /// <summary>
        /// True once the current time has passed the note beyond the miss boundary with no hit,
        /// i.e. the note should be auto-missed.
        /// </summary>
        public bool HasPassed(double noteTimeMs, double currentTimeMs)
            => currentTimeMs - noteTimeMs > Windows.MissBoundary;
    }
}
