// Faithful C# port of the Etterna MinaCalc v515 HAND-DEPENDENT pattern-mod headers (group 1 of 2):
//   Dependent/HD_PatternMods/{OHJ.h, CJOHJ.h, CJOHAnchor.h, Balance.h, Roll.h, RollJS.h, OHT.h, VOHT.h}.
// Each C++ struct becomes a sealed class of the same name. C++ operator() -> op(...); operator()(new_val) push on a
// CalcMovingWindow -> .push(x). The _params vectors + XML are dropped (per the port spec); the defaulted param member
// fields (the tuned values), the _pmod CalcPatternMod, and the name string are kept verbatim.
//
// PORT NOTES / JUDGMENT CALLS:
//  * The C++ member `base` collides with the C# `base` keyword; it is escaped as `@base` wherever it appears
//    (RollMod, RollJSMod, CJOHAnchorMod, OHTrillMod, VOHTrillMod). Value/semantics unchanged.
//  * OHTrillMod/VOHTrillMod declare member initializers that reference OTHER instance members
//    (`float moving_cv = cv_reset;`, `float pmod = min_mod;`) — illegal in C# field initializers, so those two are
//    assigned in a constructor after the (defaulted) param fields are initialized. Result is identical.
//  * advance_sequencing param names differ from the orchestration spec's placeholder names; the header names are used
//    (Roll/RollJS: `time_s`, not `row_time`; OHT/VOHT: `ms_any`, not `mw_any_ms`). Types/positions match the spec.
//  * The CalcMovingWindow<float> handed to OHT/VOHT advance_sequencing is const-ref in C++ (read-only); taken here as a
//    plain reference-type parameter, only its non-mutating getter get_cv_of_window is called.
//  * Reused nested sequencers (already defined in SeqDependent.cs): OHJ_Sequencer (OHJ, CJOHJ),
//    CJ_OHAnchor_Sequencer (CJOHAnchor). No nested sequencer needed to be added — Roll/RollJS include
//    GenericSequencing.h only for the s_init / max_rows_for_single_interval constants (no sequencer object used).
using System;
using static Mina.col_type;
using static Mina.base_type;
using static Mina.meta_type;

namespace Mina
{
    // ======================= Dependent/HD_PatternMods/OHJ.h =======================

    /// Hand-Dependent PatternMod detecting one hand jumps.
    /// Looks for one hand jumps in general in the interval.
    /// Utilizes sequencing to find continuous one hand jumps.
    public sealed class OHJumpModGuyThing
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.OHJumpMod;
        public const string name = "OHJumpMod";

        // params
        public float min_mod = 0.75F;
        public float max_mod = 1f;

        public float max_seq_weight = 0.65F;
        public float max_seq_pool = 1.2F;
        public float max_seq_scaler = 2f;

        public float prop_pool = 1.5F;
        public float prop_scaler = 1f;

        public OHJ_Sequencer ohj = new OHJ_Sequencer();
        public int max_ohjump_seq_taps = 0;
        public int cc_taps = 0;

        public float floatymcfloatface = 0f;
        // number of jumps scaled to total taps in hand
        public float base_seq_prop = 0f;
        // size of sequence scaled to total taps in hand
        public float base_jump_prop = 0f;

        public float max_seq_component = M.neutral;
        public float prop_component = M.neutral;
        public float pmod = M.neutral;

        public void full_reset()
        {
            ohj.zero();

            max_ohjump_seq_taps = 0;
            cc_taps = 0;

            floatymcfloatface = 0f;
            base_seq_prop = 0f;
            base_jump_prop = 0f;

            max_seq_component = M.neutral;
            prop_component = M.neutral;
            pmod = M.neutral;
        }

        public void advance_sequencing(col_type ct, base_type bt)
        {
            ohj.op(ct, bt);
        }

        // build component based on max sequence relative to hand taps
        public void set_max_seq_comp()
        {
            max_seq_component = max_seq_pool - (base_seq_prop * max_seq_scaler);
            max_seq_component = max_seq_component < 0.1F ? 0.1F : max_seq_component;
            max_seq_component = M.fastsqrt(max_seq_component);
        }

        // build component based on number of jumps relative to hand taps
        public void set_prop_comp()
        {
            prop_component = prop_pool - (base_jump_prop * prop_scaler);
            prop_component = prop_component < 0.1F ? 0.1F : prop_component;
            prop_component = M.fastsqrt(prop_component);
        }

