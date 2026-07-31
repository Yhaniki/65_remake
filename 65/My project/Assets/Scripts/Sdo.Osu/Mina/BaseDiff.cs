// Faithful C# port of SequencedBaseDiffCalc.h (v515): the row-by-row constructed MS/NPS/jack/chordjack/tech difficulty
// bases (structs nps, ceejay, techyo, jack_col, oversimplified_jacks, diffz) plus CalcMSEstimate and module constants.
// C++ operator() becomes op(...). Debug-only writes (debugTechVals) are skipped. Param default field initializers are
// kept verbatim (the XML load/save machinery and _params vectors are dropped).
using System;
using System.Collections.Generic;
using static Mina.CalcDiffValue;
using static Mina.GenSeq;
using static Mina.col_type;

namespace Mina
{
    /// SequencedBaseDiffCalc.h module-scope constants + the free CalcMSEstimate helper.
    public static class SBD
    {
        /// required percentage of average notes to pass
        public const float min_threshold = 0.65f;
        public static readonly float downscale_logbase = (float)Math.Log(6.2f);

        public const float scaler_for_ms_base = 1.175f;
        // i do not know a proper name for these
        public const float ms_base_finger_weighter_2 = 9.0f;
        public const float ms_base_finger_weighter = 5.5f;

        public static float CalcMSEstimate(List<float> input, int burp)
        {
            // how many ms values we use; if fewer we mock some up, if more we truncate
            uint num_used = (uint)burp;

            if (input.Count == 0)
                return 0.0f;

            // sort before truncating/filling
            input.Sort();

            const float ms_dummy = 360.0f;

            // mostly try to push down stuff like jumpjacks
            float cv_yo = M.cv_trunc_fill(input, burp, ms_dummy) + 0.5f;
            cv_yo = Math.Clamp(cv_yo, 0.5f, 1.25f);

            // basically doing a jank average, bigger m = lower difficulty
            float m = M.sum_trunc_fill(input, burp, ms_dummy);

            // add 1 to num_used because some meme about sampling
            float bpm_est = M.ms_to_bpm(m / (num_used + 1));
            float nps_est = bpm_est / 15.0f;
            float fdiff = nps_est * cv_yo;
            return fdiff;
        }
    }

    public sealed class nps
    {
        /// Per-finger data for msbase
        private sealed class FingerGaps
        {
            public List<float> ms = new List<float>();
            public float last_row_time = M.s_init;
        }

        /// determine NPSBase, itv_points, CJBase for this hand
        public static void actual_cancer(Calc calc, int hand)
        {
            float scaly_ms_estimate(List<float> input, float scaler)
            {
                float o = SBD.CalcMSEstimate(input, 3);
                if (input.Count > 3)
                    o = Math.Max(o, SBD.CalcMSEstimate(input, 4) * scaler);
                if (input.Count > 4)
                    o = Math.Max(o, SBD.CalcMSEstimate(input, 5) * scaler * scaler);
                return o;
            }

            var fingy = new List<FingerGaps>(calc.col_masks.Count);
            for (int i = 0; i < calc.col_masks.Count; i++)
                fingy.Add(new FingerGaps());
            var estimates = new List<float>();

            for (int itv = 0; itv < calc.numitv; ++itv)
            {
                int notes = 0;

                foreach (var fing in fingy)
                    fing.ms.Clear();

                // note time deltas per finger in this interval
                for (int row = 0; row < calc.itv_size[itv]; ++row)
                {
                    RowInfo cur = calc.adj_ni[itv][row];
                    uint cur_notes = cur.row_notes & calc.hand_col_masks[hand];
                    notes += cur.HandCount(hand);

                    float crt = cur.row_time;
                    for (int col_index = 0; col_index < calc.col_masks.Count; ++col_index)
                    {
                        if ((cur_notes & calc.col_masks[col_index]) != 0)
                        {
                            var fing = fingy[col_index];
                            if (fing.last_row_time != M.s_init)
                            {
                                float ms = M.ms_from(crt, fing.last_row_time);
                                fing.ms.Add(ms);
                            }
                            fing.last_row_time = crt;
                        }
                    }
                }

                // per finger deltas to ms estimates. for 4k estimates.size() == 2
                estimates.Clear();
                foreach (var fing in fingy)
                {
                    if (fing.last_row_time != M.s_init)
                    {
                        float ms_est = scaly_ms_estimate(fing.ms, SBD.scaler_for_ms_base);
                        estimates.Add(ms_est);
                    }
                }

                // sort largest first and zero pad to hand note count
                estimates.Sort((a, b) => b.CompareTo(a));
                int target = M.column_count(calc.hand_col_masks[hand]);
                while (estimates.Count < target) estimates.Add(0f);
                if (estimates.Count > target) estimates.RemoveRange(target, estimates.Count - target);

                // average fingy estimates for msbase (telescope)
                float msdiff = estimates[0];
                for (int i = 1; i < estimates.Count; ++i)
                    msdiff = M.weighted_average(msdiff, estimates[i], SBD.ms_base_finger_weighter, SBD.ms_base_finger_weighter_2);

                // nps for this interval
                float nps = (float)notes * M.finalscaler * 1.6f;
                calc.init_base_diff_vals[hand][(int)NPSBase][itv] = nps;

                // ms base for this interval
                float msbase = M.finalscaler * msdiff;
                calc.init_base_diff_vals[hand][(int)MSBase][itv] = msbase;

                // set points for this interval
                calc.itv_points[hand][itv] = notes * 2;
            }
        }

