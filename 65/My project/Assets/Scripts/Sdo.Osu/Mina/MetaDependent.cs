// Faithful C# port of the hand-dependent meta/interval headers (v515):
//   Dependent/MetaHandInfo.h, Dependent/IntervalHandInfo.h, Dependent/MetaIntervalHandInfo.h,
//   Dependent/MetaIntervalGenericHandInfo.h.
// generic_base_type + its free helpers (static class HDG) live here since they are defined in
// MetaIntervalGenericHandInfo.h. C++ operator() becomes op(...); asserts are dropped.
using System;
using static Mina.col_type;
using static Mina.base_type;
using static Mina.meta_type;
using static Mina.generic_base_type;
using static Mina.HD;
using static Mina.HDG;

namespace Mina
{
    // ======================= MetaHandInfo.h =======================

    /// row by row sequencer that constructs basic and advanced hand based patterns, generated per _hand_
    public sealed class metaHandInfo
    {
        /// col
        public col_type _ct = col_init;
        public col_type _last_ct = col_init;

        /// type of cross column hit
        public base_type _bt = base_type_init;
        public base_type _last_bt = base_type_init;

        /// needed for the BIGGEST BRAIN PLAYS
        public base_type last_last_bt = base_type_init;

        // whomst've
        public meta_type _mt = meta_type_init;
        public meta_type _last_mt = meta_type_init;

        /// number of offhand taps before this row
        public int offhand_taps = 0;
        public int offhand_ohjumps = 0;

        public Calc _calc;

        public metaHandInfo(Calc calc)
        {
            _calc = calc;
        }

        /// reset everything between hands so trailing values don't carry over
        public void full_reset()
        {
            _ct = col_init;
            _last_ct = col_init;

            _bt = base_type_init;
            _last_bt = base_type_init;
            last_last_bt = base_type_init;

            _mt = meta_type_init;
            _last_mt = meta_type_init;
        }

        /// C++ operator()(last, ct)
        public void op(metaHandInfo last, col_type ct)
        {
            // this should never ever be called on col_empty (assert)
            _ct = ct;

            // set older values
            _last_ct = last._ct;
            last_last_bt = last._last_bt;
            _last_bt = last._bt;
            _last_mt = last._mt;

            // update this hand's cc type for this row
            _bt = determine_base_pattern_type(ct, _last_ct);

            // now look for more complex patterns
            _mt = determine_meta_type(
              _bt, _last_bt, last_last_bt, last.last_last_bt, _last_mt);
        }
    }

    // ======================= IntervalHandInfo.h =======================

    /// accumulates hand specific info across an interval as it's processed by row
    public sealed class ItvHandInfo
    {
        private int[] _col_taps = new int[(int)num_col_types];

        // switch to keeping generic moving windows here
        private CalcMovingWindow<int>[] _mw_col_taps = new CalcMovingWindow<int>[(int)num_col_types];
        private CalcMovingWindow<int> _mw_hand_taps = new CalcMovingWindow<int>();

        public ItvHandInfo()
        {
            for (int i = 0; i < (int)num_col_types; i++)
                _mw_col_taps[i] = new CalcMovingWindow<int>();
        }

        public void set_col_taps(col_type col)
        {
            switch (col)
            {
                case col_left:
                case col_right:
                    ++_col_taps[(int)col];
                    break;
                case col_ohjump:
                    ++_col_taps[(int)col_left];
                    ++_col_taps[(int)col_right];
                    _col_taps[(int)col] += 2;
                    break;
                default:
                    break;
            }
        }

        /// handle end of interval behavior here
        public void interval_end()
        {
            // update interval mw for hand taps
            _mw_hand_taps.push(_col_taps[(int)col_left] + _col_taps[(int)col_right]);

            // update interval mws for col taps
            foreach (var ct in ct_loop)
                _mw_col_taps[(int)ct].push(_col_taps[(int)ct]);

            // reset taps per col on this hand
            for (int i = 0; i < _col_taps.Length; i++) _col_taps[i] = 0;
        }

        /// complete reset for when we swap hands
        public void zero()
        {
            for (int i = 0; i < _col_taps.Length; i++) _col_taps[i] = 0;

            foreach (var mw in _mw_col_taps)
                mw.zero();
            _mw_hand_taps.zero();
        }

        /// access functions for col tap counts
        public int get_col_taps_nowi(col_type ct)
        {
            return _mw_col_taps[(int)ct].get_now();
        }

        public float get_col_taps_nowf(col_type ct)
        {
            return (float)_mw_col_taps[(int)ct].get_now();
        }

        public int get_col_taps_windowi(col_type ct, int window)
        {
            return _mw_col_taps[(int)ct].get_total_for_window(window);
        }

