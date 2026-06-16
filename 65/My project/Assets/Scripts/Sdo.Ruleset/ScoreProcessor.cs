using System;

namespace Sdo.Ruleset
{
    /// <summary>
    /// SDO score. The ON-SCREEN score is the STAND-ALONE exe's own formula, recovered
    /// from the decompilation (FUN at 0x46b8a0+, 021_gameplay_0046b8a0.c:3684):
    ///
    ///   score = max(0, score + (P*5 + (B + C*2)*2 - M) * 10)   // batched per frame, floored at 0
    ///
    /// i.e. per judgement: Perfect +50, Cool +40, Bad +20, Miss -10, running total never &lt; 0.
    /// There is NO combo multiplier in the stand-alone's displayed score (that lives only in the
    /// online server formula, kept here as <see cref="ServerScore"/>). See
    /// docs/reverse-engineering/SDO_SCORE_FORMULA.md.
    ///
    /// Combo: Perfect &amp; Cool keep it, Bad &amp; Miss break it (exe FUN_00497500 + HP grade map).
    /// </summary>
    public sealed class ScoreProcessor
    {
        public const int ComboMin = 10;
        public const int ComboMax = 68;

        // stand-alone per-judgement score deltas (×10 of the raw 5/(2)/(1)/-1 weights)
        public const long PerfectPoints = 50;
        public const long CoolPoints = 40;
        public const long BadPoints = 20;
        public const long MissPoints = -10;

        public int Combo { get; private set; }
        public int MaxCombo { get; private set; }

        public int PerfectCount { get; private set; }
        public int CoolCount { get; private set; }
        public int BadCount { get; private set; }
        public int MissCount { get; private set; }

        public int TotalJudged => PerfectCount + CoolCount + BadCount + MissCount;

        private long _flatScore;   // stand-alone exe display formula (no combo multiplier)
        private double _comboScore; // per-note combo-multiplied (the live "combo 倍率")

        /// <summary>
        /// Base per-note score before judge/combo multipliers (cLevelScore-like). Tunable.
        /// Documented per-note formula: LevelScore × JudgeMul[j] × (combo+1)/2.
        /// </summary>
        public double LevelScore = 100.0;

        // cGajoong judge multipliers {Perfect, Cool, Bad, Miss} (SDO_SCORE_FORMULA.md §3.1)
        private static double JudgeMul(Judgment j)
        {
            switch (j)
            {
                case Judgment.Perfect: return 2.0;
                case Judgment.Cool: return 1.5;
                case Judgment.Bad: return 1.0;
                default: return 0.0; // Miss
            }
        }

        /// <summary>
        /// On-screen score = the packet-verified SDO formula (combo multiplier via maxCombo,
        /// capped 10..68): Perfects*C + Cools*(C-10). Realistic magnitude (thousands).
        /// </summary>
        public long Score => ServerScore;

        /// <summary>Stand-alone exe's flat display formula (no combo mult): P+50/C+40/B+20/M-10, floored.</summary>
        public long StandaloneScore => _flatScore;

        public double DisplayScore => Score;

        /// <summary>C = clamp(maxCombo, 10, 68) for the online formula.</summary>
        private int ComboValue
        {
            get
            {
                int c = MaxCombo;
                if (c < ComboMin) c = ComboMin;
                if (c > ComboMax) c = ComboMax;
                return c;
            }
        }

        /// <summary>
        /// Online (Dance!Online server) score, kept for the hybrid/online path:
        /// Perfects*C + Cools*(C-10), C = clamp(maxCombo, 10, 68). Packet-verified.
        /// </summary>
        public long ServerScore
        {
            get
            {
                int c = ComboValue;
                return (long)PerfectCount * c + (long)CoolCount * (c - 10);
            }
        }

        private static long Delta(Judgment j)
        {
            switch (j)
            {
                case Judgment.Perfect: return PerfectPoints;
                case Judgment.Cool: return CoolPoints;
                case Judgment.Bad: return BadPoints;
                default: return MissPoints; // Miss
            }
        }

        /// <param name="totalNotes">kept for call-site compatibility.</param>
        public ScoreProcessor(int totalNotes = 0)
        {
            if (totalNotes < 0) throw new ArgumentOutOfRangeException(nameof(totalNotes));
        }

        /// <summary>Apply a single (non-hold) judged event.</summary>
        public void Apply(Judgment j)
        {
            Count(j);

            // combo-multiplied score uses the combo BEFORE this note (doc: (combo+1)/2).
            _comboScore += JudgeMul(j) * (Combo + 1) * 0.5 * LevelScore;

            // stand-alone flat formula (kept for reference): running total, floored at 0.
            _flatScore += Delta(j);
            if (_flatScore < 0) _flatScore = 0;

            // Perfect & Cool keep combo; Bad & Miss break it.
            if (j == Judgment.Perfect || j == Judgment.Cool)
            {
                Combo++;
                if (Combo > MaxCombo) MaxCombo = Combo;
            }
            else
            {
                Combo = 0;
            }
        }

        /// <summary>
        /// Apply a hold's head + tail with head-merge:
        /// head Bad/Miss forces the release slot to Miss and is not judged separately.
        /// </summary>
        public void ApplyHold(Judgment head, Judgment tail)
        {
            Apply(head);
            if (head == Judgment.Bad || head == Judgment.Miss)
                Apply(Judgment.Miss);
            else
                Apply(tail);
        }

        private void Count(Judgment j)
        {
            switch (j)
            {
                case Judgment.Perfect: PerfectCount++; break;
                case Judgment.Cool: CoolCount++; break;
                case Judgment.Bad: BadCount++; break;
                case Judgment.Miss: MissCount++; break;
            }
        }
    }
}
