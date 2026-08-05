using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    public class MmdPhysicsProbeSelectionTests
    {
        private static PmxLoader.Bone Bone(string jp, int parent, string en = "")
            => new PmxLoader.Bone { NameJp = jp, NameEn = en, Parent = parent };

        private static PmxLoader.RigidBody Dynamic(string name, int bone)
            => new PmxLoader.RigidBody { Name = name, Bone = bone, Mode = 1 };

        [Test]
        public void CommandLineValueAcceptsSeparateAndEqualsForms()
        {
            Assert.AreEqual(@"H:\models with spaces\miku.pmx",
                MmdPhysicsProbeSelection.ArgValue(
                    new[] { "dance.exe", "-mmdprobe", "-mmdprobe-pmx", @"H:\models with spaces\miku.pmx" },
                    "-mmdprobe-pmx"));
            Assert.AreEqual(@"H:\out dir",
                MmdPhysicsProbeSelection.ArgValue(
                    new[] { "dance.exe", @"-mmdprobe-out=H:\out dir" },
                    "-mmdprobe-out"));
        }

        [Test]
        public void NonIkaRigidBodyNamesStillProduceAStableChain()
        {
            var bones = new List<PmxLoader.Bone>
            {
                Bone("センター", -1),
                Bone("飾り根", 0),
                Bone("飾り中", 1),
                Bone("飾り先", 2),
            };
            var bodies = new List<PmxLoader.RigidBody>
            {
                Dynamic("物理A", 1), Dynamic("物理B", 2), Dynamic("物理C", 3),
            };

            var got = MmdPhysicsProbeSelection.SelectChains(bones, bodies, 4);

            Assert.AreEqual(1, got.Count);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, got[0].Bones);
            Assert.AreEqual("chain_0001_0003", got[0].Id);
        }

        [Test]
        public void ForkProducesDeterministicRootToLeafPathsWithoutDroppingABranch()
        {
            var bones = new List<PmxLoader.Bone>
            {
                Bone("root", -1), Bone("cloth-root", 0), Bone("left", 1), Bone("right", 1), Bone("right-tip", 3),
            };
            var bodies = new List<PmxLoader.RigidBody>
            {
                Dynamic("custom", 1), Dynamic("custom", 2), Dynamic("custom", 3), Dynamic("custom", 4),
            };

            var got = MmdPhysicsProbeSelection.SelectChains(bones, bodies, 4);

            Assert.AreEqual(2, got.Count);
            Assert.AreEqual("chain_0001_0004", got[0].Id, "longer path ranks first");
            CollectionAssert.AreEqual(new[] { 1, 3, 4 }, got[0].Bones);
            Assert.AreEqual("chain_0001_0002", got[1].Id);
            CollectionAssert.AreEqual(new[] { 1, 2 }, got[1].Bones);
        }

        [Test]
        public void FindsEnglishHeadWhenJapaneseStandardNameIsAbsent()
        {
            var bones = new List<PmxLoader.Bone>
            {
                Bone("Root", -1), Bone("Neck", 0), Bone("CustomHead", 1, "Head"),
            };

            Assert.AreEqual(2, MmdPhysicsProbeSelection.FindMotionBone(bones));
        }

        [Test]
        public void ModelWithoutDynamicBodiesProducesNoProbeChains()
        {
            var bones = new List<PmxLoader.Bone> { Bone("root", -1) };
            var bodies = new List<PmxLoader.RigidBody>
            {
                new PmxLoader.RigidBody { Name = "body", Bone = 0, Mode = 0 },
            };

            Assert.IsEmpty(MmdPhysicsProbeSelection.SelectChains(bones, bodies, 4));
        }

        [Test]
        public void NonFiniteClothWarmupSampleIsRejectedBeforeRecording()
        {
            var sampledChains = new[]
            {
                new[] { Vector3.zero, new Vector3(float.NaN, float.NaN, float.NaN) },
            };
            var allPhysicsPositions = new[] { Vector3.zero, Vector3.one };

            Assert.IsFalse(MmdPhysicsProbeSelection.IsFiniteSample(
                Vector3.zero, Quaternion.identity, sampledChains, allPhysicsPositions));

            sampledChains[0][1] = Vector3.one;
            allPhysicsPositions[1] = new Vector3(float.PositiveInfinity, 0f, 0f);
            Assert.IsFalse(MmdPhysicsProbeSelection.IsFiniteSample(
                Vector3.zero, Quaternion.identity, sampledChains, allPhysicsPositions),
                "an exploding physics bone must fail even when it is not one of the four recorded chains");

            allPhysicsPositions[1] = Vector3.one;
            Assert.IsTrue(MmdPhysicsProbeSelection.IsFiniteSample(
                Vector3.zero, Quaternion.identity, sampledChains, allPhysicsPositions));
        }
    }
}
