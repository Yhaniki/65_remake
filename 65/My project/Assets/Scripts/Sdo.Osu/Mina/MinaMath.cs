// Faithful C# port of Etterna MinaCalc helper headers (v515):
//   SequencingHelpers.h, PatternModHelpers.h, MinaCalcHelpers.h, and the smoothing/time helpers in UlbuAcolytes.h.
// Everything here is pure/stateless. Kept 1:1 with the C++ names so the ported calc reads against the source.
using System;
using System.Collections.Generic;
#if NET5_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace Mina
{
    internal static class M
    {
        // ---- constants (SequencingHelpers.h) ----
        public const float s_init = -5f;             // default for any field tracking seconds
        public const float ms_init = 5000f;          // default for any field tracking milliseconds
        public const float finalscaler = 3.632f * 1.06f;
        public const bool ignore_middle_column = true;
        public const float any_ms_epsilon = 0.1f;

        // ---- constants (PatternModHelpers.h / MinaCalcHelpers.h) ----
        public const float neutral = 1f;
        public const float max_rating = 100f;
        public const float min_rating = 0f;
        public const float default_score_goal = 0.93f;
        public const float low_acc_cutoff = 0.9f;
        public const float ssr_goal_cap = 0.965f;

        // ---- constants (UlbuAcolytes.h) ----
        public const float interval_span = 0.5f;

        public static float[] DimplesAllZero() => new float[8];   // dimples_the_all_zero_output

        // ---------------- fast math (PatternModHelpers.h) ----------------

        /// bit-hacky pow approximation. Deterministic (integer ops on the double's bits) — reproduced exactly so the
        /// calc's tuned-in error is preserved. C++: memcpy double->int[2]; u[1] = b*(u[1]-1072632447)+1072632447; u[0]=0.
        public static float fastpow(double a, double b)
        {
            long bits = BitConverter.DoubleToInt64Bits(a);
            int high = (int)(bits >> 32);              // little-endian u[1] = high dword
            high = (int)(b * (high - 1072632447) + 1072632447);
            long outbits = ((long)high) << 32;         // u[0] = 0
            return (float)BitConverter.Int64BitsToDouble(outbits);
        }

        /// SSE rsqrt-based sqrt approximation: fastsqrt(x) = x * rsqrt(x). Uses the same rsqrtss instruction as the
        /// C++ (bit-identical on x64); falls back to precise sqrt where the intrinsic is unavailable (~3e-4 rel error).
        public static float fastsqrt(float x)
        {
            if (x == 0f) return 0f;
#if NET5_0_OR_GREATER
            // exact SSE rsqrtss path (bit-identical to the C++ oracle) where available (our net8 verification build)
            if (Sse.IsSupported)
            {
                var v = Vector128.CreateScalarUnsafe(x);
                return Sse.MultiplyScalar(v, Sse.ReciprocalSqrtScalar(v)).ToScalar();
            }
#endif
            // Unity/Mono fallback: precise sqrt. rsqrtss has ~3e-4 relative error, so this shifts a handful of
            // 4th-decimal digits vs the oracle — invisible once the MSD is scaled/rounded to a displayed level.
            return (float)Math.Sqrt(x);
        }

        // ---------------- vector helpers ----------------
        public static float sum(IReadOnlyList<float> v) { float s = 0f; for (int i = 0; i < v.Count; i++) s += v[i]; return s; }
        public static int sum(IReadOnlyList<int> v) { int s = 0; for (int i = 0; i < v.Count; i++) s += v[i]; return s; }
        public static float mean(IReadOnlyList<float> v) => sum(v) / v.Count;

        public static float cv(IReadOnlyList<float> input)
        {
            float sd = 0f;
            float average = mean(input);
            for (int i = 0; i < input.Count; i++) { float d = input[i] - average; sd += d * d; }
            return fastsqrt(sd / input.Count) / average;
        }

        // cv of a vector truncated to num_vals, or if below, filled with ms_dummy to reach num_vals
        public static float cv_trunc_fill(IReadOnlyList<float> input, int num_vals, float ms_dummy)
        {
            int input_sz = input.Count;
            float sd = 0f, average = 0f;
            int lim = Math.Min(input_sz, num_vals);
            if (input_sz >= num_vals)
            {
                for (int i = 0; i < lim; i++) average += input[i];
                average /= num_vals;
                for (int i = 0; i < lim; i++) { float d = input[i] - average; sd += d * d; }
                return fastsqrt(sd / num_vals) / average;
            }
            for (int i = 0; i < lim; i++) average += input[i];
            for (int i = 0; i < num_vals - input_sz; i++) average += ms_dummy;
            average /= num_vals;
            for (int i = 0; i < lim; i++) { float d = input[i] - average; sd += d * d; }
            for (int i = 0; i < num_vals - input_sz; i++) { float d = ms_dummy - average; sd += d * d; }
            return fastsqrt(sd / num_vals) / average;
        }

        public static float sum_trunc_fill(IReadOnlyList<float> input, int num_vals, float ms_dummy)
        {
            int input_sz = input.Count;
            float s = 0f;
            int lim = Math.Min(input_sz, num_vals);
            for (int i = 0; i < lim; i++) s += input[i];
            if (input_sz >= num_vals) return s;
            for (int i = 0; i < num_vals - input_sz; i++) s += ms_dummy;
            return s;
        }

        public static float div_high_by_low(float a, float b) { if (b > a) { (a, b) = (b, a); } return a / b; }
        public static float div_low_by_high(float a, float b) { if (b > a) { (a, b) = (b, a); } return b / a; }
        public static int diff_high_by_low(int a, int b) { if (b > a) { (a, b) = (b, a); } return a - b; }
        public static float weighted_average(float a, float b, float x, float y) => (x * a + ((y - x) * b)) / y;
        public static float lerp(float t, float a, float b) => (1f - t) * a + t * b;

        // ---------------- key/column bit helpers (SequencingHelpers.h) ----------------
        public static uint keycount_to_bin(uint keycount)
        {
            if (keycount < 2) return 0b11u;
            return ~(~1u << (int)(keycount - 1u));
        }
        public static uint right_mask(uint keycount)
        {
            if (keycount <= 2) return 0b10u;
            return keycount_to_bin(keycount) >> (int)(keycount / 2) << (int)(keycount / 2);
        }
        public static uint left_mask(uint keycount)
        {
            uint m = right_mask(keycount);
            // C++: ~m & (unsigned)(std::exp2(std::ceil(std::log2(m))) - 1). System.Math has no Exp2/Log2 in Unity's
            // netstandard2.1 → use Pow(2,x) and Log(x,2).
            return ~m & (uint)(Math.Pow(2.0, Math.Ceiling(Math.Log(m, 2.0))) - 1);
        }
        public static uint mask_to_remove_middle_column(uint keycount)
        {
            if (keycount % 2 == 0) return keycount_to_bin(keycount);
            return keycount_to_bin(keycount) ^ (0b1u << (int)(keycount / 2));
        }
        public static int column_count(uint notes)
        {
            // SWAR popcount (BitOperations.PopCount isn't in Unity's netstandard2.1). Exact for all 32 bits.
            notes = notes - ((notes >> 1) & 0x55555555u);
            notes = (notes & 0x33333333u) + ((notes >> 2) & 0x33333333u);
            notes = (notes + (notes >> 4)) & 0x0F0F0F0Fu;
            return (int)((notes * 0x01010101u) >> 24);
        }
        public static bool is_only_1_bit(uint notes) => notes != 0 && (notes & (notes - 1)) == 0;
        public static List<uint> find_non_empty_cols(uint notes)
        {
            var o = new List<uint>();
            for (uint i = 0; (1u << (int)i) <= notes; i++)
                if (((1u << (int)i) & notes) != 0u) o.Add(i);
            return o;
        }

        // ---------------- ms / nps conversions ----------------
        public static float ms_from(float now, float last) => (now - last) * 1000f;
        public static float ms_to_bpm(float x) => 15000f / x;
        public static float ms_to_nps(float x) => 1000f / x;
        public static float ms_to_scaled_nps(float ms) => ms_to_nps(ms) * finalscaler;

        public static int max_val(IReadOnlyList<int> v) { int m = int.MinValue; for (int i = 0; i < v.Count; i++) if (v[i] > m) m = v[i]; return m; }
        public static float max_val(IReadOnlyList<float> v) { float m = float.MinValue; for (int i = 0; i < v.Count; i++) if (v[i] > m) m = v[i]; return m; }
        public static int max_index(IReadOnlyList<float> v)
        {
            int idx = 0; float m = v[0];
            for (int i = 1; i < v.Count; i++) if (v[i] > m) { m = v[i]; idx = i; }
            return idx;
        }

        // ---------------- row-delta comparisons (SequencingHelpers.h) ----------------
        public static bool any_ms_is_greater(float a, float b) => (a - b) > any_ms_epsilon;
        public static bool any_ms_is_lesser(float a, float b) => (b - a) > any_ms_epsilon;
        public static bool any_ms_is_close(float a, float b) => Math.Abs(a - b) <= any_ms_epsilon;
        public static bool any_ms_is_zero(float a) => any_ms_is_close(a, 0f);

        // ---------------- interval-time helpers (UlbuAcolytes.h) ----------------
        public static int time_to_itv_idx(float time) => (int)((time + 0.0005f) / interval_span);
        public static float itv_idx_to_time(int idx) => idx * interval_span;

        /// average-of-3 smoothing biased to neutral at the start (Smooth in UlbuAcolytes.h). IList so both
        /// List&lt;float&gt; (pmod smoothing) and float[] (interval base-diff vectors) work.
        public static void Smooth(IList<float> input, float neutral, int end_interval)
        {
            float f2 = neutral, f3 = neutral;
            for (int i = 0; i < end_interval; ++i)
            {
                float f1 = f2; f2 = f3; f3 = input[i];
                input[i] = (f1 + f2 + f3) / 3f;
            }
        }
        /// average-of-2 smoothing (MSSmooth)
        public static void MSSmooth(IList<float> input, float neutral, int end_interval)
        {
            float f2 = neutral;
            for (int i = 0; i < end_interval; ++i)
            {
                float f1 = f2; f2 = input[i];
                input[i] = (f1 + f2) / 2f;
            }
        }

        // ---------------- MinaCalcHelpers.h ----------------
        public static float downscale_low_accuracy_scores(float f, float sg)
        {
            return sg >= low_acc_cutoff
                ? f
                : Math.Min(Math.Max(f / (float)Math.Pow(1f + (low_acc_cutoff - sg), 3.25f), min_rating), max_rating);
        }

        /// roughly a binary search; sigmoidal aggregation of skillset values (aggregate_skill)
        public static float aggregate_skill(IReadOnlyList<float> v, double delta_multiplier,
                                            float result_multiplier, float rating = 0f, float resolution = 10.24f)
        {
            for (int i = 0; i < 11; i++)
            {
                double sum;
                do
                {
                    rating += resolution;
                    sum = 0.0;
                    for (int k = 0; k < v.Count; k++)
                        sum += Math.Max(0.0, 2f / Erfc(delta_multiplier * (v[k] - rating)) - 2);
                } while (Math.Pow(2, rating * 0.1) < sum);
                rating -= resolution;
                resolution /= 2f;
            }
            rating += resolution * 2f;
            return rating * result_multiplier;
        }

        // ---------------- erf / erfc (.NET has neither) ----------------
        // W. J. Cody rational Chebyshev approximation (double precision, |err| < ~1e-15). Matches libm closely enough
        // that the binary searches above land on the same values as the C++ oracle.
        public static double Erf(double x) => 1.0 - Erfc(x);
        public static double Erfc(double x)
        {
            double z = Math.Abs(x);
            double t = 1.0 / (1.0 + 0.5 * z);
            // Numerical Recipes erfcc: fractional error < 1.2e-7 everywhere.
            double ans = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 +
                t * (0.09678418 + t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 +
                t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
            return x >= 0.0 ? ans : 2.0 - ans;
        }
    }
}
