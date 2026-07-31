// Faithful C# port of the hand-dependent basic/meta sequencing headers (v515):
//   Dependent/HD_BasicSequencing.h, Dependent/HD_MetaSequencing.h,
//   Dependent/HD_Sequencers/{GenericSequencing.h, OHJSequencing.h, RMSequencing.h, CJOHASequencing.h}.
// This file owns the shared enums col_type / base_type / meta_type / anch_status / rm_* and the free helper functions
// (packed into static class HD), plus the low-level per-finger sequencers and SequencerGeneral. C++ operator() becomes
// op(...) (or advance-style methods with distinct signatures); asserts are dropped (no-ops in the tuned NDEBUG build).
using System;
using System.Collections.Generic;
using static Mina.col_type;
using static Mina.base_type;
using static Mina.meta_type;
using static Mina.anch_status;
using static Mina.rm_behavior;
using static Mina.rm_status;
using static Mina.HD;
using static Mina.GenSeq;

namespace Mina
{
    /// GenericSequencing.h namespace-scope timing constants. Shared by the finger sequencers here and by the
    /// oversimplified jack sequencers in BaseDiff.cs, so they live in their own static holder for bare-name `using static`.
    public static class GenSeq
    {
        /****** Relevant to anchors ******/
        public const float anchor_spacing_buffer_ms = 10.0f;
        public const float anchor_speed_increase_cutoff_factor = 2.34f;
        public const int anchor_len_cap = 50;

        /****** Relevant to jacks ******/
        public const float jack_spacing_buffer_ms = 10.0f;
        public const float jack_speed_increase_cutoff_factor = 1.9f;
        public const int jack_len_cap = 4;

        /// ms to pass that definitely means an anchor has finished and started a new one
        public const float guaranteed_reset_buffer_ms = 1000.0f;
    }

    // ---------------- HD_BasicSequencing.h ----------------

    /// col_type is determined from serialized notedata. NOTE: kept as a real enum; index arrays with (int)ct.
    public enum col_type
    {
        col_left,
        col_right,
        col_ohjump,
        num_col_types,
        col_empty,
        col_init
    }

    /// basic pattern permutations based on two successive non-empty col types
    public enum base_type
    {
        base_left_right,
        base_right_left,
        base_jump_single,
        base_single_single,
        base_single_jump,
        base_jump_jump,
        num_base_types,
        base_type_init
    }

    // ---------------- HD_MetaSequencing.h ----------------

    /// meta patterns are formed by chains of base patterns
    public enum meta_type
    {
        meta_cccccc,
        meta_ccacc,
        meta_acca,
        meta_ccsjjscc,
        meta_ccsjjscc_inverted,
        meta_enigma,
        meta_meta_enigma,
        meta_unknowable_enigma,
        num_meta_types,
        meta_type_init
    }

    // ---------------- GenericSequencing.h status enums ----------------

    public enum anch_status
    {
        reset_too_slow,
        reset_too_fast,
        // _len > 2, otherwise we would be at the start of a file, or just reset due to being too fast/slow
        anchoring,
        anch_init
    }

    // ---------------- RMSequencing.h status enums ----------------

    public enum rm_behavior
    {
        rmb_off_tap_oh,
        rmb_off_tap_sh,
        rmb_anchor,
        rmb_jack, // only for the anchor col, not any col
        rmb_init
    }

    public enum rm_status
    {
        rm_inactive,
        rm_running
    }

    /// Hand-dependent free helper functions (HD_BasicSequencing.h + HD_MetaSequencing.h), plus the shared col constants
    /// and loop tables. Bare-name usage in other files via `using static Mina.HD;`.
    public static class HD
    {
        public const int num_cols_per_hand = 2;
        public static readonly col_type[] ct_loop = { col_left, col_right, col_ohjump };
        public static readonly col_type[] ct_loop_no_jumps = { col_left, col_right };

        /// Given notes for a certain hand, tell what kind of tap it is for this hand
        public static col_type determine_col_type(uint notes, uint hand_id)
        {
            uint shirt = notes & hand_id;
            if (shirt == 0)
                return col_empty;

            if (hand_id == 3)
            {
                if (shirt == 3) return col_ohjump;
                if (shirt == 1) return col_left;
                if (shirt == 2) return col_right;
            }
            else if (hand_id == 12)
            {
                if (shirt == 12) return col_ohjump;
                if (shirt == 8) return col_right;
                if (shirt == 4) return col_left;
            }
            // assert(0)
            return col_init;
        }