        /// determine grindscaler using smoothed npsbase
        public static void grindscale(Calc calc)
        {
            int populated_intervals = 0;
            float avg_notes = 0.0f;
            for (int itv = 0; itv < calc.numitv; ++itv)
            {
                float notes = 0.0f;

                foreach (var hand in Hands.both_hands)
                    notes += calc.init_base_diff_vals[hand][(int)NPSBase][itv];

                if (notes > 0)
                {
                    avg_notes += notes;
                    populated_intervals++;
                }
            }

            if (populated_intervals > 0)
            {
                avg_notes /= populated_intervals;

                int failed_intervals = 0;

                for (int itv = 0; itv < calc.numitv; itv++)
                {
                    float notes = 0.0f;
                    foreach (var hand in Hands.both_hands)
                        notes += calc.init_base_diff_vals[hand][(int)NPSBase][itv];

                    // count only intervals with notes
                    if (notes > 0.0f && notes < avg_notes * SBD.min_threshold)
                        failed_intervals++;
                }

                // base grindscaler on how many intervals are passing
                float file_length = M.itv_idx_to_time(populated_intervals - failed_intervals);
                // log minimum but if you move this you need to move the log base
                const float ping = 0.3f;
                float timescaler =
                  (ping * ((float)Math.Log(file_length + 1) / SBD.downscale_logbase)) + ping;

                calc.grindscaler = Math.Clamp(timescaler, 0.1f, 1.0f);
            }
            else
            {
                calc.grindscaler = 0.1f;
            }
        }
    }

    public sealed class ceejay
    {
        public readonly string name = "CJ_Static";

        // ---- params (default field initializers kept verbatim) ----
        public float static_ms_weight = 0.65f;
        public float min_ms = 75.0f;

        public float base_tap_scaler = 1.2f;
        public float huge_anchor_scaler = 1.15f;
        public float small_anchor_scaler = 1.15f;
        public float ccj_scaler = 1.25f;
        public float cct_scaler = 1.5f;
        public float ccn_scaler = 1.15f;
        public float ccb_scaler = 1.25f;

        public int mediterranean = 10;

        public void update_flags(uint row_notes, int row_count)
        {
            is_cj = last_row_count > 1 && row_count > 1;
            was_cj = last_row_count > 1 && last_last_row_count > 1;

            is_scj = (row_count == 1 && last_row_count > 1) &&
                     ((row_notes & last_row_notes) != 0u);

            is_actually_continuing_jack = ((row_notes & last_row_notes) != 0u);

            carpathian_basin_capsized_boat_chord_bonk.Add(row_notes == last_row_notes ? 1 : 0);

            if (carpathian_basin_capsized_boat_chord_bonk.Count > mediterranean)
                carpathian_basin_capsized_boat_chord_bonk.RemoveAt(0);

            is_at_least_3_note_anch =
              ((row_notes & last_row_notes) & last_last_row_notes) != 0u;

            last_last_row_count = last_row_count;
            last_row_count = row_count;

            last_last_row_notes = last_row_notes;
            last_row_notes = row_notes;

            last_was_3_note_anch = is_at_least_3_note_anch;

            if (is_actually_continuing_jack)
                chain++;
            else
                chain = 1;
        }

