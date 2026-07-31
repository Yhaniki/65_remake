// Faithful C# port of MinaCalc.h / NoteDataStructures.h (v515): enums, NoteInfo, RowInfo, and the Calc data model.
// Interval-indexed vectors are float[]/int[] grown by ResizeIntervalDependentVectors, exactly like the C++ std::vectors.
using System.Collections.Generic;

namespace Mina
{
    // ---- NoteDataStructures.h ----
    public struct NoteInfo
    {
        public uint notes;      // bitmask, bit0 = leftmost column
        public float rowTime;   // seconds (unrated)
    }

    public enum Skillset
    {
        Skill_Overall, Skill_Stream, Skill_Jumpstream, Skill_Handstream,
        Skill_Stamina, Skill_JackSpeed, Skill_Chordjack, Skill_Technical,
        NUM_Skillset, Skillset_Invalid
    }

    // GenericSkillset shadows: Skill_Chordstream == Skill_Jumpstream, Skill_Bracketing == Skill_Handstream

    public enum CalcPatternMod
    {
        Stream, JS, HS, CJ, CJDensity, HSDensity, CJOHAnchor, OHJumpMod, CJOHJump,
        Balance, Roll, RollJS, OHTrill, VOHTrill, Chaos, FlamJam, WideRangeRoll,
        WideRangeJumptrill, WideRangeJJ, WideRangeBalance, WideRangeAnchor,
        TheThing, TheThing2, RanMan, Minijack, TotalPatternMod,
        GStream, GChordStream, GBracketing,
        NUM_CalcPatternMod, CalcPatternMod_Invalid
    }

    public enum CalcDiffValue
    {
        NPSBase, MSBase, JackBase, CJBase, TechBase, RMABase, MSD,
        NUM_CalcDiffValue, CalcDiffValue_Invalid
    }

    public enum CalcDebugMisc { Pts, PtLoss, StamMod, NUM_CalcDebugMisc, CalcDebugMisc_Invalid }

    public static class Hands
    {
        public const int left_hand = 0;
        public const int right_hand = 1;
        public const int num_hands = 2;
        public static readonly int[] both_hands = { left_hand, right_hand };
    }

    /// Each NoteInfo row precalculated (MinaCalc.h RowInfo). Stored in Calc.adj_ni.
    public struct RowInfo
    {
        public uint row_notes;        // binary representation of notes in the row (bit0 = leftmost)
        public int row_count;         // 1-4: tap, jump, hand, or quad
        public int hand_counts0;      // left-hand note count in this row
        public int hand_counts1;      // right-hand note count in this row
        public float row_time;        // rate-scaled time of this row (seconds)
        public int HandCount(int hand) => hand == Hands.left_hand ? hand_counts0 : hand_counts1;
    }

    /// The 4K pattern-mod orchestrator seam (Bazoinkazoink). Implemented by the ported Ulbu.
    public interface IUlbu
    {
        IReadOnlyList<int>[] GetPmods();     // [skillset] -> list of (int)CalcPatternMod applied to that skillset
        float[] GetBasescalers();            // [skillset]
        void Run();                          // operator()(): fills pmod_vals + init_base_diff_vals into the Calc
        void AdjDiffFunc(int itv, int hand, int ss, float adj_npsbase, float[] pmodProduct);
    }

    /// Main driver class (MinaCalc.h Calc). Holds all interval-indexed working state.
    public sealed partial class Calc
    {
        public IUlbu ulbu_in_charge;
        public const int default_interval_count = 1000;
        public const int max_intervals = 100000;
        public const int max_rows_for_single_interval = 50;

        // config
        public bool debugmode = false;
        public bool ssr = true;
        public bool loadparams = false;
        public uint keycount = 4;
        public uint[] hand_col_masks = { 0u, 0u };
        public List<uint> col_masks = new List<uint>();

        // per interval, up to max_rows_for_single_interval RowInfo (adj_ni)
        public RowInfo[][] adj_ni;
        public int[] itv_size;                              // rows per interval
        public int[][] itv_points;                          // [hand][interval] = notes*2

        // [hand][pmod][interval]
        public float[][][] pmod_vals;
        // [hand][diffvalue][interval]
        public float[][][] init_base_diff_vals;
        // [hand][skillset][interval]
        public float[][][] base_adj_diff;
        public float[][][] base_diff_for_stam_mod;
        public float[] stam_adj_diff;                       // [interval]

        // jack difficulty: (row_time, diff) pairs per hand
        public List<(float first, float second)>[] jack_diff = { new List<(float first, float second)>(), new List<(float first, float second)>() };
        public List<float>[] jack_loss = { new List<float>(), new List<float>() };
        public List<float>[] jack_stam_stuff = { new List<float>(), new List<float>() };

        // per-row scratch for the current interval being scanned
        public float[] tc_static = new float[max_rows_for_single_interval];
        public float[] cj_static = new float[max_rows_for_single_interval];

        public int numitv = 0;
        public float MaxPoints = 0f;
        public float grindscaler = 1f;

        private const int P = (int)CalcPatternMod.NUM_CalcPatternMod;
        private const int D = (int)CalcDiffValue.NUM_CalcDiffValue;
        private const int S = (int)Skillset.NUM_Skillset;

        public Calc()
        {
            // allocate the fixed [hand][x] jagged spines; inner interval vectors grow in resize.
            itv_points = new int[Hands.num_hands][];
            pmod_vals = new float[Hands.num_hands][][];
            init_base_diff_vals = new float[Hands.num_hands][][];
            base_adj_diff = new float[Hands.num_hands][][];
            base_diff_for_stam_mod = new float[Hands.num_hands][][];
            for (int h = 0; h < Hands.num_hands; h++)
            {
                pmod_vals[h] = new float[P][];
                init_base_diff_vals[h] = new float[D][];
                base_adj_diff[h] = new float[S][];
                base_diff_for_stam_mod[h] = new float[S][];
            }
            ResizeIntervalDependentVectors(default_interval_count);
        }

        private static float[] Grow(float[] old, int amt)
        {
            if (old != null && old.Length >= amt) return old;
            var n = new float[amt];
            if (old != null) System.Array.Copy(old, n, old.Length);
            return n;
        }
        private static int[] GrowI(int[] old, int amt)
        {
            if (old != null && old.Length >= amt) return old;
            var n = new int[amt];
            if (old != null) System.Array.Copy(old, n, old.Length);
            return n;
        }

        public void ResizeIntervalDependentVectors(int amt)
        {
            if (adj_ni != null && amt < adj_ni.Length) return;

            var na = new RowInfo[amt][];
            int keep = adj_ni?.Length ?? 0;
            for (int i = 0; i < amt; i++)
                na[i] = (i < keep) ? adj_ni[i] : new RowInfo[max_rows_for_single_interval];
            adj_ni = na;

            itv_size = GrowI(itv_size, amt);
            for (int h = 0; h < Hands.num_hands; h++)
            {
                itv_points[h] = GrowI(itv_points[h], amt);
                for (int p = 0; p < P; p++) pmod_vals[h][p] = Grow(pmod_vals[h][p], amt);
                for (int d = 0; d < D; d++) init_base_diff_vals[h][d] = Grow(init_base_diff_vals[h][d], amt);
                for (int s = 0; s < S; s++) base_adj_diff[h][s] = Grow(base_adj_diff[h][s], amt);
                for (int s = 0; s < S; s++) base_diff_for_stam_mod[h][s] = Grow(base_diff_for_stam_mod[h][s], amt);
            }
            stam_adj_diff = Grow(stam_adj_diff, amt);
        }
    }
}