        /// inverting ct type for col_left or col_right only
        public static col_type invert_col(col_type col)
        {
            return col == col_left ? col_right : col_left;
        }

        /// Given two consecutive column types, what is the pattern forming?
        public static base_type determine_base_pattern_type(col_type now, col_type last)
        {
            if (last == col_init)
                return base_type_init;

            bool single_tap = now == col_left || now == col_right;
            if (last == col_ohjump)
            {
                if (single_tap)
                    return base_jump_single;
                // can't be anything else
                return base_jump_jump;
            }
            else if (!single_tap)
            {
                return base_single_jump;
                // if we are on left col _now_, we are right to left
            }
            else if (now == col_left && last == col_right)
            {
                return base_right_left;
            }
            else if (now == col_right && last == col_left)
            {
                return base_left_right;
            }
            else if (now == last)
            {
                // anchor/jack
                return base_single_single;
            }

            // makes no logical sense (assert(0))
            return base_type_init;
        }

        /// cross column hits are base_left_right / base_right_left
        public static bool is_cc_tap(base_type bt)
        {
            return bt == base_left_right || bt == base_right_left;
        }

        // ----- HD_MetaSequencing.h -----

        public static bool detecc_cccccc(base_type now, base_type last_last)
        {
            return now == last_last;
        }

        public static bool detecc_acca(base_type a, base_type b, base_type c)
        {
            // 1122, 2211, etc
            return a == base_single_single && is_cc_tap(b) && c == base_single_single;
        }

        public static bool detecc_sjjscc(base_type last, base_type last_last, base_type last_last_last)
        {
            // check last_last_last first, if it's not cc, throw it out
            if (!is_cc_tap(last_last_last))
                return false;

            // last is exiting the jump, last_last is entering it
            return last == base_jump_single && last_last == base_single_jump;
        }

        public static meta_type determine_meta_type(base_type now,
                                                    base_type last,
                                                    base_type last_last,
                                                    base_type last_last_last,
                                                    meta_type last_mt)
        {
            // this is either cccccc or ccacc
            if (is_cc_tap(now) && is_cc_tap(last_last))
            {
                if (detecc_cccccc(now, last_last))
                    return meta_cccccc; // 1212, 2121, etc
                return meta_ccacc;      // 1221, 2112, etc
            }

            if (detecc_acca(now, last, last_last))
                return meta_acca;

            // we need to be on a cc to have ccsjjscc
            if (is_cc_tap(now))
            {
                // this is a 5 row pattern, so we have to go deep
                if (detecc_sjjscc(last, last_last, last_last_last))
                {
                    if (now == last_last_last)
                        return meta_ccsjjscc;           // 12 [12] 12
                    return meta_ccsjjscc_inverted;      // 12 [12] 21
                }
            }

            if (last_mt == meta_enigma)
                return meta_meta_enigma;

            if (last_mt == meta_meta_enigma)
                return meta_unknowable_enigma;

            return meta_enigma;
        }
    }

    // ======================= GenericSequencing.h =======================

    /* important note about timing names: any_ms = this row to last row with a note (independent of column),
     * sc_ms = same column ms, cc_ms = cross column ms */

    /// Individual anchors, 2 objects per hand on 4k. Abstract base for Jack_Sequencing and Anchor_Sequencing.
    public abstract class Finger_Sequencing
    {
        // what column this anchor is on (set on startup by the sequencer)
        public col_type _ct = col_init;
        public anch_status _status = anch_init;

        // aside from the first note, _len is always at least 2
        public int _len = 1;
        /// same-column ms: time between now and previous tap
        public float _sc_ms = M.ms_init;
        /// highest ms value found in the anchor
        public float _max_ms = M.ms_init;
        /// difficulty at the len cap
        public float _len_cap_ms = M.ms_init;
        /// row_time of last note on this col
        public float _last = M.s_init;
        public float _start = M.s_init;

        public virtual void full_reset()
        {
            // never reset col_type
            _sc_ms = M.ms_init;
            _max_ms = M.ms_init;
            _last = M.s_init;
            _start = M.s_init;
            _len = 1;
            _status = anch_init;
            _len_cap_ms = M.ms_init;
        }

        /// based on the anchoring status, do an action
        public virtual void check_status()
        {
            switch (_status)
            {
                case reset_too_slow:
                case reset_too_fast:
                    _start = _last;
                    _len = 2;
                    break;
                case anchoring:
                    ++_len;
                    break;
                case anch_init:
                    // nothing to do
                    break;
            }
        }

