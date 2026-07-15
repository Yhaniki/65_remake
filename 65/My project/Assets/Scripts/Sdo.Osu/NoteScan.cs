using System.Collections.Generic;

namespace Sdo.Osu
{
    /// <summary>
    /// Pure windowing helpers so the gameplay loop scans only the notes that matter this frame instead of the
    /// whole chart. On dense charts (5k–10k notes) a per-frame <c>foreach</c> over every note is the real cost —
    /// most iterations touch notes hours away or long retired. These functions bound the scan to a small live
    /// window over a START-TIME-ASCENDING note list.
    ///
    /// No UnityEngine — unit-tested (see NoteScanTests).
    /// </summary>
    public static class NoteScan
    {
        /// <summary>
        /// Exclusive upper index for a time-windowed scan: the first index in <c>[from, count]</c> whose start
        /// time is strictly greater than <paramref name="limitMs"/>. Every index in <c>[from, result)</c> has
        /// start ≤ limitMs, so a judge scan (heads due at/behind <c>now</c>, nearest-hittable within a window)
        /// can iterate <c>[from, result)</c> and never walk the future tail of the chart.
        ///
        /// Requires <paramref name="startsAsc"/> to be ascending from <paramref name="from"/> onward (the note
        /// list is sorted by start time at load). Binary search → O(log n).
        /// </summary>
        public static int UpperBound(IReadOnlyList<double> startsAsc, int from, double limitMs)
        {
            int lo = from < 0 ? 0 : from;
            int hi = startsAsc.Count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (startsAsc[mid] <= limitMs) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// Advance a "first still-live note" cursor past leading retired notes. <paramref name="retired"/>[i] is
        /// true once note i is fully done (judged AND scrolled off), so the returned index is the smallest
        /// <c>i ≥ firstAlive</c> with <c>retired[i] == false</c> (or <c>Count</c> if all are retired). Callers
        /// start every per-frame scan there instead of at 0.
        /// </summary>
        public static int Advance(IReadOnlyList<bool> retired, int firstAlive)
        {
            int i = firstAlive < 0 ? 0 : firstAlive;
            while (i < retired.Count && retired[i]) i++;
            return i;
        }
    }
}