        public float get_col_taps_windowf(col_type ct, int window)
        {
            return (float)_mw_col_taps[(int)ct].get_total_for_window(window);
        }

        /// col operations
        public bool cols_equal_now()
        {
            return get_col_taps_nowi(col_left) == get_col_taps_nowi(col_right);
        }

        public bool cols_equal_window(int window)
        {
            return get_col_taps_windowi(col_left, window) ==
                   get_col_taps_windowi(col_right, window);
        }

        public float get_col_prop_high_by_low()
        {
            return M.div_high_by_low(get_col_taps_nowf(col_left),
                                     get_col_taps_nowf(col_right));
        }

        public float get_col_prop_low_by_high()
        {
            return M.div_low_by_high(get_col_taps_nowf(col_left),
                                     get_col_taps_nowf(col_right));
        }

        public float get_col_prop_high_by_low_window(int window)
        {
            return M.div_high_by_low(get_col_taps_windowf(col_left, window),
                                     get_col_taps_windowf(col_right, window));
        }

        public float get_col_prop_low_by_high_window(int window)
        {
            return M.div_low_by_high(get_col_taps_windowf(col_left, window),
                                     get_col_taps_windowf(col_right, window));
        }

        public int get_col_diff_high_by_low()
        {
            return M.diff_high_by_low(get_col_taps_nowi(col_left),
                                      get_col_taps_nowi(col_right));
        }

        public int get_col_diff_high_by_low_window(int window)
        {
            return M.diff_high_by_low(get_col_taps_windowi(col_left, window),
                                      get_col_taps_windowi(col_right, window));
        }

        /// access functions for hand tap counts
        public int get_taps_nowi()
        {
            return _mw_hand_taps.get_now();
        }

        public float get_taps_nowf()
        {
            return (float)_mw_hand_taps.get_now();
        }

        public int get_taps_windowi(int window)
        {
            return _mw_hand_taps.get_total_for_window(window);
        }

        public float get_taps_windowf(int window)
        {
            return (float)_mw_hand_taps.get_total_for_window(window);
        }
    }

    // ======================= MetaIntervalHandInfo.h =======================

    public sealed class metaItvHandInfo
    {
        public ItvHandInfo _itvhi = new ItvHandInfo();

        /// handle end of interval
        public void interval_end()
        {
            for (int i = 0; i < _base_types.Length; i++) _base_types[i] = 0;
            for (int i = 0; i < _meta_types.Length; i++) _meta_types[i] = 0;

            _itvhi.interval_end();
        }

        /// zero everything out for end of hand loop
        public void zero()
        {
            for (int i = 0; i < _base_types.Length; i++) _base_types[i] = 0;
            for (int i = 0; i < _meta_types.Length; i++) _meta_types[i] = 0;

            _itvhi.zero();
        }

        public int[] _base_types = new int[(int)num_base_types];
        public int[] _meta_types = new int[(int)num_meta_types];
    }

    // ======================= MetaIntervalGenericHandInfo.h =======================

    public enum generic_base_type
    {
        gbase_hard_trill,          // '12' in 6k (ring-middle or pinky-ring)
        gbase_easy_trill,          // '23' in 6k (index-middle or opposite fingers of hand)
        gbase_single_single,       // jack on any column alone
        gbase_single_chord_jack,   // single into a chord forming a jack
        gbase_single_chord_trill,  // single into a chord forming a trill
        gbase_chord_chord_jack,    // 2 chords forming a jack
        gbase_chord_chord_trill,   // 2 chords forming no jack
        gbase_chord_weird,         // 2 chords with a jack and a trill
        num_gbase_types,
        gbase_type_init
    }

    /// Free helpers from MetaIntervalGenericHandInfo.h. Bare-name usage via `using static Mina.HDG;`.
    public static class HDG
    {
        // return the mask of notes that would be hard to hit given the notes that were just hit
        public static uint hard_trill_mask(uint last_notes, uint hand_mask)
        {
            int keycount_on_hand = M.column_count(hand_mask);
            bool is_left_hand = (hand_mask & 0x1) > 0u;
            uint result_mask = 0x0;
            if (keycount_on_hand <= 2 || keycount_on_hand > 5)
            {
                // for 2 columns on this hand, it's always "easy", and for more than 5... dont care
                return result_mask;
            }

            // 3 columns
            if (keycount_on_hand == 3)
            {
                result_mask = is_left_hand ? 0x3u : 0x6u;
            }
            // 4 columns
            else if (keycount_on_hand == 4)
            {
                result_mask = 0x6u;
            }
            // 5 columns
            else
            {
                result_mask = is_left_hand ? 0xCu : 0x6u;
            }

            if ((result_mask & last_notes) > 0u)
                return result_mask & ~last_notes;
            else
                return 0x0;
        }

