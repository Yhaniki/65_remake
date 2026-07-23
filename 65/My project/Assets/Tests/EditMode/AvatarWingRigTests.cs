using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Guards the animated glide-wing (_G) rig resolution — the online-only feature (sdo.bin FUN_0083f540) that mounts
    /// a wing's own skeleton + looping .mot on the back. The 265-id set is decompiled ground truth, so a test pins it
    /// against accidental truncation and pins the path derivation the room builder relies on.
    /// </summary>
    public class AvatarWingRigTests
    {
        [Test]
        public void HuaYuWings_HaveAnimatedRig()   // 花雨飛翼 男 23920 / 女 23921 — both in the decompiled switch
        {
            Assert.IsTrue(AvatarWingRig.HasAnimatedRig(23920), "花雨飛翼男 (23920) should have an animated rig");
            Assert.IsTrue(AvatarWingRig.HasAnimatedRig(23921), "花雨飛翼女 (23921) should have an animated rig");
        }

        [Test]
        public void DecompiledFlyers_HaveAnimatedRig()
        {
            Assert.IsTrue(AvatarWingRig.HasAnimatedRig(8448));    // 0x2100 (offline hardcoded flyer, first switch case)
            Assert.IsTrue(AvatarWingRig.HasAnimatedRig(21089));   // 0x5261 (one of the 4 always-on wings)
            Assert.IsTrue(AvatarWingRig.HasAnimatedRig(23691));   // 甜心飛翼女
            Assert.IsTrue(AvatarWingRig.HasAnimatedRig(23692));   // 甜心飛翼男
        }

        [Test]
        public void NonAnimatedWing_And_NonWing_AreNotRigged()
        {
            // 036937 ships a _CHIBANG_G mesh but is NOT in the online animated-wing switch — the exact false positive
            // the old "_G mesh ⇒ flies" heuristic floated. It must NOT get an animated rig.
            Assert.IsFalse(AvatarWingRig.HasAnimatedRig(36937));
            Assert.IsFalse(AvatarWingRig.HasAnimatedRig(26394));  // 甜心飛翼 mesh-only synth id — no per-item _G switch case
            Assert.IsFalse(AvatarWingRig.HasAnimatedRig(900018)); // a body coat, not a wing
        }

        [Test]
        public void TryResolve_MaleWing_DerivesGRigPaths()
        {
            Assert.IsTrue(AvatarWingRig.TryResolve("AVATAR/023920_MAN_CHIBANG.MSH", out var p));
            Assert.AreEqual("AVATAR/023920_MAN_CHIBANG_G.HRC", p.HrcRel);
            Assert.AreEqual("AVATAR/023920_MAN_CHIBANG_G.MSH", p.MshRel);
            Assert.AreEqual("MOTION/023920_MAN_CHIBANG.MOT", p.MotRel);
            Assert.AreEqual(23920, p.ModelId);
            Assert.IsTrue(p.Male);
        }

        [Test]
        public void TryResolve_FemaleWing_DerivesGRigPaths()
        {
            Assert.IsTrue(AvatarWingRig.TryResolve("AVATAR/023921_WOMAN_CHIBANG.MSH", out var p));
            Assert.AreEqual("AVATAR/023921_WOMAN_CHIBANG_G.HRC", p.HrcRel);
            Assert.AreEqual("AVATAR/023921_WOMAN_CHIBANG_G.MSH", p.MshRel);
            Assert.AreEqual("MOTION/023921_WOMAN_CHIBANG.MOT", p.MotRel);
            Assert.AreEqual(23921, p.ModelId);
            Assert.IsFalse(p.Male);   // "WOMAN" checked before "MAN" (substring trap)
        }

        [Test]
        public void TryResolve_AlreadyGStem_IsTolerated()   // defensive: an already-_G path collapses back to the base stem
        {
            Assert.IsTrue(AvatarWingRig.TryResolve("AVATAR/023920_MAN_CHIBANG_G.MSH", out var p));
            Assert.AreEqual("AVATAR/023920_MAN_CHIBANG_G.HRC", p.HrcRel);
            Assert.AreEqual("MOTION/023920_MAN_CHIBANG.MOT", p.MotRel);
        }

        [Test]
        public void TryResolve_NonRiggedWing_ReturnsFalse()
        {
            Assert.IsFalse(AvatarWingRig.TryResolve("AVATAR/036937_WOMAN_CHIBANG.MSH", out _));
        }

        [Test]
        public void TryResolve_NonWingPart_ReturnsFalse()
        {
            Assert.IsFalse(AvatarWingRig.TryResolve("AVATAR/900018_WOMAN_COAT.MSH", out _));
            Assert.IsFalse(AvatarWingRig.TryResolve("", out _));
            Assert.IsFalse(AvatarWingRig.TryResolve(null, out _));
        }

        [Test]
        public void WearsAnimatedWing_ScansParts()
        {
            var parts = new List<string>
            {
                "AVATAR/900007_WOMAN_FACE.MSH",
                "AVATAR/023921_WOMAN_CHIBANG.MSH",   // the animated wing
                "AVATAR/900017_WOMAN_HAIR.MSH",
            };
            Assert.IsTrue(AvatarWingRig.WearsAnimatedWing(parts));
            Assert.IsFalse(AvatarWingRig.WearsAnimatedWing(new[] { "AVATAR/900007_WOMAN_FACE.MSH" }));
        }

        [Test]
        public void RigModelIdSet_HasExpectedCount()   // pin the decompiled table so a bad edit can't silently drop ids
        {
            Assert.AreEqual(265, AvatarWingRig.AnimatedWingModelIds.Distinct().Count());
        }
    }
}