        public void advance_base(float any_ms, Calc calc)
        {
            if (row_counter >= Calc.max_rows_for_single_interval)
                return;

            // pushing back ms values, so multiply to nerf
            float pewpew = base_tap_scaler;

            if (chain < 3)
                pewpew *= 1.1f; // stupid and gay
            if (chain == 3)
                pewpew /= 1.1f; // yes im really doing this

            int laguardiaairport = 0;
            for (int i = 0; i < carpathian_basin_capsized_boat_chord_bonk.Count; i++)
                laguardiaairport += carpathian_basin_capsized_boat_chord_bonk[i];

            // C++ std::pow(1.025F, int) promotes to double; multiply-into-float rounds once
            pewpew = (float)((double)pewpew * Math.Pow((double)1.025f, (double)laguardiaairport));

            float ms = Math.Max(min_ms, any_ms * pewpew);
            calc.cj_static[row_counter] = ms;
            ++row_counter;
        }

        // final output difficulty for this interval
        public float get_itv_diff(Calc calc)
        {
            if (row_counter == 0)
                return 0.0f;

            // ms vals to counts
            var mode = new Dictionary<int, int>();
            var static_ms = new List<float>();
            for (int i = 0; i < row_counter; ++i)
            {
                int v = (int)calc.cj_static[i];
                static_ms.Add(calc.cj_static[i]);
                if (mode.ContainsKey(v)) mode[v]++; else mode[v] = 1; // mode[v]++
            }
            int modev = 0;
            int modefreq = 0;
            // C++ keeps the FIRST key with the max frequency (strict >) while iterating a std::unordered_map, whose
            // order is implementation-defined. We iterate Dictionary insertion order — closest to MSVC's oracle of the
            // orders tested (asc/desc overshoot/undershoot more). When several ms values tie on frequency this leaves a
            // <=1.1% gap on Chordjack ONLY vs the MSVC oracle; it never affects Overall unless a chart is CJ-dominant.
            foreach (var it in mode)
            {
                if (it.Value > modefreq)
                {
                    modev = it.Key;
                    modefreq = it.Value;
                }
            }
            for (int i = 0; i < static_ms.Count; i++)
            {
                // weight = 0 means all values become modev
                static_ms[i] = M.weighted_average(static_ms[i],
                                                  (float)modev,
                                                  static_ms_weight,
                                                  1.0f);
            }

            float ms_total = M.sum(static_ms);
            float ms_mean = ms_total / (float)row_counter;
            return M.ms_to_scaled_nps(ms_mean);
        }

        public void interval_end()
        {
            row_counter = 0;
        }

        public void full_reset()
        {
            is_cj = false;
            was_cj = false;
            is_scj = false;
            is_at_least_3_note_anch = false;
            last_was_3_note_anch = false;

            is_actually_continuing_jack = false;

            last_row_count = 0;
            last_last_row_count = 0;

            last_row_notes = 0U;
            last_last_row_notes = 0U;

            chain = 1;

            carpathian_basin_capsized_boat_chord_bonk.Clear();
        }

        // ---- private ----
        private int row_counter = 0;

        private bool is_cj = false;
        private bool was_cj = false;
        private bool is_scj = false;
        private bool is_at_least_3_note_anch = false;
        private bool last_was_3_note_anch = false;

        private bool is_actually_continuing_jack = false;

        private int last_row_count = 0;
        private int last_last_row_count = 0;

        private uint last_row_notes = 0U;
        private uint last_last_row_notes = 0U;

        private int chain = 1;

        private List<int> carpathian_basin_capsized_boat_chord_bonk = new List<int>();
    }

    /// if this looks ridiculous, that's because it is
    public sealed class techyo
    {
        public readonly string name = "TC_Static";

        // ---- params (default field initializers kept verbatim) ----
        public float tc_base_weight = 4.0f;
        public float nps_base_weight = 9.0f;
        public float rm_base_weight = 1.0f;

        public float balance_comp_window = 36.0f;
        public float chaos_comp_window = 4.0f;
        public float tc_static_base_window = 2.0f;

