using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>The decompiled special-item idle/walk traits (<see cref="SpecialMotionItems"/>): flying wings grant the
    /// flystay floating idle (rest cat 0x2c) and speed shoes bump the walk to 5.0. Pure logic — no Unity/disk.</summary>
    public class SpecialMotionItemsTests
    {
        // ---- id membership (verbatim from gameplay/023:4068 & 4072-4076) ----

        [TestCase(8448)]  [TestCase(8449)]  [TestCase(8483)]  [TestCase(8484)]  [TestCase(20003)]
        public void FlyingWing_Ids_AreRecognised(int modelId)
            => Assert.IsTrue(SpecialMotionItems.IsFlyingWing(modelId));

        [TestCase(11106)] [TestCase(11107)] [TestCase(11114)] [TestCase(11115)]
        public void FastWalkShoe_Ids_AreRecognised(int modelId)
            => Assert.IsTrue(SpecialMotionItems.IsFastWalkShoe(modelId));

        [Test]
        public void PlainItems_AreNeitherTrait()
        {
            Assert.IsFalse(SpecialMotionItems.IsFlyingWing(23108));   // an ordinary wing
            Assert.IsFalse(SpecialMotionItems.IsFastWalkShoe(23108));
            Assert.IsFalse(SpecialMotionItems.IsFlyingWing(11106));   // a speed SHOE is not a flying wing
            Assert.IsFalse(SpecialMotionItems.IsFastWalkShoe(8448));  // a flying WING is not a speed shoe
        }

        // ---- mesh-path → model id ----

        [TestCase("AVATAR/011106_WOMAN_SHOES.MSH", 11106)]
        [TestCase("AVATAR/008448_WOMAN_CHIBANG.MSH", 8448)]
        [TestCase("AVATAR\\020003_WOMAN_CHIBANG.MSH", 20003)]   // backslash path
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
        public void WearsFlyingWing_TrueWhenWingEquipped()
        {
            var outfit = new[] { Starter[0], Starter[1], "AVATAR/008448_WOMAN_CHIBANG.MSH" };
            Assert.IsTrue(SpecialMotionItems.WearsFlyingWing(outfit));
            Assert.IsFalse(SpecialMotionItems.WearsFlyingWing(Starter));   // starter has no wings
        }

        [Test]
        public void WearsFastWalkShoe_TrueWhenSpeedShoeReplacesDefault()
        {
            var outfit = new[] { Starter[0], "AVATAR/011106_WOMAN_SHOES.MSH" };
            Assert.IsTrue(SpecialMotionItems.WearsFastWalkShoe(outfit));
            Assert.IsFalse(SpecialMotionItems.WearsFastWalkShoe(Starter));  // default 900020 shoes aren't a speed shoe
        }

        [Test]
        public void Scanners_HandleNullAndEmpty()
        {
            Assert.IsFalse(SpecialMotionItems.WearsFlyingWing(null));
            Assert.IsFalse(SpecialMotionItems.WearsFastWalkShoe(new string[0]));
            Assert.IsFalse(SpecialMotionItems.WearsFlyingWing(null, _ => true));
        }

        // ---- flying-wing by _CHIBANG_G glide model on disk (covers wings NOT in the hardcoded 5-id set) ----

        // 026394 (Fly 甜心飛翼) isn't one of the 5 hardcoded ids, but it ships 026394_WOMAN_CHIBANG_G.MSH → flyable.
        private static readonly System.Func<string, bool> HasGlide =
            rel => rel != null && rel.ToUpperInvariant().EndsWith("_CHIBANG_G.MSH");

        [Test]
        public void HasGlideVariant_TrueWhenGlideMeshExists()
        {
            Assert.IsTrue(SpecialMotionItems.HasGlideVariant("AVATAR/026394_WOMAN_CHIBANG.MSH", HasGlide));
            Assert.IsTrue(SpecialMotionItems.HasGlideVariant("AVATAR\\026844_WOMAN_CHIBANG.MSH", HasGlide));
        }

        [Test]
        public void HasGlideVariant_FalseForNonWingOrMissingGlide()
        {
            Assert.IsFalse(SpecialMotionItems.HasGlideVariant("AVATAR/011106_WOMAN_SHOES.MSH", HasGlide));   // not a wing
            Assert.IsFalse(SpecialMotionItems.HasGlideVariant("AVATAR/003593_MAN_CHIBANG.MSH", _ => false)); // plain-only wing, no _G on disk
            Assert.IsFalse(SpecialMotionItems.HasGlideVariant("AVATAR/026394_WOMAN_CHIBANG_G.MSH", HasGlide)); // already the glide mesh
        }

        [Test]
        public void WearsFlyingWing_MeshExistsOverload_CoversGlideWings()
        {
            var outfit = new[] { Starter[0], "AVATAR/026394_WOMAN_CHIBANG.MSH" };   // id 26394 ∉ hardcoded set
            Assert.IsFalse(SpecialMotionItems.WearsFlyingWing(outfit));             // id-only check misses it
            Assert.IsTrue(SpecialMotionItems.WearsFlyingWing(outfit, HasGlide));    // _G glide model → flyable
            // hardcoded id still works through the overload even without a glide probe
            Assert.IsTrue(SpecialMotionItems.WearsFlyingWing(new[] { "AVATAR/008448_WOMAN_CHIBANG.MSH" }, _ => false));
        }

        [Test]
        public void IdleMotFor_MeshExistsOverload_FloatsGlideWings()
        {
            var outfit = new[] { "AVATAR/026394_WOMAN_CHIBANG.MSH" };
            Assert.AreEqual("MOTION/FLYSTAY_NV.MOT", SpecialMotionItems.IdleMotFor(outfit, false, "MOTION/WREST0056.MOT", HasGlide));
            Assert.AreEqual("MOTION/WREST0056.MOT", SpecialMotionItems.IdleMotFor(Starter, false, "MOTION/WREST0056.MOT", HasGlide));
        }

        // ---- idle clip resolution (rest cat 0x2c = flystay) ----

        [Test]
        public void IdleMotFor_ReturnsFlystayForWings_ElseNormal()
        {
            var wings = new[] { "AVATAR/008449_MAN_CHIBANG.MSH" };
            Assert.AreEqual("MOTION/FLYSTAY_NAN.MOT", SpecialMotionItems.IdleMotFor(wings, male: true, "MOTION/MREST0082.MOT"));
            Assert.AreEqual("MOTION/FLYSTAY_NV.MOT", SpecialMotionItems.IdleMotFor(new[] { "AVATAR/008448_WOMAN_CHIBANG.MSH" }, male: false, "MOTION/WREST0072.MOT"));
            // no wing → caller's normal idle is passed through unchanged
            Assert.AreEqual("MOTION/WREST0056.MOT", SpecialMotionItems.IdleMotFor(Starter, male: false, "MOTION/WREST0056.MOT"));
        }

        [Test]
        public void FlyIdleMot_IsGenderSpecific()
        {
            Assert.AreEqual("MOTION/FLYSTAY_NAN.MOT", SpecialMotionItems.FlyIdleMot(male: true));
            Assert.AreEqual("MOTION/FLYSTAY_NV.MOT", SpecialMotionItems.FlyIdleMot(male: false));
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
    }
}
