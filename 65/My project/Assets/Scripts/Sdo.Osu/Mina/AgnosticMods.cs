// Faithful C# port of the hand-agnostic pattern mods (v515):
//   Agnostic/HA_PatternMods/{Stream,JS,HS,CJ,CJDensity,HSDensity,FlamJam,TheThingFinder,GenericChordstream}.h
// Each C++ struct -> a sealed class with the same name. C++ operator() becomes op(...). The mods' _params vectors and
// XML load/save are dropped; the defaulted param fields (the official tuned values) are kept verbatim, along with the
// _pmod field and the name string. Sequencers (THT_Sequencing / FJ_Sequencer / TT_Sequencing / TT_Sequencing2) are
// REUSED from SeqAgnostic.cs, not redefined here.
//
// PORT NOTES (judgment calls):
//  * C++ field `base` is a C# reserved word -> escaped as `@base` (name preserved).
//  * C++ in-class initializers that reference a sibling field (e.g. `float total_prop_min = min_mod;`,
//    `float pmod = min_mod;`) are illegal as C# instance-field initializers (no `this` in an initializer), so they are
//    performed in a parameterless constructor instead. Field initializers run before the ctor body, so min_mod/max_mod
//    already hold their defaults when the ctor copies them -> identical result to the C++ member init order.
using System;
using static Mina.tap_size;

namespace Mina
{
    // ======================= Stream.h =======================

    /// Hand-Agnostic PatternMod detecting Stream.
    /// Looks for single taps out of all taps in the interval.
    /// Begins to dampen in value if too many jacks are present
    public sealed class StreamMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.Stream;
        public readonly string name = "StreamMod";
        public readonly int _tap_size = (int)single;

        // params
        public float @base = 0f;
        public float min_mod = 0.6f;
        public float max_mod = 1.0f;
        public float prop_buffer = 1f;
        public float prop_scaler = 1.41f;

        public float jack_pool = 4f;
        public float jack_comp_min = 0.5f;
        public float jack_comp_max = 1f;

        public float vibro_flag = 1f;

        public float tht_scaler = 0f;
        public float tht_cv_threshold = 0.5f;
        public float tht_trill_buffer = 1.4f;
        public float tht_trill_scaler = 1f;
        public float tht_jump_buffer = 1f;
        public float tht_jump_scaler = 0.5f;
        public float tht_jump_weight = 0.0f;
        public float tht_min_prop = 0.0f;
        public float tht_max_prop = 1f;

        public float prop_component = 0f;
        public float jack_component = 0f;
        public float pmod;                       // C++: = min_mod (set in ctor)

        public THT_Sequencing trillsequencer = new THT_Sequencing();

        public StreamMod()
        {
            pmod = min_mod;
        }

        public void setup()
        {
            trillsequencer.set_params(tht_cv_threshold,
                                      tht_trill_buffer,
                                      tht_trill_scaler,
                                      tht_jump_buffer,
                                      tht_jump_scaler,
                                      tht_jump_weight,
                                      tht_min_prop,
                                      tht_max_prop);
        }

        public void advance_sequencing(float ms_now, uint notes)
        {
            trillsequencer.op(ms_now, notes);
        }

        public void full_reset()
        {
            trillsequencer.reset();
        }

