// Faithful C# port of the hand-agnostic sequencing headers (v515):
//   Agnostic/HA_Sequencing.h (bitwise helpers, packed into static class HA) and
//   Agnostic/HA_Sequencers/{TrillSequencing.h, FlamSequencing.h, ThingSequencing.h}.
// C++ operator() becomes op(...); CalcMovingWindow<T>.push is the C++ operator()(new_val). Asserts are dropped.
using System;
using static Mina.col_type;
using static Mina.HD;

namespace Mina
{
    // ======================= HA_Sequencing.h =======================

    /// bitwise operations on noteinfo.notes (unsigned ints only). Bare-name usage via `using static Mina.HA;`.
    public static class HA
    {
        /// detect if this row of NoteInfo notes is a single tap
        public static bool is_single_tap(uint a)
        {
            return (a & (a - 1)) == 0;
        }

        /// jack at a column between two successive rows
        public static bool is_jack_at_col(uint columnId, uint row_notes, uint last_row_notes)
        {
            return ((columnId & row_notes) != 0U) && ((columnId & last_row_notes) != 0U);
        }

        /// detect a tap and then a chord, or a chord and then a tap (doesn't check for jacks)
        public static bool is_alternating_chord_single(uint a, uint b)
        {
            return (a > 1 && b == 1) || (a == 1 && b > 1);
        }

        /// find 1[n]1 or [n]1[n] with no jacks between first/second and second/third elements
        public static bool is_alternating_chord_stream(uint a, uint b, uint c)
        {
            if (is_single_tap(a))
            {
                if (is_single_tap(b))
                    return false; // single single, don't care, bail
                if (!is_single_tap(c))
                    return false; // single, chord, chord, bail
            }
            else
            {
                if (!is_single_tap(b))
                    return false; // chord chord, don't care, bail
                if (is_single_tap(c))
                    return false; // chord, single, single, bail
            }
            // we have either 1[n]1 or [n]1[n], check for any jacks
            return (((a & b) != 0U) && ((b & c) != 0U) ? 1 : 0) == 0;
        }
    }

    // ======================= TrillSequencing.h =======================

    public sealed class twohandtrill
    {
        // trill scenarios
        public enum trillnario
        {
            the_lack_thereof, // empty state
            need_opposite
        }

        // for the trill to continue, the next note must be on the opposite hand
        public int current_hand = Hands.left_hand;
        // for the trill to continue, the left hand notes must be on this column (allow for ohj)
        public col_type left_hand_state = col_init;
        public col_type right_hand_state = col_init;

        public trillnario current_scenario = trillnario.the_lack_thereof;

        /// a trill length 1 is 2 notes long and has "completed"
        public int cur_length = 0;
        public int jump_count = 0;

        public CalcMovingWindow<float> trill_ms = new CalcMovingWindow<float>();
        public int trills_in_interval = 0;
        public int total_taps = 0;

        // trill ms cv greater than this is rejected
        public float cv_threshold = 0.5f;

        public uint last_notes = 0x0;
        public uint last_last_notes = 0x0;

        // true if is a hand or two hand jump
        public bool two_hand_jump(uint notes)
        {
            return ((notes & 0xC) != 0U) && ((notes & 0x3) != 0U);
        }