        public abstract void set_status();
        /// C++ operator()(ct, now): sequence updating given the column type and time of current row
        public abstract void op(col_type ct, float now);
        /// returns an adjusted MS average value, not converted to nps
        public abstract float get_ms();
    }

    /// Individual jacks, rather than anchors, with more nuance.
    public sealed class Jack_Sequencing : Finger_Sequencing
    {
        public override void set_status()
        {
            if (_sc_ms > _max_ms + jack_spacing_buffer_ms)
                _status = reset_too_slow;
            else if (_sc_ms * jack_speed_increase_cutoff_factor < _max_ms)
                _status = reset_too_fast;
            else
                _status = anchoring;
        }

        public override void op(col_type ct, float now)
        {
            _sc_ms = M.ms_from(now, _last);

            if (ct == col_init)
            {
                _last = now;
                return;
            }

            set_status();
            check_status();

            _max_ms = _sc_ms;
            _last = now;
        }

        public override float get_ms()
        {
            // return whatever the last calculated value was after this point
            if (_len > jack_len_cap)
                return _len_cap_ms;

            const float avg_ms_mult = 1.5f;
            const float anchor_time_buffer_ms = 30.0f;
            const float min_ms = 95.0f;

            float total_ms = M.ms_from(_last, _start);
            float len = (float)(_len - 1);
            float avg_ms = total_ms / len;
            float adj_total_ms = total_ms + anchor_time_buffer_ms + avg_ms * avg_ms_mult;
            float ms = adj_total_ms / len;

            // BAD TEMP HACK LUL
            if (_len == 2)
            {
                ms *= 1.1f;
                ms = ms < 180.0f ? 180.0f : ms;
            }

            ms = ms < min_ms ? min_ms : ms;

            if (float.IsNaN(ms))
                ms = _max_ms;

            if (_len == jack_len_cap)
                _len_cap_ms = ms;

            return ms;
        }
    }

    /// Anchors; effectively the same as jacks, but handled slightly differently.
    public sealed class Anchor_Sequencing : Finger_Sequencing
    {
        public override void set_status()
        {
            if (_sc_ms > _max_ms + anchor_spacing_buffer_ms)
                _status = reset_too_slow;
            else if (_sc_ms * anchor_speed_increase_cutoff_factor < _max_ms)
                _status = reset_too_fast;
            else
                _status = anchoring;
        }

        public override void op(col_type ct, float now)
        {
            _sc_ms = M.ms_from(now, _last);

            if (ct == col_init)
            {
                _last = now;
                return;
            }

            set_status();
            check_status();

            _max_ms = _sc_ms;
            _last = now;
        }

        /// returns an adjusted MS average value, not converted to nps (((currently unused)))
        public override float get_ms()
        {
            if (_len > anchor_len_cap)
                return _len_cap_ms;

            const float avg_ms_mult = 1.0f;
            const float anchor_time_buffer_ms = 0.0f;
            const float min_ms = 0.0f;

            float total_ms = M.ms_from(_last, _start);
            float len = (float)(_len - 1);
            float avg_ms = total_ms / len;
            float adj_total_ms = total_ms + anchor_time_buffer_ms + avg_ms * avg_ms_mult;
            float ms = adj_total_ms / len;

            // BAD TEMP HACK LUL
            if (_len == 2)
            {
                ms *= 1.1f;
                ms = ms < 155.0f ? 155.0f : ms;
            }

            ms = ms < min_ms ? min_ms : ms;

            if (float.IsNaN(ms))
                ms = _max_ms;

            if (_len == anchor_len_cap)
                _len_cap_ms = ms;

            return ms;
        }
    }

    /// CURRENTLY ALSO BEING USED TO STORE THE OLD CC/TC MS VALUES IN MHI
    public sealed class AnchorSequencer
    {
        /// anchor sequencers for each finger
        public readonly Anchor_Sequencing[] anch = new Anchor_Sequencing[HD.num_cols_per_hand];
        /// jack sequencers for each finger
        public readonly Jack_Sequencing[] jack = new Jack_Sequencing[HD.num_cols_per_hand];

        /// information for each column to store in the movingwindow_max, this interval
        public readonly int[] max_seen = new int[HD.num_cols_per_hand];
        /// track windows of highest anchor per col seen during an interval
        public readonly CalcMovingWindow<int>[] _mw_max = new CalcMovingWindow<int>[HD.num_cols_per_hand];

