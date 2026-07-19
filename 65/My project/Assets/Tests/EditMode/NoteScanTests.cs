using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// NoteScan = the per-frame "which notes to look at" window that lets a 5k–10k-note chart scan only its live
    /// slice. UpperBound is the exclusive end of a time-windowed judge scan; Advance skips leading retired notes.
    /// </summary>
    public class NoteScanTests
    {
        static readonly List<double> Starts = new List<double> { 0, 100, 100, 250, 500, 900, 900, 1500 };

        [Test]
        public void UpperBound_ExcludesEverythingStrictlyAfterLimit()
        {
            // limit 250 → indices with start ≤ 250 are 0..3 (0,100,100,250); first start>250 is index 4.
            Assert.AreEqual(4, NoteScan.UpperBound(Starts, 0, 250));
        }

        [Test]
        public void UpperBound_IncludesAllEqualToLimit()
        {
            // both notes at 900 are ≤ 900 → they are inside the window; first start>900 is index 7 (1500).
            Assert.AreEqual(7, NoteScan.UpperBound(Starts, 0, 900));
        }

        [Test]
        public void UpperBound_HonoursFromCursor()
        {
            // starting the scan at index 4 never looks back at the retired prefix.
            Assert.AreEqual(7, NoteScan.UpperBound(Starts, 4, 900));
            Assert.AreEqual(5, NoteScan.UpperBound(Starts, 4, 500));
        }

        [Test]
        public void UpperBound_LimitBeforeEverything_IsFrom()
        {
            Assert.AreEqual(0, NoteScan.UpperBound(Starts, 0, -1));
        }

        [Test]
        public void UpperBound_LimitAfterEverything_IsCount()
        {
            Assert.AreEqual(Starts.Count, NoteScan.UpperBound(Starts, 0, 99999));
        }

        [Test]
        public void UpperBound_EmptyList_IsFrom()
        {
            Assert.AreEqual(0, NoteScan.UpperBound(new List<double>(), 0, 100));
        }

        [Test]
        public void UpperBound_FromPastEnd_IsCount()
        {
            // cursor already at the end (all retired) → nothing to scan.
            Assert.AreEqual(Starts.Count, NoteScan.UpperBound(Starts, Starts.Count, 100));
        }

        [Test]
        public void Advance_SkipsLeadingRetired()
        {
            var retired = new List<bool> { true, true, false, false, true };
            Assert.AreEqual(2, NoteScan.Advance(retired, 0));
        }

        [Test]
        public void Advance_StaysWhenCurrentIsLive()
        {
            var retired = new List<bool> { true, false, true, false };
            Assert.AreEqual(1, NoteScan.Advance(retired, 1));   // index 1 already live → no move
        }

        [Test]
        public void Advance_AllRetired_IsCount()
        {
            var retired = new List<bool> { true, true, true };
            Assert.AreEqual(3, NoteScan.Advance(retired, 0));
        }

        [Test]
        public void Advance_DoesNotWalkBackBehindCursor()
        {
            // a live note behind the cursor is irrelevant: Advance only moves forward from firstAlive.
            var retired = new List<bool> { false, true, true, false };
            Assert.AreEqual(3, NoteScan.Advance(retired, 1));
        }
    }
}
