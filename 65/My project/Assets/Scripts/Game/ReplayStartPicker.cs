using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>
    /// Picks WHERE inside the looping result-screen background replay to start, so each settle opens on a GOOD,
    /// DIFFERENT slice of the choreography instead of always the song's opening (the loop plays the whole chart at
    /// 1× real-time, and nobody sits on the result panel long enough to reach past the first few seconds).
    ///
    /// Hard requirement: the start must sit inside a stretch where the on-stage dancer (the #1 player) is ACTUALLY
    /// dancing for at least <c>minRunMs</c> continuously — so the viewer always gets a sustained run, never a spot
    /// that immediately idles / stops / loops. Among the valid starts we bias toward the CLIMAX (the busiest
    /// <c>minRunMs</c> window of note starts), then add a per-entry random jitter for variety. Pure + deterministic
    /// (the RNG is passed in as <paramref name="randomUnit"/>) so it is unit-tested independently of ScreenGameplay.
    /// </summary>
    public static class ReplayStartPicker
    {
        // jitter spans ±(this × the interval's start slack) around the climax — enough to vary the opening frame
        // between visits without ever leaving the guaranteed ≥minRunMs dance run.
        private const double JitterFraction = 0.5;

        /// <summary>
        /// Offset (ms) into the replay loop to start from. <paramref name="danceIntervals"/> are the continuous
        /// [start,end] spans (ms, ascending, non-overlapping) where the dancer is actually dancing — the choreography
        /// end and any gate-off (HP-out / settle) already clamped in. <paramref name="randomUnit"/> ∈ [0,1] jitters
        /// the pick. Prefers an interval that can hold a full <paramref name="minRunMs"/> run and opens on its busiest
        /// window; falls back to the LONGEST interval's start when none is long enough; 0 when nothing is danceable.
        /// </summary>
        public static double Pick(IReadOnlyList<double> starts,
                                  IReadOnlyList<(double start, double end)> danceIntervals,
                                  double randomUnit, double minRunMs)
        {
            if (danceIntervals == null || danceIntervals.Count == 0) return 0.0;

            // Among intervals long enough to hold a full run, find the one whose densest minRunMs window has the most
            // notes; start at that window's left edge (so the shown run IS that busy stretch).
            double bestS = double.NaN; int bestCount = -1; double runA = 0.0, runHiEdge = 0.0;
            for (int k = 0; k < danceIntervals.Count; k++)
            {
                double a = danceIntervals[k].start, b = danceIntervals[k].end;
                if (b - a < minRunMs) continue;                       // can't hold a full ≥minRunMs run
                double hiEdge = b - minRunMs;                         // last left edge that keeps [S,S+minRunMs] inside
                DensestRunStart(starts, a, hiEdge, minRunMs, out double s, out int cnt);
                if (cnt > bestCount) { bestCount = cnt; bestS = s; runA = a; runHiEdge = hiEdge; }
            }

            if (bestCount >= 0)
            {
                double jitter = (randomUnit - 0.5) * JitterFraction * (runHiEdge - runA);
                double S = bestS + jitter;
                if (S < runA) S = runA;
                if (S > runHiEdge) S = runHiEdge;                     // guarantees S + minRunMs ≤ interval end
                return S;
            }

            // No interval is ≥ minRunMs — show as much continuous dance as we can: start at the LONGEST one.
            double longestStart = 0.0, longestLen = -1.0;
            for (int k = 0; k < danceIntervals.Count; k++)
            {
                double len = danceIntervals[k].end - danceIntervals[k].start;
                if (len > longestLen) { longestLen = len; longestStart = danceIntervals[k].start; }
            }
            return longestStart;
        }

        /// <summary>
        /// Left edge S ∈ [loEdge, hiEdge] maximizing the number of <paramref name="starts"/> in [S, S+runMs], with S
        /// anchored on a note (the densest fixed-width window's left edge can always sit on a point). Ascending input.
        /// Ties keep the earliest S. When no note falls in range, returns loEdge with count 0.
        /// </summary>
        public static void DensestRunStart(IReadOnlyList<double> starts, double loEdge, double hiEdge, double runMs,
                                           out double s, out int count)
        {
            s = loEdge; count = 0;
            if (starts == null || starts.Count == 0 || hiEdge < loEdge || runMs <= 0.0) return;
            int j = 0;
            for (int i = 0; i < starts.Count; i++)
            {
                double a = starts[i];
                if (a < loEdge) continue;
                if (a > hiEdge) break;                    // left edge must keep the run inside the interval
                if (j < i) j = i;
                while (j < starts.Count && starts[j] <= a + runMs) j++;
                int c = j - i;                            // notes in [a, a+runMs] — all have index ≥ i (ascending)
                if (c > count) { count = c; s = a; }
            }
        }
    }
}