        // drive the sequencer
        public void process(float ms_now, uint notes)
        {
            int notes_hand = Hands.left_hand;
            col_type notes_coltype = col_init;
            uint prev_notes = last_notes;
            uint prev_prev_notes = last_last_notes;
            last_last_notes = last_notes;
            last_notes = notes;

            if (two_hand_jump(notes))
            {
                if (two_hand_jump(prev_notes))
                {
                    dead_trill();
                    return;
                }
                if (two_hand_jump(prev_prev_notes))
                {
                    dead_trill();
                    return;
                }

                if ((notes & 0xF) == 0xF)
                {
                    // quad
                    dead_trill();
                    return;
                }
                else if ((notes & 0xC) == 0xC)
                {
                    // jump on left
                    notes_hand = Hands.left_hand;
                    notes_coltype = col_ohjump;
                }
                else if ((notes & 0x3) == 0x3)
                {
                    // jump on right
                    notes_hand = Hands.right_hand;
                    notes_coltype = col_ohjump;
                }
                else
                {
                    // actual two hand jump
                    if (((notes & prev_prev_notes) != 0U) && ((notes & prev_notes) == 0U))
                    {
                        // a jack separated by a note (23[24])
                        uint ppn = notes & prev_prev_notes;
                        if ((ppn & 0xC) != 0U)
                        {
                            notes_hand = Hands.left_hand;
                            notes_coltype = determine_col_type(notes, 0xC);
                        }
                        else if ((ppn & 0x3) != 0U)
                        {
                            notes_hand = Hands.right_hand;
                            notes_coltype = determine_col_type(notes, 0x3);
                        }
                        else
                        {
                            // preposterous
                            dead_trill();
                            return;
                        }
                    }
                    else
                    {
                        // what (232[24])
                        dead_trill();
                        return;
                    }
                }
            }
            else if ((notes & 0xC) != 0)
            {
                notes_hand = Hands.left_hand;
                notes_coltype = determine_col_type(notes, 0xC);
            }
            else if ((notes & 0x3) != 0)
            {
                notes_hand = Hands.right_hand;
                notes_coltype = determine_col_type(notes, 0x3);
            }
            else
            {
                // there is a lack of taps. this typically doesnt happen
                reset();
                return;
            }

            switch (current_scenario)
            {
                case trillnario.the_lack_thereof:
                {
                    // begin a trill anywhere
                    current_hand = notes_hand;
                    set_hand_states(notes_coltype);
                    current_scenario = trillnario.need_opposite;
                    break;
                }
                case trillnario.need_opposite:
                {
                    if (current_hand == notes_hand)
                    {
                        // trill broken
                        dead_trill();
                    }
                    else
                    {
                        if (notes_coltype == col_ohjump)
                            jump_count++;
                        if (current_hand == Hands.left_hand)
                        {
                            if (right_hand_state == col_init ||
                                right_hand_state == notes_coltype)
                            {
                                right_hand_state = notes_coltype;
                                calc_trill(ms_now);
                                cur_length++;
                                total_taps++;
                            }
                            else
                            {
                                // trill broken
                                dead_trill();
                                right_hand_state = notes_coltype;
                                current_scenario = trillnario.need_opposite;
                            }
                        }
                        else
                        {
                            if (left_hand_state == col_init ||
                                left_hand_state == notes_coltype)
                            {
                                left_hand_state = notes_coltype;
                                calc_trill(ms_now);
                                cur_length++;
                                total_taps++;
                            }
                            else
                            {
                                // trill broken
                                dead_trill();
                                left_hand_state = notes_coltype;
                                current_scenario = trillnario.need_opposite;
                            }
                        }
                    }
                    break;
                }
                default:
                    break;
            }
        }

        public void set_hand_states(col_type coltype)
        {
            if (current_hand == Hands.left_hand)
                left_hand_state = coltype;
            else
                right_hand_state = coltype;
        }

        public bool calc_trill(float ms_now)
        {
            // int window = Math.Min(cur_length, WindowConst.max_moving_window_size);
            trill_ms.push(ms_now);
            // float cv = trill_ms.get_cv_of_window(window);
            // for high cv, this may be a flam
            return true;
            // return cv < cv_threshold;
        }

        public void reset()
        {
            trills_in_interval = 0;
            total_taps = 0;
            cur_length = 0;
            trill_ms.zero();
            current_scenario = trillnario.the_lack_thereof;
            left_hand_state = col_init;
            right_hand_state = col_init;
            last_notes = 0x0;
            last_last_notes = 0x0;
        }

        public void dead_trill()
        {
            cur_length = 0;
            trill_ms.zero();
            current_scenario = trillnario.the_lack_thereof;
            left_hand_state = col_init;
            right_hand_state = col_init;
            last_notes = 0x0;
            last_last_notes = 0x0;
        }
    }

    /// Two Hand Trill Sequencing
    public sealed class THT_Sequencing
    {
        public twohandtrill trill = new twohandtrill();

        public const int _tap_size = 1;
        public const int _jump_size = 2;

        public float trill_buffer = 0.0f;
        public float trill_scaler = 1.0f;
        public float jump_buffer = 0.0f;
        public float jump_scaler = 1.0f;
        public float jump_weight = 0.5f;

        public float min_val = 0.0f;
        public float max_val = 1.5f;

        public void set_params(float cv, float tbuffer, float tscaler, float jbuffer,
                               float jscaler, float jweight, float min, float max)
        {
            trill.cv_threshold = cv;
            trill_buffer = tbuffer;
            trill_scaler = tscaler;
            jump_buffer = jbuffer;
            jump_scaler = jscaler;
            jump_weight = jweight;
            min_val = min;
            max_val = max;
        }

        /// C++ operator()(ms_now, notes)
        public void op(float ms_now, uint notes)
        {
            trill.process(ms_now, notes);
        }

        public void reset()
        {
            trill.reset();
        }

