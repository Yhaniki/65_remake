using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    // The result-screen background replay must open on a GOOD slice: a ≥20s stretch where the dancer is actually
    // dancing (a dance interval), biased to its busiest window, with per-visit jitter — never always the opening.
    public class ReplayStartPickerTests
    {
        private const double MinRun = 20000.0;

        private static List<(double, double)> Ivs(params (double, double)[] xs) => new List<(double, double)>(xs);

        // A cluster of 10 notes (2s apart) sitting at 50000..68000, plus two sparse notes off to the sides.
        private static List<double> ClusterAt50k()
        {
            var s = new List<double> { 5000 };
            for (double t = 50000; t <= 68000; t += 2000) s.Add(t);
            s.Add(95000);
            return s;
        }

        [Test]
        public void Opens_On_The_Busiest_Run_When_Centred()
        {
            // one long interval, no jitter (randomUnit 0.5) → left edge of the densest 20s window = 50000
            double s = ReplayStartPicker.Pick(ClusterAt50k(), Ivs((0, 100000)), 0.5, MinRun);
            Assert.AreEqual(50000.0, s, 1e-6);
            Assert.LessOrEqual(s + MinRun, 100000.0);   // ≥20s of dance ahead
        }

        [Test]
        public void Jitter_Shifts_The_Opening_But_Keeps_The_20s_Run()
        {
            var notes = ClusterAt50k();
            // band = hiEdge - a = 80000; jitter = (u-0.5)*0.5*80000 = ±20000 around the climax (50000)
            Assert.AreEqual(30000.0, ReplayStartPicker.Pick(notes, Ivs((0, 100000)), 0.0, MinRun), 1e-6);
            Assert.AreEqual(70000.0, ReplayStartPicker.Pick(notes, Ivs((0, 100000)), 1.0, MinRun), 1e-6);
        }

        [Test]
        public void Never_Starts_Inside_The_Final_20s_Of_A_Run()
        {
            // fuzz every jitter value: the chosen start always leaves ≥20s of dance before the interval ends
            var notes = ClusterAt50k();
            for (double u = 0.0; u <= 1.0001; u += 0.05)
            {
                double s = ReplayStartPicker.Pick(notes, Ivs((0, 100000)), u, MinRun);
                Assert.GreaterOrEqual(s, 0.0);
                Assert.LessOrEqual(s + MinRun, 100000.0);
            }
        }

        [Test]
        public void Picks_The_Interval_With_The_Denser_Run()
        {
            var notes = new List<double> { 5000, 7000, 9000 };                 // 3 in the first interval
            for (double t = 120000; t <= 138000; t += 2000) notes.Add(t);      // 10 in the second
            double s = ReplayStartPicker.Pick(notes, Ivs((0, 50000), (100000, 160000)), 0.5, MinRun);
            Assert.AreEqual(120000.0, s, 1e-6);
        }

        [Test]
        public void Falls_Back_To_The_Longest_Interval_When_None_Holds_A_Full_Run()
        {
            // both intervals shorter than 20s → show the longest one from its start (max continuous dance)
            var notes = new List<double> { 5000, 25000 };
            double s = ReplayStartPicker.Pick(notes, Ivs((0, 10000), (20000, 35000)), 0.5, MinRun);
            Assert.AreEqual(20000.0, s, 1e-6);   // longest = [20000,35000]
        }

        [Test]
        public void An_Interval_Exactly_MinRun_Long_Starts_At_Its_Beginning()
        {
            double s = ReplayStartPicker.Pick(new List<double> { 35000 }, Ivs((30000, 50000)), 0.9, MinRun);
            Assert.AreEqual(30000.0, s, 1e-6);   // hiEdge == a → no room to move
        }

        [Test]
        public void No_Danceable_Intervals_Starts_At_The_Top()
        {
            Assert.AreEqual(0.0, ReplayStartPicker.Pick(new List<double> { 1000 }, Ivs(), 0.5, MinRun), 1e-6);
            Assert.AreEqual(0.0, ReplayStartPicker.Pick(null, null, 0.5, MinRun), 1e-6);
        }

        [Test]
        public void DensestRunStart_Anchors_On_A_Note_And_Respects_The_Edge()
        {
            ReplayStartPicker.DensestRunStart(new List<double> { 10, 12, 14, 100 }, 0, 90, 10, out double s, out int n);
            Assert.AreEqual(10.0, s, 1e-6);      // window [10,20] holds 10,12,14
            Assert.AreEqual(3, n);
        }

        [Test]
        public void DensestRunStart_With_No_Notes_In_Range_Returns_LoEdge()
        {
            ReplayStartPicker.DensestRunStart(new List<double> { 500, 100000 }, 30000, 60000, 20000, out double s, out int n);
            Assert.AreEqual(30000.0, s, 1e-6);
            Assert.AreEqual(0, n);
        }
    }
}