        // determines steepness of non-1/2 balance ratios
        public float balance_power = 2.0f;
        public float min_balance_ratio = 0.2f;
        public float balance_ratio_scaler = 1.0f;

        // moving window of the last 3 {column, time} that showed up
        public const int trill_window = 3;
        public (col_type first, float second)[] mw_dt = new (col_type, float)[trill_window];

        public void advance_base(SequencerGeneral seq,
                                 col_type ct,
                                 Calc calc,
                                 int hand,
                                 float row_time)
        {
            if (row_counter >= max_rows_for_single_interval)
                return;

            // process_mw_dt(ct, seq.get_any_ms_now());
            // advance_trill_base(calc);
            // increment_column_counters(ct);
            // auto balance_comp = Math.Max(calc_balance_comp() * balance_ratio_scaler, min_balance_ratio);
            float chaos_comp = calc_chaos_comp(seq, ct, calc, hand, row_time);
            // insert(balance_ratios, balance_comp);
            teehee.push(chaos_comp);
            calc.tc_static[row_counter] =
              teehee.get_mean_of_window((int)tc_static_base_window);
            ++row_counter;
        }

        public void advance_rm_comp(float rm_diff)
        {
            rm_itv_max_diff = Math.Max(rm_itv_max_diff, rm_diff);
        }

        public void advance_jack_comp(float hardest_itv_jack_ms)
        {
            const float jack_base_scale = 1.01f;
            jack_itv_diff = M.ms_to_scaled_nps(hardest_itv_jack_ms) * jack_base_scale;
        }

        // for debug
        public float get_itv_rma_diff()
        {
            return rm_itv_max_diff;
        }

        // not for debug
        public float get_itv_jack_diff()
        {
            return jack_itv_diff;
        }

        // final output difficulty for this interval (officially TechBase for an interval)
        public float get_itv_diff(float nps_base, Calc calc)
        {
            float rmbase = rm_itv_max_diff;
            float nps_biased_chaos_base = M.weighted_average(
              get_tc_base(calc), nps_base, tc_base_weight, nps_base_weight);
            if (rmbase >= nps_biased_chaos_base)
            {
                // for rm dominant intervals, use tc to drag diff down (weight [0,1]; 1 -> all rm, 0 -> all tc)
                rmbase = M.weighted_average(
                  rmbase, nps_biased_chaos_base, rm_base_weight, 1.0f);
            }
            return Math.Max(nps_biased_chaos_base, rmbase);

            /*
            const auto trillbase = get_tb_base(calc);
            const auto jackbase = jack_itv_diff;
            const auto how_flammy = get_itv_flam_factor();
            const auto combinedbase =
              weighted_average(trillbase, jackbase, how_flammy, 1.F);
            return std::max(rmbase, combinedbase);
            */
        }

        public void interval_end()
        {
            row_counter = 0;
            rm_itv_max_diff = 0.0f;
            jack_itv_diff = 0.0f;
            for (int i = 0; i < balance_ratios.Length; i++) balance_ratios[i] = 0;
            //count_left.fill(0);
            //count_right.fill(0);
        }

        public void full_reset()
        {
            row_counter = 0;
            rm_itv_max_diff = 0.0f;
            jack_itv_diff = 0.0f;
            teehee.zero();
            for (int i = 0; i < count_left.Length; i++) count_left[i] = 0;
            for (int i = 0; i < count_right.Length; i++) count_right[i] = 0;

            for (int i = 0; i < mw_dt.Length; i++) mw_dt[i] = (col_empty, M.ms_init);
            for (int i = 0; i < tb_static.Length; i++) tb_static[i] = M.ms_init;
            for (int i = 0; i < flammity.Length; i++) flammity[i] = 0.0f;
        }

        // ---- private ----
        private const int max_rows_for_single_interval = Calc.max_rows_for_single_interval;

        // how many non-empty rows have we seen
        private int row_counter = 0;

        // jank stuff.. keep a small moving average of the base diff
        private CalcMovingWindow<float> teehee = new CalcMovingWindow<float>();

        // 1 or 0 continuously
        private int[] count_left = new int[max_rows_for_single_interval];
        private int[] count_right = new int[max_rows_for_single_interval];

        // balance ratio values for an interval
        private float[] balance_ratios = new float[max_rows_for_single_interval];