        /// numerical output (kind of like a proportion. 1 is "all trills")
        public float get(metaItvInfo mitvi)
        {
            float trill_taps = (float)trill.total_taps;
            float trill_jumps = (float)trill.jump_count;
            ItvInfo itvi = mitvi._itvi;
            float taps = (float)itvi.total_taps;

            if (taps == 0.0f)
                return 0.0f;

            float jumps = (float)itvi.taps_by_size[_jump_size];

            float trill_proportion = (trill_taps + trill_buffer) /
                                     Math.Max(taps - trill_buffer, 1.0f) *
                                     trill_scaler;

            float jump_proportion = (jumps - trill_jumps + jump_buffer) /
                                    Math.Max(taps - jump_buffer, 1.0f) * jump_scaler;

            // jump_weight = 0 will make it all jump_proportion
            float prop = M.weighted_average(trill_proportion,
                                            jump_proportion,
                                            Math.Clamp(1 - jump_weight, 0.0f, 1.0f),
                                            1.0f);

            return Math.Clamp(prop, min_val, max_val);
        }
    }

    // ======================= FlamSequencing.h =======================

    /// Struct describing a flam of 2-4 taps. A flam is not 1 tap.
    public sealed class flam
    {
        public const int max_flam_jammies = 4;

        /// columns seen
        public uint unsigned_unseen = 0U;

        /// size in ROWS, not columns
        public int size = 1;

        /// size > 1
        public bool flammin = false;

        /// ms values, 3 ms values = 4 rows
        public float[] ms = { 0.0f, 0.0f, 0.0f };

        /// is this row exclusively additive with the current flam sequence?
        public bool comma_comma_coolmeleon(uint notes)
        {
            return (unsigned_unseen & notes) == 0U;
        }

        /// gather cumulative millisecond gap for entire flam
        public float get_dur()
        {
            switch (size)
            {
                case 1:
                // can't have 1 row flams (assert(0)); in the tuned release build C++ falls through to case 2
                case 2:
                    return ms[0];
                case 3:
                    return ms[0] + ms[1];
                case 4:
                    return ms[0] + ms[1] + ms[2];
                default:
                    break;
            }
            return 0.0f;
        }

        /// begin flam sequence processing
        public void start(float ms_now, uint notes)
        {
            flammin = true;
            unsigned_unseen = 0U;

            grow(ms_now, notes);
        }

        /// continue flam sequence processing
        public void grow(float ms_now, uint notes)
        {
            if (size == max_flam_jammies)
                return;

            unsigned_unseen |= notes;
            ms[size - 1] = ms_now;

            // adjust size after setting ms, size starts at 1
            ++size;
        }

        public void reset()
        {
            flammin = false;
            size = 1;
        }
    }

    /// Sequencer for FlamJam, collects up to 4 flams per interval.
    public sealed class FJ_Sequencer
    {
        /// current tracking flam
        public flam flim = new flam();

        /// scan for flam chords in this window
        public float group_tol = 0.0f;
        /// tolerance for each column step
        public float step_tol = 0.0f;
        public float mod_scaler = 0.0f;

        /// number of flams
        public int flam_counter = 0;

        public float[] mod_parts = { 1.0f, 1.0f, 1.0f, 1.0f };

        /// too many flams already, shortcut into a minset in flamjammod
        public bool the_fifth_flammament = false;

        public void set_params(float gt, float st, float ms)
        {
            group_tol = gt;
            step_tol = st;
            mod_scaler = ms;
        }

        public void complete_seq()
        {
            if (flam_counter < flam.max_flam_jammies)
            {
                mod_parts[flam_counter] = construct_mod_part();
                ++flam_counter;
            }
            else
            {
                // bro its just flams
                the_fifth_flammament = true;
            }

            flim.reset();
        }

        public bool flammin_col_check(uint notes)
        {
            // this function should never be used to start a flam (assert(flim.flammin))
            return flim.comma_comma_coolmeleon(notes);
        }

        /// check for anything that would break the sequence
        public bool flammin_tol_check(float ms_now)
        {
            // check if ms from last row is greater than the group tolerance
            if (ms_now > group_tol)
                return false;

            // check if the new flam duration would exceed the group tolerance with the current row added
            if (flim.get_dur() + ms_now > group_tol)
                return false;

            return true;
        }

