// Faithful C# port of the hand-dependent pattern mods, group 2 of 2 (v515):
//   Dependent/HD_PatternMods/{Chaos,RunningMan,Minijack,WideRangeBalance,WideRangeRoll,
//                             WideRangeJumptrill,WideRangeJJ,WideRangeAnchor}.h
// Each C++ struct -> a sealed class with the same name. C++ operator() becomes op(...). The mods' _params vectors and
// XML load/save are dropped; the defaulted param fields (the official tuned values) are kept verbatim, along with the
// _pmod field and the name string. Sequencers (RM_Sequencer / RunningMan / AnchorSequencer / Anchor_Sequencing) and the
// helper types (ItvHandInfo, CalcMovingWindow) are REUSED from SeqDependent.cs / MetaDependent.cs / SeqCommon.cs,
// never redefined here.
//
// PORT NOTES (judgment calls):
//  * C++ field `base` is a C# reserved word -> escaped as `@base` (identifier name preserved verbatim). Likewise the
//    C++ parameter/argument `as` (AnchorSequencer) -> `@as` (matches the existing SeqDependent.cs convention).
//  * C++ in-class initializers that reference a sibling field (`float pmod = min_mod;`, `float moving_cv = cv_reset;`)
//    are illegal as C# instance-field initializers (no `this` in an initializer), so they are performed in a
//    parameterless constructor instead. Field initializers run first, so min_mod / cv_reset already hold their defaults
//    when the ctor copies them -> identical result to the C++ member-init order.
//  * Header param names win over the task's suggested names where they differ (per instructions). Noted inline:
//      - ChaosMod.op / RunningManMod.op / MinijackMod: C++ arg is `total_taps` (not `taps_nowi`).
//      - ChaosMod.advance_sequencing: C++ arg is `ms_any` (not `mw_any_ms`).
//      - WideRangeRollMod.advance_sequencing: C++ 5th arg is `tc_ms` (not `sc_ms_now`).
//      - WideRangeJJMod.advance_sequencing: C++ 2nd arg is `time_s` (not `row_time`).
//      - WideRangeAnchorMod.op/set_pmod: C++ 1st arg is `itvhi` (not `ihi`).
//  * RM_Sequencer is a value type (struct) in C++, so `highest_rm = get_active_rm_with_higher_difficulty()` takes a
//    DEEP COPY of the chosen live sequencer; `interval_end()`'s `highest_rm.full_reset()` therefore must NOT reset the
//    live `rms[c]`. Since RM_Sequencer is a reference type in this port, a hand-written deep clone (CloneRm) reproduces
//    the C++ by-value copy so the live running-man sequencers survive across intervals. (RunningMan.h:171/195/299)
//  * WideRangeJumptrill takes `CalcMovingWindow<float>& ms_any` (NON-const ref) and calls the mutate-then-restore
//    timing checks on it; this differs from the general "read-only" guidance, so per instructions we TRUST THE HEADER
//    and pass the live window reference and call the mutating checks. (WideRangeJumptrill.h:97,112-128)
//  * `wrjt_cv_factor` is a namespace-scope `constexpr` in the C++ header; it is only used by WideRangeJumptrillMod, so
//    it is scoped as a const inside that class here. (WideRangeJumptrill.h:5)
//  * `max_rows_for_single_interval` lives on Calc in this port -> referenced as Calc.max_rows_for_single_interval.
using System;
using System.Collections.Generic;
using static Mina.col_type;
using static Mina.base_type;
using static Mina.meta_type;
using static Mina.rm_status;
using static Mina.HD;

namespace Mina
{
    // ======================= Chaos.h =======================

    /// slightly different implementation of the old chaos mod, basically picks up polyishness and tries to detect
    /// awkward transitions. In other words, detects chaotic timing between continuous notes.
    public sealed class ChaosMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.Chaos;
        public const string name = "ChaosMod";

        // params
        public float min_mod = 0.88f;
        public float max_mod = 1.07f;
        public float @base = -0.088f;

        // don't allow this to be a modifiable param
        public const int window = 6;

        public CalcMovingWindow<float> _u = new CalcMovingWindow<float>();
        public CalcMovingWindow<float> _wot = new CalcMovingWindow<float>();

        public float pmod = M.neutral;

        public void full_reset()
        {
            _u.zero();
            _wot.zero();
            pmod = M.neutral;
        }

        public void advance_sequencing(CalcMovingWindow<float> ms_any)
        {
            // most recent value
            float a = ms_any.get_now();

            // previous value
            float b = ms_any.get_last();

            if (M.any_ms_is_zero(a) || M.any_ms_is_zero(b) || M.any_ms_is_close(a, b))
            {
                _u.push(1f);
                _wot.push(_u.get_mean_of_window(window));
                return;
            }

            float prop = M.div_high_by_low(a, b);
            int mop = (int)prop;
            float flop = prop - (float)mop;

            if (flop == 0f)
            {
                flop = 1f;
            }
            else if (flop >= 0.5f)
            {
                flop = Math.Abs(flop - 1f) + 1f;
            }
            else if (flop < 0.5f)
            {
                flop += 1f;
            }

            _u.push(flop);
            _wot.push(_u.get_mean_of_window(window));
        }

