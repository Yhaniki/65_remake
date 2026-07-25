using System.Collections.Generic;

namespace Sdo.Osu
{
    /// <summary>
    /// Bridges the game's <see cref="OsuBeatmap"/> to the faithfully-ported Etterna MinaCalc v515 (the <c>Mina.*</c>
    /// classes under Mina/). Produces the eight skillset MSD values on the SAME scale Etterna itself uses, so an
    /// external song can be rated by MinaCalc instead of the osu!mania strain rating (<see cref="ManiaStarRating"/>).
    /// 4K only — the highway is hardwired to 4K. Rate 1.0, goal 0.93 (Etterna's default cache goal).
    ///
    /// The port matches the reference C++ calc bit-for-bit on 7 of 8 skillsets (Overall included); Chordjack can differ
    /// by ≤~1% on a few charts due to an implementation-defined <c>std::unordered_map</c> tie-break that can't be
    /// reproduced exactly (documented in Mina/BaseDiff.cs). See docs — verified against a standalone MinaCalc oracle.
    /// </summary>
    public static class ManiaMsd
    {
        /// <summary>The eight MinaCalc skillsets. Overall is the headline number. Valid=false for empty/short charts.</summary>
        public struct Result
        {
            public float Overall, Stream, Jumpstream, Handstream, Stamina, JackSpeed, Chordjack, Technical;
            public bool Valid;
        }

        /// <summary>Full skillset breakdown for a 4K beatmap. Empty Result (all zero, Valid=false) on empty/short charts.</summary>
        public static Result Compute(OsuBeatmap bm, float rate = 1.0f)
        {
            var ni = ToNoteInfo(bm);
            if (ni.Count <= 1) return default;
            var calc = new Mina.Calc();
            var o = Mina.MinaSD.MinaSDCalc(ni, rate, 0.93f, 4u, calc);
            if (o == null || o.Length < 8) return default;
            return new Result
            {
                Overall = o[0], Stream = o[1], Jumpstream = o[2], Handstream = o[3],
                Stamina = o[4], JackSpeed = o[5], Chordjack = o[6], Technical = o[7],
                Valid = o[0] > 0f,
            };
        }

        /// <summary>Just the overall MinaCalc MSD (0 on empty/short charts).</summary>
        public static float Overall(OsuBeatmap bm, float rate = 1.0f) => Compute(bm, rate).Overall;

        // ---- displayed difficulty number (MinaCalc mode) ----
        // The raw MSD (~10..30) is mapped to a shown difficulty by MSD^Exponent × Scale. Tunables — retune here to
        // recalibrate the whole scale. UNCAPPED on purpose (no 1..99 ceiling like the osu level): a very hard chart
        // reads above 99. FLOOR at the raw MSD: for low MSD the curve compresses below the raw value, and we don't
        // want an easy chart to read lower than its own MSD — so the shown value is max(curve, raw MSD).
        public const double LevelExponent = 1.9;
        public const double LevelScale = 0.1;

        /// <summary>Displayed difficulty for a raw overall MSD: round(max(MSD^<see cref="LevelExponent"/> ×
        /// <see cref="LevelScale"/>, MSD)). No upper cap. 0 for a non-computed / zero MSD.</summary>
        public static int ToLevel(float msd)
        {
            if (msd <= 0f) return 0;
            double v = System.Math.Pow(msd, LevelExponent) * LevelScale;
            if (v < msd) v = msd;   // 算出來比原始 MSD 小 → 用原始 MSD
            return (int)System.Math.Round(v, System.MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Group hit objects into rows (one per distinct <see cref="OsuHitObject.StartTimeMs"/>) → one
        /// <see cref="Mina.NoteInfo"/> per row with a column bitmask (bit0 = leftmost lane). Holds contribute only their
        /// head — MinaCalc ignores hold duration. Bombs/mines are excluded (never present in Etterna NoteInfo). Rows are
        /// emitted in ascending time; MinaCalc requires strictly-increasing row times, which distinct int ms guarantees.
        /// </summary>
        public static List<Mina.NoteInfo> ToNoteInfo(OsuBeatmap bm)
        {
            var rows = new SortedDictionary<int, uint>();   // startMs -> column bitmask
            if (bm != null)
            {
                int keys = bm.Keys > 0 ? bm.Keys : 4;
                foreach (var h in bm.HitObjects)
                {
                    if (h.IsBomb) continue;
                    int lane = h.Lane;
                    if (lane < 0) lane = 0;
                    if (lane > keys - 1) lane = keys - 1;
                    int t = h.StartTimeMs;
                    rows.TryGetValue(t, out uint mask);
                    rows[t] = mask | (1u << lane);
                }
            }
            var ni = new List<Mina.NoteInfo>(rows.Count);
            foreach (var kv in rows)
                ni.Add(new Mina.NoteInfo { notes = kv.Value, rowTime = kv.Key / 1000f });
            return ni;
        }
    }
}