        // trill base values
        private float[] tb_static = new float[max_rows_for_single_interval];
        private float[] flammity = new float[max_rows_for_single_interval];

        // max value of rm diff for this interval (stored pre-converted, not as ms)
        private float rm_itv_max_diff = 0.0f;
        private float jack_itv_diff = 0.0f;

        // get the interval base diff, merged via weighted average with npsbase, then compared to max_rm diff
        private float get_tc_base(Calc calc)
        {
            if (row_counter == 0)
                return 0.0f;

            float ms_total = 0.0f;
            for (int i = 0; i < row_counter; ++i)
                ms_total += calc.tc_static[i];

            float ms_mean = ms_total / (float)row_counter;
            return M.ms_to_scaled_nps(ms_mean);
        }

        // produces the average value of the trill ms value for the interval converted to nps
        private float get_tb_base(Calc calc)
        {
            if (row_counter < 3)
                return 0.0f;

            float ms_total = 0.0f;
            for (int i = 0; i < row_counter; ++i)
                ms_total += tb_static[i];
            float ms_mean = ms_total / (float)row_counter;
            return M.ms_to_scaled_nps(ms_mean);
        }

        // produces the average value of the flam value for the interval [0,1]
        private float get_itv_flam_factor()
        {
            if (row_counter == 0)
                return 0.0f;

            float total = 0.0f;
            for (int i = 0; i < row_counter; ++i)
                total += flammity[i];

            float mean = total / (float)row_counter;
            return mean;
        }

        /// find the balance between columns in a hand, returns [0,1]
        private float calc_balance_comp()
        {
            float left = get_total_for_windowf(count_left, (int)balance_comp_window);
            float right = get_total_for_windowf(count_right, (int)balance_comp_window);

            // dont care about half empty hands or fully balanced hands
            if (left == 0.0f || right == 0.0f)
                return 1.0f;
            if (left == right)
                return 1.0f;

            // make sure power is at least 2 and divisible by 2, but not greater than 30 (int overflow)
            float bal_power =
              Math.Clamp((int)Math.Max(balance_power, 2.0f) % 2 == 0
                           ? balance_power
                           : balance_power - 1.0f,
                         2.0f,
                         30.0f);
            float bal_factor = (float)Math.Pow(2.0f, bal_power);

            float high = left > right ? left : right;
            float low = left > right ? right : left;

            float x = low / high;
            float y = (-bal_factor * (float)Math.Pow(x - 0.5f, bal_power)) + 1;
            return y;
        }

        /// in some wildly confusing fashion, find a degree of chaoticness but using MS->NPS
        private float calc_chaos_comp(SequencerGeneral seq,
                                      col_type ct,
                                      Calc calc, int hand, float row_time)
        {
            float a = seq.get_sc_ms_now(ct);
            float b;
            if (ct == col_ohjump)
                b = seq.get_sc_ms_now(ct, false);
            else
                b = seq.get_cc_ms_now();

            // arithmetic mean of ms times since (last note in this column) and (last note in the other column)
            float c = (a + b) / 2;

            // coeff var. of last N ms times on either / left / right column
            float pineapple = seq._mw_any_ms.get_cv_of_window((int)chaos_comp_window);
            float porcupine = seq._mw_sc_ms[(int)col_left].get_cv_of_window((int)chaos_comp_window);
            float sequins = seq._mw_sc_ms[(int)col_right].get_cv_of_window((int)chaos_comp_window);

            // clamp to [0.5, 1.5]
            float oioi = 0.5f;
            float ioio = 0.5f;
            pineapple = Math.Clamp(pineapple + oioi, oioi, ioio + oioi);
            porcupine = Math.Clamp(porcupine + oioi, oioi, ioio + oioi);
            sequins = Math.Clamp(sequins + oioi, oioi, ioio + oioi);

            // most recent ms time in left / right column
            float scoliosis = seq._mw_sc_ms[(int)col_left].get_now();
            float poliosis = seq._mw_sc_ms[(int)col_right].get_now();

            float obliosis;
            if (ct == col_left)
                obliosis = poliosis / scoliosis;
            else
                obliosis = scoliosis / poliosis;

            // ratio of most recent ms times between columns must be [1,10]
            obliosis = Math.Clamp(obliosis, 1.0f, 10.0f);

            // sqrt of ( [1,inf] - 1 ) (fastsqrt IS NOT ACCURATE)
            float pewp = M.fastsqrt(M.div_high_by_low(scoliosis, poliosis) - 1.0f);
            // [0,inf] divided by [1,10]
            pewp /= obliosis;

            // (calc.debugmode debugTechVals write skipped)

            // average of (cv left, cv right, cv both), clamped [0.5,1.5]
            float vertebrae = Math.Clamp(
              ((pineapple + porcupine + sequins) / 3.0f), oioi, ioio + oioi);

            // result is ms divided by fudgy cv number
            return c / vertebrae;
        }

