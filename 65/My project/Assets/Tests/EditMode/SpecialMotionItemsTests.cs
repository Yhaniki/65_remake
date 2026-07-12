using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>The decompiled special-item idle/walk traits (<see cref="SpecialMotionItems"/>): flying wings float
    /// (flystay idle + forward-lean glide walk + hover) and speed shoes bump the walk to 5.0. Pure logic — no Unity/disk.</summary>
    public class SpecialMotionItemsTests
    {
        // ---- flying-wing id membership: the 5 decompiled ids (gameplay/023:4068) + curated online additions ----

        [TestCase(8448)]  [TestCase(8449)]  [TestCase(8483)]  [TestCase(8484)]  [TestCase(20003)]  // decompiled 5
        [TestCase(26394)]                                                                          // online-confirmed (Fly 甜心飛翼)
        public void FlyingWing_Ids_AreRecognised(int modelId)
            => Assert.IsTrue(SpecialMotionItems.IsFlyingWing(modelId));

        [TestCase(11106)] [TestCase(11107)] [TestCase(11114)] [TestCase(11115)]
        public void FastWalkShoe_Ids_AreRecognised(int modelId)
            => Assert.IsTrue(SpecialMotionItems.IsFastWalkShoe(modelId));

        [Test]
        public void NonFlyingWings_AreNotRecognised()
        {
            // 036937 ships a _CHIBANG_G glide mesh but is NOT a flying wing — the earlier "_G exists" heuristic wrongly
            // floated it. It (and any wing not on the curated list) must NOT be flagged.
            Assert.IsFalse(SpecialMotionItems.IsFlyingWing(36937));
            Assert.IsFalse(SpecialMotionItems.IsFlyingWing(23108));   // an ordinary wing
        }

        [Test]
        public void CrossTrait_IdsDoNotLeak()
        {
            Assert.IsFalse(SpecialMotionItems.IsFastWalkShoe(8448));  // a flying WING is not a speed shoe
            Assert.IsFalse(SpecialMotionItems.IsFlyingWing(11106));   // a speed SHOE is not a flying wing
            Assert.IsFalse(SpecialMotionItems.IsFastWalkShoe(23108));
        }

        // ---- mesh-path → model id ----

        [TestCase("AVATAR/011106_WOMAN_SHOES.MSH", 11106)]
        [TestCase("AVATAR/008448_WOMAN_CHIBANG.MSH", 8448)]
        [TestCase("AVATAR\\026394_WOMAN_CHIBANG.MSH", 26394)]   // backslash path
        [TestCase("900020_WOMAN_SHOES.MSH", 900020)]            // bare filename, no dir
        public void ModelIdFromMeshPath_ParsesLeadingDigits(string path, int expected)
            => Assert.AreEqual(expected, SpecialMotionItems.ModelIdFromMeshPath(path));

        [TestCase(null)]
        [TestCase("")]
        [TestCase("AVATAR/FEMALE.HRC")]      // no leading digits
        public void ModelIdFromMeshPath_NullWhenNoDigits(string path)
            => Assert.IsNull(SpecialMotionItems.ModelIdFromMeshPath(path));

        // ---- outfit scanning ----

        private static readonly string[] Starter =
        {
            "AVATAR/900007_WOMAN_FACE.MSH", "AVATAR/900017_WOMAN_HAIR.MSH", "AVATAR/900018_WOMAN_COAT.MSH",
            "AVATAR/900019_WOMAN_PANT.MSH", "AVATAR/900020_WOMAN_SHOES.MSH", "AVATAR/900011_WOMAN_HAND.MSH",
        };

        [Test]
        public void WearsFlyingWing_TrueOnlyForListedWings()
        {
            Assert.IsTrue(SpecialMotionItems.WearsFlyingWing(new[] { Starter[0], "AVATAR/008448_WOMAN_CHIBANG.MSH" }));
            Assert.IsTrue(SpecialMotionItems.WearsFlyingWing(new[] { Starter[0], "AVATAR/026394_WOMAN_CHIBANG.MSH" }));
            Assert.IsFalse(SpecialMotionItems.WearsFlyingWing(new[] { Starter[0], "AVATAR/036937_WOMAN_CHIBANG.MSH" }));  // not listed
            Assert.IsFalse(SpecialMotionItems.WearsFlyingWing(Starter));   // starter has no wings
        }

        [Test]
        public void WearsFastWalkShoe_TrueWhenSpeedShoeReplacesDefault()
        {
            Assert.IsTrue(SpecialMotionItems.WearsFastWalkShoe(new[] { Starter[0], "AVATAR/011106_WOMAN_SHOES.MSH" }));
            Assert.IsFalse(SpecialMotionItems.WearsFastWalkShoe(Starter));  // default 900020 shoes aren't a speed shoe
        }

        [Test]
        public void Scanners_HandleNullAndEmpty()
        {
            Assert.IsFalse(SpecialMotionItems.WearsFlyingWing(null));
            Assert.IsFalse(SpecialMotionItems.WearsFastWalkShoe(new string[0]));
        }

        // ---- idle clip (flystay cat 0x2c) ----

        [Test]
        public void IdleMotFor_ReturnsFlystayForWings_ElseNormal()
        {
            Assert.AreEqual("MOTION/FLYSTAY_NAN.MOT", SpecialMotionItems.IdleMotFor(new[] { "AVATAR/008449_MAN_CHIBANG.MSH" }, true, "MOTION/MREST0082.MOT"));
            Assert.AreEqual("MOTION/FLYSTAY_NV.MOT", SpecialMotionItems.IdleMotFor(new[] { "AVATAR/026394_WOMAN_CHIBANG.MSH" }, false, "MOTION/WREST0072.MOT"));
            Assert.AreEqual("MOTION/WREST0056.MOT", SpecialMotionItems.IdleMotFor(Starter, false, "MOTION/WREST0056.MOT"));   // no wing → passthrough
        }

        // ---- walk clip (forward-lean glide, cat 0x2b) ----

        [Test]
        public void WalkMotFor_ReturnsGlideForWings_ElseNormal()
        {
            Assert.AreEqual("MOTION/FLY_NV.MOT", SpecialMotionItems.WalkMotFor(new[] { "AVATAR/008448_WOMAN_CHIBANG.MSH" }, false, "MOTION/WWALK0001.MOT"));
            Assert.AreEqual("MOTION/FLY_NAN.MOT", SpecialMotionItems.WalkMotFor(new[] { "AVATAR/008449_MAN_CHIBANG.MSH" }, true, "MOTION/MWALK0001.MOT"));
            Assert.AreEqual("MOTION/WWALK0001.MOT", SpecialMotionItems.WalkMotFor(Starter, false, "MOTION/WWALK0001.MOT"));   // no wing → passthrough
        }

        [Test]
        public void FlyClips_AreGenderSpecific()
        {
            Assert.AreEqual("MOTION/FLYSTAY_NAN.MOT", SpecialMotionItems.FlyIdleMot(true));
            Assert.AreEqual("MOTION/FLYSTAY_NV.MOT", SpecialMotionItems.FlyIdleMot(false));
            Assert.AreEqual("MOTION/FLY_NAN.MOT", SpecialMotionItems.FlyWalkMot(true));
            Assert.AreEqual("MOTION/FLY_NV.MOT", SpecialMotionItems.FlyWalkMot(false));
        }

        // ---- walk speed gate (Player_StepMovement 028:2774-2780) ----

        [Test]
        public void WalkSpeedMult_RunsForSpeedShoe_UnlessWingForcesWalk()
        {
            Assert.AreEqual(RoomMovement.RunSpeed,  SpecialMotionItems.WalkSpeedMult(fastWalkShoe: true,  flyingWing: false)); // 5.0
            Assert.AreEqual(RoomMovement.WalkSpeed, SpecialMotionItems.WalkSpeedMult(fastWalkShoe: false, flyingWing: false)); // 3.0
            Assert.AreEqual(RoomMovement.WalkSpeed, SpecialMotionItems.WalkSpeedMult(fastWalkShoe: true,  flyingWing: true));  // wing forces 3.0
            Assert.AreEqual(RoomMovement.WalkSpeed, SpecialMotionItems.WalkSpeedMult(fastWalkShoe: false, flyingWing: true));
        }

        [Test]
        public void FlyHover_MatchesDecompiledOffset()
            => Assert.AreEqual(10f, SpecialMotionItems.FlyHoverY);   // Player_StepMovement 028:2852: world-Y += 10
    }
}
