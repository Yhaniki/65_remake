// Faithful C# port of the hand-agnostic interval/meta headers (v515):
//   Agnostic/IntervalInfo.h, Agnostic/MetaIntervalInfo.h, Agnostic/MetaRowInfo.h.
// These accumulate raw + first/second-level meta info per row/interval, working purely on NoteInfo.notes (no derived
// column logic). C++ operator() becomes op(...). Free helpers used here live in static class HA (SeqAgnostic.cs).
using static Mina.HA;

namespace Mina
{
    // ---------------- IntervalInfo.h ----------------

    public enum tap_size
    {
        single,
        jump,
        hand,
        quad,
        five_chord,
        six_chord,
        seven_chord,
        eight_chord,
        nine_chord,
        ten_chord,
        eleven_chord,
        twelve_chord,
        thirteen_chord,
        fourteen_chord,
        fifteen_chord,
        sixteen_chord,
        num_tap_size
    }

    /// simple struct to accumulate raw note info across an interval as it's processed by row
    public sealed class ItvInfo
    {
        /// total taps
        public int total_taps = 0;
        /// non single taps
        public int chord_taps = 0;
        /// count of taps for each tap_size
        public int[] taps_by_size = new int[(int)tap_size.num_tap_size];
        /// number related to amount of jumps in the interval. inflated by dense hs/js mix
        public int mixed_hs_density_tap_bonus = 0;

        /// resets all the stuff that accumulates across intervals
        public void handle_interval_end()
        {
            total_taps = 0;

            chord_taps = 0;
            mixed_hs_density_tap_bonus = 0;

            for (int i = 0; i < taps_by_size.Length; i++) taps_by_size[i] = 0;
        }

        public void update_tap_counts(int row_count)
        {
            total_taps += row_count;

            // ALWAYS COUNT NUMBER OF TAPS IN CHORDS
            if (row_count > 1)
                chord_taps += row_count;

            // ALWAYS COUNT NUMBER OF TAPS IN CHORDS
            taps_by_size[row_count - 1] += row_count;

            // we want mixed hs/js to register as hs, even at relatively sparse hand density
            if (taps_by_size[(int)tap_size.hand] > 0)
            {
                // it'll add the number of jumps in the whole interval every hand
                mixed_hs_density_tap_bonus += taps_by_size[(int)tap_size.jump];
            }
        }
    }

    // ---------------- MetaIntervalInfo.h ----------------

    /// Meta info for the interval, hand _agnostic_ (operates fully on note info).
    public sealed class metaItvInfo
    {
        public ItvInfo _itvi = new ItvInfo();

        public int _idx = 0;
        // meta info for this interval extracted from the base noterow progression
        public int seriously_not_js = 0;
        public int definitely_not_jacks = 0;
        public int actual_jacks = 0;
        public int actual_jacks_cj = 0;
        public int not_js = 0;
        public int not_hs = 0;
        public int zwop = 0;
        public int shared_chord_jacks = 0;
        public bool dunk_it = false;

        // keep an array of 3, run a comparison loop that sets 0s to a new value if that value doesn't match any non 0
        // value, and set a bool flag if we have filled the array with unique values
        public uint[] row_variations = { 0, 0, 0 };
        public int num_var = 0;
        // unique(noteinfos for interval) < 3, or row_variations[2] == 0 by interval end
        public bool basically_vibro = true;

        public void reset()
        {
            // at the moment this also resets to default
            _itvi.handle_interval_end();

            _idx = 0;
            seriously_not_js = 0;
            definitely_not_jacks = 0;
            actual_jacks = 0;
            actual_jacks_cj = 0;
            not_js = 0;
            not_hs = 0;
            zwop = 0;
            shared_chord_jacks = 0;
            dunk_it = false;

            for (int i = 0; i < row_variations.Length; i++) row_variations[i] = 0;
            num_var = 0;

            basically_vibro = false; // C++ assigns 0 to a bool here
        }

        public void handle_interval_end()
        {
            // seriously_not_js isn't reset, preserve behavior (tracks longer sequences of single notes)

            // alternating chordstream detected (used by cj only atm)
            definitely_not_jacks = 0;

            // number of shared jacks between two successive rows, used by js/hs to depress jumpjacks
            actual_jacks = 0;

            // almost same thing as above (see comment in jack_scan)
            actual_jacks_cj = 0;

            not_js = 0;
            not_hs = 0;

            // recycle var for any int assignments
            zwop = 0;

            // self explanatory and unused atm
            shared_chord_jacks = 0;

            for (int i = 0; i < row_variations.Length; i++) row_variations[i] = 0;
            num_var = 0;

            // see def
            basically_vibro = true;
            dunk_it = false;

            // reset our interval info
            _itvi.handle_interval_end();
        }
    }

    // ---------------- MetaRowInfo.h ----------------

    /// counterpart to metahandinfo
    public sealed class metaRowInfo
    {
        public const bool dbg = false;
        public const bool dbg_lv2 = false;

