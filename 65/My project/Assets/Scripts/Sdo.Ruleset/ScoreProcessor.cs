using System;

namespace Sdo.Ruleset
{
    /// <summary>
    /// SDO score. The ON-SCREEN score (<see cref="Score"/>) is reproduced from official result
    /// screenshots (4 games / 18 rows, see docs/reverse-engineering/SDO_SCORE_FORMULA.md §0.1):
    ///
    ///   hits = Perfect + Cool
    ///   score = floor(1.04 × (68×maxCombo + 48.4×(hits − maxCombo) − 10×Cool))
    ///
    /// i.e. the notes inside the single longest unbroken run are worth the full combo multiplier
    /// (68); every note outside it is worth a fixed ~48.4 no matter how long its own run was; a Cool
    /// is worth 10 less than a Perfect either way; Bad/Miss score nothing directly and only hurt by
    /// shortening the longest run.
    ///
    /// This REPLACES the old <see cref="ServerScore"/> (Perfects*C + Cools*(C-10), C =
    /// clamp(maxCombo, 10, 68)). That one is the per-STATS-PACKET formula — the captured packets in
    /// Arrowgene's GameStatsData show its judgement counts are per-segment deltas while points
    /// accumulate — so applying it to a whole song made the combo multiplier saturate at 68 and lose
    /// all resolution above it (a 1253-combo run scored below an 892-combo one). It is kept below
    /// because it is still exactly right for a single packet.
    ///
    /// The stand-alone exe's own display formula is kept alongside as <see cref="StandaloneScore"/>,
    /// recovered from the decompilation (FUN at 0x46b8a0+, 021_gameplay_0046b8a0.c:3684):
    ///
    ///   score = max(0, score + (P*5 + (B + C*2)*2 - M) * 10)   // batched per frame, floored at 0
    ///
    /// i.e. per judgement: Perfect +50, Cool +40, Bad +20, Miss -10, running total never &lt; 0 — no
    /// combo multiplier. (That is the OFFLINE exe; the screenshots above are the online client.)
    ///
    /// Combo: Perfect &amp; Cool keep it, Bad &amp; Miss break it (exe FUN_00497500 + HP grade map).
    /// </summary>
    public sealed class ScoreProcessor
    {
        public const int ComboMin = 10;
        public const int ComboMax = 68;

        // ---- on-screen formula constants, as exact integer ratios so floor() has no float edges ----
        // 顯示分要再乘 1.04 = 26/25。三首不同歌的全連玩家(87014 / 109925 / 133355)都精確到個位數。
        private const long DisplayNum = 26, DisplayDen = 25;
        // 不在最長連段裡的音符,等效 combo 倍率固定 48.4 = 242/5(與該段自己多長無關 —— 官方 15 列
        // 解出來是 40.8~50.5,而各列的平均段長差了 20 倍)。整份公式用 5 倍整數算,見 RawDisplayScore5。
        private const long BrokenNum = 242, BrokenDen = 5;

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

        // HP 歸零後的分數凍結（見 FreezeScore）。顯示分是由 MaxCombo 推導出來的，
        // 所以「不再加分」必須是快照——只停止累加沒有用（之後的 combo 會把舊 perfect 的倍率一起拉高）。
        private bool _frozen;
        private long _frozenDisplay, _frozenFlat;

        /// <summary>
        /// On-screen score, reproduced from the official result screenshots:
        /// floor(1.04 × (68×maxCombo + 48.4×(hits − maxCombo) − 10×Cool)), hits = Perfect + Cool.
        /// </summary>
        public long Score => _frozen ? _frozenDisplay : RawDisplayScore;

        /// <summary>The live on-screen formula, before the 完奏模式 death freeze (<see cref="FreezeScore"/>).</summary>
        private long RawDisplayScore => DisplayNum * RawDisplayScore5 / (DisplayDen * BrokenDen);

        /// <summary>
        /// The combo-weighted base, ×5 so that the 48.4 stays integral. Never negative: every note
        /// is worth at least ComboMin(10) ×5 = 50, and a Cool only gives back 10 ×5 = 50 of that.
        /// </summary>
        private long RawDisplayScore5
        {
            get
            {
                long hits = (long)PerfectCount + CoolCount;
                if (hits <= 0) return 0;

                // 最長連段裡的音符吃滿倍率；連段本身還沒長到上限時，倍率就是連段長度（夾在 10..68）。
                long inRun = MaxCombo; if (inRun > hits) inRun = hits;
                long full5 = (long)ComboValue * BrokenDen;
                // 斷過連的音符吃固定的 48.4 —— 但連最長段都還沒到 48.4 時，它不可能比最長段更值錢。
                long broken5 = BrokenNum < full5 ? BrokenNum : full5;

                return full5 * inRun + broken5 * (hits - inRun) - 10 * BrokenDen * CoolCount;
            }
        }

        /// <summary>Stand-alone exe's flat display formula (no combo mult): P+50/C+40/B+20/M-10, floored.</summary>
        public long StandaloneScore => _frozen ? _frozenFlat : _flatScore;

        /// <summary>分數已凍結（HP 歸零那一刻鎖住分數，判定統計繼續累計）。</summary>
        public bool ScoreFrozen => _frozen;

        /// <summary>
        /// HP 歸零：分數就地凍結（血空之後還可能繼續打 —— 完奏模式打到曲末，一般模式在連線時要打到
        /// 房裡所有人都倒下，見 <see cref="GameOverGate.EliminatedNow"/>）。
        /// 之後的判定照樣進統計（PerfectCount/CoolCount/…、Combo/MaxCombo 都繼續動，判定字樣/特效也照舊），
        /// 只有 <see cref="Score"/> / <see cref="StandaloneScore"/> 不再變動 —— 血用完後打得再好也不加分。
        /// 冪等：重複呼叫不會二次覆蓋快照。
        /// </summary>
        public void FreezeScore()
        {
            if (_frozen) return;
            _frozenDisplay = RawDisplayScore;
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
        /// The per-STATS-PACKET formula: Perfects*C + Cools*(C-10), C = clamp(maxCombo, 10, 68).
        /// Packet-verified against Arrowgene's captures (79P/3C = 5546 = 79*68 + 3*58), but only for
        /// ONE packet — the counts in a packet are that segment's deltas. Do not use it for a whole
        /// song; that is what <see cref="Score"/> is for.
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