        public AnchorSequencer()
        {
            for (int i = 0; i < HD.num_cols_per_hand; i++)
            {
                anch[i] = new Anchor_Sequencing();
                jack[i] = new Jack_Sequencing();
                _mw_max[i] = new CalcMovingWindow<int>();
            }
            foreach (var c in ct_loop_no_jumps)
            {
                anch[(int)c]._ct = c;
                jack[(int)c]._ct = c;
            }
            full_reset();
        }

        public void full_reset()
        {
            for (int i = 0; i < HD.num_cols_per_hand; i++) max_seen[i] = 0;

            foreach (var c in ct_loop_no_jumps)
            {
                anch[(int)c].full_reset();
                jack[(int)c].full_reset();
                _mw_max[(int)c].zero();
            }
        }

        /// C++ operator()(ct, row_time): derives sc_ms, which sequencer general will pull for its moving window
        public void op(col_type ct, float row_time)
        {
            if (ct == col_left || ct == col_right)
            {
                col_type opposite_col = ct == col_left ? col_right : col_left;
                anch[(int)ct].op(ct, row_time);
                jack[(int)ct].op(ct, row_time);

                max_seen[(int)ct] = anch[(int)ct]._len > max_seen[(int)ct]
                                      ? anch[(int)ct]._len
                                      : max_seen[(int)ct];

                // reset the other column if necessary (particularly for jacks)
                if (M.ms_from(row_time, anch[(int)opposite_col]._last) > guaranteed_reset_buffer_ms)
                {
                    anch[(int)opposite_col].full_reset();
                    jack[(int)opposite_col].full_reset();
                }
            }
            else if (ct == col_ohjump)
            {
                foreach (var c in ct_loop_no_jumps)
                {
                    anch[(int)c].op(c, row_time);
                    jack[(int)c].op(c, row_time);

                    max_seen[(int)c] = anch[(int)c]._len > max_seen[(int)c]
                                         ? anch[(int)c]._len
                                         : max_seen[(int)c];
                }
            }
        }

        public int get_max_for_window_and_col(col_type ct, int window)
        {
            return _mw_max[(int)ct].get_max_for_window(window);
        }

        public void interval_end()
        {
            foreach (var c in ct_loop_no_jumps)
            {
                _mw_max[(int)c].push(max_seen[(int)c]);
                max_seen[(int)c] = 0;
            }
        }

        public float get_lowest_anchor_ms()
        {
            return Math.Min(anch[(int)col_left].get_ms(), anch[(int)col_right].get_ms());
        }

        public float get_lowest_jack_ms()
        {
            return Math.Min(jack[(int)col_left].get_ms(), jack[(int)col_right].get_ms());
        }
    }

    /// keep timing stuff here instead of in mhi, use mhi exclusively for pattern detection
    public sealed class SequencerGeneral
    {
        /// moving window of ms_any values (row with something on this hand to last such row)
        public readonly CalcMovingWindow<float> _mw_any_ms = new CalcMovingWindow<float>();
        /// moving window of cc_ms values
        public readonly CalcMovingWindow<float> _mw_cc_ms = new CalcMovingWindow<float>();
        /// moving window of sc_ms values
        public readonly CalcMovingWindow<float>[] _mw_sc_ms = new CalcMovingWindow<float>[HD.num_cols_per_hand];

        /// basic sequencers
        public readonly AnchorSequencer _as = new AnchorSequencer();

        public SequencerGeneral()
        {
            for (int i = 0; i < HD.num_cols_per_hand; i++)
                _mw_sc_ms[i] = new CalcMovingWindow<float>();
        }

        /// sc_ms is the time from the current note to the last note in the same column
        public void set_sc_ms(col_type ct)
        {
            if (ct == col_left || ct == col_right)
                _mw_sc_ms[(int)ct].push(_as.anch[(int)ct]._sc_ms);

            if (ct == col_ohjump)
                foreach (var c in ct_loop_no_jumps)
                    _mw_sc_ms[(int)c].push(_as.anch[(int)c]._sc_ms);
        }

        /// cc_ms is the time from the current note to the last note in the cross column
        public void set_cc_ms(col_type ct, float row_time)
        {
            if (ct == col_left || ct == col_right)
                _mw_cc_ms.push(M.ms_from(row_time, _as.anch[(int)invert_col(ct)]._last));

            if (ct == col_ohjump)
                _mw_cc_ms.push(get_sc_ms_now(col_ohjump));
        }

