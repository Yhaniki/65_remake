using System;

namespace Sdo.Ruleset
{
    /// <summary>
    /// SDO score. The ON-SCREEN score (<see cref="Score"/>) is the packet-verified online formula
    /// <see cref="ServerScore"/>: Perfects*C + Cools*(C-10), C = clamp(maxCombo, 10, 68).
    ///
    /// The stand-alone exe's own display formula is kept alongside as <see cref="StandaloneScore"/>,
    /// recovered from the decompilation (FUN at 0x46b8a0+, 021_gameplay_0046b8a0.c:3684):
    ///
    ///   score = max(0, score + (P*5 + (B + C*2)*2 - M) * 10)   // batched per frame, floored at 0
    ///
    /// i.e. per judgement: Perfect +50, Cool +40, Bad +20, Miss -10, running total never &lt; 0 — no
    /// combo multiplier. See docs/reverse-engineering/SDO_SCORE_FORMULA.md.
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
        /// 斷 combo，但**不**計入任何判定統計(MissCount 不加、flat score 不動)。
        /// 用於踩到炸彈:炸彈不是真的音符,只該斷連(HP 由 <see cref="HealthProcessor"/> 另外扣),
        /// 不能灌進 miss 數 —— 見 ScreenGameplay.ExplodeBomb。MaxCombo 已在前面連段時記錄,這裡不受影響。
        /// </summary>
        public void BreakCombo() => Combo = 0;

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
