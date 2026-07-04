using System;

namespace Sdo.Ruleset
{
    /// <summary>
    /// SDO <b>ShowTime</b> (氣條 / energy) meter — pure logic, model from the ONLINE sdo.bin.c decompile
    /// (docs/reverse-engineering/SDO_SHOWTIME.md). The gauge is a <b>three-band LAP counter</b>, NOT one continuous
    /// ms bar (the earlier remake conflated two separate exe tables):
    /// <list type="bullet">
    /// <item>a cumulative integer <see cref="FillCount"/> grows on good hits;</item>
    /// <item><see cref="BandCaps"/> are the cumulative fill targets green/yellow/red. The exe's practice caps are
    /// 50/150/250; online reads them from config, so they are remake tunables (default scaled so band 0 fills at
    /// ~combo 130 with <see cref="GainPerfect"/>=1).</item>
    /// <item>the VISIBLE bar re-bases per band (0→full within the current band) and CHANGES COLOUR (MyEnergy5/6/7),
    /// so it looks like "fill green to full, reset to empty, fill yellow, reset, fill red" —
    /// see <see cref="DisplayBand"/>/<see cref="DisplayFraction"/>.</item>
    /// <item><see cref="WindowDurationsMs"/> = {8000,12000,18000} is a SEPARATE table: the auto-perfect WINDOW length
    /// in ms for a release at band 0/1/2 — NOT a fill threshold.</item>
    /// </list>
    /// On SPACE the window runs <see cref="WindowDurationsMs"/>[armed band] ms (forced PERFECT); the fill resets to 0.
    /// A score bonus stacks +1 per release (cap 6×). The exe only accumulates the fill FORWARD (Bad/Miss don't reduce
    /// it); the per-hit increment is chart note-interval × speed, so the gains here are remake tunables.
    /// </summary>
    public sealed class ShowtimeMeter
    {
        /// <summary>Cumulative fill targets for band 0/1/2 (yellow/blue/red). Ascending. Reaching BandCaps[0] arms
        /// tier 1. Values = the user's OFFICIAL-client measurement (all-good hits): tier 1 at hit 130, tier 2 at 237,
        /// tier 3 at 410. (The exe reads score-tiered caps from config and its per-hit quantum is chart-dependent;
        /// with GainPerfect=1 these hit-counts reproduce the measured official pacing.)</summary>
        public int[] BandCaps = { 130, 237, 410 };

        /// <summary>Per good-hit fill increment (exe = chart note-interval × speed → remake tunable). Bad/Miss add 0.</summary>
        public int GainPerfect = 1;
        public int GainCool = 1;

        /// <summary>Auto-perfect WINDOW length in ms for a release at band 0/1/2 (exe dur[] 8000/12000/18000). Separate
        /// from the fill counter.</summary>
        public int[] WindowDurationsMs = { 8000, 12000, 18000 };

        /// <summary>Activation counter caps here → bonus multiplier caps at MaxActivations+1 (exe 0x408 caps 5 → 6×).</summary>
        public const int MaxActivations = 5;

        /// <summary>Cumulative fill counter, clamped [0, BandCaps[last]].</summary>
        public int FillCount { get; private set; }

        /// <summary>True while a ShowTime window is running (input auto-driven).</summary>
        public bool Active { get; private set; }

        /// <summary>ms timestamp when the active window ends (valid only while <see cref="Active"/>).</summary>
        public double UntilMs { get; private set; }

        /// <summary>Full length (ms) of the current window = WindowDurationsMs[released band].</summary>
        public double WindowMs { get; private set; }

        /// <summary>-1 before any release; 0-based count of releases so far (exe 0x408). Caps at MaxActivations.</summary>
        public int ActivationCount { get; private set; } = -1;

        /// <summary>Accumulated ShowTime bonus points (exe 0x840 / the "EnergyBonus" number).</summary>
        public long Bonus { get; private set; }

        /// <summary>Band (0/1/2) the current window was released at, else -1.</summary>
        public int ReleasedLevel { get; private set; } = -1;

        /// <summary>Number of bands whose cap the fill has reached (0..3).</summary>
        public int CompletedBands
        {
            get
            {
                int n = 0;
                for (int i = 0; i < BandCaps.Length; i++) if (FillCount >= BandCaps[i]) n++;
                return n;
            }
        }

        /// <summary>Highest band ARMED for release (0/1/2), or -1 below green. = CompletedBands − 1 (exe 0x1834 level;
        /// 0xff=none).</summary>
        public int ArmedLevel => CompletedBands - 1;

        /// <summary>The band currently FILLING / shown on the bar (0/1/2 → MyEnergy5/6/7). = min(CompletedBands, 2).</summary>
        public int DisplayBand { get { int b = CompletedBands; return b > 2 ? 2 : b; } }