        private void advance_trill_base(Calc calc)
        {
            float flamentation = 0.5f;
            float trill_ms_value = M.ms_init;

            var a = mw_dt[0];
            if (a.first == col_init)
            {
                // do nothing special, not a complete trill
            }
            else
            {
                var b = mw_dt[1];
                var c = mw_dt[2];

                float flam_of_the_trill = M.ms_init;
                float third_tap = M.ms_init;

                if (b.second == 0.0f && a.first != b.first)
                {
                    // first 2 notes form a jump
                    flam_of_the_trill = 0.0f;
                    third_tap = c.second * 2.0f;
                }
                else if (c.second == 0.0f && b.first != c.first)
                {
                    // last 2 notes form a jump
                    flam_of_the_trill = 0.0f;
                    third_tap = b.second * 2.0f;
                }
                else
                {
                    // determine ... trillflamjackyness
                    if (a.first == c.first && a.first != b.first)
                    {
                        // it's a trill (121 or 212)
                        flam_of_the_trill = Math.Min(b.second, c.second);
                        third_tap = Math.Max(b.second, c.second);
                    }
                    else if (a.first == c.first)
                    {
                        // it's 111, treat it like a flam (thus like a jack)
                        flam_of_the_trill = 0.0f;
                        third_tap = c.second;
                    }
                    else
                    {
                        if (a.first != b.first)
                        {
                            // it's 211
                            flam_of_the_trill = b.second;
                            third_tap = c.second;
                        }
                        else
                        {
                            // it's 112
                            flam_of_the_trill = c.second;
                            third_tap = b.second;
                        }
                    }
                }

                if (flam_of_the_trill == 0.0f && third_tap == 0.0f)
                {
                    // effectively a forced dropped note
                    flamentation = 0.0f;
                    trill_ms_value = 0.1f;
                }
                else
                {
                    // ratio = [0,1]; 0 = flam involved, 1 = straight trill
                    const float flam_ms_depressor = 0.0f;
                    float ratio = M.div_low_by_high(
                      Math.Max(flam_of_the_trill - flam_ms_depressor, 0.0f),
                      third_tap);
                    flamentation = Math.Clamp((float)Math.Pow(ratio, 0.125f), 0.0f, 1.0f);
                    trill_ms_value = (flam_of_the_trill + third_tap) / 2.0f;
                }
            }

            flammity[row_counter] = flamentation;
            tb_static[row_counter] = trill_ms_value;
        }

        //////////////////////////////////////////////////////////////
        // util
        private void increment_column_counters(col_type ct)
        {
            switch (ct)
            {
                case col_left:
                    insert(count_left, 1);
                    insert(count_right, 0);
                    break;
                case col_right:
                    insert(count_left, 0);
                    insert(count_right, 1);
                    break;
                case col_ohjump:
                    insert(count_left, 1);
                    insert(count_right, 1);
                    break;
                default:
                    insert(count_left, 0);
                    insert(count_right, 0);
                    break;
            }
        }

        private float get_total_for_windowf(int[] arr, int window)
        {
            float o = 0.0f;
            int i = max_rows_for_single_interval;
            while (i > max_rows_for_single_interval - window)
            {
                i--;
                o += arr[i];
            }
            return o;
        }

        private void insert<T>(T[] arr, T value)
        {
            for (int i = 1; i < max_rows_for_single_interval; i++)
                arr[i - 1] = arr[i];
            arr[max_rows_for_single_interval - 1] = value;
        }

