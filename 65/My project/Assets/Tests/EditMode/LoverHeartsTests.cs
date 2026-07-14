using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    /// <summary>
    /// Faithful-port tests for <see cref="LoverHearts"/> against the CN online client behaviour
    /// (sdo.bin.c:464933-464951). See docs/reverse-engineering/SDO_COUPLE_MODE.md §2.
    /// </summary>
    public class LoverHeartsTests
    {
        [Test]
        public void Starts_Empty()
        {
            var h = new LoverHearts();
            Assert.AreEqual(0, h.Count(0));
            Assert.AreEqual(0, h.Count(1));
            Assert.AreEqual(0, h.Total);
            Assert.IsFalse(h.WinkRowA);
            Assert.IsFalse(h.WinkRowB);
        }

        [Test]
        public void AddHeart_Increments_One_Per_Call()
        {
            var h = new LoverHearts();
            h.AddHeart(0);
            h.AddHeart(0);
            h.AddHeart(0);
            Assert.AreEqual(3, h.Count(0));
            Assert.AreEqual(3, h.Total);
        }

        [Test]
        public void AddHeart_Is_Clamped_At_20_Without_Failing()
        {
            var h = new LoverHearts();
            for (int i = 0; i < 25; i++) h.AddHeart(0);   // over the clamp
            Assert.AreEqual(LoverHearts.MaxPerDancer, h.Count(0)); // 20, not 25
            Assert.AreEqual(20, h.Count(0));
            Assert.IsTrue(h.IsFull(0));
        }

        [Test]
        public void Slots_Are_Independent()
        {
            var h = new LoverHearts();
            h.AddHeart(0);
            h.AddHeart(1);
            h.AddHeart(1);
            Assert.AreEqual(1, h.Count(0));
            Assert.AreEqual(2, h.Count(1));
            Assert.AreEqual(3, h.Total);
        }

        // The decompiled address math (param&1)+(param>>1)*2 is the identity — document it so a
        // future change that "simplifies" it can't silently diverge from the binary.
        [TestCase(0, 0)]
        [TestCase(1, 1)]
        [TestCase(2, 2)]
        [TestCase(3, 3)]
        [TestCase(7, 7)]
        [TestCase(0xDF, 0xDF)]
        public void SlotFromParam_Equals_Param(int param, int expected)
        {
            Assert.AreEqual(expected, LoverHearts.SlotFromParam(param));
        }

        [Test]
        public void ApplyEvent_Below_0xE0_Adds_A_Heart_To_That_Slot()
        {
            var h = new LoverHearts();
            h.ApplyEvent(0);      // slot 0
            h.ApplyEvent(1);      // slot 1
            h.ApplyEvent(1);      // slot 1 again
            Assert.AreEqual(1, h.Count(0));
            Assert.AreEqual(2, h.Count(1));
            Assert.IsFalse(h.WinkRowA);
            Assert.IsFalse(h.WinkRowB);
        }

        [Test]
        public void ApplyEvent_0xE0_To_0xEF_Sets_RowB_Wink_No_Count_Change()
        {
            var h = new LoverHearts();
            h.ApplyEvent(0xE5);
            Assert.IsTrue(h.WinkRowB);
            Assert.IsFalse(h.WinkRowA);
            Assert.AreEqual(0, h.Total);   // celebration flag only, no heart added
        }

        [Test]
        public void ApplyEvent_At_Or_Above_0xF0_Sets_RowA_Wink_No_Count_Change()
        {
            var h = new LoverHearts();
            h.ApplyEvent(0xF3);
            Assert.IsTrue(h.WinkRowA);
            Assert.IsFalse(h.WinkRowB);
            Assert.AreEqual(0, h.Total);
        }

        [Test]
        public void ClearWinks_Resets_Only_Flags()
        {
            var h = new LoverHearts();
            h.AddHeart(0);
            h.ApplyEvent(0xF0);
            h.ApplyEvent(0xE0);
            h.ClearWinks();
            Assert.IsFalse(h.WinkRowA);
            Assert.IsFalse(h.WinkRowB);
            Assert.AreEqual(1, h.Count(0));   // hearts untouched
        }

        [Test]
        public void Reset_Clears_Hearts_And_Flags()
        {
            var h = new LoverHearts();
            h.AddHeart(0);
            h.AddHeart(1);
            h.ApplyEvent(0xF0);
            h.Reset();
            Assert.AreEqual(0, h.Total);
            Assert.IsFalse(h.WinkRowA);
            Assert.IsFalse(h.WinkRowB);
        }
    }
}