        public void advance_sequencing(col_type ct, float row_time, float ms_now)
        {
            if (ct != col_ohjump)
            {
                bool reset_sequencer = M.ms_from(row_time, _as.anch[(int)ct]._last) > guaranteed_reset_buffer_ms;
                if (reset_sequencer)
                {
                    _as.anch[(int)ct].full_reset();
                    _as.jack[(int)ct].full_reset();
                    _mw_sc_ms[(int)ct].fill(M.ms_init);
                    _mw_cc_ms.fill(M.ms_init);
                    _mw_any_ms.fill(M.ms_init);
                }
            }

            // update sequencers
            _as.op(ct, row_time);

            // sc ms needs to be set first, cc ms will reference it for ohjumps
            set_sc_ms(ct);
            set_cc_ms(ct, row_time);
            _mw_any_ms.push(ms_now);
        }

        public float get_sc_ms_now(col_type ct, bool lower = true)
        {
            if (ct == col_init)
                return M.ms_init;

            // if ohjump, grab the smaller value by default
            if (ct == col_ohjump)
            {
                if (lower)
                {
                    return _mw_sc_ms[(int)col_left].get_now() < _mw_sc_ms[(int)col_right].get_now()
                             ? _mw_sc_ms[(int)col_left].get_now()
                             : _mw_sc_ms[(int)col_right].get_now();
                }
                return _mw_sc_ms[(int)col_left].get_now() > _mw_sc_ms[(int)col_right].get_now()
                         ? _mw_sc_ms[(int)col_left].get_now()
                         : _mw_sc_ms[(int)col_right].get_now();
            }

            return _mw_sc_ms[(int)ct].get_now();
        }

        /// C++ returns by value; Clone preserves that (pattern mods run mutating timing checks on the result)
        public CalcMovingWindow<float> get_mw_sc_ms(col_type ct)
        {
            if (ct == col_left || ct == col_ohjump)
                return _mw_sc_ms[(int)col_left].Clone();
            return _mw_sc_ms[(int)col_right].Clone();
        }

        public float get_any_ms_now() { return _mw_any_ms.get_now(); }
        public float get_cc_ms_now() { return _mw_cc_ms.get_now(); }

        public void interval_end() { _as.interval_end(); }

        public void full_reset()
        {
            _mw_any_ms.fill(M.ms_init);
            _mw_cc_ms.fill(M.ms_init);

            foreach (var c in ct_loop_no_jumps)
                _mw_sc_ms[(int)c].fill(M.ms_init);

            _as.full_reset();
        }
    }

    // ======================= OHJSequencing.h =======================

    /// one hand jump sequencer, used by ohjmod
    public sealed class OHJ_Sequencer
    {
        // COUNT TAPS IN JUMPS
        public int cur_seq_taps = 0;
        public int max_seq_taps = 0;

        public int get_largest_seq_taps()
        {
            return cur_seq_taps > max_seq_taps ? cur_seq_taps : max_seq_taps;
        }

        public void complete_seq()
        {
            max_seq_taps = get_largest_seq_taps();
            cur_seq_taps = 0;
        }

        public void zero()
        {
            cur_seq_taps = 0;
            max_seq_taps = 0;
        }

        /// C++ operator()(ct, bt)
        public void op(col_type ct, base_type bt)
        {
            if (cur_seq_taps == 0)
            {
                if (ct != col_ohjump)
                    return;
                // allow sequences of 1 by starting any time we hit an ohjump
                cur_seq_taps += 2;
            }

            switch (bt)
            {
                case base_jump_jump:
                    cur_seq_taps += 2;
                    break;
                case base_jump_single:
                    // just came out of a jump seq, wait to see what happens
                    break;
                case base_left_right:
                case base_right_left:
                    if (cur_seq_taps == 2)
                        cur_seq_taps -= 1;
                    else
                        cur_seq_taps -= 3;
                    complete_seq();
                    break;
                case base_single_single:
                    complete_seq();
                    break;
                case base_single_jump:
                    complete_seq();
                    break;
                case base_type_init:
                    // do nothing, we don't have enough info yet
                    break;
                default:
                    break;
            }
        }
    }

    // ======================= RMSequencing.h =======================

