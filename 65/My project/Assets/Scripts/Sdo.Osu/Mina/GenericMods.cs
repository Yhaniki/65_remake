// Faithful C# port of the hand-dependent generic pattern mods (v515):
//   Dependent/HD_PatternMods/{GenericStream,GenericBracketing}.h
// Each C++ struct -> a sealed class with the same name. C++ operator() becomes op(...). Both mods take a
// const metaItvGenericHandInfo& (ported as metaItvGenericHandInfo in MetaDependent.cs). The _params vectors and XML
// load/save are dropped; the defaulted param fields (the official tuned values) are kept verbatim, along with _pmod
// and name.
//
// PORT NOTES (judgment calls, same as AgnosticMods.cs):
//  * C++ field `base` -> `@base` (C# reserved word; name preserved).
//  * C++ in-class initializers referencing a sibling field (`total_prop_min = min_mod`, `pmod = min_mod`, ...) are
//    illegal as C# instance-field initializers, so they are copied in a parameterless constructor. Field initializers
//    run before the ctor body, so the referenced defaults are already in place -> identical to C++ member init order.
using System;
using static Mina.tap_size;

namespace Mina
{
    // ======================= GenericStream.h =======================

    /// Hand-Agnostic PatternMod detecting Stream.
    /// Looks for single taps out of all taps in the interval.
    /// Begins to dampen in value if too many jacks or chords are present
    public sealed class GStreamMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.GStream;
        public readonly string name = "GenericStreamMod";
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

        public float prop_component = 0f;
        public float jack_component = 0f;
        public float pmod;                       // C++: = min_mod (set in ctor)

        public GStreamMod()
        {
            pmod = min_mod;
        }

        public void setup()
        {
        }

        public void advance_sequencing(float ms_now, uint notes)
        {
        }

        public void full_reset()
        {
        }

        public float op(metaItvGenericHandInfo mitvghi)
        {
            // it needs more taps to bracket
            if (mitvghi.total_taps < 2)
            {
                return M.neutral;
            }

            // it's all chords
            if (mitvghi.taps_by_size[_tap_size] == 0)
            {
                return min_mod;
            }

            prop_component =
              (mitvghi.taps_by_size[_tap_size] + prop_buffer) /
              ((float)mitvghi.total_taps -
               prop_buffer) *
              prop_scaler;

            pmod = M.fastsqrt(prop_component);

            pmod = Math.Clamp(@base + pmod, min_mod, max_mod);

            // actual mod
            return pmod;
        }
    }

    // ======================= GenericBracketing.h =======================

    /// Hand-Agnostic PatternMod detecting Handstream.
    /// Looks for jacks, jumptrills, and hands (3-chords)
    public sealed class GBracketingMod
    {
        public readonly CalcPatternMod _pmod = CalcPatternMod.GBracketing;
        public readonly string name = "GenericBracketingMod";
        public readonly int min_tap_size = (int)jump;

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
        public float bracket_taps = 0f;

        public GBracketingMod()
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

        public float op(metaItvGenericHandInfo mitvghi)
        {
            // empty interval, don't decay mod or update last_mod
            if (mitvghi.total_taps == 0)
            {
                return M.neutral;
            }

            // definitely no brackets, decay
            if (mitvghi.taps_bracketing == 0)
            {
                decay_mod();
                return pmod;
            }

            t_taps = (float)mitvghi.total_taps;
            bracket_taps = (float)mitvghi.taps_bracketing;

            total_prop =
              total_prop_base + ((bracket_taps + prop_buffer) /
                                 (t_taps - prop_buffer) * total_prop_scaler);
            total_prop =
              Math.Clamp(M.fastsqrt(total_prop), total_prop_min, total_prop_max);

            // limits
            pmod = Math.Clamp(total_prop, min_mod, max_mod);

            // for decay
            last_mod = pmod;

            return pmod;
        }
    }
}
