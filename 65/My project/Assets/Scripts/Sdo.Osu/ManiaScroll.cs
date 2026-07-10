using System;
using System.Collections.Generic;

namespace Sdo.Osu
{
    /// <summary>
    /// Note-scroll positioner, ported from osu!mania's default (Sequential algorithm + relative beat-length
    /// scaling) but anchored to a FIXED base tempo so every song scrolls at the same base speed — what the
    /// user asked for ("不依 BPM 改變速度基準，base 140 BPM").
    ///
    /// How it mirrors osu (see assets/osu-master):
    ///   • The base velocity (px/s at multiplier 1.0) is constant — it does NOT scale with the song's BPM.
    ///     It is calibrated with the official SDO formula  px/s = BPM × speed × 1.6  at a REFERENCE BPM of
    ///     140 (see docs / memory sdo-note-scroll-speed): vBase = 140 × speedMul × 1.6.
    ///   • Within a song the scroll still VARIES: each segment's multiplier =
    ///       SV × (mostCommonBeatLength / localBeatLength)
    ///     exactly like osu's MultiplierControlPoint (Velocity=1 for mania). At the song's most-common BPM
    ///     with SV=1 the multiplier is 1.0 (→ base speed); a ÷2/×2 BPM gimmick or an osu! green-line SV
    ///     locally speeds up / slows down the notes — RelativeScaleBeatLengths=true in mania.
    ///   • Position integrates the multiplier across segments (osu SequentialScrollAlgorithm), so a note's
    ///     on-screen distance from the judge line = vBase × ∫ multiplier dτ between now and the note time.
    ///
    /// <see cref="ConstantScroll"/> reproduces osu's "Constant Speed" mod: all multipliers are ignored, so
    /// the scroll is perfectly linear at vBase regardless of BPM/SV.
    ///
    /// Pure logic (no UnityEngine) — fully unit-testable.
    /// </summary>
    public sealed class ManiaScroll
    {
        /// <summary>Reference tempo the constant base speed is calibrated to (the user's "base 140 BPM").</summary>
        public const double DefaultReferenceBpm = 130.0;

        /// <summary>Official SDO scroll constant: on-screen px/s = BPM × speed × <see cref="OfficialPxPerBpmSpeed"/>.</summary>
        public const double OfficialPxPerBpmSpeed = 1.6;

        private readonly double _vBase;        // design-px per second at multiplier 1.0
        private readonly double[] _time;       // control-point start times (ms), ascending
        private readonly double[] _mult;       // multiplier in [_time[i], _time[i+1])
        private readonly double[] _prefix;     // ∫ multiplier dτ (ms) from _time[0] to _time[i]

        private ManiaScroll(double vBase, double[] time, double[] mult, double[] prefix)
        {
            _vBase = vBase; _time = time; _mult = mult; _prefix = prefix;
        }

        /// <summary>Base velocity in design-px/s (the speed where multiplier == 1.0).</summary>
        public double BaseVelocity => _vBase;

        /// <summary>vBase = referenceBpm × speedMul × 1.6 (design-px/s).</summary>
        public static double BaseVelocityFor(double speedMul, double referenceBpm = DefaultReferenceBpm)
            => referenceBpm * speedMul * OfficialPxPerBpmSpeed;

        /// <summary>
        /// Build a scroll positioner for <paramref name="map"/> at the given speed step.
        /// <paramref name="constantScroll"/> = osu "Constant Speed" mod (ignore all BPM/SV variation).
        /// </summary>
        public static ManiaScroll Build(OsuBeatmap map, double speedMul,
            bool constantScroll = false, double referenceBpm = DefaultReferenceBpm, bool followSongBpm = false)
        {
            // Base-velocity anchor. Default: fixed referenceBpm (every song scrolls at the same base speed).
            // followSongBpm: anchor to THIS song's most-common BPM, so the base tempo scrolls at the official
            // px/s = songBpm × speed × 1.6 and mid-song ½/×2 changes scale it to currentBpm × speed × 1.6
            // (the internal multiplier uses the same most-common beat length, so it is 1.0 at the base tempo).
            double anchorBpm = referenceBpm;
            if (followSongBpm && map != null)
            {
                double baseBeat = (map.TimingPoints != null && map.TimingPoints.Count > 0)
                    ? MostCommonBeatLength(map.TimingPoints, LastObjectMs(map)) : 0.0;
                if (baseBeat <= 0.0) baseBeat = 60000.0 / Math.Max(1.0, map.Bpm);
                anchorBpm = 60000.0 / Math.Max(1e-9, baseBeat);
            }
            double vBase = BaseVelocityFor(speedMul, anchorBpm);
            var pts = (map == null || constantScroll) ? null : BuildMultiplierPoints(map);
            if (pts == null || pts.Count == 0)
                return new ManiaScroll(vBase, new[] { 0.0 }, new[] { 1.0 }, new[] { 0.0 });

            int n = pts.Count;
            var time = new double[n];
            var mult = new double[n];
            var prefix = new double[n];
            for (int i = 0; i < n; i++) { time[i] = pts[i].time; mult[i] = pts[i].mult; }
            prefix[0] = 0.0;
            for (int i = 1; i < n; i++)
                prefix[i] = prefix[i - 1] + (time[i] - time[i - 1]) * mult[i - 1];
            return new ManiaScroll(vBase, time, mult, prefix);
        }