        public static generic_base_type determine_generic_base_pattern_type(uint notes_now,
                                                                            uint last_notes,
                                                                            uint hand_mask)
        {
            if (last_notes == 0u || notes_now == 0u)
                return gbase_type_init;

            bool now_single = M.is_only_1_bit(notes_now);
            bool last_single = M.is_only_1_bit(last_notes);
            bool has_jack = (notes_now & last_notes) > 0u;

            if (now_single && last_single)
            {
                if (has_jack)
                    return gbase_single_single;
                if ((notes_now & hard_trill_mask(last_notes, hand_mask)) > 0u)
                    return gbase_hard_trill;
                return gbase_easy_trill;
            }

            // only one single
            if (now_single || last_single)
            {
                if (has_jack)
                    return gbase_single_chord_jack;
                else
                    return gbase_single_chord_trill;
            }

            // at this point only consecutive chords are possible
            if (has_jack)
            {
                bool has_trill = (notes_now ^ last_notes) > 0u;
                if (has_trill)
                    return gbase_chord_weird; // [12][13]...
                else
                    return gbase_chord_chord_jack;
            }
            else
            {
                return gbase_chord_chord_trill;
            }
        }

        public static bool is_bracket(uint notes_row, uint last_notes, uint lastlast_notes)
        {
            // firstsec and secthird rows must form some trill; firstthird rows must "jack"
            return (notes_row ^ last_notes) > 0 && (last_notes ^ lastlast_notes) > 0 &&
                   (notes_row & lastlast_notes) > 0;
        }

        public static bool basetype_stops_bracket(generic_base_type bt)
        {
            switch (bt)
            {
                case gbase_single_single:
                case gbase_chord_chord_jack:
                case gbase_single_chord_jack:
                case gbase_easy_trill:
                case gbase_chord_weird:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// tracks hand movements within an interval generically (trills, jacks, chords on a hand)
    public sealed class metaItvGenericHandInfo
    {
        public uint lastlast_row = 0u;
        public uint last_row = 0u;
        public generic_base_type last_type = gbase_type_init;

        public int total_taps = 0;
        public int chord_taps = 0;

        // total number of taps involved in a bracket
        public int taps_bracketing = 0;
        public bool bracketing = false;

        /// handle end of interval
        public void interval_end()
        {
            for (int i = 0; i < _base_types.Length; i++) _base_types[i] = 0;
            for (int i = 0; i < taps_by_size.Length; i++) taps_by_size[i] = 0;
            total_taps = 0;
            chord_taps = 0;
            taps_bracketing = 0;
            bracketing = false;
            last_type = gbase_type_init;
        }

        /// zero everything out for end of hand loop
        public void zero()
        {
            for (int i = 0; i < _base_types.Length; i++) _base_types[i] = 0;
            for (int i = 0; i < taps_by_size.Length; i++) taps_by_size[i] = 0;
            total_taps = 0;
            chord_taps = 0;
            taps_bracketing = 0;
            bracketing = false;
            last_type = gbase_type_init;
        }

        public void handle_row(uint new_row, uint hand_mask)
        {
            generic_base_type pattern_type = determine_generic_base_pattern_type(
              new_row, last_row, hand_mask);

            int taps_in_row = M.column_count(new_row);
            total_taps += taps_in_row;
            if (taps_in_row > 1)
                chord_taps += taps_in_row;

            if (pattern_type >= num_gbase_types)
            {
                lastlast_row = last_row;
                last_row = new_row;
                return;
            }

            // stop the bracketing
            if (basetype_stops_bracket(pattern_type))
            {
                bracketing = false;
            }
            // we might be able to start bracketing
            else if (!basetype_stops_bracket(last_type))
            {
                if (is_bracket(new_row, last_row, lastlast_row))
                {
                    if (bracketing)
                    {
                        taps_bracketing += taps_in_row;
                    }
                    else
                    {
                        bracketing = true;
                        taps_bracketing += taps_in_row + M.column_count(last_row) +
                                           M.column_count(lastlast_row);
                    }
                }
            }

            lastlast_row = last_row;
            last_row = new_row;
            last_type = pattern_type;
            _base_types[(int)pattern_type]++;
            taps_by_size[taps_in_row - 1] += taps_in_row;
        }

        public int possible_brackets()
        {
            return _base_types[(int)gbase_hard_trill] +
                   _base_types[(int)gbase_single_chord_trill] +
                   _base_types[(int)gbase_chord_chord_trill];
        }

        public int[] _base_types = new int[(int)num_gbase_types];
        public int[] taps_by_size = new int[(int)tap_size.num_tap_size];
    }
}