        /// C++ operator()(ms_now, notes)
        public void op(float ms_now, uint notes)
        {
            // if we already have the max number of flams
            if (the_fifth_flammament)
                return;

            // haven't started, if we're under the step tolerance, start
            if (!flim.flammin)
            {
                // 99.99% of cases
                if (ms_now > step_tol)
                    return;
                flim.start(ms_now, notes);
            }
            else
            {
                // passed the tolerance checks, run the col checks
                if (flammin_tol_check(ms_now))
                {
                    // passed col check, advance flam
                    if (flammin_col_check(notes))
                    {
                        flim.grow(ms_now, notes);
                    }
                    else
                    {
                        // row is eligible to begin a new flam sequence, complete existing and start again
                        complete_seq();
                        flim.start(ms_now, notes);
                    }
                }
                else
                {
                    // reset if we exceed tolerance checks
                    complete_seq();
                }
            }
        }

        public void handle_interval_end()
        {
            // just let it build potential sequences across intervals (flam.reset() intentionally omitted)
            the_fifth_flammament = false;
            flam_counter = 0;

            // reset everything to 1, as we build flams we will replace 1 with < 1 values
            for (int i = 0; i < mod_parts.Length; i++) mod_parts[i] = 1.0f;
        }

        public float construct_mod_part()
        {
            // total duration of flam
            float dur = flim.get_dur();

            // scale to size of flam
            float dur_prop = dur / group_tol;
            dur_prop /= ((float)flim.size / mod_scaler);
            dur_prop = Math.Clamp(dur_prop, 0.0f, 1.0f);

            return M.fastsqrt(dur_prop);
        }
    }

    // ======================= ThingSequencing.h =======================

    /// find [xx]a[yy]b[zz]; used by tt_sequencer and thing 1
    public sealed class the_slip
    {
        // to_slide_or_not_to_slide (nested; slide counter is an int so these are const ints)
        public const int slip_unbeginninged = 0;
        public const int needs_single = 1;
        public const int needs_23_jump = 2;
        public const int needs_opposing_single = 3;
        public const int needs_opposing_ohjump = 4;
        public const int slip_complete = 5;

        /// what caused us to slip
        public uint slip = 0;
        /// are we slipping
        public bool slippin_till_ya_slips_come_true = false;
        /// how far those whomst'd've been slippinging
        public int slide = 0;

        public bool the_slip_is_the_boot(uint notes)
        {
            switch (slide)
            {
                // just started, need single note with no jack between our starting point and [23]
                case needs_single:
                    // 1100 requires 0001
                    if (slip == 3 || slip == 7)
                    {
                        if (notes == 8)
                            return true;
                    }
                    else if (notes == 1) // not a left hand jump -> right hand one, we need 1000
                    {
                        return true;
                    }
                    break;
                case needs_23_jump:
                    // has to be [23]
                    if (notes == 6)
                        return true;
                    break;
                case needs_opposing_single:
                    // same as 1 but reversed
                    if (slip == 3 || slip == 7)
                    {
                        if (notes == 1)
                            return true;
                    }
                    else if (notes == 8) // we need 0001
                    {
                        return true;
                    }
                    break;
                case needs_opposing_ohjump:
                    if (slip == 3 || slip == 7)
                    {
                        // started on 1100, end on 0011 (allow 0100 too)
                        if (notes == 12 || notes == 14)
                            return true;
                    }
                    else if (notes == 3 || notes == 7) // starting on 0011 ends on 1100 (allow 0010)
                    {
                        return true;
                    }
                    break;
                default:
                    break;
            }
            return false;
        }

        public void start(float ms_now, uint notes)
        {
            slip = notes;
            slide = 0;
            slippin_till_ya_slips_come_true = true;
            grow(ms_now, notes);
        }

        public void grow(float ms_now, uint notes)
        {
            // ms[slide] = ms_now;
            ++slide;
        }

        public void reset() { slippin_till_ya_slips_come_true = false; }
    }

    /// find [12]3[24]1[34]2[13]4[12]; used by tt2 and thing 2
    public sealed class the_slip2
    {
        // to_slide_or_not_to_slide (nested)
        public const int slip_unbeginninged = 0;
        public const int needs_single = 1;
        public const int needs_door = 2;
        public const int needs_blaap = 3;
        public const int needs_opposing_ohjump = 4;
        public const int slip_complete = 5;

        /// what caused us to slip
        public uint slip = 0;
        public bool slippin_till_ya_slips_come_true = false;
        /// how far those whomst'd've been slippinging
        public int slide = 0;