        public float op(metaItvInfo mitvi)
        {
            ItvInfo itvi = mitvi._itvi;

            // 1 tap is by definition a single tap
            if (itvi.total_taps < 2)
            {
                return M.neutral;
            }

            if (itvi.taps_by_size[_tap_size] == 0)
            {
                return min_mod;
            }

            /* we want very light js to register as stream, something like jumps on
             * every other 4th, so 17/19 ratio should return full points, but maybe
             * we should allow for some leeway in bad interval slicing this maybe
             * doesn't need to be so severe, on the other hand, maybe it doesn'ting
             * need to be not needing'nt to be so severe */

            // we could make better use of sequencing here since now it's easy

            prop_component =
              (itvi.taps_by_size[_tap_size] + prop_buffer) /
              ((float)itvi.total_taps - prop_buffer) *
              prop_scaler;

            // allow for a mini/triple jack in streams.. but not more than that
            jack_component = Math.Clamp(
              jack_pool - mitvi.actual_jacks, jack_comp_min, jack_comp_max);
            pmod = M.fastsqrt(prop_component * jack_component);

            // water downing based on two hand trills
            float tht_prop = trillsequencer.get(mitvi);
            pmod *= (1f - (tht_prop * tht_scaler));
            trillsequencer.reset();

            pmod = Math.Clamp(@base + pmod, min_mod, max_mod);

            if (mitvi.basically_vibro)
            {
                if (mitvi.num_var == 1)
                {
                    pmod *= 0.5f * vibro_flag;
                }
                else if (mitvi.num_var == 2)
                {
                    pmod *= 0.9f * vibro_flag;
                }
                else if (mitvi.num_var == 3)
                {
                    pmod *= 0.95f * vibro_flag;
                }
            }

            // actual mod
            return pmod;
        }
    }

    // ======================= JS.h =======================

    /// Hand-Agnostic PatternMod detecting Jumpstream.
    /// Looks for jacks, jumptrills, and jumps (2-chords)
    public sealed class JSMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.JS;
        public readonly string name = "JSMod";
        public readonly int _tap_size = (int)jump;

        // params
        public float min_mod = 0.6f;
        public float max_mod = 1.1f;
        public float mod_base = 0f;
        public float prop_buffer = 1f;

        public float total_prop_min;             // C++: = min_mod (set in ctor)
        public float total_prop_max;             // C++: = max_mod (set in ctor)
        public float total_prop_scaler = 2.714f; // ~19/7

        public float split_hand_pool = 1.5f;
        public float split_hand_min = 0.9f;
        public float split_hand_max = 1f;
        public float split_hand_scaler = 1f;

        public float jack_pool = 1.35f;
        public float jack_min = 0.5f;
        public float jack_max = 1f;
        public float jack_scaler = 1f;

        public float decay_factor = 0.05f;

        public float total_prop = 0f;
        public float jumptrill_prop = 0f;
        public float jack_prop = 0f;
        public float last_mod;                   // C++: = min_mod (set in ctor)
        public float pmod;                       // C++: = min_mod (set in ctor)
        public float t_taps = 0f;

        public JSMod()
        {
            total_prop_min = min_mod;
            total_prop_max = max_mod;
            last_mod = min_mod;
            pmod = min_mod;
        }

        public void full_reset()
        {
            last_mod = min_mod;
        }

        public void decay_mod()
        {
            pmod = Math.Clamp(last_mod - decay_factor, min_mod, max_mod);
            last_mod = pmod;
        }

        public float op(metaItvInfo mitvi)
        {
            ItvInfo itvi = mitvi._itvi;

            // empty interval, don't decay js mod or update last_mod
            if (itvi.total_taps == 0)
            {
                return M.neutral;
            }

            // at least 1 tap but no jumps
            if (itvi.taps_by_size[_tap_size] == 0)
            {
                decay_mod();
                return pmod;
            }

            /* end case optimizations */

            t_taps = (float)itvi.total_taps;

            // creepy banana
            total_prop =
              (itvi.taps_by_size[_tap_size] + prop_buffer) /
              (t_taps - prop_buffer) * total_prop_scaler;
            total_prop =
              Math.Clamp(M.fastsqrt(total_prop), total_prop_min, total_prop_max);

            // punish lots splithand jumptrills
            // uhh this might also catch oh jumptrills can't remember
            jumptrill_prop = Math.Clamp(
              split_hand_pool - ((float)mitvi.not_js / t_taps),
              split_hand_min,
              split_hand_max);

            // downscale by jack density rather than upscale like cj
            // theoretically the ohjump downscaler should handle
            // this but handling it here gives us more flexbility
            // with the ohjump mod
            jack_prop = Math.Clamp(
              jack_pool - ((float)mitvi.actual_jacks / t_taps),
              jack_min,
              jack_max);

            pmod =
              Math.Clamp(total_prop * jumptrill_prop * jack_prop, min_mod, max_mod);

            if (mitvi.dunk_it)
            {
                pmod *= 0.99f;
            }

            // set last mod, we're using it to create a decaying mod that won't
            // result in extreme spikiness if files alternate between js and
            // hs/stream
            last_mod = pmod;

            return pmod;
        }
    }

    // ======================= HS.h =======================

    /// Hand-Agnostic PatternMod detecting Handstream.
    /// Looks for jacks, jumptrills, and hands (3-chords)
    public sealed class HSMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.HS;
        public readonly string name = "HSMod";
        public readonly int _tap_size = (int)hand;

        // params
        public float min_mod = 0.6f;
        public float max_mod = 1.1f;
        public float mod_base = 0.4f;
        public float prop_buffer = 1f;

        public float total_prop_min;             // C++: = min_mod (set in ctor)
        public float total_prop_max;             // C++: = max_mod (set in ctor)

        // was ~32/7, is higher now to push up light hs (maybe overkill tho)
        public float total_prop_scaler = 5.571f;
        public float total_prop_base = 0.4f;

        public float split_hand_pool = 1.6f;
        public float split_hand_min = 0.89f;
        public float split_hand_max = 1f;
        public float split_hand_scaler = 1f;

        public float jack_pool = 1.35f;
        public float jack_min = 0.5f;
        public float jack_max = 1f;
        public float jack_scaler = 1f;

        public float decay_factor = 0.05f;

        public float total_prop = 0f;
        public float jumptrill_prop = 0f;
        public float jack_prop = 0f;
        public float last_mod;                   // C++: = min_mod (set in ctor)
        public float pmod;                       // C++: = min_mod (set in ctor)
        public float t_taps = 0f;

        public HSMod()
        {
            total_prop_min = min_mod;
            total_prop_max = max_mod;
            last_mod = min_mod;
            pmod = min_mod;
        }

        public void full_reset()
        {
            last_mod = min_mod;
        }

        public void decay_mod()
        {
            pmod = Math.Clamp(last_mod - decay_factor, min_mod, max_mod);
            last_mod = pmod;
        }

        public float op(metaItvInfo mitvi)
        {
            ItvInfo itvi = mitvi._itvi;

            // empty interval, don't decay mod or update last_mod
            if (itvi.total_taps == 0)
            {
                return M.neutral;
            }

            // look ma no hands
            if (itvi.taps_by_size[_tap_size] == 0)
            {
                decay_mod();
                return pmod;
            }

            t_taps = (float)itvi.total_taps;

            // when bark of dog into canyon scream at you
            total_prop = total_prop_base +
                         (((itvi.taps_by_size[_tap_size] +
                            itvi.mixed_hs_density_tap_bonus) +
                           prop_buffer) /
                          (t_taps - prop_buffer) * total_prop_scaler);
            total_prop =
              Math.Clamp(M.fastsqrt(total_prop), total_prop_min, total_prop_max);

            // downscale jumptrills for hs as well
            jumptrill_prop = Math.Clamp(
              split_hand_pool - ((float)mitvi.not_hs / t_taps),
              split_hand_min,
              split_hand_max);

            // downscale by jack density rather than upscale, like cj does
            jack_prop = Math.Clamp(
              jack_pool - ((float)mitvi.actual_jacks / t_taps),
              jack_min,
              jack_max);

            pmod =
              Math.Clamp(total_prop * jumptrill_prop * jack_prop, min_mod, max_mod);

            if (mitvi.dunk_it)
            {
                pmod *= 0.99f;
            }

            // set last mod, we're using it to create a decaying mod that won't
            // result in extreme spikiness if files alternate between js and
            // hs/stream
            last_mod = pmod;

            return pmod;
        }
    }

    // ======================= CJ.h =======================

    /// Hand-Agnostic PatternMod detecting Chordjacks.
    /// Looks for continuous chords which form jacks
    public sealed class CJMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.CJ;
        public readonly string name = "CJMod";

        // params
        public float min_mod = 0.6f;
        public float max_mod = 1f;
        public float mod_base = 0.4f;
        public float prop_buffer = 1f;

        public float total_prop_min;             // C++: = min_mod (set in ctor)
        public float total_prop_max;             // C++: = max_mod (set in ctor)
        public float total_prop_scaler = 5.428f;

        public float jack_base = 2f;
        public float jack_min = 0.625f;
        public float jack_max = 1f;
        public float jack_scaler = 1f;

        public float not_jack_pool = 1.2f;
        public float not_jack_min = 0.4f;
        public float not_jack_max = 1f;
        public float not_jack_scaler = 1f;

        public float vibro_flag = 1f;
        public float decay_factor = 0.1f;

        public float total_prop = 0f;
        public float jack_prop = 0f;
        public float not_jack_prop = 0f;
        public float pmod;                       // C++: = min_mod (set in ctor)
        public float t_taps = 0f;
        public float last_mod = 0f;

        public CJMod()
        {
            total_prop_min = min_mod;
            total_prop_max = max_mod;
            pmod = min_mod;
        }

        public void full_reset()
        {
            last_mod = min_mod;
        }

        public void decay_mod()
        {
            pmod = Math.Clamp(last_mod - decay_factor, min_mod, max_mod);
            last_mod = pmod;
        }

        public float op(metaItvInfo mitvi)
        {
            ItvInfo itvi = mitvi._itvi;

            if (itvi.total_taps == 0)
            {
                return M.neutral;
            }

            // no chords
            if (itvi.chord_taps == 0)
            {
                decay_mod();
                return pmod;
            }

            t_taps = (float)itvi.total_taps;

            // we have at least 1 chord we want to give a little leeway for single
            // taps but not too much or sections of [12]4[123] [123]4[23] will be
            // flagged as chordjack when they're really just broken chordstream, and
            // we also want to give enough leeway so that hyperdense chordjacks at
            // lower bpms aren't automatically rated higher than more sparse jacks
            // at higher bpms
            total_prop = ((float)itvi.chord_taps +
                          prop_buffer) /
                         (t_taps - prop_buffer) * total_prop_scaler;
            total_prop =
              Math.Clamp(M.fastsqrt(total_prop), total_prop_min, total_prop_max);

            // make sure there's at least a couple of jacks
            jack_prop =
              Math.Clamp((float)mitvi.actual_jacks_cj - jack_base,
                         jack_min,
                         jack_max);

            // explicitly detect broken chordstream type stuff so we can give more
            // leeway to single note jacks brop_two_return_of_brop_electric_bropaloo
            not_jack_prop = Math.Clamp(
              not_jack_pool -
                (((float)mitvi.definitely_not_jacks *
                  not_jack_scaler) /
                 t_taps),
              not_jack_min,
              not_jack_max);

            pmod =
              Math.Clamp(total_prop * jack_prop * not_jack_prop, min_mod, max_mod);

            // ITS JUST VIBRO THEN(unique note permutations per interval < 3 ), use
            // this other places ?
            if (mitvi.basically_vibro)
            {
                if (mitvi.num_var == 1)
                {
                    pmod *= 0.5f * vibro_flag;
                }
                else if (mitvi.num_var == 2)
                {
                    pmod *= 0.9f * vibro_flag;
                }
                else if (mitvi.num_var == 3)
                {
                    pmod *= 0.95f * vibro_flag;
                }
            }

            // set for decay
            last_mod = pmod;

            return pmod;
        }
    }

    // ======================= CJDensity.h =======================

    /// Hand-Agnostic PatternMod describing chord density.
    /// Forms a value based on counts of chords of different sizes
    /// relative to the number of notes in the interval
    public sealed class CJDensityMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.CJDensity;
        public readonly string name = "CJDensityMod";
        public readonly int _tap_size = (int)quad;

        // params
        public float min_mod = 0.98f;
        public float max_mod = 1f;
        public float @base = 0f;

        public float single_scaler = 1f;
        public float jump_scaler = 1.25f;
        public float hand_scaler = 0.9f;
        public float quad_scaler = 1.15f;

        public float pmod = M.neutral;

        public float op(metaItvInfo mitvi)
        {
            ItvInfo itvi = mitvi._itvi;
            if (itvi.total_taps == 0)
            {
                return M.neutral;
            }

            float t_taps = (float)itvi.total_taps;
            float a0 =
              ((float)itvi.taps_by_size[(int)single] *
               single_scaler) /
              t_taps;
            float a1 =
              ((float)itvi.taps_by_size[(int)jump] * jump_scaler) /
              t_taps;
            float a2 =
              ((float)itvi.taps_by_size[(int)hand] * hand_scaler) /
              t_taps;
            float a3 =
              ((float)itvi.taps_by_size[(int)quad] * quad_scaler) /
              t_taps;

            float aaa = a0 + a1 + a2 + a3;

            pmod = Math.Clamp(@base + M.fastsqrt(aaa), min_mod, max_mod);

            return pmod;
        }
    }

    // ======================= HSDensity.h =======================

    /// Hand-Agnostic PatternMod describing chord density.
    /// Forms a value based on counts of chords of different sizes
    /// relative to the number of notes in the interval
    public sealed class HSDensityMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.HSDensity;
        public readonly string name = "HSDensityMod";
        public readonly int _tap_size = (int)quad;

        // params
        public float min_mod = 1f;
        public float max_mod = 1f;
        public float @base = 0f;

        public float single_scaler = 2f;
        public float jump_scaler = 1.2f;
        public float hand_scaler = 0.95f;
        public float quad_scaler = 0.95f;

        public float pmod = M.neutral;

        public float op(metaItvInfo mitvi)
        {
            ItvInfo itvi = mitvi._itvi;
            if (itvi.total_taps == 0)
            {
                return M.neutral;
            }

            float t_taps = (float)itvi.total_taps;
            float a0 =
              ((float)itvi.taps_by_size[(int)single] *
               single_scaler) /
              t_taps;
            float a1 =
              ((float)itvi.taps_by_size[(int)jump] * jump_scaler) /
              t_taps;
            float a2 =
              ((float)itvi.taps_by_size[(int)hand] * hand_scaler) /
              t_taps;
            float a3 =
              ((float)itvi.taps_by_size[(int)quad] * quad_scaler) /
              t_taps;

            float aaa = a0 + a1 + a2 + a3;

            pmod = Math.Clamp(@base + M.fastsqrt(aaa), min_mod, max_mod);

            return pmod;
        }
    }

    // ======================= FlamJam.h =======================

    /// Hand-Agnostic PatternMod detecting continuous flams.
    /// Flams are n taps which are close enough to be hit as a chord.
    /// Intended to downscale patterns which take advantage of
    /// flams as being misinterpreted as stream instead of something like
    /// chordjacks or jumpstream.
    ///
    /// note for improvement:
    /// MAKE FLAM WIDE RANGE?
    /// ^ YES DO THIS
    public sealed class FlamJamMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.FlamJam;
        public readonly string name = "FlamJamMod";

        // params
        public float min_mod = 0.3f;
        public float max_mod = 1f;
        public float scaler = 0.001f;
        public float @base = 0.5f;

        public float group_tol = 35f;
        public float step_tol = 17.5f;

        // sequencer
        public FJ_Sequencer fj = new FJ_Sequencer();
        public float pmod = M.neutral;

        public void setup() { fj.set_params(group_tol, step_tol, scaler); }

        public void advance_sequencing(float ms_now, uint notes)
        {
            fj.op(ms_now, notes);
        }

        public float op()
        {
            // no flams
            if (fj.mod_parts[0] == 1f)
            {
                return M.neutral;
            }

            // if (fj.the_fifth_flammament) {
            //	return min_mod;
            //}

            // water down single flams
            pmod = 1f;
            foreach (float mp in fj.mod_parts)
            {
                pmod += mp;
            }
            pmod /= 5f;
            pmod = Math.Clamp(@base + pmod, min_mod, max_mod);

            // reset flags n stuff
            fj.handle_interval_end();

            return pmod;
        }
    }

    // ======================= TheThingFinder.h =======================

    /// Hand-Agnostic PatternMod detecting rolly Jumpstream.
    /// Looks for continuous segments of a pattern with jumps
    /// which physically plays as a jumptrill or a mashed roll.
    ///
    /// dev note:
    /// probably add a timing check to this as well
    public sealed class TheThingLookerFinderThing
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.TheThing;
        public readonly string name = "TheThingMod";

        // params
        public float min_mod = 0.15f;
        public float max_mod = 1f;
        public float @base = 0.05f;

        // params for tt_sequencing
        public float group_tol = 35f;
        public float step_tol = 17.5f;
        public float scaler = 0.2f;

        // sequencer
        public TT_Sequencing tt = new TT_Sequencing();
        public float pmod;                       // C++: = min_mod (set in ctor)

        public TheThingLookerFinderThing()
        {
            pmod = min_mod;
        }

        public void setup() { tt.set_params(group_tol, step_tol, scaler); }

        public void advance_sequencing(float ms_now, uint notes)
        {
            tt.op(ms_now, notes);
        }

        public float op()
        {
            pmod =
              tt.mod_parts[0] + tt.mod_parts[1] + tt.mod_parts[2] + tt.mod_parts[3];
            pmod /= 4f;
            pmod = Math.Clamp(@base + pmod, min_mod, max_mod);

            // reset flags n stuff
            tt.reset();

            return pmod;
        }
    }

    /// Hand-Agnostic PatternMod detecting rolly Jumpstream.
    /// Looks for continuous segments of a pattern with jumps
    /// which physically plays as a jumptrill or a mashed roll.
    ///
    /// dev note:
    /// probably add a timing check to this as well
    public sealed class TheThingLookerFinderThing2
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.TheThing2;
        public readonly string name = "TheThing2Mod";

        // params
        public float min_mod = 0.15f;
        public float max_mod = 1f;
        public float @base = 0.05f;

        // params for tt_sequencing
        public float group_tol = 35f;
        public float step_tol = 17.5f;
        public float scaler = 0.2f;

        // sequencer
        public TT_Sequencing2 tt2 = new TT_Sequencing2();
        public float pmod;                       // C++: = min_mod (set in ctor)

        public TheThingLookerFinderThing2()
        {
            pmod = min_mod;
        }

        public void setup() { tt2.set_params(group_tol, step_tol, scaler); }

        public void advance_sequencing(float ms_now, uint notes)
        {
            tt2.op(ms_now, notes);
        }

        public float op()
        {
            pmod = tt2.mod_parts[0] + tt2.mod_parts[1] + tt2.mod_parts[2] +
                   tt2.mod_parts[3];
            pmod /= 4f;
            pmod = Math.Clamp(@base + pmod, min_mod, max_mod);

            // reset flags n stuff
            tt2.reset();

            return pmod;
        }
    }

    // ======================= GenericChordstream.h =======================

    /// Hand-Agnostic PatternMod detecting Jumpstream.
    /// Looks for jacks, jumptrills, and jumps (2-chords)
    public sealed class GChordStreamMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.GChordStream;
        public readonly string name = "GenericChordStreamMod";
        public readonly int min_tap_size = (int)jump;

        // params
        public float min_mod = 0.6f;
        public float max_mod = 1.1f;
        public float mod_base = 0f;
        public float prop_buffer = 1f;

        public float total_prop_min;             // C++: = min_mod (set in ctor)
        public float total_prop_max;             // C++: = max_mod (set in ctor)
        public float total_prop_scaler = 2.714f; // ~19/7

        public float split_hand_pool = 1.5f;
        public float split_hand_min = 0.9f;
        public float split_hand_max = 1f;
        public float split_hand_scaler = 1f;

        public float jack_pool = 1.35f;
        public float jack_min = 0.5f;
        public float jack_max = 1f;
        public float jack_scaler = 1f;

        public float decay_factor = 0.05f;

        public float total_prop = 0f;
        public float jumptrill_prop = 0f;
        public float jack_prop = 0f;
        public float last_mod;                   // C++: = min_mod (set in ctor)
        public float pmod;                       // C++: = min_mod (set in ctor)
        public float t_taps = 0f;

        public GChordStreamMod()
        {
            total_prop_min = min_mod;
            total_prop_max = max_mod;
            last_mod = min_mod;
            pmod = min_mod;
        }

        public void full_reset()
        {
            last_mod = min_mod;
        }

        public void decay_mod()
        {
            pmod = Math.Clamp(last_mod - decay_factor, min_mod, max_mod);
            last_mod = pmod;
        }

        public float op(metaItvInfo mitvi)
        {
            ItvInfo itvi = mitvi._itvi;

            // empty interval, don't decay js mod or update last_mod
            if (itvi.total_taps == 0)
            {
                return M.neutral;
            }

            // at least 1 tap but no jumps
            if (itvi.taps_by_size[min_tap_size] == 0)
            {
                decay_mod();
                return pmod;
            }

            /* end case optimizations */

            t_taps = (float)itvi.total_taps;

            // creepy banana
            total_prop =
              (itvi.taps_by_size[min_tap_size] + prop_buffer) /
              (t_taps - prop_buffer) * total_prop_scaler;
            total_prop =
              Math.Clamp(M.fastsqrt(total_prop), total_prop_min, total_prop_max);

            // punish lots splithand jumptrills
            // uhh this might also catch oh jumptrills can't remember
            jumptrill_prop = Math.Clamp(
              split_hand_pool - ((float)mitvi.not_js / t_taps),
              split_hand_min,
              split_hand_max);

            // downscale by jack density rather than upscale like cj
            // theoretically the ohjump downscaler should handle
            // this but handling it here gives us more flexbility
            // with the ohjump mod
            jack_prop = Math.Clamp(
              jack_pool - ((float)mitvi.actual_jacks / t_taps),
              jack_min,
              jack_max);

            pmod =
              Math.Clamp(total_prop * jumptrill_prop * jack_prop, min_mod, max_mod);

            if (mitvi.dunk_it)
            {
                pmod *= 0.99f;
            }

            // set last mod, we're using it to create a decaying mod that won't
            // result in extreme spikiness if files alternate between js and
            // hs/stream
            last_mod = pmod;

            return pmod;
        }
    }
}