        // C++ header arg name is total_taps (task suggested taps_nowi); header wins.
        public float op(int total_taps)
        {
            if (total_taps == 0)
            {
                return M.neutral;
            }

            pmod = @base + _wot.get_mean_of_window(WindowConst.max_moving_window_size);
            pmod = Math.Clamp(pmod, min_mod, max_mod);
            return pmod;
        }
    }

    // ======================= RunningMan.h =======================

    /// Hand-Dependent PatternMod detecting a RunningMen. Runningman sequencing has 2 core purposes: (1) tracking the
    /// anchor speed of a runningman sequence to guess a more ms-oriented baseline difficulty (rolled into tech in ulbu's
    /// main row loop, NOT here), and (2) the usual pattern-mod generation from the runningman characteristics only.
    public sealed class RunningManMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.RanMan;
        public const string name = "RunningManMod";

        // params
        public float min_mod = 1f;
        public float max_mod = 1.1f;
        public float @base = 0.5f;
        public float min_anchor_len = 5f;
        public float min_taps_in_rm = 1f;
        public float min_off_taps_same = 1f;

        public float offhand_tap_prop_scaler = 1f;
        public float offhand_tap_prop_min = 0f;
        public float offhand_tap_prop_max = 1f;
        public float offhand_tap_prop_base = 1.7f;

        public float offhand_tap_prop_anch_diff_base = 1.7f;
        public float offhand_tap_prop_anch_diff_scaler = 1.1f;
        public float offhand_tap_prop_anch_diff_min = 0.75f;
        public float offhand_tap_prop_anch_diff_max = 1f;

        public float off_tap_same_prop_scaler = 1f;
        public float off_tap_same_prop_min = 0f;
        public float off_tap_same_prop_max = 1.25f;
        public float off_tap_same_prop_base = 0.8f;

        public float anchor_len_divisor = 5f;
        public float anchor_len_comp_min = 0f;
        public float anchor_len_comp_max = 1.25f;

        public float min_jack_taps_for_bonus = 1f;
        public float jack_bonus_base = 0.1f;

        public float min_oht_taps_for_bonus = 1f;
        public float oht_bonus_base = 0.1f;

        // params for rm_sequencing, these define conditions for resetting runningmen sequences
        public float max_oht_len = 2f;
        public float max_off_len = 3f;
        public float max_ot_sh_len = 2f;
        public float max_burst_len = 6f;
        public float max_jack_len = 3f;
        public float max_anch_len = 5f;

        // stuff for making mod
        public RM_Sequencer[] rms = new RM_Sequencer[num_cols_per_hand];

        // for an interval, active rm sequence with the highest difficulty
        public RM_Sequencer highest_rm = new RM_Sequencer();

        public int test = 0;
        public float offhand_tap_prop = 0f;
        public float off_tap_same_prop = 0f;

        public float anchor_len_comp = 0f;
        public float jack_bonus = 0f;
        public float oht_bonus = 0f;

        public float pmod = M.neutral;

        public RunningManMod()
        {
            for (int i = 0; i < num_cols_per_hand; i++)
                rms[i] = new RM_Sequencer();
        }

        /// Deep clone reproducing C++ RM_Sequencer value-copy semantics (nested RunningMan copied by value too).
        private static RM_Sequencer CloneRm(RM_Sequencer s)
        {
            var d = new RM_Sequencer();
            d.max_oht_len = s.max_oht_len;
            d.max_off_len = s.max_off_len;
            d.max_ot_sh_len = s.max_ot_sh_len;
            d.max_burst_len = s.max_burst_len;
            d.max_jack_len = s.max_jack_len;
            d.max_anchor_len = s.max_anchor_len;
            d._ct = s._ct;
            d._status = s._status;
            d._rmb = s._rmb;
            d._last_rmb = s._last_rmb;
            d.is_bursting = s.is_bursting;
            d.had_burst = s.had_burst;
            d.last_anchor_time = s.last_anchor_time;
            d._start = s._start;
            d._rm.ran_taps = s._rm.ran_taps;
            d._rm._len = s._rm._len;
            d._rm.off_taps = s._rm.off_taps;
            d._rm.off_len = s._rm.off_len;
            d._rm.off_taps_sh = s._rm.off_taps_sh;
            d._rm.oht_taps = s._rm.oht_taps;
            d._rm.oht_len = s._rm.oht_len;
            d._rm.ot_sh_len = s._rm.ot_sh_len;
            d._rm.jack_taps = s._rm.jack_taps;
            d._rm.jack_len = s._rm.jack_len;
            d._rm.anch_len = s._rm.anch_len;
            return d;
        }

        public void full_reset()
        {
            foreach (var rm in rms)
            {
                rm.full_reset();
            }

            offhand_tap_prop = 0f;
            off_tap_same_prop = 0f;

            anchor_len_comp = 0f;
            jack_bonus = 0f;
            oht_bonus = 0f;

            pmod = M.neutral;
        }

        /* keep parallel rm sequencers for both left and right column for each hand, this way we don't have to worry
         * about trying to figure out which column the runningman anchor should be on */
        public void setup()
        {
            foreach (var c in ct_loop_no_jumps)
            {
                rms[(int)c]._ct = c;
                rms[(int)c].set_params(max_oht_len,
                                       max_off_len,
                                       max_ot_sh_len,
                                       max_burst_len,
                                       max_jack_len,
                                       max_anch_len);
            }
        }

        public void advance_off_hand_sequencing()
        {
            foreach (var c in ct_loop_no_jumps)
            {
                rms[(int)c].advance_off_hand_sequencing();
            }
        }

        public void advance_sequencing(col_type ct,
                                       base_type bt,
                                       meta_type mt,
                                       AnchorSequencer @as)
        {
            foreach (var c in ct_loop_no_jumps)
            {
                rms[(int)c].op(ct, bt, mt, @as.anch[(int)c]);
            }

            highest_rm = get_active_rm_with_higher_difficulty();
        }

        public float get_highest_anchor_difficulty()
        {
            /* see off_hand_tap_prop for a detailed explanation, basically only the rm mod was downscaling rolls, short
             * burst rolls that escaped roll detection but flagged high on rm diff were super overrated without this
             * adjustment. called immediately after advance_sequencing, so highest_rm is already chosen */

            float oht_p = offhand_tap_prop_anch_diff_base -
                          (highest_rm._rm.get_offhand_tap_prop() *
                           offhand_tap_prop_anch_diff_scaler);

            oht_p = Math.Clamp(oht_p,
                               offhand_tap_prop_anch_diff_min,
                               offhand_tap_prop_anch_diff_max);

            return highest_rm.get_difficulty() * oht_p;
        }

        // C++ returns RM_Sequencer by value -> clone the selected live sequencer.
        public RM_Sequencer get_active_rm_with_higher_difficulty()
        {
            if (rms[(int)col_left]._status == rm_running &&
                rms[(int)col_right]._status == rm_running)
            {
                return rms[(int)col_left].get_difficulty() >
                           rms[(int)col_right].get_difficulty()
                         ? CloneRm(rms[(int)col_left])
                         : CloneRm(rms[(int)col_right]);
            }

            return rms[(int)col_left]._status == rm_running ? CloneRm(rms[(int)col_left])
                                                            : CloneRm(rms[(int)col_right]);
        }

        /* Note: this mod is only used for pushing up runningmen focused stream/js _patterns_, the anchor difficulty
         * isn't used here, that's used in tech. */
        public void set_pmod(int total_taps)
        {
            /* nothing here */
            if (total_taps == 0)
            {
                pmod = M.neutral;
                return;
            }

            RunningMan rm = highest_rm._rm;

            // min mod optimization
            if (rm._len < min_anchor_len || rm.ran_taps < min_taps_in_rm ||
                rm.off_taps_sh < min_off_taps_same)
            {
                pmod = min_mod;
                return;
            }

            /* the larger the share of off hand taps to anchor taps, the higher the probability we're just looking at
             * something like rolls. this is only a nerf. */
            offhand_tap_prop = offhand_tap_prop_base - (rm.get_offhand_tap_prop() *
                                                        offhand_tap_prop_scaler);
            offhand_tap_prop = Math.Clamp(
              offhand_tap_prop, offhand_tap_prop_min, offhand_tap_prop_max);

            /* number of same hand off anchor taps / anchor taps */
            off_tap_same_prop =
              off_tap_same_prop_base +
              (rm.get_off_tap_same_prop() * off_tap_same_prop_scaler);

            off_tap_same_prop = Math.Clamp(
              off_tap_same_prop, off_tap_same_prop_min, off_tap_same_prop_max);

            /* anchor length component: longer runningmen register more strongly, but not infinitely */
            anchor_len_comp = (float)rm._len / anchor_len_divisor;
            anchor_len_comp =
              Math.Clamp(anchor_len_comp, anchor_len_comp_min, anchor_len_comp_max);

            // jacks in anchor component, give a small bonus i guess
            jack_bonus =
              rm.jack_taps >= min_jack_taps_for_bonus ? jack_bonus_base : 0f;

            // ohts in anchor component, give a small bonus i guess
            oht_bonus =
              rm.oht_taps >= min_oht_taps_for_bonus ? oht_bonus_base : 0f;

            pmod = @base + anchor_len_comp + jack_bonus + oht_bonus;
            pmod = Math.Clamp(M.fastsqrt(pmod * off_tap_same_prop * offhand_tap_prop),
                              min_mod,
                              max_mod);
        }

        // C++ header arg name is total_taps (task suggested taps_nowi); header wins.
        public float op(int total_taps)
        {
            set_pmod(total_taps);

            interval_end();
            return pmod;
        }

        public void interval_end() { highest_rm.full_reset(); }
    }

    // ======================= Minijack.h =======================

    /// Hand-Dependent PatternMod detecting minijacks. Minijacks cannot lead into minijacks. Minijacks are simply cases
    /// of 11 or 22.
    public sealed class MinijackMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.Minijack;
        public const string name = "MinijackMod";

        // params
        public float min_mod = 1f;
        public float max_mod = 1.25f;
        public float @base = 0.4f;

        public float mj_scaler = 2.6f;
        public float mj_buffer = 0.3f;

        // ms times for each finger
        public CalcMovingWindow<float> left_ms = new CalcMovingWindow<float>();
        public CalcMovingWindow<float> right_ms = new CalcMovingWindow<float>();

        // 2.0 is equivalent to 8th -> 16th (a 16th minijack in 16th js) if the min of the window is
        public const float minijack_speed_increase_factor = 1.9f;

        // if the ms gap after a minijack larger by this factor then the ms gap for the minijack is confirmed a minijack
        public const float minijack_confirmation_factor = 1.3f;

        // throw out data if this many ms pass
        public const float dont_care_threshold = 500f;

        // 150ms is a 100 bpm 16th
        public const float slow_minijack_cutoff_ms = 149.5f;

        public const int window = 3;
        public int minijacks = 0;
        public float pmod;                       // C++: = min_mod (set in ctor)

        public int left_since_last_right = 0;
        public int right_since_last_left = 0;
        public CalcMovingWindow<int> left_notes = new CalcMovingWindow<int>();
        public CalcMovingWindow<int> right_notes = new CalcMovingWindow<int>();

        // tracking taps that occur on the opposite hand
        public int off_since_last_on = 0;
        public CalcMovingWindow<int> off_hand_notes = new CalcMovingWindow<int>();

        public MinijackMod()
        {
            pmod = min_mod;
        }

        public void full_reset()
        {
            pmod = M.neutral;
            minijacks = 0;
            left_ms.fill(M.ms_init);
            right_ms.fill(M.ms_init);
            left_notes.fill(0);
            right_notes.fill(0);
            off_hand_notes.fill(0);
            left_since_last_right = 0;
            right_since_last_left = 0;
            off_since_last_on = 0;
        }

        public void reset_mw_for_ct(col_type ct)
        {
            switch (ct)
            {
                case col_left:
                {
                    left_ms.fill(M.ms_init);
                    break;
                }
                case col_right:
                {
                    right_ms.fill(M.ms_init);
                    break;
                }
                case col_ohjump:
                {
                    left_ms.fill(M.ms_init);
                    right_ms.fill(M.ms_init);
                    break;
                }
                default:
                    break;
            }
        }

        public void minijack_check(CalcMovingWindow<float> mv, CalcMovingWindow<int> mwOffTapCounts)
        {
            // make sure the window is filled out
            float max = mv.get_max_for_window(window);
            if (max != M.ms_init)
            {
                int i = WindowConst.max_moving_window_size;
                float min = mv.get_min_for_window(window);
                float recent_ms = mv[--i];
                float last_ms = mv[--i];
                float last_last_ms = mv[--i];

                // basically we dont care if the "minijack" exists if it is so slow
                if (last_ms > slow_minijack_cutoff_ms)
                {
                    return;
                }

                // for a minijack to count it must have a gap before it and after
                // the gap before : speedup of basically 8th -> 16th or faster
                // the gap after : slowdown of basically 16th -> 12th or slower
                if (last_ms == min &&
                    recent_ms > last_ms * minijack_confirmation_factor &&
                    last_last_ms > last_ms * minijack_speed_increase_factor)
                {
                    // require that the taps which fit the minijack speed condition have no off tap between. this would
                    // make it a trill instead.
                    if (mwOffTapCounts[WindowConst.max_moving_window_size - 2] == 0)
                    {
                        // nerf case: require that the minijack is not part of a two hand trill
                        if (off_hand_notes[WindowConst.max_moving_window_size - 2] == 0)
                        {
                            minijacks++;
                        }
                    }
                }
            }
        }

        public void advance_sequencing(col_type ct, float ms_now)
        {
            /*
            if (ms_now > dont_care_threshold) {
                // data has become stale
                reset_mw_for_ct(ct);
                return;
            }
            */

            switch (ct)
            {
                case col_left:
                {
                    // if we see a left note after having seen right notes we just went through a trill
                    if (right_since_last_left > 0 || left_since_last_right > 0)
                    {
                        right_notes.push(right_since_last_left);
                    }
                    left_since_last_right++;
                    right_since_last_left = 0;
                    left_ms.push(ms_now);
                    commit_off_hand_taps();
                    minijack_check(left_ms, right_notes);
                    break;
                }
                case col_right:
                {
                    // if we see a right note after x lefts... trill happen
                    if (left_since_last_right > 0 || right_since_last_left > 0)
                    {
                        left_notes.push(left_since_last_right);
                    }
                    right_since_last_left++;
                    left_since_last_right = 0;
                    right_ms.push(ms_now);
                    commit_off_hand_taps();
                    minijack_check(right_ms, left_notes);
                    break;
                }
                case col_ohjump:
                {
                    // jumps reset trill conditions; 1[12] and [12]1 considered minijacks
                    left_notes.push(left_since_last_right);
                    right_notes.push(right_since_last_left);
                    left_since_last_right = 0;
                    right_since_last_left = 0;
                    left_ms.push(ms_now);
                    right_ms.push(ms_now);
                    commit_off_hand_taps();
                    minijack_check(left_ms, right_notes);
                    minijack_check(right_ms, left_notes);
                    break;
                }
                default:
                    break;
            }
        }

        public void advance_off_hand_sequencing()
        {
            off_since_last_on++;
        }

        public void commit_off_hand_taps()
        {
            off_hand_notes.push(off_since_last_on);
            off_since_last_on = 0;
        }

        public void set_pmod(ItvHandInfo itvhi)
        {
            if (minijacks == 0 || itvhi.get_taps_nowi() == 0)
            {
                pmod = M.neutral;
                return;
            }

            float mj = ((float)minijacks + mj_buffer) * mj_scaler;
            float taps = itvhi.get_taps_nowf() - mj_buffer;

            pmod = @base + mj / taps;

            pmod = Math.Clamp(pmod, min_mod, max_mod);
        }

        public float op(ItvHandInfo itvhi)
        {
            set_pmod(itvhi);

            interval_end();
            return pmod;
        }

        public void interval_end()
        {
            minijacks = 0;
        }
    }

    // ======================= WideRangeBalance.h =======================

    /// Hand-Dependent PatternMod describing balance between fingers.
    public sealed class WideRangeBalanceMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.WideRangeBalance;
        public const string name = "WideRangeBalanceMod";

        // params
        public float window_param = 2f;

        public float min_mod = 0.94f;
        public float max_mod = 1.05f;
        public float @base = 0.425f;

        public float buffer = 1f;
        public float scaler = 1f;
        public float other_scaler = 4f;

        public int window = 0;
        public float pmod = M.neutral;

        public void full_reset() { pmod = M.neutral; }

        public void setup()
        {
            // setup should be run after loading params from disk
            window = Math.Clamp((int)window_param, 1, WindowConst.max_moving_window_size);
        }

        public float op(ItvHandInfo itvhi)
        {
            // nothing here
            if (itvhi.get_taps_nowi() == 0)
            {
                return M.neutral;
            }

            // same number of taps on each column for this window
            if (itvhi.cols_equal_window(window))
            {
                return min_mod;
            }

            pmod = itvhi.get_col_prop_low_by_high_window(window);

            pmod = (@base + (buffer + (scaler / pmod)) / other_scaler);
            pmod = Math.Clamp(pmod, min_mod, max_mod);

            return pmod;
        }
    }

    // ======================= WideRangeRoll.h =======================

    /// Hand-Dependent PatternMod detecting continuous rolls. Lenient in a particular way to detect patterns that are
    /// mostly jumptrilly.
    public sealed class WideRangeRollMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.WideRangeRoll;
        public const string name = "WideRangeRollMod";

        // params
        public float window_param = 5f;

        public float min_mod = 0.25f;
        public float max_mod = 1f;
        public float @base = 0.15f;
        public float scaler = 0.9f;

        public float cv_reset = 1f;
        public float cv_threshold = 0.35f;
        public float other_cv_threshold = 0.3f;

        public int window = 0;

        // moving window of longest roll sequences seen in the interval
        public CalcMovingWindow<int> _mw_max = new CalcMovingWindow<int>();

        // we want to keep custom adjusted ms values here
        public CalcMovingWindow<float> _mw_adj_ms = new CalcMovingWindow<float>();

        public bool last_passed_check = false;
        public int nah_this_file_aint_for_real = 0;
        public int max_thingy = 0;
        public float hi_im_a_float = 0f;

        // WE CAN JUST MOVE THE TIMING CHECK FUNCTIONS INTO CALCWINDOW LUL
        public List<float> idk_ms = new List<float> { 0f, 0f, 0f, 0f };
        public List<float> seq_ms = new List<float> { 0f, 0f, 0f };

        public float moving_cv;                  // C++: = cv_reset (set in ctor)
        public float pmod;                       // C++: = min_mod (set in ctor)

        public WideRangeRollMod()
        {
            moving_cv = cv_reset;
            pmod = min_mod;
        }

        public void full_reset()
        {
            _mw_max.zero();
            _mw_adj_ms.zero();

            last_passed_check = false;
            nah_this_file_aint_for_real = 0;
            max_thingy = 0;
            hi_im_a_float = 0f;

            for (int i = 0; i < seq_ms.Count; i++)
            {
                seq_ms[i] = 0f;
            }
            for (int i = 0; i < idk_ms.Count; i++)
            {
                idk_ms[i] = 0f;
            }

            moving_cv = cv_reset;
            pmod = M.neutral;
        }

        public void setup()
        {
            window = Math.Clamp((int)window_param, 1, WindowConst.max_moving_window_size);
        }

        public void zoop_the_woop(int pos, float div, float scaler = 1f)
        {
            seq_ms[pos] /= div;
            last_passed_check = do_timing_thing(scaler);
            seq_ms[pos] *= div;
        }

        public void woop_the_zoop(int pos, float mult, float scaler = 1f)
        {
            seq_ms[pos] *= mult;
            last_passed_check = do_timing_thing(scaler);
            seq_ms[pos] /= mult;
        }

        public bool do_timing_thing(float scaler)
        {
            _mw_adj_ms.push(seq_ms[1]);

            if (_mw_adj_ms.get_cv_of_window(window) > other_cv_threshold)
            {
                return false;
            }

            hi_im_a_float = M.cv(seq_ms);

            // ok we're pretty sure it's a roll don't bother with the test
            if (hi_im_a_float < 0.12f)
            {
                moving_cv = (hi_im_a_float + moving_cv + hi_im_a_float) / 3f;
                return true;
            }
            {
                moving_cv = (hi_im_a_float + moving_cv) / 2f;
            }

            return moving_cv < cv_threshold / scaler;
        }

        public bool do_other_timing_thing(float scaler)
        {
            _mw_adj_ms.push(idk_ms[1]);
            _mw_adj_ms.push(idk_ms[2]);

            if (_mw_adj_ms.get_cv_of_window(window) > other_cv_threshold)
            {
                return false;
            }

            hi_im_a_float = M.cv(idk_ms);

            // ok we're pretty sure it's a roll don't bother with the test
            if (hi_im_a_float < 0.12f)
            {
                moving_cv = (hi_im_a_float + moving_cv + hi_im_a_float) / 3f;
                return true;
            }
            {
                moving_cv = (hi_im_a_float + moving_cv) / 2f;
            }

            return moving_cv < cv_threshold / scaler;
        }

        public void handle_ccacc_timing_check() { zoop_the_woop(1, 2.5f, 1.25f); }

        public void handle_roll_timing_check()
        {
            if (M.any_ms_is_greater(seq_ms[1], seq_ms[0]))
            {
                zoop_the_woop(1, 2.5f);
            }
            else
            {
                seq_ms[0] /= 2.5f;
                seq_ms[2] /= 2.5f;
                last_passed_check = do_timing_thing(1f);
                seq_ms[0] *= 2.5f;
                seq_ms[2] *= 2.5f;
            }
        }

        public void handle_ccsjjscc_timing_check(float now)
        {
            // translate over the values
            idk_ms[2] = seq_ms[0];
            idk_ms[1] = seq_ms[1];
            idk_ms[0] = seq_ms[2];

            // add the new value
            idk_ms[3] = now;

            // run 2 tests so we can keep a stricter cutoff
            // check 1
            idk_ms[1] /= 2.5f;
            idk_ms[2] /= 2.5f;

            do_other_timing_thing(1.25f);

            idk_ms[1] *= 2.5f;
            idk_ms[2] *= 2.5f;

            if (last_passed_check)
            {
                return;
            }

            // test again
            idk_ms[1] /= 3f;
            idk_ms[2] /= 3f;

            do_other_timing_thing(1.25f);

            idk_ms[1] *= 3f;
            idk_ms[2] *= 3f;
        }

        public void complete_seq()
        {
            if (nah_this_file_aint_for_real > 0)
            {
                max_thingy = nah_this_file_aint_for_real > max_thingy
                               ? nah_this_file_aint_for_real
                               : max_thingy;
            }
            nah_this_file_aint_for_real = 0;
        }

        public void bibblybop(meta_type _last_mt)
        {
            // see below
            if (_last_mt == meta_enigma)
            {
                moving_cv = (moving_cv + hi_im_a_float) / 2f;
            }
            else if (_last_mt == meta_meta_enigma)
            {
                moving_cv = (moving_cv + hi_im_a_float + hi_im_a_float) / 3f;
            }

            if (!last_passed_check)
            {
                complete_seq();
                return;
            }

            ++nah_this_file_aint_for_real;

            // if we are here and mt.last == meta enigma, we skipped 1 note before we identified a jumptrillable roll
            // continuation, if meta meta enigma, 2

            // borp it
            if (_last_mt == meta_enigma)
            {
                ++nah_this_file_aint_for_real;
            }

            // same but even more-er
            if (_last_mt == meta_meta_enigma)
            {
                nah_this_file_aint_for_real += 2;
            }
        }

        // C++ 5th header arg name is tc_ms (task suggested sc_ms_now); header wins.
        public void advance_sequencing(base_type bt,
                                       meta_type mt,
                                       meta_type _last_mt,
                                       float any_ms,
                                       float tc_ms)
        {
            // we will let ohjumps through here

            update_seq_ms(bt, any_ms, tc_ms);
            if (bt == base_single_jump || bt == base_jump_single)
            {
                return;
            }

            if (bt == base_jump_jump)
            {
                // its an actual jumpjack/jumptrill, don't bother with timing checks
                if (nah_this_file_aint_for_real > 0)
                {
                    bibblybop(_last_mt);
                }
                return;
            }

            // look for stuff thats jumptrillyable.. if that stuff... then leads into more stuff.. badonk it
            switch (mt)
            {
                case meta_acca:
                    // unlike wrjt we want to complete and reset on these
                    complete_seq();
                    break;
                case meta_cccccc:
                    handle_roll_timing_check();
                    bibblybop(_last_mt);
                    break;
                case meta_ccacc:
                    handle_ccacc_timing_check();
                    bibblybop(_last_mt);
                    break;
                case meta_ccsjjscc:
                case meta_ccsjjscc_inverted:
                    handle_ccsjjscc_timing_check(any_ms);
                    bibblybop(_last_mt);
                    break;
                case meta_type_init:
                case meta_enigma:
                    // this could yet be something we are interested in, but we don't know yet, so wait and see
                    break;
                case meta_meta_enigma:
                case meta_unknowable_enigma:
                    // it's been too long...
                    complete_seq();
                    break;
                default:
                    // assert(0)
                    break;
            }
        }

        public void update_seq_ms(base_type bt, float any_ms, float tc_ms)
        {
            seq_ms[0] = seq_ms[1]; // last_last
            seq_ms[1] = seq_ms[2]; // last

            // update now; for anchors, track tc_ms
            if (bt == base_single_single)
            {
                seq_ms[2] = tc_ms;
                // for base_left_right or base_right_left, track cc_ms
            }
            else
            {
                seq_ms[2] = any_ms;
            }
        }

        public void set_pmod(ItvHandInfo itvhi)
        {
            // check taps for _this_ interval, then window values, then _mw_max total
            if (itvhi.get_taps_nowi() == 0 || itvhi.get_taps_windowi(window) == 0 ||
                _mw_max.get_total_for_window(window) == 0)
            {
                pmod = M.neutral;
                return;
            }

            // really uncertain about using the total of _mw_max here, but that's what it was
            float zomg = itvhi.get_taps_windowf(window) /
                         _mw_max.get_total_for_windowf(window);

            pmod *= zomg;
            pmod = Math.Clamp(@base + M.fastsqrt(pmod), min_mod, max_mod);
        }

        public float op(ItvHandInfo itvhi)
        {
            max_thingy = nah_this_file_aint_for_real > max_thingy
                           ? nah_this_file_aint_for_real
                           : max_thingy;

            _mw_max.push(max_thingy);

            set_pmod(itvhi);

            interval_end();
            return pmod;
        }

        public void interval_end() { max_thingy = 0; }
    }

    // ======================= WideRangeJumptrill.h =======================

    /// Hand-Dependent PatternMod detecting Jumptrills. Lenient in a particular way so as to detect a roll as a
    /// jumptrill.
    public sealed class WideRangeJumptrillMod
    {
        // big brain stuff (namespace-scope constexpr in C++; only used here)
        public const float wrjt_cv_factor = 3f;

        public readonly CalcPatternMod _pmod = CalcPatternMod.WideRangeJumptrill;
        public const string name = "WideRangeJumptrillMod";

        // params
        public float window_param = 3f;

        public float min_mod = 0.25f;
        public float max_mod = 1f;

        public float cv_threshhold = 0.05f;

        public int window = 0;
        public CalcMovingWindow<int> _mw_jt = new CalcMovingWindow<int>();
        public int jt_counter = 0;

        public bool bro_is_this_file_for_real = false;
        public bool last_passed_check = false;

        public float pmod = M.neutral;

        public float[] seq_ms = { 0f, 0f, 0f };

        public void full_reset()
        {
            _mw_jt.zero();
            jt_counter = 0;
            Array.Fill(seq_ms, 0f);

            bro_is_this_file_for_real = false;
            last_passed_check = false;
            pmod = M.neutral;
        }

        public void setup()
        {
            window = Math.Clamp((int)window_param, 1, WindowConst.max_moving_window_size);
        }

        public bool check_last_mt(meta_type mt)
        {
            if (mt == meta_acca || mt == meta_ccacc || mt == meta_cccccc)
            {
                if (last_passed_check)
                {
                    return true;
                }
            }
            return false;
        }

        public void bibblybop(meta_type mt)
        {
            ++jt_counter;
            if (bro_is_this_file_for_real)
            {
                ++jt_counter;
            }
            if (check_last_mt(mt))
            {
                ++jt_counter;
                bro_is_this_file_for_real = true;
            }
        }

        // Header takes CalcMovingWindow<float>& (NON-const) and calls the mutate-then-restore timing checks -> pass the
        // live window reference and call the mutating checks (trusting the header over the general read-only guidance).
        public void advance_sequencing(base_type bt,
                                       meta_type mt,
                                       meta_type _last_mt,
                                       CalcMovingWindow<float> ms_any)
        {
            // ignore if we hit a jump
            if (bt == base_jump_jump || bt == base_single_jump)
            {
                return;
            }

            // look for stuff thats jumptrillyable.. if that stuff... then leads into more stuff.. badonk it
            switch (mt)
            {
                case meta_cccccc:
                    if ((last_passed_check = ms_any.roll_timing_check(
                           wrjt_cv_factor, cv_threshhold)))
                    {
                        bibblybop(_last_mt);
                        return;
                    }
                    break;
                case meta_ccacc:
                    if ((last_passed_check = ms_any.ccacc_timing_check(
                           wrjt_cv_factor, cv_threshhold)))
                    {
                        bibblybop(_last_mt);
                        return;
                    }
                    break;
                case meta_acca:
                    // don't bother adding if the ms values look benign
                    if ((last_passed_check = ms_any.acca_timing_check(
                           wrjt_cv_factor, cv_threshhold)))
                    {
                        bibblybop(_last_mt);
                        return;
                    }
                    break;
                default:
                    break;
            }

            bro_is_this_file_for_real = false;
        }

        public void set_pmod(ItvHandInfo itvhi)
        {
            // no taps, no jt
            if (itvhi.get_taps_windowi(window) == 0 ||
                _mw_jt.get_total_for_window(window) == 0)
            {
                pmod = M.neutral;
                return;
            }

            if (_mw_jt.get_total_for_window(window) < 20)
            {
                pmod = M.neutral;
                return;
            }

            pmod = itvhi.get_taps_windowf(window) /
                   _mw_jt.get_total_for_windowf(window) * 0.75f;

            pmod = Math.Clamp(pmod, min_mod, max_mod);
        }

        public float op(ItvHandInfo itvhi)
        {
            _mw_jt.push(jt_counter);

            set_pmod(itvhi);

            interval_end();
            return pmod;
        }

        public void interval_end()
        {
            // reset every interval when finished
            jt_counter = 0;
        }
    }

    // ======================= WideRangeJJ.h =======================

    /// Hand-Dependent PatternMod detecting jump jacks but considering flammyness. Collects the number of consecutive
    /// jumpjacks, largest sequence of them.
    public sealed class WideRangeJJMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.WideRangeJJ;
        public const string name = "WideRangeJJMod";

        // params
        public float window_param = 3f;
        // how many jumpjacks are required for the pmod to not be neutral (over the entire combined moving window)
        public float jj_required = 30f;

        public float min_mod = 0.25f;
        public float max_mod = 1f;
        public float total_scaler = 2.5f;
        public float cur_interval_tap_scaler = 1.2f;

        // ms apart for 2 taps to be considered a jumpjack (0.075=200bpm 16th, 0.050=300, 0.037=400, 0.020=750)
        public float ms_threshold = 0.065f;

        // add this much to the pmod before sqrt when below threshold
        public float calming_comp = 0.05f;

        // changes the direction and sharpness of the result curve as the jumpjack width is between 0 and ms_threshold
        public float diff_falloff_power = 6f;

        public int window = 0;

        // indices
        public int lc = 0;
        public int rc = 0;

        // moving window of "problems" (longest consecutive series of jumpjackyness)
        public CalcMovingWindow<float> _mw_max_problems = new CalcMovingWindow<float>();
        public float current_problems = 0f;
        public float max_interval_problems = 0f;

        public float pmod = M.neutral;

        // timestamps of notes in columns
        public float[] _left_times = new float[Calc.max_rows_for_single_interval];
        public float[] _right_times = new float[Calc.max_rows_for_single_interval];

        public void full_reset()
        {
            _mw_max_problems.zero();
            Array.Fill(_left_times, M.s_init);
            Array.Fill(_right_times, M.s_init);
            current_problems = 0f;
            max_interval_problems = 0f;
            lc = 0;
            rc = 0;

            pmod = M.neutral;
        }

        public void setup()
        {
            window = Math.Clamp((int)window_param, 1, WindowConst.max_moving_window_size);
        }

        public void check()
        {
            // check times in parallel; any times within the window count as the jumpishjack, then determine the degree
            int lindex = 0;
            int rindex = 0;
            bool jumpJacking = false;
            int failedLeft = 0;
            int failedRight = 0;
            while (lindex < lc && rindex < rc)
            {
                float l = _left_times[lindex];
                float r = _right_times[rindex];
                float diff = Math.Abs(l - r);

                if (diff < ms_threshold)
                {
                    lindex++;
                    rindex++;

                    // werent previously jumpjacking, restart at 0
                    if (!jumpJacking)
                    {
                        current_problems = 0f;
                    }

                    // given time_scaler = 1, diff of ms_threshold gives a value of 1 ("1 jumpjack"), but a flammy one is
                    // worth less; x=0 would be y=1 (a jump). using std::pow for accuracy here
                    float x = (float)Math.Pow(diff / Math.Max(ms_threshold, 0.00001f),
                                              diff_falloff_power);
                    float v = 1 + (x / (x - 2));
                    current_problems += v;
                    if (current_problems > max_interval_problems)
                    {
                        max_interval_problems = current_problems;
                    }
                    jumpJacking = true;
                }
                else
                {
                    // failed case: throw the oldest value and try again
                    if (l > r)
                    {
                        rindex++;
                        if (failedRight != 0)
                        {
                            jumpJacking = false;
                        }
                        failedRight = 1;
                    }
                    else if (r > l)
                    {
                        lindex++;
                        if (failedLeft != 0)
                        {
                            jumpJacking = false;
                        }
                        failedLeft = 1;
                    }
                    else
                    {
                        // this case exists to prevent infinite loops; should never happen unless bad params
                        lindex++;
                        rindex++;

                        if (failedLeft != 0 || failedRight != 0)
                        {
                            jumpJacking = false;
                        }
                        failedLeft = 1;
                        failedRight = 1;
                    }
                }
            }
        }

        // C++ 2nd header arg name is time_s (task suggested row_time); header wins.
        public void advance_sequencing(col_type ct, float time_s)
        {
            if (lc >= Calc.max_rows_for_single_interval ||
                rc >= Calc.max_rows_for_single_interval)
            {
                // completely impossible condition; checking for sanity and safety
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
            float taps_in_window =
              itvhi.get_taps_windowf(window) * cur_interval_tap_scaler;
            float problems_in_window =
              _mw_max_problems.get_total_for_windowf(window) * total_scaler;

            // no taps or below threshold, or actionable condition
            if (taps_in_window == 0f || problems_in_window < jj_required)
            {
                // when below threshold, the pmod will drift back to neutral
                pmod = M.fastsqrt(pmod + Math.Clamp(calming_comp, 0f, 1f));
            }
            else
            {
                pmod = taps_in_window / problems_in_window * 0.75f;
            }

            pmod = Math.Clamp(pmod, min_mod, max_mod);
        }

        public float op(ItvHandInfo itvhi)
        {
            check();
            _mw_max_problems.push(max_interval_problems);

            set_pmod(itvhi);

            interval_end();
            return pmod;
        }

        public void interval_end()
        {
            // reset every interval when finished
            current_problems = 0f;
            max_interval_problems = 0f;
            Array.Fill(_left_times, M.s_init);
            Array.Fill(_right_times, M.s_init);
            lc = 0;
            rc = 0;
        }
    }

    // ======================= WideRangeAnchor.h =======================

    /// Hand-Dependent PatternMod detecting anchors in general.
    public sealed class WideRangeAnchorMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.WideRangeAnchor;
        public const string name = "WideRangeAnchorMod";

        // params
        public float window_param = 2f;

        public float min_mod = 1f;
        public float max_mod = 1.1f;
        public float @base = 1f;

        public float diff_min = 4f;
        public float diff_max = 16f;
        public float scaler = 0.5f;

        public int window = 0;
        public int a = 0;
        public int b = 0;
        public int diff = 0;

        // set in setup
        public float divisor = 0f;
        public float pmod;                       // C++: = min_mod (set in ctor)

        public WideRangeAnchorMod()
        {
            pmod = min_mod;
        }

        public void full_reset()
        {
            interval_end();
            pmod = M.neutral;
        }

        public void setup()
        {
            // setup should be run after loading params from disk
            window = Math.Clamp((int)window_param, 1, WindowConst.max_moving_window_size);
            divisor = (float)((int)diff_max - (int)diff_min);

            // /0 lul
            if (divisor < 0.1f)
                divisor = 0.1f;
        }

        // C++ 1st header arg name is itvhi (task suggested ihi); header wins.
        public void set_pmod(ItvHandInfo itvhi, AnchorSequencer @as)
        {
            a = @as.get_max_for_window_and_col(col_left, window);
            b = @as.get_max_for_window_and_col(col_right, window);

            diff = M.diff_high_by_low(a, b);

            // nothing here
            if (a == 0 && b == 0)
            {
                pmod = M.neutral;
                return;
            }

            // set max mod if either is 0
            if (a == 0 || b == 0)
            {
                pmod = max_mod;
                return;
            }

            // difference won't matter
            if (diff <= (int)diff_min)
            {
                pmod = min_mod;
                return;
            }

            // would max anyway
            if (diff > (int)diff_max)
            {
                pmod = max_mod;
                return;
            }

            pmod =
              @base + (scaler * (((float)diff - diff_min) / divisor));
            pmod = Math.Clamp(pmod, min_mod, max_mod);
        }

        public float op(ItvHandInfo itvhi, AnchorSequencer @as)
        {
            set_pmod(itvhi, @as);

            interval_end();
            return pmod;
        }

        /* ok technically not necessary since we don't ever do anything before these values are updated, however,
         * supposing we do add anything like shortcut case handling prior, better this is already in place */
        public void interval_end()
        {
            diff = 0;
            a = 0;
            b = 0;
        }
    }
}