        public bool the_slip_is_the_boot(uint notes)
        {
            switch (slide)
            {
                case needs_single:
                    // 1100 requires 0010
                    if (slip == 3)
                    {
                        if (notes == 4)
                            return true;
                    }
                    else if (notes == 2)
                    {
                        return true;
                    }
                    break;
                case needs_door:
                    if (slip == 3)
                    {
                        if (notes == 10)
                            return true;
                    }
                    else if (notes == 5)
                    {
                        return true;
                    }
                    break;
                case needs_blaap:
                    // requires 1000
                    if (slip == 3)
                    {
                        if (notes == 1)
                            return true;
                    }
                    else if (notes == 8)
                    {
                        return true;
                    }
                    break;
                case needs_opposing_ohjump:
                    if (slip == 3)
                    {
                        // started on 1100, end on 0011
                        if (notes == 12)
                            return true;
                    }
                    else if (notes == 3) // starting on 0011 ends on 1100
                    {
                        return true;
                    }
                    break;
                default:
                    break;
            }
            return false;
        }

        public void start(float ms_now, uint notes)
        {
            slip = notes;
            slide = 0;
            slippin_till_ya_slips_come_true = true;
            grow(ms_now, notes);
        }

        public void grow(float ms_now, uint notes)
        {
            // ms[slide] = ms_now;
            ++slide;
        }

        public void reset() { slippin_till_ya_slips_come_true = false; }
    }

    /// sort of the same concept as fj; used by thing1
    public sealed class TT_Sequencing
    {
        public the_slip fizz = new the_slip();
        public int slip_counter = 0;
        public const int max_slips = 4;
        public float[] mod_parts = { 1.0f, 1.0f, 1.0f, 1.0f };

        public float scaler = 0.0f;

        public void set_params(float gt, float st, float ms)
        {
            // group_tol = gt; step_tol = st;
            scaler = ms;
        }

        public void complete_slip(float ms_now, uint notes)
        {
            if (slip_counter < max_slips)
                mod_parts[slip_counter] = construct_mod_part();
            ++slip_counter;

            // any time we complete a slip we can start another, so just start again
            fizz.start(ms_now, notes);
        }

        // only start if we pick up ohjump or hand with an ohjump, not a quad, not singles
        public static bool start_test(uint notes)
        {
            return notes == 3 || notes == 7 || notes == 12 || notes == 14;
        }

        /// C++ operator()(ms_now, notes)
        public void op(float ms_now, uint notes)
        {
            // ignore quads
            if (notes == 15)
            {
                if (fizz.slippin_till_ya_slips_come_true)
                    fizz.reset();
                return;
            }

            // haven't started
            if (!fizz.slippin_till_ya_slips_come_true)
            {
                if (start_test(notes))
                    fizz.start(ms_now, notes);
                return;
            }
            // run the col checks for continuation
            if (fizz.the_slip_is_the_boot(notes))
            {
                fizz.grow(ms_now, notes);
                // we found... the thing
                if (fizz.slide == 5)
                    complete_slip(ms_now, notes);
            }
            else
            {
                fizz.reset();
            }
        }

        public void reset()
        {
            slip_counter = 0;
            for (int i = 0; i < mod_parts.Length; i++) mod_parts[i] = 1.0f;
        }

        public float construct_mod_part() { return scaler; }
    }

    /// sort of the same concept as fj; used by thing2
    public sealed class TT_Sequencing2
    {
        public the_slip2 fizz = new the_slip2();
        public int slip_counter = 0;
        public const int max_slips = 4;
        public float[] mod_parts = { 1.0f, 1.0f, 1.0f, 1.0f };

        public float scaler = 0.0f;

        public void set_params(float gt, float st, float ms)
        {
            // group_tol = gt; step_tol = st;
            scaler = ms;
        }

        public void complete_slip(float ms_now, uint notes)
        {
            if (slip_counter < max_slips)
                mod_parts[slip_counter] = construct_mod_part();
            ++slip_counter;

            fizz.start(ms_now, notes);
        }

        public static bool start_test(uint notes)
        {
            return notes == 3 || notes == 12;
        }

        /// C++ operator()(ms_now, notes)
        public void op(float ms_now, uint notes)
        {
            // ignore quads
            if (notes == 15)
            {
                if (fizz.slippin_till_ya_slips_come_true)
                    fizz.reset();
                return;
            }

            if (!fizz.slippin_till_ya_slips_come_true)
            {
                if (start_test(notes))
                    fizz.start(ms_now, notes);
                return;
            }
            if (fizz.the_slip_is_the_boot(notes))
            {
                fizz.grow(ms_now, notes);
                if (fizz.slide == 5)
                    complete_slip(ms_now, notes);
            }
            else
            {
                fizz.reset();
            }
        }

        public void reset()
        {
            slip_counter = 0;
            for (int i = 0; i < mod_parts.Length; i++) mod_parts[i] = 1.0f;
        }

        public float construct_mod_part() { return scaler; }
    }
}
