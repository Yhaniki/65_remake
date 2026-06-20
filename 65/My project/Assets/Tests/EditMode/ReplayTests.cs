using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    public class ReplayTests
    {
        [Test]
        public void Record_AppendsOnlyOnMaskChange()
        {
            var r = new Replay();
            Assert.IsTrue(r.Record(0, 0));     // first frame always recorded
            Assert.IsFalse(r.Record(10, 0));   // unchanged mask -> dedup
            Assert.IsTrue(r.Record(20, 1));    // change -> recorded
            Assert.IsFalse(r.Record(30, 1));   // unchanged
            Assert.IsTrue(r.Record(40, 0));    // change back
            Assert.AreEqual(3, r.Frames.Count);
        }

        [Test]
        public void Record_SameTimestamp_ReplacesLatestMask()
        {
            var r = new Replay();
            r.Record(0, 1);
            r.Record(0, 3);    // same time, different mask -> overwrite, not append
            Assert.AreEqual(1, r.Frames.Count);
            Assert.AreEqual(3, r.Frames[0].KeyMask);
        }

        [Test]
        public void MaskAt_ReturnsMostRecentFrameAtOrBeforeTime()
        {
            var r = new Replay();
            r.Record(0, 0);
            r.Record(100, 1);   // lane 0 down
            r.Record(200, 5);   // lanes 0 and 2 down
            r.Record(300, 0);   // all up
            Assert.AreEqual(0, r.MaskAt(50));
            Assert.AreEqual(1, r.MaskAt(100));
            Assert.AreEqual(1, r.MaskAt(150));
            Assert.AreEqual(5, r.MaskAt(250));
            Assert.AreEqual(0, r.MaskAt(999));
        }

        [Test]
        public void MaskAt_BeforeFirstFrame_IsZero()
        {
            var r = new Replay();
            r.Record(100, 7);
            Assert.AreEqual(0, r.MaskAt(0));
        }

        [Test]
        public void LengthMs_IsLastFrameTime()
        {
            var r = new Replay();
            Assert.AreEqual(0.0, r.LengthMs);
            r.Record(0, 1);
            r.Record(1234.5, 0);
            Assert.AreEqual(1234.5, r.LengthMs);
        }

        [Test]
        public void Clear_ResetsFramesAndDedupState()
        {
            var r = new Replay();
            r.Record(0, 1);
            r.Clear();
            Assert.AreEqual(0, r.Frames.Count);
            Assert.IsTrue(r.Record(0, 0));   // after clear, first frame records again (dedup state reset)
        }
    }
}
