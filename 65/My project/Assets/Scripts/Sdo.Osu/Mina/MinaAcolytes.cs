// Faithful C# port of UlbuAcolytes.h (v515): the PatternMods setter/smoothing helpers, the agnostic/dependent mod
// lists, and fast_walk_and_check_for_skip (turns NoteInfo[] into the per-interval RowInfo grid). Smooth/MSSmooth and
// time_to_itv_idx live in Mina.M.
using System;
using System.Collections.Generic;

namespace Mina
{
    internal static class PatternMods
    {
        // which pmods get smoothed / copied left->right after the agnostic pass
        public static readonly CalcPatternMod[] agnostic_mods = {
            CalcPatternMod.Stream, CalcPatternMod.JS, CalcPatternMod.HS, CalcPatternMod.CJ,
            CalcPatternMod.CJDensity, CalcPatternMod.HSDensity, CalcPatternMod.FlamJam,
            CalcPatternMod.TheThing, CalcPatternMod.TheThing2, CalcPatternMod.GChordStream,
        };

        public static readonly CalcPatternMod[] dependent_mods = {
            CalcPatternMod.OHJumpMod, CalcPatternMod.Balance,
            CalcPatternMod.Roll, CalcPatternMod.RollJS,
            CalcPatternMod.OHTrill, CalcPatternMod.VOHTrill,
            CalcPatternMod.Chaos, CalcPatternMod.WideRangeBalance,
            CalcPatternMod.WideRangeRoll, CalcPatternMod.WideRangeJumptrill,
            CalcPatternMod.WideRangeJJ, CalcPatternMod.WideRangeAnchor,
            CalcPatternMod.RanMan, CalcPatternMod.Minijack,
            CalcPatternMod.CJOHJump, CalcPatternMod.GStream, CalcPatternMod.GBracketing,
        };

        public static void set_agnostic(CalcPatternMod pmod, float val, int pos, Calc calc)
            => calc.pmod_vals[Hands.left_hand][(int)pmod][pos] = val;

        public static void set_dependent(int hand, CalcPatternMod pmod, float val, int pos, Calc calc)
            => calc.pmod_vals[hand][(int)pmod][pos] = val;

        public static void run_agnostic_smoothing_pass(int end_itv, Calc calc)
        {
            foreach (var pmod in agnostic_mods)
                SmoothArr(calc.pmod_vals[Hands.left_hand][(int)pmod], M.neutral, end_itv);
        }

        public static void run_dependent_smoothing_pass(int end_itv, Calc calc)
        {
            foreach (var pmod in dependent_mods)
                for (int h = 0; h < Hands.num_hands; h++)
                    SmoothArr(calc.pmod_vals[h][(int)pmod], M.neutral, end_itv);
        }

        public static void bruh_they_the_same(int end_itv, Calc calc)
        {
            foreach (var pmod in agnostic_mods)
                for (int i = 0; i < end_itv; i++)
                    calc.pmod_vals[Hands.right_hand][(int)pmod][i] =
                        calc.pmod_vals[Hands.left_hand][(int)pmod][i];
        }

        // Smooth() operates on a float[] here (pmod_vals inner vectors are arrays, not List<float>)
        public static void SmoothArr(float[] input, float neutral, int end_interval)
        {
            float f2 = neutral, f3 = neutral;
            for (int i = 0; i < end_interval; ++i)
            {
                float f1 = f2; f2 = f3; f3 = input[i];
                input[i] = (f1 + f2 + f3) / 3f;
            }
        }
    }

    internal static class FastWalk
    {
        /// checks the notedata fits our arrays; if it's some garbage joke file returns true (skip). Otherwise fills
        /// calc.adj_ni / itv_size / hand_col_masks / col_masks / numitv. Faithful to fast_walk_and_check_for_skip.
        public static bool fast_walk_and_check_for_skip(IReadOnlyList<NoteInfo> ni, float rate, Calc calc, float offset = 0f)
        {
            float lastRowTime = ni[ni.Count - 1].rowTime;
            if (float.IsInfinity(lastRowTime) || float.IsNaN(lastRowTime))
                return true;

            calc.numitv = M.time_to_itv_idx(lastRowTime / rate) + 1;

            if (calc.numitv >= calc.itv_size.Length)
            {
                if (calc.numitv >= Calc.max_intervals)
                    return true;
                calc.ResizeIntervalDependentVectors(calc.numitv + 2);
            }

            // successive row times must strictly increase
            for (int i = 1; i < ni.Count; ++i)
                if (ni[i - 1].rowTime >= ni[i].rowTime)
                    return true;

            uint max_keycount_notes = M.keycount_to_bin(calc.keycount);
            uint all_columns_without_middle = max_keycount_notes;
            if (M.ignore_middle_column)
                all_columns_without_middle = M.mask_to_remove_middle_column(calc.keycount);
            uint left_hand_mask = M.left_mask(calc.keycount) & all_columns_without_middle;
            uint right_hand_mask = M.right_mask(calc.keycount) & all_columns_without_middle;

            calc.hand_col_masks = new[] { left_hand_mask, right_hand_mask };
            calc.col_masks.Clear();
            for (uint i = 0; i < calc.keycount; i++)
                calc.col_masks.Add(1u << (int)i);

            int itv = 0, last_itv = 0, row_counter = 0;
            float scaled_time = 0f;
            for (int k = 0; k < ni.Count; k++)
            {
                var i = ni[k];

                if (row_counter >= Calc.max_rows_for_single_interval)
                    return true;

                if (i.notes > max_keycount_notes)   // (notes is uint so the <0 check is vacuous)
                    return true;

                scaled_time = (i.rowTime + offset) / rate;
                itv = M.time_to_itv_idx(scaled_time);

                if (itv > last_itv)
                {
                    if (itv - last_itv > 1)
                        for (int j = last_itv + 1; j < itv; ++j)
                            calc.itv_size[j] = 0;

                    calc.itv_size[last_itv] = row_counter;
                    last_itv = itv;
                    row_counter = 0;
                }

                ref var nri = ref calc.adj_ni[itv][row_counter];
                nri.row_notes = i.notes;
                nri.row_count = M.column_count(i.notes);
                nri.row_time = scaled_time;
                nri.hand_counts0 = M.column_count(i.notes & left_hand_mask);
                nri.hand_counts1 = M.column_count(i.notes & right_hand_mask);

                ++row_counter;
            }

            if (itv - last_itv > 1)
                for (int j = last_itv + 1; j < itv; ++j)
                    calc.itv_size[j] = 0;

            calc.itv_size[itv] = row_counter;
            calc.numitv = itv + 1;
            return false;
        }
    }
}