    /// Maintains the information pertaining to a single runningman (for one anchor finger).
    public sealed class RunningMan
    {
        public int ran_taps = 0;
        public int _len = 0;
        public int off_taps = 0;
        public int off_len = 0;
        public int off_taps_sh = 0;
        public int oht_taps = 0;
        public int oht_len = 0;
        public int ot_sh_len = 0;
        public int jack_taps = 0;
        public int jack_len = 0;
        public int anch_len = 0;

        public void full_reset()
        {
            // don't touch anchor col
            _len = 0;
            off_taps_sh = 0;
            off_taps = 0;
            off_len = 0;
            oht_taps = 0;
            oht_len = 0;
            jack_taps = 0;
            jack_len = 0;
            anch_len = 0;
        }

        public void add_off_tap_sh()
        {
            ++off_taps_sh;
            ++ot_sh_len;
            add_off_tap();
        }

        public void add_off_tap()
        {
            ++off_len;
            ++off_taps;
            ++ran_taps;
        }

        public void add_oht_tap()
        {
            ++oht_len;
            ++oht_taps;
        }

        public void add_anchor_tap()
        {
            ++_len;
            ++anch_len;
            ++ran_taps;
        }

        public void add_jack_tap()
        {
            ++jack_len;
            ++jack_taps;
            ++ran_taps;
        }

        public void end_jack_and_anch_runs()
        {
            end_anch_run();
            end_jack_run();
        }

        public void end_anch_run() { anch_len = 0; }
        public void end_jack_run() { jack_len = 0; }

        public void end_off_tap_run()
        {
            off_len = 0;
            ot_sh_len = 0;
        }

        public void restart()
        {
            off_taps_sh = 0;
            off_taps = 0;
            off_len = 0;
            oht_taps = 0;
            oht_len = 0;
            jack_taps = 0;
            jack_len = 0;
            anch_len = 0;
        }

        public float get_off_tap_prop()
        {
            if (off_taps == 0)
                return 0.0f;
            return (float)_len / (float)off_taps;
        }

        public float get_offhand_tap_prop()
        {
            if (off_taps - off_taps_sh <= 0)
                return 0.0f;
            return (float)(off_taps - off_taps_sh) / (float)_len;
        }

        public float get_off_tap_same_prop()
        {
            if (off_taps_sh == 0)
                return 0.0f;
            return (float)off_taps_sh / (float)_len;
        }
    }

    /// used by ranmen mod, for ranmen sequencing
    public sealed class RM_Sequencer
    {
        // this is 1.06 * (4k tech base scaler)
        public const float rma_diff_scaler = 1.06f * 1.06f;

        // params, loaded by runningman and then set from there
        public int max_oht_len = 0;
        public int max_off_len = 0;
        public int max_ot_sh_len = 0;
        public int max_burst_len = 0;
        public int max_jack_len = 0;
        public int max_anchor_len = 0;

        public void set_params(float moht, float moff, float motsh, float mburst, float mjack, float manch)
        {
            max_oht_len = (int)moht;
            max_off_len = (int)moff;
            max_ot_sh_len = (int)motsh;
            max_burst_len = (int)mburst;
            max_jack_len = (int)mjack;
            max_anchor_len = (int)manch;
        }

        public col_type _ct = col_init;
        public rm_status _status = rm_inactive;
        public rm_behavior _rmb = rmb_init;
        public rm_behavior _last_rmb = rmb_init;

        public readonly RunningMan _rm = new RunningMan();

        public bool is_bursting = false;
        public bool had_burst = false;

        public float last_anchor_time = M.s_init;
        public float _start = M.s_init;

        public void full_reset()
        {
            _status = rm_inactive;
            _rmb = rmb_init;
            _last_rmb = rmb_init;

            _start = M.s_init;
            last_anchor_time = M.s_init;

            is_bursting = false;
            had_burst = false;

            _rm.full_reset();
        }

        public void restart(Anchor_Sequencing @as)
        {
            _start = @as._last - (@as._sc_ms / 1000.0f);
            last_anchor_time = @as._last;
            _rm._len = 2;
            _rm.ran_taps = 2;

            is_bursting = false;
            had_burst = false;
            _rm.restart();

            handle_last_rmb();
        }

        public bool should_restart() { return _last_rmb == rmb_off_tap_sh; }

        public void end_off_tap_run()
        {
            if (is_bursting)
            {
                is_bursting = false;
                had_burst = true;
            }
            _rm.end_off_tap_run();
        }