        public float time = M.s_init;
        // time from last row (ms)
        public float ms_now = M.ms_init;
        public int count = 0;
        public int last_count = 0;
        public int last_last_count = 0;
        public uint notes = 0;
        public uint last_notes = 0;
        public uint last_last_notes = 0;

        // per row bool flags, these must be directly set every row
        public bool alternating_chordstream = false;
        public bool alternating_chord_single = false;
        public bool gluts_maybe = false; // not really used/tested yet
        public bool twas_jack = false;

        public Calc _calc;

        public metaRowInfo(Calc calc)
        {
            _calc = calc;
        }

        public void reset()
        {
            time = M.s_init;
            ms_now = M.ms_init;
            count = 0;
            last_count = 0;
            last_last_count = 0;
            notes = 0;
            last_notes = 0;
            last_last_notes = 0;

            alternating_chordstream = false;
            alternating_chord_single = false;
            gluts_maybe = false;
            twas_jack = false;
        }

        public void set_row_variations(metaItvInfo mitvi)
        {
            // already determined there's enough variation in this interval
            if (!mitvi.basically_vibro)
                return;

            // trying to fill array with up to 3 unique row_note configurations
            for (int k = 0; k < mitvi.row_variations.Length; k++)
            {
                // already a stored value here
                if (mitvi.row_variations[k] != 0)
                {
                    // already have one of these
                    if (mitvi.row_variations[k] == notes)
                        return;
                }
                else // == 0
                {
                    // nothing stored here and isn't a duplicate, store it and iterate num_var
                    mitvi.row_variations[k] = notes;
                    ++mitvi.num_var;

                    // check if we filled the array with unique values
                    if (mitvi.row_variations[2] != 0)
                        mitvi.basically_vibro = false;
                    return;
                }
            }
        }

        // scan for jacks and jack counts between this row and the last
        public void jack_scan(metaItvInfo mitvi)
        {
            twas_jack = false;

            foreach (var id in _calc.col_masks)
            {
                if (is_jack_at_col(id, notes, last_notes))
                {
                    // not scaled to the number of jacks anymore
                    ++mitvi.actual_jacks;
                    twas_jack = true;
                    // try to pick up gluts maybe?
                    if (count > 1 && M.column_count(last_notes) > 1)
                        ++mitvi.shared_chord_jacks;
                }
            }

            // catch stuff like splithand jumptrills registering as chordjacks when they shouldn't be
            if (twas_jack)
                ++mitvi.actual_jacks_cj;
        }

        public void basic_row_sequencing(metaRowInfo last, metaItvInfo mitvi)
        {
            jack_scan(mitvi);
            set_row_variations(mitvi);

            // check for [123]4[123] [12]3[124] etc which isn't actually chordjack, just broken hs/js
            alternating_chordstream =
              is_alternating_chord_stream(notes, last_notes, last.last_notes);
            if (alternating_chordstream)
                ++mitvi.definitely_not_jacks;

            if (alternating_chordstream)
            {
                // put mixed density stuff here later
            }

            // only cares about single vs chord, not jacks
            // (C++ params are unsigned; count/last.count are int and convert implicitly)
            alternating_chord_single =
              is_alternating_chord_single((uint)count, (uint)last.count);
            if (alternating_chord_single)
            {
                if (!twas_jack)
                    mitvi.seriously_not_js -= 3;
            }

            if (last.count == 1 && count == 1)
            {
                mitvi.seriously_not_js =
                  0 > mitvi.seriously_not_js ? 0 : mitvi.seriously_not_js;
                ++mitvi.seriously_not_js;

                // light js really stops at [12]321[23] kind of density
                if (mitvi.seriously_not_js > 3)
                {
                    mitvi.not_js += mitvi.seriously_not_js;
                    // give light hs the light js treatment
                    mitvi.not_hs += mitvi.seriously_not_js;
                }
            }
            else if (last.count > 1 && count > 1)
            {
                // suppress jumptrilly garbage a little bit
                mitvi.not_hs += count;
                mitvi.not_js += count;

                // might be overkill
                if ((notes & last_notes) == 0)
                {
                    ++mitvi.not_hs;
                    ++mitvi.not_js;
                }
                else
                {
                    gluts_maybe = true;
                }
            }

            // if the previous 3 rows do not form any jacks and the current and previous rows are chords
            if ((notes & last_notes) == 0 && count > 1 && last_count > 1)
            {
                if ((last_notes & last.last_notes) == 0 && last_count > 1)
                    mitvi.dunk_it = true;
            }
        }

        /// C++ operator()(last, mitvi, row_time, row_count, row_notes)
        public void op(metaRowInfo last,
                       metaItvInfo mitvi,
                       float row_time,
                       int row_count,
                       uint row_notes)
        {
            time = row_time;
            last_last_count = last.last_count;
            last_count = last.count;
            count = row_count;

            last_last_notes = last.last_notes;
            last_notes = last.notes;
            notes = row_notes;

            ms_now = M.ms_from(time, last.time);

            mitvi._itvi.update_tap_counts(count);
            basic_row_sequencing(last, mitvi);
        }
    }
}
