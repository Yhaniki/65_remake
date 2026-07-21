using System;
using System.Collections.Generic;

namespace Sdo.Osu
{
    /// <summary>
    /// osu!mania Star Rating, ported from osu! lazer ManiaDifficultyCalculator (faithful to the reference Python in
    /// H:\bms\tools\bms_sdo\gn_master_core.py — osu_calculate_star_rating). Works on any <see cref="OsuBeatmap"/>, so
    /// StepMania charts (converted to an OsuBeatmap by <see cref="SmChart.ToBeatmap"/>) get a star rating on the SAME
    /// scale as osu maps — that's how both get a displayed LEVEL.
    ///
    /// <see cref="Level"/> = round(star × 7), clamped 1..99 (2.35★→16, 5.0★→35, 8.0★→56). The scale factor is a
    /// display convention only — it stretches the usable star range over more of the 1..99 LV band than the
    /// reference tool's ×5 did. Engine-free / unit-testable.
    /// </summary>
    public static class ManiaStarRating
    {
        private const double SectionLength = 400.0;
        private const double DecayWeight = 0.9;
        private const double IndividualDecayBase = 0.125;
        private const double OverallDecayBase = 0.30;
        private const double DifficultyMultiplier = 0.018;
        private const double Eps = 1.0;              // osu Precision.DefinitelyBigger default (1 ms)
        private const int LevelMin = 1, LevelMax = 99;
        private const double LevelPerStar = 7.0;     // display scale: LV = round(star × this)

        /// <summary>GN level for a chart = round(star × 7), clamped 1..99. 0-note charts → LevelMin.</summary>
        public static int Level(OsuBeatmap bm) => LevelFromStar(Calculate(bm));

        /// <summary>round(star × 7) clamped to 1..99.</summary>
        public static int LevelFromStar(double star)
        {
            int v = (int)Math.Round(star * LevelPerStar, MidpointRounding.AwayFromZero);
            return Math.Max(LevelMin, Math.Min(LevelMax, v));
        }

        /// <summary>The osu!mania star rating of a beatmap (no mods / clock rate 1.0). 0 for empty/non-mania.</summary>
        public static double Calculate(OsuBeatmap bm)
        {
            if (bm == null) return 0.0;
            int keyCount = bm.Keys > 0 ? bm.Keys : 4;
            var objects = BuildDifficultyObjects(bm, keyCount);
            if (objects.Count == 0) return 0.0;

            var individualStrains = new double[keyCount];
            double highestIndividualStrain = 0.0;
            double overallStrain = 1.0;
            double currentStrain = 0.0;

            var strainPeaks = new List<double>();
            double currentSectionPeak = 0.0;
            double currentSectionEnd = 0.0;

            for (int i = 0; i < objects.Count; i++)
            {
                var cur = objects[i];
                if (i == 0)
                    currentSectionEnd = Math.Ceiling(cur.StartTime / SectionLength) * SectionLength;

                while (cur.StartTime > currentSectionEnd)
                {
                    strainPeaks.Add(currentSectionPeak);
                    double prevStart = i > 0 ? objects[i - 1].StartTime : cur.StartTime;
                    double offset = currentSectionEnd - prevStart;
                    currentSectionPeak = ApplyDecay(highestIndividualStrain, offset, IndividualDecayBase)
                                       + ApplyDecay(overallStrain, offset, OverallDecayBase);
                    currentSectionEnd += SectionLength;
                }

                individualStrains[cur.Column] = ApplyDecay(individualStrains[cur.Column], cur.ColumnStrainTime, IndividualDecayBase);
                individualStrains[cur.Column] += EvalIndividual(cur);

                if (cur.DeltaTime <= 1)
                    highestIndividualStrain = Math.Max(highestIndividualStrain, individualStrains[cur.Column]);
                else
                    highestIndividualStrain = individualStrains[cur.Column];

                overallStrain = ApplyDecay(overallStrain, cur.DeltaTime, OverallDecayBase);
                overallStrain += EvalOverall(cur);

                double strainValueOf = highestIndividualStrain + overallStrain - currentStrain;
                currentStrain += strainValueOf;   // = highestIndividualStrain + overallStrain

                if (currentStrain > currentSectionPeak) currentSectionPeak = currentStrain;
            }
            strainPeaks.Add(currentSectionPeak);

            var peaks = new List<double>();
            foreach (var p in strainPeaks) if (p > 0) peaks.Add(p);
            peaks.Sort((a, b) => b.CompareTo(a));   // descending

            double difficulty = 0.0, weight = 1.0;
            foreach (var p in peaks) { difficulty += p * weight; weight *= DecayWeight; }
            return difficulty * DifficultyMultiplier;
        }