        public void handle_last_rmb()
        {
            switch (_last_rmb)
            {
                case rmb_off_tap_sh:
                    _rm.add_off_tap_sh();
                    break;
                default:
                    break;
            }
        }

        public bool off_len_exceeds_max()
        {
            if (_rm.off_len <= max_off_len)
                return false;

            if (had_burst || _rm.off_len > max_burst_len)
                return true;

            is_bursting = true;
            return false;
        }

        public bool ot_sh_len_exceeds_max() { return _rm.ot_sh_len > max_ot_sh_len; }
        public bool jack_len_exceeds_max() { return _rm.jack_len > max_jack_len; }
        public bool anch_len_exceeds_max() { return _rm.anch_len > max_anchor_len; }
        public bool oht_len_exceeds_max() { return _rm.oht_len > max_oht_len; }

        public void handle_anchor_behavior(Anchor_Sequencing @as)
        {
            if (anch_len_exceeds_max())
            {
                _status = rm_inactive;
                return;
            }

            switch (@as._status)
            {
                case reset_too_slow:
                case reset_too_fast:
                    if (should_restart())
                        restart(@as);
                    else
                        _status = rm_inactive;
                    break;
                case anchoring:
                    _rm.add_anchor_tap();
                    _rm.end_off_tap_run();
                    goto case anch_init; // C++ fallthrough
                case anch_init:
                    // do nothing
                    break;
            }
        }

        public void handle_off_tap_sh_behavior()
        {
            _rm.add_off_tap_sh();
            if (off_len_exceeds_max() || ot_sh_len_exceeds_max())
            {
                _status = rm_inactive;
            }
            else
            {
                _rm.end_jack_and_anch_runs();
            }
        }

        public void handle_off_tap_oh_behavior()
        {
            _rm.add_off_tap();
            if (off_len_exceeds_max())
            {
                _status = rm_inactive;
            }
            else
            {
                _rm.end_jack_run();
            }
        }

        public void handle_jack_behavior()
        {
            _rm.add_jack_tap();
            if (jack_len_exceeds_max())
            {
                _status = rm_inactive;
            }
            else
            {
                end_off_tap_run();
            }
        }

        public void handle_oht_behavior(col_type ct)
        {
            if (ct != _ct)
            {
                if (_rm.oht_len == 0)
                    _rm.add_oht_tap();

                _rm.add_oht_tap();
                if (oht_len_exceeds_max())
                    _status = rm_inactive;
            }
        }

        public void handle_rmb(Anchor_Sequencing @as)
        {
            switch (_rmb)
            {
                case rmb_off_tap_oh:
                    // should only ever be called from advance_off_hand_sequencing (assert(0))
                    break;
                case rmb_off_tap_sh:
                    handle_off_tap_sh_behavior();
                    break;
                case rmb_anchor:
                    handle_anchor_behavior(@as);
                    break;
                case rmb_jack:
                    handle_jack_behavior();
                    break;
                default:
                    break;
            }
        }

        public void advance_off_hand_sequencing()
        {
            handle_off_tap_oh_behavior();
            _last_rmb = rmb_off_tap_oh;
        }

        /// C++ operator()(ct, bt, mt, as)
        public void op(col_type ct, base_type bt, meta_type mt, Anchor_Sequencing @as)
        {
            if (mt == meta_cccccc)
                handle_oht_behavior(ct);

            last_anchor_time = @as._last;

            // if anchor_sequencing passes forward s_init, reset everything
            if (last_anchor_time == M.s_init)
            {
                full_reset();
                return;
            }

            switch (bt)
            {
                case base_left_right:
                case base_right_left:
                case base_single_single:
                    if (_ct == ct)
                        _rmb = rmb_anchor;
                    else
                        _rmb = rmb_off_tap_sh;
                    break;
                case base_jump_single:
                    if (_last_rmb == rmb_off_tap_oh)
                    {
                        if (_ct == ct)
                            _rmb = rmb_anchor;
                        else
                            _rmb = rmb_off_tap_sh;
                    }
                    else
                    {
                        _rmb = rmb_jack;
                    }
                    break;
                case base_single_jump:
                case base_jump_jump:
                    if (_last_rmb == rmb_off_tap_oh)
                        _rmb = rmb_anchor;
                    else
                        _rmb = rmb_jack;
                    break;
                case base_type_init:
                    return;
                default:
                    break;
            }

            if (_status == rm_inactive)
            {
                if (_rmb == rmb_anchor && _last_rmb == rmb_off_tap_sh)
                {
                    _status = rm_running;
                    restart(@as);
                }
            }
            else
            {
                handle_rmb(@as);
            }

            _last_rmb = _rmb;
        }