        public void set_pmod(metaItvHandInfo mitvhi)
        {
            ItvHandInfo itvhi = mitvhi._itvhi;

            cc_taps = mitvhi._base_types[(int)base_left_right] +
                      mitvhi._base_types[(int)base_right_left];

            // assert(cc_taps >= 0);

            // if cur_seq > max when we ended the interval, grab it
            max_ohjump_seq_taps = ohj.cur_seq_taps > ohj.max_seq_taps
                                    ? ohj.cur_seq_taps
                                    : ohj.max_seq_taps;

            /* case optimization start */

            // nothing here or there are no ohjumps
            if (itvhi.get_taps_nowi() == 0 ||
                itvhi.get_col_taps_nowi(col_ohjump) == 0)
            {
                pmod = M.neutral;
                return;
            }

            // everything in the interval is in an ohj sequence
            if (max_ohjump_seq_taps >= itvhi.get_taps_nowi())
            {
                pmod = min_mod;
                return;
            }

            /* prop scaling only case */

            // no repeated oh jumps, prop scale only based on jumps taps in hand
            // taps if the jump was immediately broken by a cross column single tap
            // we can have values of 1, otherwise 2
            if (max_ohjump_seq_taps < 3)
            {

                // need to set now
                base_jump_prop =
                  itvhi.get_col_taps_nowf(col_ohjump) / itvhi.get_taps_nowf();
                set_prop_comp();

                pmod = Math.Clamp(prop_component, min_mod, max_mod);
                return;
            }

            /* seq scaling only case */

            // if this is true we have some combination of single notes
            // and jumps where the single notes are all on the same
            // column
            if (cc_taps == 0)
            {
                // we don't want to treat 2[12][12][12]2222 2222[12][12][12]2
                // differently, so use the max sequence here exclusively
                // shortcut mod calculations, we need the base props now

                // build now
                floatymcfloatface = (float)max_ohjump_seq_taps;
                base_seq_prop = floatymcfloatface / itvhi.get_taps_nowf();
                set_max_seq_comp();

                pmod = Math.Clamp(max_seq_component, min_mod, max_mod);
                return;
            }

            /* case optimization end */

            // for js we lean into max sequences more, since they're better
            // indicators of inflated difficulty

            // set either after case optimizations or in case optimizations, after
            // the simple checks, for optimization
            floatymcfloatface = (float)max_ohjump_seq_taps;
            base_seq_prop = floatymcfloatface / mitvhi._itvhi.get_taps_nowf();
            set_max_seq_comp();
            max_seq_component = Math.Clamp(max_seq_component, 0.1F, max_mod);

            base_jump_prop =
              itvhi.get_col_taps_nowf(col_ohjump) / itvhi.get_taps_nowf();
            set_prop_comp();
            prop_component = Math.Clamp(prop_component, 0.1F, max_mod);

            pmod = M.weighted_average(
              max_seq_component, prop_component, max_seq_weight, 1f);
            pmod = Math.Clamp(pmod, min_mod, max_mod);
        }

        public float op(metaItvHandInfo mitvhi)
        {
            set_pmod(mitvhi);

            interval_end();
            return pmod;
        }