        /// <summary>
        /// On-screen distance (design-px) a note travels between <paramref name="fromMs"/> and
        /// <paramref name="toMs"/>. Positive when toMs &gt; fromMs (note in the future). Use as the note's
        /// distance from the judge line: Y = judgeLineY + PixelDistance(now, noteMs).
        /// </summary>
        public double PixelDistance(double fromMs, double toMs)
            => _vBase / 1000.0 * (WeightedMsAt(toMs) - WeightedMsAt(fromMs));

        /// <summary>∫ multiplier dτ from _time[0] to <paramref name="t"/> (ms). Extrapolates outside the range.</summary>
        private double WeightedMsAt(double t)
        {
            int lo = 0, hi = _time.Length - 1, s = 0;
            while (lo <= hi) { int mid = (lo + hi) >> 1; if (_time[mid] <= t) { s = mid; lo = mid + 1; } else hi = mid - 1; }
            return _prefix[s] + (t - _time[s]) * _mult[s];
        }

        // ---- control-point aggregation (mirrors osu DrawableScrollingRuleset.load + MultiplierControlPoint) ----

        private static List<(double time, double mult)> BuildMultiplierPoints(OsuBeatmap map)
        {
            var tps = map.TimingPoints;
            if (tps == null || tps.Count == 0) return null;

            double lastObjMs = LastObjectMs(map);
            double baseBeat = MostCommonBeatLength(tps, lastObjMs);
            if (baseBeat <= 0.0) baseBeat = 60000.0 / Math.Max(1.0, map.Bpm);

            // sort by (time, original index) so equal-time points keep the LAST one (osu collapses same-time).
            int n = tps.Count;
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, (a, b) =>
            {
                int c = tps[a].TimeMs.CompareTo(tps[b].TimeMs);
                return c != 0 ? c : a.CompareTo(b);
            });

            var result = new List<(double time, double mult)>(n);
            double curBeat = baseBeat;   // last uninherited beat length (before any → treat as base → mult 1)
            double curSv = 1.0;          // last inherited SV multiplier
            for (int j = 0; j < n; j++)
            {
                var p = tps[idx[j]];
                if (p.Uninherited) curBeat = p.BeatLength > 0.0 ? p.BeatLength : curBeat;
                else curSv = p.SpeedMultiplier;
                double mult = curSv * baseBeat / Math.Max(1e-9, curBeat);
                if (result.Count > 0 && result[result.Count - 1].time == p.TimeMs)
                    result[result.Count - 1] = (p.TimeMs, mult);   // same time → overwrite (keep latest)
                else
                    result.Add((p.TimeMs, mult));
            }
            return result;
        }

        /// <summary>Largest note time (hold end or tap), or 0 if none.</summary>
        private static double LastObjectMs(OsuBeatmap map)
        {
            double last = 0.0;
            var hos = map.HitObjects;
            if (hos != null)
                for (int i = 0; i < hos.Count; i++)
                {
                    var h = hos[i];
                    double t = h.EndTimeMs ?? h.StartTimeMs;
                    if (t > last) last = t;
                }
            return last;
        }

        /// <summary>
        /// The uninherited beat length covering the most total time (osu Beatmap.GetMostCommonBeatLength).
        /// Each tempo segment is weighted by its duration (this point → next tempo point, last → lastObjMs).
        /// Returns 0 if there are no uninherited points.
        /// </summary>
        internal static double MostCommonBeatLength(IReadOnlyList<OsuTimingPoint> tps, double lastObjMs)
        {
            // collect uninherited points sorted by time
            var times = new List<double>();
            var beats = new List<double>();
            for (int i = 0; i < tps.Count; i++)
                if (tps[i].Uninherited) { times.Add(tps[i].TimeMs); beats.Add(tps[i].BeatLength); }
            int m = times.Count;
            if (m == 0) return 0.0;

            var order = new int[m];
            for (int i = 0; i < m; i++) order[i] = i;
            Array.Sort(order, (a, b) => times[a].CompareTo(times[b]));

            // total time each beat length is in effect; the FIRST segment is forced to start at 0 (osu
            // GetMostCommonBeatLength does `i==0 ? 0 : t.Time`), so a chart whose first tempo point is at
            // time > 0 still weights that opening segment from the song start.
            double end = Math.Max(lastObjMs, times[order[m - 1]]);
            var durByBeat = new Dictionary<double, double>();
            for (int k = 0; k < m; k++)
            {
                double t0 = (k == 0) ? 0.0 : times[order[k]];
                double t1 = (k + 1 < m) ? times[order[k + 1]] : end;
                double bl = beats[order[k]];
                durByBeat.TryGetValue(bl, out double acc);
                durByBeat[bl] = acc + Math.Max(0.0, t1 - t0);
            }
            // pick the longest-total beat length; ties → the one encountered earliest in time (osu's
            // OrderByDescending is stable on insertion/encounter order). Strict > over time-sorted order does this.
            double best = beats[order[0]]; double bestDur = -1.0;
            for (int k = 0; k < m; k++)
            {
                double d = durByBeat[beats[order[k]]];
                if (d > bestDur) { bestDur = d; best = beats[order[k]]; }
            }
            return best;
        }
    }
}