        public float get_difficulty()
        {
            if (_status == rm_inactive || _rm._len < 3)
                return 1.0f;

            float flool = M.ms_from(last_anchor_time, _start);

            float len = (float)_rm._len;
            float len_1 = (float)(_rm._len - 1);

            float pule = (flool / len_1) * (len / len_1);
            float drool = M.ms_to_scaled_nps(pule) * rma_diff_scaler;
            return drool;
        }
    }

    // ======================= CJOHASequencing.h =======================

    /// Looks for anchors and chains (chordjack one-hand anchors).
    public sealed class CJ_OHAnchor_Sequencer
    {
        /// multiply the time between previous jacks by this; if the gap is bigger it is a new chain/anchor
        public const float chain_slowdown_scale_threshold = 1.99f;

        public int chain_swaps = 0;
        public int not_swaps = 0;
        public int cur_len = 0;
        public int cur_anchor_len = 0;
        public bool chain_swapping = false;

        public int max_chain_swaps = 0;
        public int max_not_swaps = 0;
        public int max_total_len = 0;
        public int max_anchor_len = 0;
        public col_type anchor_col = col_init;
        public float last_ms = M.ms_init;

        public void zero()
        {
            reset_max_seq();
            reset_seq();
            last_ms = M.ms_init;
        }

        public void reset_seq()
        {
            chain_swaps = 0;
            not_swaps = 0;
            cur_len = 0;
            cur_anchor_len = 0;

            chain_swapping = false;
            anchor_col = col_init;
            // not resetting ms time here
        }

        public void reset_max_seq()
        {
            max_chain_swaps = 0;
            max_not_swaps = 0;
            max_total_len = 0;
            max_anchor_len = 0;
        }

        public void update_max_seq()
        {
            max_chain_swaps = get_max_chain_swaps();
            max_not_swaps = get_max_not_swaps();
            max_total_len = get_max_total_len();
            max_anchor_len = get_max_anchor_len();
        }

        public void complete_seq()
        {
            // if a chain swap was in progress we ended on a jump; fail a swap
            if (chain_swapping)
                swap_failed();

            if (chain_swaps != 0 || not_swaps != 0)
                update_max_seq();

            reset_seq();
        }

        public void chain_swap()
        {
            chain_swapping = false;
            max_anchor_len = get_max_anchor_len();
            cur_anchor_len = 0;
            chain_swaps++;
        }

        public void swap_failed()
        {
            chain_swapping = false;
            not_swaps++;
            // anchor continues ...
        }

        /// C++ operator()(ct, bt, last_ct, any_ms)
        public void op(col_type ct, base_type bt, col_type last_ct, float any_ms)
        {
            if (last_ms * chain_slowdown_scale_threshold < any_ms)
                complete_seq();
            last_ms = any_ms;

            switch (bt)
            {
                case base_left_right:
                case base_right_left:
                    complete_seq();
                    break;
                case base_jump_jump:
                    cur_len++;
                    cur_anchor_len++;
                    if (anchor_col != col_init)
                        chain_swapping = true;
                    break;
                case base_single_single:
                    anchor_col = ct;
                    cur_len++;
                    cur_anchor_len++;
                    break;
                case base_single_jump:
                    anchor_col = last_ct; // set to the column we used to be on
                    cur_len++;
                    chain_swapping = true;
                    break;
                case base_jump_single:
                    cur_len++;
                    cur_anchor_len++;

                    if ((anchor_col == col_left && ct == col_right) ||
                        (anchor_col == col_right && ct == col_left))
                    {
                        if (chain_swapping)
                            chain_swap();
                    }
                    else
                    {
                        if (chain_swapping)
                            swap_failed();
                    }
                    anchor_col = ct;
                    break;
                case base_type_init:
                    // no info
                    break;
                default:
                    break;
            }
        }

        public int get_max_total_len() { return cur_len > max_total_len ? cur_len : max_total_len; }
        public int get_max_anchor_len() { return cur_anchor_len > max_anchor_len ? cur_anchor_len : max_anchor_len; }
        public int get_max_chain_swaps() { return chain_swaps > max_chain_swaps ? chain_swaps : max_chain_swaps; }
        public int get_max_not_swaps() { return not_swaps > max_not_swaps ? not_swaps : max_not_swaps; }
    }
}