        public void interval_end()
        {
            // reset any interval stuff here
            cc_taps = 0;
            ohj.max_seq_taps = 0;
            max_ohjump_seq_taps = 0;
        }
    }

    // ======================= Dependent/HD_PatternMods/CJOHJ.h =======================

    /// Hand-Dependent PatternMod detecting one hand jumps.
    /// This is used specifically for Chordjacks.
    /// initially copied from ohj but the logic should probably be adjusted on
    /// multiple levels
    public sealed class CJOHJumpMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.CJOHJump;
        public const string name = "CJOHJumpMod";

        // params
        public float min_mod = 0.57F;
        public float max_mod = 1f;

        public float prop_pool = 1f;
        public float prop_scaler = 0.63F;

        public OHJ_Sequencer ohj = new OHJ_Sequencer();
        public int max_ohjump_seq_taps = 0;

        // number of jumps scaled to total taps in hand
        public float base_seq_prop = 0f;
        // size of sequence scaled to total taps in hand
        public float base_jump_prop = 0f;

        public float prop_component = M.neutral;
        public float pmod = M.neutral;

        public void full_reset()
        {
            ohj.zero();

            max_ohjump_seq_taps = 0;

            base_seq_prop = 0f;
            base_jump_prop = 0f;

            prop_component = M.neutral;
            pmod = M.neutral;
        }

        public void advance_sequencing(col_type ct, base_type bt)
        {
            ohj.op(ct, bt);
        }

        // build component based on number of jumps relative to hand taps
        public void set_prop_comp()
        {
            prop_component = prop_pool - (base_jump_prop * prop_scaler);
            prop_component = prop_component < 0.1F ? 0.1F : prop_component;
        }

        public void set_pmod(metaItvHandInfo mitvhi)
        {
            ItvHandInfo itvhi = mitvhi._itvhi;

            // if cur_seq > max when we ended the interval, grab it
            max_ohjump_seq_taps = ohj.cur_seq_taps > ohj.max_seq_taps
                                    ? ohj.cur_seq_taps
                                    : ohj.max_seq_taps;

            /* case optimization start */

            // nothing here or there are no ohjumps
            if (itvhi.get_taps_nowi() == 0 ||
                itvhi.get_col_taps_nowi(col_ohjump) == 0)
            {
                pmod = M.neutral;
                return;
            }

            // everything in the interval is in an ohj sequence
            if (max_ohjump_seq_taps >= itvhi.get_taps_nowi())
            {
                pmod = min_mod;
                return;
            }

            // floats for less casting
            // these should always be whole numbers
            float ohjcount = itvhi.get_col_taps_nowf(col_ohjump) / 2f;
            float tapcount = (itvhi.get_col_taps_nowf(col_left) - ohjcount) +
                             (itvhi.get_col_taps_nowf(col_right) - ohjcount);
            float rows = ohjcount + tapcount;

            base_jump_prop = ohjcount / rows;
            set_prop_comp();
            prop_component = Math.Clamp(prop_component, 0.1F, max_mod);

            pmod = prop_component;
            pmod = Math.Clamp(pmod, min_mod, max_mod);
        }

        public float op(metaItvHandInfo mitvhi)
        {
            set_pmod(mitvhi);

            interval_end();
            return pmod;
        }

        public void interval_end()
        {
            // reset any interval stuff here
            ohj.max_seq_taps = 0;
            max_ohjump_seq_taps = 0;
        }
    }

    // ======================= Dependent/HD_PatternMods/CJOHAnchor.h =======================

    /// Hand-Dependent PatternMod detecting Chains.
    /// Looks for jack into jump into jack on alternate finger.
    /// Very lenient: accepts 11...1[12]...[12]2...22
    /// Also accepts 11...1[12]...[12]1...11
    public sealed class CJOHAnchorMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.CJOHAnchor;
        public const string name = "CJOHAnchorMod";

        // params
        public float min_mod = 1f;
        public float max_mod = 1.1F;
        public float @base = .5F;

        public float len_scaler = 0.2F;
        public float anchor_len_weight = 1f;
        public float swap_scaler = 0.10775F;
        public float not_swap_scaler = 0.019F;

        public CJ_OHAnchor_Sequencer chain = new CJ_OHAnchor_Sequencer();
        public int max_chain_swaps = 0;
        public int max_not_swaps = 0;
        public int max_total_len = 0;
        public int max_anchor_len = 0;

        public float pmod = M.neutral;

        public void full_reset()
        {
            chain.zero();

            max_chain_swaps = 0;
            max_not_swaps = 0;
            max_total_len = 0;
            max_anchor_len = 0;

            pmod = M.neutral;
        }

        public void advance_sequencing(col_type ct, base_type bt, col_type last_ct, float any_ms)
        {
            chain.op(ct, bt, last_ct, any_ms);
        }

        public void set_pmod(metaItvHandInfo mitvhi)
        {
            ItvHandInfo itvhi = mitvhi._itvhi;
            int[] base_types = mitvhi._base_types;

            // if cur > max when we ended the interval, grab it
            max_chain_swaps = chain.get_max_chain_swaps();
            max_not_swaps = chain.get_max_not_swaps();
            max_total_len = chain.get_max_total_len();
            max_anchor_len = chain.get_max_anchor_len();

            // nothing here
            if (itvhi.get_taps_nowi() == 0)
            {
                pmod = M.neutral;
                return;
            }

            // pmod = base + (sum)
            //	max anchor / total rows * scale
            //	max swaps / total rows * scale

            // an interval with only jacks of 11112222 has no completed seq
            // the clamp should catch that scenario
            // otherwise assume conditions which continue chains are in chains
            int taps_in_any_sequence = Math.Clamp(base_types[(int)base_single_single] +
                                                     base_types[(int)base_single_jump] +
                                                     base_types[(int)base_jump_single],
                                                   1,
                                                   Math.Max(1, max_total_len));
            float tapsF = (float)taps_in_any_sequence;

            // 11[12]22
            float csF = (float)max_chain_swaps;
            // 11[12]11
            // auto cnF = static_cast<float>(max_not_swaps);
            // 111[12]222 = 1[12]2[12]1[12]2
            float clF = (float)max_total_len;
            // 1[12]2[12]1[12]2 = small number < 11111111111[12]2222
            float caF = (float)max_anchor_len;

            // anchor_len_weight should be [0,1]
            // 1 -> worth = entire chain length
            // 0 -> worth = longest anchor length
            float anchor_len_worth =
              M.weighted_average(clF, caF, anchor_len_weight, 1f);

            float anchor_worth = M.fastsqrt(anchor_len_worth / tapsF) * len_scaler;
            float swap_worth = M.fastsqrt(csF / tapsF) * swap_scaler;
            // auto not_swap_worth = fastsqrt(cnF / tapsF) * not_swap_scaler;
            pmod = Math.Clamp(@base + anchor_worth + swap_worth + not_swap_scaler, min_mod, max_mod);
        }

        public float op(metaItvHandInfo mitvhi)
        {
            set_pmod(mitvhi);

            interval_end();
            return pmod;
        }

        public void interval_end()
        {
            // reset any interval stuff here
            chain.reset_max_seq();
            max_chain_swaps = 0;
            max_not_swaps = 0;
            max_total_len = 0;
            max_anchor_len = 0;
        }
    }

    // ======================= Dependent/HD_PatternMods/Balance.h =======================

    /// Hand-Dependent PatternMod describing the balance between fingers.
    /// The value of the mod is lowest when both fingers are equally loaded.
    /// Currently only runs off of raw tap counts in the interval
    public sealed class BalanceMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.Balance;
        public const string name = "BalanceMod";

        // params
        public float min_mod = 0.95F;
        public float max_mod = 1.05F;
        public float mod_base = 0.325F;
        public float buffer = 1f;
        public float scaler = 1f;
        public float other_scaler = 4f;

        public float pmod = M.neutral;

        public void full_reset() { pmod = M.neutral; }

        public float op(ItvHandInfo itvhi)
        {
            // nothing here
            if (itvhi.get_taps_nowi() == 0)
            {
                return M.neutral;
            }

            // same number of taps on each column
            if (itvhi.cols_equal_now())
            {
                return min_mod;
            }

            // probably should NOT do this but leaving enabled for now so i can
            // verify structural changes dont change output diff
            // jack, dunno if this is worth bothering about? it would only matter
            // for tech and it may matter too much there? idk
            if (itvhi.get_col_taps_nowi(col_left) == 0 ||
                itvhi.get_col_taps_nowi(col_right) == 0)
            {
                return max_mod;
            }

            pmod = itvhi.get_col_prop_low_by_high();
            pmod = (mod_base + (buffer + (scaler / pmod)) / other_scaler);
            pmod = Math.Clamp(pmod, min_mod, max_mod);

            return pmod;
        }
    }

    // ======================= Dependent/HD_PatternMods/Roll.h =======================

    public sealed class RollMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.Roll;
        public const string name = "RollMod";

        // params
        public float min_mod = 0.85F;
        public float max_mod = 1f;
        public float @base = 0.1F;
        public float jj_scaler = 2.5F;

        // ms apart for 2 taps to be considered a jumpjack
        // 0.075 is 200 bpm 16th trills
        // 0.050 is 300 bpm
        // 0.037 is 400 bpm
        // 0.020 is 750 bpm (375 bpm 64th)
        public float ms_threshold = 0.0701F;

        // changes the direction and sharpness of the result curve
        // as the jumpjack width is between 0 and ms_threshold
        // a higher number here makes numbers closer to ms_threshold
        // worth more -- the falloff occurs late
        public float diff_falloff_power = 1f;

        public float required_notes_before_nerf = 6f;

        // indices
        public int lc = 0;
        public int rc = 0;

        // a "problem" is a rough value of jumpjackyness
        // whereas a 1 is 1 jump and the worst possible flam is nearly 0
        // this tracks amount of "problems" in consecutive intervals
        public float current_problems = 0f;

        public float pmod = M.neutral;

        // timestamps of notes in columns
        public float[] _left_times = new float[Calc.max_rows_for_single_interval];
        public float[] _right_times = new float[Calc.max_rows_for_single_interval];

        public void full_reset()
        {
            Array.Fill(_left_times, M.s_init);
            Array.Fill(_right_times, M.s_init);
            current_problems = 0f;
            lc = 0;
            rc = 0;

            pmod = M.neutral;
        }

        public void setup() { }

        public void check()
        {
            // check times in parallel
            // any times within the window count as the jumpishjack
            // just ... determine the degree of jumpy the jumpyjack is
            // using ms ... or something
            int lindex = 0;
            int rindex = 0;
            while (lindex < lc && rindex < rc)
            {
                float l = _left_times[lindex];
                float r = _right_times[rindex];
                float diff = Math.Abs(l - r);

                if (diff <= ms_threshold)
                {
                    lindex++;
                    rindex++;

                    // given time_scaler = 1
                    // diff of ms_threshold gives a v of 1
                    // meaning "1 jumpjack" or "1 problem"
                    // but a flammy one, is worth not so much of a jumpjack
                    // (this function at x=[0,1] begins slow and drops fast)
                    // using std::pow for accuracy here
                    float x = (float)Math.Pow(diff / Math.Max(ms_threshold, 0.00001F),
                                              diff_falloff_power);
                    float v = 1 + (x / (x - 2));
                    current_problems += v;
                }
                else
                {
                    // failed case
                    // throw the oldest value and try again...
                    if (l > r)
                    {
                        rindex++;
                    }
                    else if (r > l)
                    {
                        lindex++;
                    }
                    else
                    {
                        // this case exists to prevent infinite loops
                        // it should never happen unless you put bad values in
                        // params
                        lindex++;
                        rindex++;
                    }
                }
            }
        }

        public void advance_sequencing(col_type ct, float time_s)
        {
            if (lc >= Calc.max_rows_for_single_interval ||
                rc >= Calc.max_rows_for_single_interval)
            {
                // completely impossible condition
                // checking for sanity and safety
                return;
            }

            switch (ct)
            {
                case col_left:
                    {
                        _left_times[lc++] = time_s;
                        break;
                    }
                case col_right:
                    {
                        _right_times[rc++] = time_s;
                        break;
                    }
                case col_ohjump:
                    {
                        _left_times[lc++] = time_s;
                        _right_times[rc++] = time_s;
                        break;
                    }
                default:
                    break;
            }
        }

        public void set_pmod(ItvHandInfo itvhi)
        {
            // no taps, no jj
            if (itvhi.get_taps_nowi() == 0 || current_problems == 0f)
            {
                pmod = M.neutral;
                return;
            }

            if (itvhi.get_taps_nowf() < required_notes_before_nerf)
            {
                pmod = M.neutral;
                return;
            }

            pmod = itvhi.get_taps_nowf() / ((current_problems * 2f) * jj_scaler);
            pmod = Math.Clamp(@base + pmod, min_mod, max_mod);
        }

        public float op(ItvHandInfo itvhi)
        {
            check();
            set_pmod(itvhi);

            interval_end();
            return pmod;
        }

        public void interval_end()
        {
            // reset every interval when finished
            current_problems = 0f;
            Array.Fill(_left_times, M.s_init);
            Array.Fill(_right_times, M.s_init);
            lc = 0;
            rc = 0;
        }
    }

    // ======================= Dependent/HD_PatternMods/RollJS.h =======================

    public sealed class RollJSMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.RollJS;
        public const string name = "RollJSMod";

        // params
        public float min_mod = 0.85F;
        public float max_mod = 1f;
        public float @base = 0.1F;
        public float jj_scaler = 2f;

        // ms apart for 2 taps to be considered a jumpjack
        // 0.075 is 200 bpm 16th trills
        // 0.050 is 300 bpm
        // 0.037 is 400 bpm
        // 0.020 is 750 bpm (375 bpm 64th)
        public float ms_threshold = 0.0701F;

        // changes the direction and sharpness of the result curve
        // as the jumpjack width is between 0 and ms_threshold
        // a higher number here makes numbers closer to ms_threshold
        // worth more -- the falloff occurs late
        public float diff_falloff_power = 1f;

        public float required_notes_before_nerf = 6f;

        // indices
        public int lc = 0;
        public int rc = 0;

        // a "problem" is a rough value of jumpjackyness
        // whereas a 1 is 1 jump and the worst possible flam is nearly 0
        // this tracks amount of "problems" in consecutive intervals
        public float current_problems = 0f;

        public float pmod = M.neutral;

        // timestamps of notes in columns
        public float[] _left_times = new float[Calc.max_rows_for_single_interval];
        public float[] _right_times = new float[Calc.max_rows_for_single_interval];

        public void full_reset()
        {
            Array.Fill(_left_times, M.s_init);
            Array.Fill(_right_times, M.s_init);
            current_problems = 0f;
            lc = 0;
            rc = 0;

            pmod = M.neutral;
        }

        public void setup() { }

        public void check()
        {
            // check times in parallel
            // any times within the window count as the jumpishjack
            // just ... determine the degree of jumpy the jumpyjack is
            // using ms ... or something
            int lindex = 0;
            int rindex = 0;
            while (lindex < lc && rindex < rc)
            {
                float l = _left_times[lindex];
                float r = _right_times[rindex];
                float diff = Math.Abs(l - r);

                if (diff <= ms_threshold)
                {
                    lindex++;
                    rindex++;

                    // given time_scaler = 1
                    // diff of ms_threshold gives a v of 1
                    // meaning "1 jumpjack" or "1 problem"
                    // but a flammy one, is worth not so much of a jumpjack
                    // (this function at x=[0,1] begins slow and drops fast)
                    // using std::pow for accuracy here
                    float x = (float)Math.Pow(diff / Math.Max(ms_threshold, 0.00001F),
                                              diff_falloff_power);
                    float v = 1 + (x / (x - 2));
                    current_problems += v;
                }
                else
                {
                    // failed case
                    // throw the oldest value and try again...
                    if (l > r)
                    {
                        rindex++;
                    }
                    else if (r > l)
                    {
                        lindex++;
                    }
                    else
                    {
                        // this case exists to prevent infinite loops
                        // it should never happen unless you put bad values in
                        // params
                        lindex++;
                        rindex++;
                    }
                }
            }
        }

        public void advance_sequencing(col_type ct, float time_s)
        {
            if (lc >= Calc.max_rows_for_single_interval ||
                rc >= Calc.max_rows_for_single_interval)
            {
                // completely impossible condition
                // checking for sanity and safety
                return;
            }

            switch (ct)
            {
                case col_left:
                    {
                        _left_times[lc++] = time_s;
                        break;
                    }
                case col_right:
                    {
                        _right_times[rc++] = time_s;
                        break;
                    }
                case col_ohjump:
                    {
                        _left_times[lc++] = time_s;
                        _right_times[rc++] = time_s;
                        break;
                    }
                default:
                    break;
            }
        }

        public void set_pmod(ItvHandInfo itvhi)
        {
            // no taps, no jj
            if (itvhi.get_taps_nowi() == 0 || current_problems == 0f)
            {
                pmod = M.neutral;
                return;
            }

            if (itvhi.get_taps_nowf() < required_notes_before_nerf)
            {
                pmod = M.neutral;
                return;
            }

            pmod = itvhi.get_taps_nowf() / ((current_problems * 2f) * jj_scaler);
            pmod = Math.Clamp(@base + pmod, min_mod, max_mod);
        }

        public float op(ItvHandInfo itvhi)
        {
            check();
            set_pmod(itvhi);

            interval_end();
            return pmod;
        }

        public void interval_end()
        {
            // reset every interval when finished
            current_problems = 0f;
            Array.Fill(_left_times, M.s_init);
            Array.Fill(_right_times, M.s_init);
            lc = 0;
            rc = 0;
        }
    }

    // ======================= Dependent/HD_PatternMods/OHT.h =======================

    /// Hand-Dependent PatternMod detecting one hand trills.
    /// almost identical to wrr, refer to comments there
    public sealed class OHTrillMod
    {
        // file-scope const in OHT.h
        public const int max_trills_per_interval = 4;

        public readonly CalcPatternMod _pmod = CalcPatternMod.OHTrill;
        public const string name = "OHTrillMod";

        // params
        public float window_param = 3f;

        public float min_mod = 0.9F;
        public float max_mod = 1f;
        public float @base = 1.35F;
        public float suppression = 0.4F;

        public float cv_reset = 1f;
        public float cv_threshhold = 0.5F;

        public int window = 0;
        public int cc_window = 0;

        public bool luca_turilli = false;

        // ok new plan, ohj, wrjt and wrr are relatively well tuned so i'll try this
        // here, handle merging multiple sequences in a single interval into one
        // value at interval end and keep a window of that. suppose we have two
        // intervals of 12 notes with 8 in trill formation, one has an 8 note trill
        // and the other has two 4 note trills at the start/end, we want to punish
        // the 8 note trill harder, this means we _will_ be resetting the
        // consecutive trill counter every interval, but will not be resetting the
        // trilling flag, this way we don't have to futz around with awkward
        // proportion math, similar to thing 1 and thing 2
        public CalcMovingWindow<float> badjuju = new CalcMovingWindow<float>();
        public CalcMovingWindow<int> _mw_oht_taps = new CalcMovingWindow<int>();

        public int[] foundyatrills = new int[max_trills_per_interval] { 0, 0, 0, 0 };

        public int found_oht = 0;
        public int oht_len = 0;
        public int oht_taps = 0;

        public float hello_my_name_is_goat = 0f;

        // C++ member initializers `moving_cv = cv_reset;` / `pmod = min_mod;` reference other members; assigned in ctor.
        public float moving_cv;
        public float pmod;

        public OHTrillMod()
        {
            moving_cv = cv_reset;
            pmod = min_mod;
        }

        public void full_reset()
        {
            badjuju.zero();
            _mw_oht_taps.zero();

            luca_turilli = false;
            found_oht = 0;
            oht_len = 0;

            for (int i = 0; i < foundyatrills.Length; i++)
            {
                foundyatrills[i] = 0;
            }

            moving_cv = cv_reset;
            pmod = M.neutral;
        }

        public void setup()
        {
            window =
              Math.Clamp((int)window_param, 1, WindowConst.max_moving_window_size);
            cc_window =
              Math.Clamp((int)window_param, 1, WindowConst.max_moving_window_size);
        }

        public float make_thing(float itv_taps)
        {
            hello_my_name_is_goat = 0f;

            if (found_oht == 0)
            {
                return 0f;
            }

            foreach (int v in foundyatrills)
            {
                if (v == 0)
                {
                    continue;
                }

                // water down smaller sequences
                hello_my_name_is_goat =
                  ((float)v / itv_taps) - suppression;
            }
            return Math.Clamp(hello_my_name_is_goat, 0.1F, 1f);
        }

        public void complete_seq()
        {
            if (!luca_turilli || oht_len == 0)
            {
                return;
            }

            if (found_oht < max_trills_per_interval)
            {
                foundyatrills[found_oht] = oht_len;
            }

            luca_turilli = false;
            oht_len = 0;
            ++found_oht;
            moving_cv = (moving_cv + cv_reset) / 2f;
        }

        public bool oht_timing_check(CalcMovingWindow<float> ms_any)
        {
            moving_cv = (moving_cv + ms_any.get_cv_of_window(cc_window)) / 2f;
            // the primary difference from wrr, just check cv on the base ms values,
            // we are looking for values that are all close together without any
            // manipulation
            return moving_cv < cv_threshhold;
        }

        public void wifflewaffle()
        {
            if (luca_turilli)
            {
                ++oht_len;
                ++oht_taps;
            }
            else
            {
                luca_turilli = true;
                oht_len += 3;
                oht_taps += 3;
            }
        }

        public void advance_sequencing(meta_type mt,
                                       CalcMovingWindow<float> ms_any)
        {

            switch (mt)
            {
                case meta_cccccc:
                    if (oht_timing_check(ms_any))
                    {
                        wifflewaffle();
                    }
                    else
                    {
                        complete_seq();
                    }
                    break;
                case meta_ccacc:
                    // wait to see what happens
                    break;
                case meta_enigma:
                case meta_meta_enigma:
                    // also wait to see what happens, but not if last was ccacc,
                    // since we only don't complete there if we don't immediately go
                    // back into ohts

                    // this seems to be overkill with how lose the detection is
                    // already anyway

                    // if (now.last_cc == meta_ccacc) {
                    //	complete_seq();
                    //}
                    // break;
                default:
                    complete_seq();
                    break;
            }
        }

        public void set_pmod(ItvHandInfo itvhi)
        {

            // no taps, no trills
            if (itvhi.get_taps_windowi(window) == 0 ||
                _mw_oht_taps.get_total_for_window(window) == 0)
            {
                pmod = M.neutral;
                return;
            }

            // full oht
            if (itvhi.get_taps_windowi(window) ==
                _mw_oht_taps.get_total_for_window(window))
            {
                pmod = min_mod;
                return;
            }

            badjuju.push(make_thing(itvhi.get_taps_nowf()));

            pmod = @base - badjuju.get_mean_of_window(window);
            pmod = Math.Clamp(pmod, min_mod, max_mod);
        }

        public float op(ItvHandInfo itvhi)
        {
            if (oht_len > 0 && found_oht < max_trills_per_interval)
            {
                foundyatrills[found_oht] = oht_len;
                ++found_oht;
            }

            _mw_oht_taps.push(oht_taps);

            set_pmod(itvhi);

            interval_end();
            return pmod;
        }

        public void interval_end()
        {
            Array.Fill(foundyatrills, 0);
            found_oht = 0;
            oht_len = 0;
            oht_taps = 0;
        }
    }

    // ======================= Dependent/HD_PatternMods/VOHT.h =======================

    /// Hand-Dependent PatternMod detecting one hand trills.
    /// Specific meant to downscale long continuous one hand trills
    /// to nerf jumpjack vibro.
    /// almost identical to wrr, refer to comments there
    public sealed class VOHTrillMod
    {
        // file-scope const in VOHT.h
        public const int max_vtrills_per_interval = 4;

        public readonly CalcPatternMod _pmod = CalcPatternMod.VOHTrill;
        public const string name = "VOHTrillMod";

        // params
        public float window_param = 2f;

        public float min_mod = 0.25F;
        public float max_mod = 1f;
        public float @base = 1.5F;
        public float suppression = 0.2F;

        public float cv_reset = 1f;
        public float cv_threshhold = 0.25F;

        public float min_len = 8f;

        public int window = 0;
        public int cc_window = 0;

        public bool luca_turilli = false;

        public CalcMovingWindow<float> badjuju = new CalcMovingWindow<float>();
        public CalcMovingWindow<int> _mw_oht_taps = new CalcMovingWindow<int>();

        public int[] foundyatrills = new int[max_vtrills_per_interval] { 0, 0, 0, 0 };

        public int found_oht = 0;
        public int oht_len = 0;
        public int oht_taps = 0;

        public float hello_my_name_is_goat = 0f;

        // C++ member initializers `moving_cv = cv_reset;` / `pmod = min_mod;` reference other members; assigned in ctor.
        public float moving_cv;
        public float pmod;

        public VOHTrillMod()
        {
            moving_cv = cv_reset;
            pmod = min_mod;
        }

        public void full_reset()
        {
            badjuju.zero();

            luca_turilli = false;
            found_oht = 0;
            oht_len = 0;

            for (int i = 0; i < foundyatrills.Length; i++)
            {
                foundyatrills[i] = 0;
            }

            moving_cv = cv_reset;
            pmod = M.neutral;
        }

        public void setup()
        {
            window =
              Math.Clamp((int)window_param, 1, WindowConst.max_moving_window_size);
            cc_window =
              Math.Clamp((int)window_param, 1, WindowConst.max_moving_window_size);
        }

        public float make_thing(float itv_taps)
        {
            hello_my_name_is_goat = 0f;

            if (found_oht == 0)
            {
                return 0f;
            }

            foreach (int v in foundyatrills)
            {
                if (v == 0)
                {
                    continue;
                }

                // water down smaller sequences
                hello_my_name_is_goat =
                  ((float)v / itv_taps) - suppression;
            }
            return Math.Clamp(hello_my_name_is_goat, 0.1F, 1f);
        }

        public void complete_seq()
        {
            if (!luca_turilli || oht_len == 0)
            {
                return;
            }

            if (found_oht < max_vtrills_per_interval)
            {
                foundyatrills[found_oht] = oht_len;
            }

            luca_turilli = false;
            oht_len = 0;
            ++found_oht;
            moving_cv = (moving_cv + cv_reset) / 2f;
        }

        public bool oht_timing_check(CalcMovingWindow<float> ms_any)
        {
            moving_cv = (moving_cv + ms_any.get_cv_of_window(cc_window)) / 2f;
            return moving_cv < cv_threshhold;
        }

        public void wifflewaffle()
        {
            if (luca_turilli)
            {
                ++oht_len;
                ++oht_taps;
            }
            else
            {
                luca_turilli = true;
                oht_len += 3;
                oht_taps += 3;
            }
        }

        public void advance_sequencing(meta_type mt,
                                       CalcMovingWindow<float> ms_any)
        {

            switch (mt)
            {
                case meta_cccccc:
                    if (oht_timing_check(ms_any))
                    {
                        wifflewaffle();
                    }
                    else
                    {
                        complete_seq();
                    }
                    break;
                case meta_ccacc:
                    // wait to see what happens
                    break;
                case meta_enigma:
                case meta_meta_enigma:
                default:
                    complete_seq();
                    break;
            }
        }

        public void set_pmod(ItvHandInfo itvhi)
        {

            // no taps, no trills
            if (itvhi.get_taps_windowi(window) == 0 ||
                _mw_oht_taps.get_total_for_window(window) == 0)
            {
                pmod = M.neutral;
                return;
            }

            if (_mw_oht_taps.get_total_for_window(window) < min_len)
            {
                pmod = M.neutral;
                return;
            }

            // full oht
            if (itvhi.get_taps_windowi(window) ==
                _mw_oht_taps.get_total_for_window(window))
            {
                pmod = min_mod;
                return;
            }

            badjuju.push(make_thing(itvhi.get_taps_nowf()));

            pmod = @base - badjuju.get_mean_of_window(window);
            pmod = Math.Clamp(pmod, min_mod, max_mod);
        }

        public float op(ItvHandInfo itvhi)
        {
            if (oht_len > 0 && found_oht < max_vtrills_per_interval)
            {
                foundyatrills[found_oht] = oht_len;
                ++found_oht;
            }

            _mw_oht_taps.push(oht_taps);

            set_pmod(itvhi);

            interval_end();
            return pmod;
        }

        public void interval_end()
        {
            Array.Fill(foundyatrills, 0);
            found_oht = 0;
            oht_len = 0;
            oht_taps = 0;
        }
    }
}
