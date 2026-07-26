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

        // 完奏模式 HP 歸零後的分數凍結（見 FreezeScore）。ServerScore 是由 MaxCombo 推導出來的，
        // 所以「不再加分」必須是快照——只停止累加沒有用（之後的 combo 會把舊 perfect 的倍率一起拉高）。
        private bool _frozen;
        private long _frozenServer, _frozenFlat;

        /// <summary>
        /// On-screen score = the packet-verified SDO formula (combo multiplier via maxCombo,
        /// capped 10..68): Perfects*C + Cools*(C-10). Realistic magnitude (thousands).
        /// </summary>
        public long Score => ServerScore;

        /// <summary>Stand-alone exe's flat display formula (no combo mult): P+50/C+40/B+20/M-10, floored.</summary>
        public long StandaloneScore => _frozen ? _frozenFlat : _flatScore;

        /// <summary>分數已凍結（完奏模式在 HP 歸零那一刻鎖住分數，判定統計繼續累計）。</summary>
        public bool ScoreFrozen => _frozen;

        /// <summary>
        /// 完奏模式 (ScreenGameplay.playFullSong) HP 歸零：分數就地凍結。
        /// 之後的判定照樣進統計（PerfectCount/CoolCount/…、Combo/MaxCombo 都繼續動，判定字樣/特效也照舊），
        /// 只有 <see cref="Score"/> / <see cref="StandaloneScore"/> 不再變動 —— 血用完後打得再好也不加分。
        /// 冪等：重複呼叫不會二次覆蓋快照。
        /// </summary>
        public void FreezeScore()
        {
            if (_frozen) return;
            _frozenServer = RawServerScore;
            _frozenFlat = _flatScore;
            _frozen = true;
        }

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
        public long ServerScore => _frozen ? _frozenServer : RawServerScore;

        /// <summary>The live formula, before the 完奏模式 death freeze (<see cref="FreezeScore"/>) is applied.</summary>
        private long RawServerScore
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