        /// <summary>Fill fraction 0..1 WITHIN the current display band, re-based each band (the "reset &amp; refill").</summary>
        public float DisplayFraction
        {
            get
            {
                int b = DisplayBand;
                int start = b == 0 ? 0 : BandCaps[b - 1];
                int end = BandCaps[b];
                if (end <= start) return 1f;
                float f = (float)(FillCount - start) / (end - start);
                return f < 0f ? 0f : (f > 1f ? 1f : f);
            }
        }

        /// <summary>The bar is filled enough to release (band 0 reached) and not already running.</summary>
        public bool Ready => !Active && ArmedLevel >= 0;

        /// <summary>Bonus multiplier applied to each note's points during the current/next window (1..6).</summary>
        public int BonusMultiplier => Math.Min(ActivationCount, MaxActivations) + 1;

        /// <summary>ms remaining in the active window (0 when inactive).</summary>
        public double RemainingMs(double nowMs) => Active ? Math.Max(0.0, UntilMs - nowMs) : 0.0;

        /// <summary>Fraction 1→0 of the active window still remaining (for a draining HUD bar); 0 when inactive.</summary>
        public float WindowRemainingFraction(double nowMs) =>
            Active && WindowMs > 0.0 ? (float)(RemainingMs(nowMs) / WindowMs) : 0f;

        private int MaxFill => BandCaps[BandCaps.Length - 1];

        // exe flat per-note weights (×10): PERFECT 50 / COOL 40 / BAD 20 / MISS −10
        private static int FlatBase(Judgment j)
        {
            switch (j)
            {
                case Judgment.Perfect: return 50;
                case Judgment.Cool: return 40;
                case Judgment.Bad: return 20;
                default: return -10; // Miss
            }
        }

        /// <summary>
        /// Feed one judged event. During a ShowTime window the fill is frozen (the timer drains the window) and the
        /// score bonus accrues (multiplier = <see cref="BonusMultiplier"/>). Otherwise good hits accumulate the fill
        /// FORWARD (Bad/Miss don't reduce it — matches the exe).
        /// </summary>
        public void OnJudge(Judgment j)
        {
            if (Active)
            {
                Bonus += (long)BonusMultiplier * FlatBase(j);
                if (Bonus < 0) Bonus = 0;
                return;
            }
            switch (j)
            {
                case Judgment.Perfect: FillCount += GainPerfect; break;
                case Judgment.Cool: FillCount += GainCool; break;
            }
            if (FillCount < 0) FillCount = 0;
            else if (FillCount > MaxFill) FillCount = MaxFill;
        }

        /// <summary>
        /// Try to release ShowTime at <paramref name="nowMs"/>. Succeeds only when <see cref="Ready"/> (band 0 reached).
        /// Window length: <paramref name="windowMsOverride"/> when &gt; 0 (the caller computed the official
        /// pas-quantised duration from the chart — sdo.bin accumulates WHOLE dance segments until the tier budget
        /// 8000/12000/18000 ms is reached, so real windows run ~9.5/13.8/19.6 s), else the raw budget table.
        /// The fill spends the released band's cost and KEEPS the overflow (exe: residual = fill − cap[level],
        /// carried into the next charge — FUN_00643030 @348249). Advances the bonus multiplier.
        /// </summary>
        public bool TryActivate(double nowMs, double windowMsOverride = 0.0)
        {
            if (Active) return false;
            int level = ArmedLevel;
            if (level < 0) return false;

            if (ActivationCount < MaxActivations) ActivationCount++;
            ReleasedLevel = level;
            Active = true;
            WindowMs = windowMsOverride > 0.0 ? windowMsOverride : WindowDurationsMs[level];
            UntilMs = nowMs + WindowMs;
            FillCount -= BandCaps[level];           // spend the released cost; overflow above the cap carries over
            if (FillCount < 0) FillCount = 0;
            return true;
        }

        /// <summary>
        /// Advance the active window. Returns true on the single frame the window ends (so callers can revert the note
        /// skin / hit effect / dance), false otherwise.
        /// </summary>
        public bool Tick(double nowMs)
        {
            if (!Active) return false;
            if (nowMs < UntilMs) return false;
            Active = false;
            ReleasedLevel = -1;
            return true;
        }

        /// <summary>Reset everything for a fresh song (keeps the tunables).</summary>
        public void Reset()
        {
            FillCount = 0;
            Active = false;
            UntilMs = 0.0;
            WindowMs = 0.0;
            ActivationCount = -1;
            Bonus = 0;
            ReleasedLevel = -1;
        }
    }
}