        private sealed class Obj
        {
            public int Column;
            public double StartTime, EndTime, DeltaTime, ColumnStrainTime;
            public Obj[] Previous;   // last object per column as seen at the PREVIOUS note
        }

        private static List<Obj> BuildDifficultyObjects(OsuBeatmap bm, int keyCount)
        {
            // (column, start, end); end = hold tail else start.
            int n = bm.HitObjects.Count;
            var raw = new List<double[]>(n);   // [col, start, end, origIndex]
            for (int i = 0; i < n; i++)
            {
                var o = bm.HitObjects[i];
                int col = o.Lane; if (col < 0) col = 0; if (col > keyCount - 1) col = keyCount - 1;
                double start = o.StartTimeMs;
                double end = o.EndTimeMs ?? o.StartTimeMs;
                raw.Add(new double[] { col, start, end, i });
            }
            // stable sort by round(start) (osu sorts by int(round(StartTime)); tie-break on original index for stability).
            raw.Sort((a, b) =>
            {
                long ra = (long)Math.Round(a[1], MidpointRounding.AwayFromZero);
                long rb = (long)Math.Round(b[1], MidpointRounding.AwayFromZero);
                int c = ra.CompareTo(rb);
                return c != 0 ? c : a[3].CompareTo(b[3]);
            });

            var objects = new List<Obj>(Math.Max(0, n - 1));
            var perColumnLast = new Obj[keyCount];
            for (int i = 1; i < raw.Count; i++)
            {
                int col = (int)raw[i][0];
                double rawStart = raw[i][1], rawEnd = raw[i][2];
                double prevRawStart = raw[i - 1][1];
                double start = rawStart, end = rawEnd, delta = rawStart - prevRawStart;   // clock rate = 1

                var sameColPrev = perColumnLast[col];
                double columnStrainTime = sameColPrev != null ? start - sameColPrev.StartTime : start;

                Obj[] prevHit;
                if (objects.Count > 0)
                {
                    var prevNote = objects[objects.Count - 1];
                    prevHit = (Obj[])prevNote.Previous.Clone();
                    prevHit[prevNote.Column] = prevNote;
                }
                else prevHit = new Obj[keyCount];

                var obj = new Obj
                {
                    Column = col, StartTime = start, EndTime = end,
                    DeltaTime = delta, ColumnStrainTime = columnStrainTime, Previous = prevHit,
                };
                objects.Add(obj);
                perColumnLast[col] = obj;
            }
            return objects;
        }

        private static double EvalIndividual(Obj cur)
        {
            double holdFactor = 1.0;
            foreach (var prev in cur.Previous)
            {
                if (prev == null) continue;
                if (Bigger(prev.EndTime, cur.EndTime) && Bigger(cur.StartTime, prev.StartTime)) { holdFactor = 1.25; break; }
            }
            return 2.0 * holdFactor;
        }

        private static double EvalOverall(Obj cur)
        {
            double start = cur.StartTime, end = cur.EndTime;
            bool isOverlapping = false;
            double closestEnd = Math.Abs(end - start);
            double holdFactor = 1.0;
            foreach (var prev in cur.Previous)
            {
                if (prev == null) continue;
                if (Bigger(prev.EndTime, start) && Bigger(end, prev.EndTime) && Bigger(start, prev.StartTime))
                    isOverlapping = true;
                if (Bigger(prev.EndTime, end) && Bigger(start, prev.StartTime))
                    holdFactor = 1.25;
                closestEnd = Math.Min(closestEnd, Math.Abs(end - prev.EndTime));
            }
            double holdAddition = isOverlapping ? Logistic(closestEnd, 0.27, 30.0) : 0.0;
            return (1.0 + holdAddition) * holdFactor;
        }

        private static double ApplyDecay(double value, double deltaMs, double decayBase)
            => deltaMs <= 0 ? value : value * Math.Pow(decayBase, deltaMs / 1000.0);

        private static bool Bigger(double a, double b) => (a - b) > Eps;
        private static double Logistic(double x, double mult, double mid) => 1.0 / (1.0 + Math.Exp(-mult * (x - mid)));
    }
}