        private void process_mw_dt(col_type ct, float ms_now)
        {
            var arr = mw_dt;
            if (ct == col_ohjump)
            {
                for (int i = 1; i < trill_window; i++)
                    arr[i - 1] = arr[i];
                arr[trill_window - 1] = (col_left, ms_now);

                for (int i = 1; i < trill_window; i++)
                    arr[i - 1] = arr[i];
                arr[trill_window - 1] = (col_right, 0.0f);
            }
            else
            {
                for (int i = 1; i < trill_window; i++)
                    arr[i - 1] = arr[i];
                arr[trill_window - 1] = (ct, ms_now);
            }
        }
    }

    public sealed class jack_col
    {
        public int len = 1;
        public float last_ms = M.ms_init;
        public float max_ms = M.ms_init;
        public float len_capped_ms = M.ms_init;
        public float last_note_sec = M.s_init;
        public float start_note_sec = M.s_init;

        public void reset()
        {
            len = 1;
            last_ms = M.ms_init;
            max_ms = M.ms_init;
            len_capped_ms = M.ms_init;
            last_note_sec = M.s_init;
            start_note_sec = M.s_init;
        }

        public void check_reset(float now)
        {
            float since_last = M.ms_from(now, last_note_sec);
            if (since_last > guaranteed_reset_buffer_ms)
                reset();
        }

        /// C++ operator()(now)
        public void op(float now)
        {
            last_ms = M.ms_from(now, last_note_sec);
            if (last_ms > max_ms + jack_spacing_buffer_ms ||
                last_ms * jack_speed_increase_cutoff_factor < max_ms)
            {
                // too slow reset, too fast reset
                start_note_sec = last_note_sec;
                len = 2;
            }
            else
            {
                len++;
            }
            max_ms = last_ms;
            last_note_sec = now;
        }

        public float get_ms()
        {
            if (len > jack_len_cap)
                return len_capped_ms;

            const float avg_ms_mult = 1.5f;
            const float anchor_time_buffer_ms = 30.0f;
            const float min_ms = 95.0f;
            float total_ms = M.ms_from(last_note_sec, start_note_sec);
            float _len = (float)(len - 1);
            float avg_ms = total_ms / _len;
            float adj_total_ms = total_ms + anchor_time_buffer_ms + avg_ms * avg_ms_mult;
            float ms = adj_total_ms / _len;
            if (len == 2)
            {
                ms *= 1.1f;
                ms = ms < 180.0f ? 180.0f : ms;
            }
            ms = ms < min_ms ? min_ms : ms;
            if (float.IsNaN(ms))
                ms = max_ms;
            if (len == jack_len_cap)
                len_capped_ms = ms;
            return ms;
        }
    }

    public sealed class oversimplified_jacks
    {
        public List<jack_col> sequencers = new List<jack_col>();

        public List<uint> left_hand_mask = new List<uint>();
        public List<uint> right_hand_mask = new List<uint>();

        public void init(int keycount)
        {
            sequencers = new List<jack_col>();
            for (int i = 0; i < keycount; i++) sequencers.Add(new jack_col());
            left_hand_mask.Clear();
            right_hand_mask.Clear();
            for (int i = 0; i < keycount / 2; i++)
                left_hand_mask.Add((uint)i);
            /*
            if (keycount > 2 && keycount % 2 != 0) {
                left_hand_mask.push_back(keycount / 2);
            }
            */
            for (int i = keycount / 2; i < keycount; i++)
                right_hand_mask.Add((uint)i);
            reset();
        }

        public void reset()
        {
            foreach (var seq in sequencers)
                seq.reset();
        }

        /// C++ operator()(column, now)
        public void op(int column, float now)
        {
            foreach (var seq in sequencers)
                seq.check_reset(now);
            sequencers[column].op(now);
        }

        public float get_lowest_jack_ms(uint hand, Calc calc)
        {
            var mask = hand == Hands.left_hand ? left_hand_mask : right_hand_mask;

            float min = M.ms_init;
            foreach (var col in mask)
            {
                float v = sequencers[(int)col].get_ms();
                if (v < min)
                    min = v;
            }
            return min;
        }
    }

    public sealed class diffz
    {
        public nps _nps = new nps();
        public techyo _tc = new techyo();
        public ceejay _cj = new ceejay();

        public void interval_end()
        {
            _tc.interval_end();
            _cj.interval_end();
        }

        public void full_reset()
        {
            interval_end();
            _cj.full_reset();
            _tc.full_reset();
        }
    }
}
