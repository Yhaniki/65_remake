using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Pure tests for <see cref="HoldCapOrient.FlipY"/> — the long-note end-cap vertical-flip rule.
    /// Covers the three shipped art families: combined funnel caps (NOTEIMAGE_5/9/10), the upside-down-stored
    /// combined cap (NOTEIMAGE_8 updown), and per-lane arrow caps (NOTEIMAGE_6).
    /// </summary>
    public class HoldCapOrientTests
    {
        // ---- combined funnel cap, art stored the right way up (NOTEIMAGE_5/9/10) ----

        [Test]
        public void Combined_Normal_Up_NotFlipped()   // 向上: cap at bottom, taper points down/outward as drawn
            => Assert.IsFalse(HoldCapOrient.FlipY(perLane: false, bakedFlip: false, downScroll: false));

        [Test]
        public void Combined_Normal_Down_Flipped()     // 向下: cap at top → flip so taper points up/outward
            => Assert.IsTrue(HoldCapOrient.FlipY(perLane: false, bakedFlip: false, downScroll: true));

        // ---- combined cap whose texture ships upside-down (NOTEIMAGE_8 updown lanes): baked flip inverts both ----

        [Test]
        public void Combined_Baked_Up_Flipped()        // 向上 but art inverted → needs the extra flip
            => Assert.IsTrue(HoldCapOrient.FlipY(perLane: false, bakedFlip: true, downScroll: false));

        [Test]
        public void Combined_Baked_Down_NotFlipped()   // 向下 flip XOR baked flip cancel out
            => Assert.IsFalse(HoldCapOrient.FlipY(perLane: false, bakedFlip: true, downScroll: true));

        // ---- per-lane arrow caps (NOTEIMAGE_6): never take the scroll flip ----

        [Test]
        public void PerLane_Ignores_Scroll_Direction()
        {
            Assert.IsFalse(HoldCapOrient.FlipY(perLane: true, bakedFlip: false, downScroll: false), "向上");
            Assert.IsFalse(HoldCapOrient.FlipY(perLane: true, bakedFlip: false, downScroll: true), "向下 must NOT flip a per-lane arrow cap");
        }

        [Test]
        public void PerLane_Still_Honours_Baked_Flip()
        {
            // a per-lane cap that shipped inverted would still flip, but independent of scroll direction
            Assert.IsTrue(HoldCapOrient.FlipY(perLane: true, bakedFlip: true, downScroll: false));
            Assert.IsTrue(HoldCapOrient.FlipY(perLane: true, bakedFlip: true, downScroll: true));
        }
    }
}
