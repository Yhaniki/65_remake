// Faithful C# port of CalcWindow.h (v515): the CalcMovingWindow<T> ring buffer shared by both the hand-agnostic and
// hand-dependent sequencers. This is the shared home for cross-cutting sequencing types that don't belong to a single
// header. NOTE ON GENERICS: C++ instantiates CalcMovingWindow<int> and CalcMovingWindow<float>; C# (pre-.NET7, Unity)
// can't do arithmetic on an unconstrained T, so we route arithmetic through IConvertible (values are small exact ints
// or floats, so float<->int round-trips are exact and reproduce the C++ results). The C++ operator()(new_val) push is
// named push() here; operator[] is the indexer.
using System;

namespace Mina
{
    /// constants from CalcWindow.h
    public static class WindowConst
    {
        public const int max_moving_window_size = 6;
        /// applies to acca and cccccc as well
        public const int ccacc_timing_check_size = 3;
    }

    /// custom moving window container that can do basic statistical operations on a dynamic window
    public sealed class CalcMovingWindow<T> where T : struct, IConvertible
    {
        // the earliest values in the window are the oldest (scanning row by row)
        private readonly T[] _itv_vals = new T[WindowConst.max_moving_window_size];

        private static float F(T v) { return v.ToSingle(null); }
        private static T From(float v)
        {
            if (typeof(T) == typeof(int)) return (T)(object)(int)v;
            return (T)(object)v;
        }

        /// C++ operator()(const T& new_val): shift window and set newest at back
        public void push(T new_val)
        {
            for (int i = 1; i < WindowConst.max_moving_window_size; ++i)
                _itv_vals[i - 1] = _itv_vals[i];
            _itv_vals[WindowConst.max_moving_window_size - 1] = new_val;
        }

        /// index moving window like an array (C++ operator[])
        public T this[int pos] { get { return _itv_vals[pos]; } }

        /// get most recent value in moving window
        public T get_now() { return _itv_vals[WindowConst.max_moving_window_size - 1]; }
        /// get oldest value in moving window
        public T get_last() { return _itv_vals[WindowConst.max_moving_window_size - 2]; }

        /// get the sum for the moving window up to a given size
        public T get_total_for_window(int window)
        {
            float o = 0f;
            int i = WindowConst.max_moving_window_size;
            while (i > WindowConst.max_moving_window_size - window) { --i; o += F(_itv_vals[i]); }
            return From(o);
        }

        /// get the max for the moving window up to a given size
        public T get_max_for_window(int window)
        {
            float o = 0f;
            int i = WindowConst.max_moving_window_size;
            while (i > WindowConst.max_moving_window_size - window) { --i; float v = F(_itv_vals[i]); o = v > o ? v : o; }
            return From(o);
        }

        /// get the min for the moving window up to a given size
        public T get_min_for_window(int window)
        {
            float o = F(get_now());
            int i = WindowConst.max_moving_window_size;
            while (i > WindowConst.max_moving_window_size - window) { --i; float v = F(_itv_vals[i]); o = v < o ? v : o; }
            return From(o);
        }

        /// get the mean for the moving window up to a given size
        public float get_mean_of_window(int window)
        {
            float o = 0f;
            int i = WindowConst.max_moving_window_size;
            while (i > WindowConst.max_moving_window_size - window) { --i; o += F(_itv_vals[i]); }
            return o / (float)window;
        }

        /// get the total for the moving window up to a given size (float)
        public float get_total_for_windowf(int window)
        {
            float o = 0f;
            int i = WindowConst.max_moving_window_size;
            while (i > WindowConst.max_moving_window_size - window) { --i; o += F(_itv_vals[i]); }
            return o;
        }

        /// get the coefficient of variance of the moving window up to a given size
        public float get_cv_of_window(int window)
        {
            float sd = 0f;
            float avg = get_mean_of_window(window);
            // if window is 4, we check values 6/5/4/3, since this window is always 6
            int i = WindowConst.max_moving_window_size;
            while (i > WindowConst.max_moving_window_size - window)
            {
                --i;
                float d = F(_itv_vals[i]) - avg;
                sd += d * d;
            }
            return M.fastsqrt(sd / (float)window) / avg;
        }

        /* cv checks for pattern detection (see long comment in CalcWindow.h) */

        /// perform cv check internally: anchor in the center, divide by factor, 4 is the middle value of the last 3
        public bool ccacc_timing_check(float factor, float threshold)
        {
            _itv_vals[4] = From(F(_itv_vals[4]) / factor);
            float o = get_cv_of_window(WindowConst.ccacc_timing_check_size);
            _itv_vals[4] = From(F(_itv_vals[4]) * factor);
            return o < threshold;
        }

        /// perform cv check internally: cc in the center, multiply by factor
        public bool acca_timing_check(float factor, float threshold)
        {
            _itv_vals[4] = From(F(_itv_vals[4]) * factor);
            float o = get_cv_of_window(WindowConst.ccacc_timing_check_size);
            _itv_vals[4] = From(F(_itv_vals[4]) / factor);
            return o < threshold;
        }

        /// perform cv check internally: cccccc formation, branch to ccacc or acca depending on which value is higher
        public bool roll_timing_check(float factor, float threshold)
        {
            bool o;
            if (M.any_ms_is_greater(F(_itv_vals[4]), F(_itv_vals[5])))
                o = ccacc_timing_check(factor, threshold);
            else
                o = acca_timing_check(factor, threshold);
            return o;
        }

        /// set everything to zero
        public void zero() { for (int i = 0; i < _itv_vals.Length; i++) _itv_vals[i] = default(T); }
        public void fill(T val) { for (int i = 0; i < _itv_vals.Length; i++) _itv_vals[i] = val; }

        /// C++ returns this container by value in a couple of places (e.g. SequencerGeneral::get_mw_sc_ms), which pattern
        /// mods then run mutating timing checks on. Clone preserves those value semantics.
        public CalcMovingWindow<T> Clone()
        {
            var c = new CalcMovingWindow<T>();
            Array.Copy(_itv_vals, c._itv_vals, _itv_vals.Length);
            return c;
        }
    }
}
