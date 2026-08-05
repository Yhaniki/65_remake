using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Unit tests for <see cref="MmdClothChains"/> — the split of a PMX's dynamic rigid bodies into chains and the
    /// merge of identically-behaving chains into one Magica cloth.
    ///
    /// The regression that motivated this class: a model whose "hair" holds both long LOCKED-joint twintails and short
    /// SPRUNG tufts used to average them together, and the 6% of sprung bones dictated the tuning for all of it
    /// (restoration 0.052 instead of 0.9). <see cref="TuftsDoNotDiluteTwintails"/> is that case, in miniature.
    /// </summary>
    public class MmdClothChainsTests
    {
        // ---- tiny model builder ----------------------------------------------------------------------------------

        private sealed class Model
        {
            public readonly List<PmxLoader.Bone> Bones = new List<PmxLoader.Bone>();
            public readonly List<PmxLoader.RigidBody> Bodies = new List<PmxLoader.RigidBody>();
            public readonly Dictionary<int, float> Limit = new Dictionary<int, float>();
            public readonly Dictionary<int, float> Spring = new Dictionary<int, float>();

            public int Bone(string nameJp, int parent, float y = 0f)
            {
                Bones.Add(new PmxLoader.Bone { NameJp = nameJp, NameEn = nameJp, Parent = parent, Position = new Vector3(0f, y, 0f) });
                return Bones.Count - 1;
            }

            /// <summary>A dynamic (cloth) body on <paramref name="bone"/>, plus its joint's limit/spring.</summary>
            public void Dyn(int bone, string name, float limitRad, float spring, float mass = 1f,
                            float linDamp = 0.5f, float angDamp = 1f, byte shape = 0, byte group = 1, ushort mask = 0xFFFF)
            {
                Bodies.Add(new PmxLoader.RigidBody
                {
                    Name = name, Bone = bone, Mode = 1, Mass = mass, LinearDamp = linDamp, AngularDamp = angDamp,
                    Shape = shape, Size = new Vector3(0.2f, 0.2f, 0.2f), Group = group, Mask = mask,
                });
                Limit[bone] = limitRad;
                Spring[bone] = spring;
            }

            /// <summary>A kinematic body = a body collider.</summary>
            public PmxLoader.RigidBody Kin(int bone, string name, byte group = 0, ushort mask = 0xFFFF)
            {
                var rb = new PmxLoader.RigidBody { Name = name, Bone = bone, Mode = 0, Group = group, Mask = mask, Size = new Vector3(1f, 1f, 1f) };
                Bodies.Add(rb);
                return rb;
            }

            /// <summary>A chain of <paramref name="len"/> bones hanging off <paramref name="parent"/>.</summary>
            public int Chain(string namePrefix, int parent, int len, float limitRad, float spring,
                             float rootMass = 1f, float tipMass = 1f, byte group = 1, ushort mask = 0xFFFF)
            {
                int root = -1, p = parent;
                for (int i = 0; i < len; i++)
                {
                    int b = Bone(namePrefix + "_" + i, p, -i);
                    if (i == 0) root = b;
                    Dyn(b, namePrefix + "_" + i, limitRad, spring, i == 0 ? rootMass : tipMass, 0.5f, 1f, 0, group, mask);
                    p = b;
                }
                return root;
            }

            public List<MmdClothChains.ClothGroup> Build(IList<PmxLoader.RigidBody> colliders = null)
                => MmdClothChains.Build(Bones, Bodies, Limit, Spring, colliders);
        }

        private static Model Body()
        {
            var m = new Model();
            m.Bone("センター", -1, 0f);
            m.Bone("上半身", 0, 1f);
            m.Bone("上半身2", 1, 2f);
            m.Bone("首", 2, 3f);
            m.Bone("頭", 3, 4f);       // index 4
            return m;
        }
        private const int Head = 4;

        // ---- the regression this class exists for ----------------------------------------------------------------

        [Test]
        public void TuftsDoNotDiluteTwintails()
        {
            var m = Body();
            int hairRoot = m.Bone("髪根", Head, 4f);   // the nameless bone strands actually hang off
            for (int i = 0; i < 4; i++) m.Chain("TwinHair" + i, hairRoot, 10, 0f, 0f, 20f, 0.1f);   // long, LOCKED, no spring
            for (int i = 0; i < 3; i++) m.Chain("TuftHair" + i, hairRoot, 2, 0.35f, 5f, 0.2f, 0.1f); // short, SPRUNG

            var groups = m.Build();

            var twin = groups.Find(g => g.Label.StartsWith("TwinHair"));
            var tuft = groups.Find(g => g.Label.StartsWith("TuftHair"));
            Assert.NotNull(twin, "twintails must form their own cloth");
            Assert.NotNull(tuft, "tufts must form their own cloth");
            Assert.AreEqual(4, twin.ChainCount);
            Assert.AreEqual(3, tuft.ChainCount);

            // the twintails are LOCKED (no spring) -> full restoration + the root-weighted curve
            var twinPart = MmdClothChains.Derive(twin.Stats);
            Assert.AreEqual(0.9f, twinPart.AngleStiffness, 1e-4f);
            Assert.IsTrue(twinPart.RootWeighted);
            Assert.AreEqual(0f, twinPart.GravityFalloff, 1e-4f, "a free pendulum keeps full gravity");

            // the tufts are SPRUNG at 5 -> pinned to their authored shape
            var tuftPart = MmdClothChains.Derive(tuft.Stats);
            Assert.AreEqual(0.9f, tuftPart.AngleStiffness, 1e-4f);
            Assert.IsFalse(tuftPart.RootWeighted);
            Assert.AreEqual(1f, tuftPart.GravityFalloff, 1e-4f);
        }

        [Test]
        public void AveragingAllHairTogetherWouldHaveCollapsedTheStiffness()
        {
            // The OLD behaviour, computed the old way over the same model: 6 sprung bones out of 46 give springMean
            // 0.65 -> the "sprung" branch -> restoration 0.117 for everything. This is what per-chain avoids; if this
            // assertion ever reads 0.9 the mixing formula changed and the guard above is no longer meaningful.
            float springSum = 3 * 2 * 5f;          // 3 tufts × 2 bones × spring 5
            int boneN = 4 * 10 + 3 * 2;            // twintails + tufts
            float springMean = springSum / boneN;
            float diluted = Mathf.Clamp01(Mathf.Clamp01(springMean / 5f) * 0.9f);
            Assert.Less(diluted, 0.2f, "the whole-part average is the bug being fixed");
        }

        // ---- splitting -------------------------------------------------------------------------------------------

        [Test]
        public void ForkedStrandIsOneChain()
        {
            var m = Body();
            int root = m.Chain("Fork", Head, 2, 0f, 0f);
            int mid = root + 1;
            // two branches off the same bone
            int b1 = m.Bone("Fork_a", mid, -3f); m.Dyn(b1, "Fork_a", 0f, 0f);
            int b2 = m.Bone("Fork_b", mid, -3f); m.Dyn(b2, "Fork_b", 0f, 0f);

            var groups = m.Build();
            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual(1, groups[0].ChainCount, "a fork is still one chain (one root)");
            Assert.AreEqual(4, groups[0].BoneCount);
        }

        [Test]
        public void PartComesFromTheBodyNamesNotTheBoneNames()
        {
            var m = Body();
            m.Chain("Dress_0", 0, 3, 0.09f, 0f);      // skirt panel off センター
            m.Chain("Tie_0", 2, 3, 0.09f, 0f);        // tie off 上半身2
            var groups = m.Build();
            Assert.AreEqual(MmdClothPartId.Skirt, groups.Find(g => g.Label.StartsWith("Dress")).Part);
            Assert.AreEqual(MmdClothPartId.Tie, groups.Find(g => g.Label.StartsWith("Tie")).Part);
        }

        [Test]
        public void ChainsWithDifferentFirmnessDoNotShareACloth()
        {
            var m = Body();
            m.Chain("HairSoft", Head, 5, 0.7f, 0f);
            m.Chain("HairHard", Head, 5, 0f, 0f);
            Assert.AreEqual(2, m.Build().Count);
        }

        [Test]
        public void IdenticalChainsShareOneCloth()
        {
            var m = Body();
            for (int i = 0; i < 5; i++) m.Chain("Same" + i, Head, 5, 0f, 0f);
            var groups = m.Build();
            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual(5, groups[0].ChainCount);
            Assert.AreEqual(25, groups[0].BoneCount);
            Assert.AreEqual(5, groups[0].RootBones.Count);
        }

        // ---- anchoring -------------------------------------------------------------------------------------------

        [Test]
        public void AnchorIsTheBodyBoneNotTheNamelessHairRoot()
        {
            var m = Body();
            int hairRoot = m.Bone("髪根", Head, 4f);
            m.Chain("Hair", hairRoot, 3, 0f, 0f);
            var groups = m.Build();
            Assert.AreEqual(Head, groups[0].AnchorBone, "the cloth must ride the 頭 bone, not the intermediate hair root");
        }

        [Test]
        public void ChainsOnDifferentBodyPartsStayApart()
        {
            var m = Body();
            m.Chain("HairX", Head, 4, 0f, 0f);      // off the head
            m.Chain("HairY", 0, 4, 0f, 0f);         // off センター — same numbers, different reference frame
            var groups = m.Build();
            Assert.AreEqual(2, groups.Count, "different inertia references must not be merged");
        }

        [Test]
        public void NonStandardSkeletonFallsBackToTheChainsOwnParent()
        {
            var m = new Model();
            int r = m.Bone("root", -1);
            int mid = m.Bone("mystery", r);
            m.Chain("Hair", mid, 3, 0f, 0f);
            Assert.AreEqual(mid, m.Build()[0].AnchorBone);
        }

        // ---- collision filtering ---------------------------------------------------------------------------------

        [Test]
        public void OnlyTheCollidersTheAuthorAllowsAreSelected()
        {
            var m = Body();
            // skirt in group 6, NOT allowed to touch group 3 (the hip flare): mask bit 3 cleared
            m.Chain("Dress_0", 0, 3, 0.09f, 0f, 1f, 1f, 6, unchecked((ushort)~(1 << 3)));
            var hip = m.Kin(1, "hip", 3, 0xFFFF);     // group 3
            var leg = m.Kin(2, "leg", 4, 0xFFFF);     // group 4
            var groups = m.Build(new List<PmxLoader.RigidBody> { hip, leg });

            Assert.AreEqual(1, groups.Count);
            CollectionAssert.AreEqual(new[] { 1 }, groups[0].ColliderIndices, "only the leg (index 1) may be touched");
        }

        [Test]
        public void ChainsThatTouchDifferentCollidersDoNotShareACloth()
        {
            var m = Body();
            m.Chain("HairA", Head, 4, 0f, 0f, 1f, 1f, 1, 0xFFFF);
            m.Chain("HairB", Head, 4, 0f, 0f, 1f, 1f, 1, unchecked((ushort)~(1 << 3)));
            var hip = m.Kin(1, "hip", 3, 0xFFFF);
            Assert.AreEqual(2, m.Build(new List<PmxLoader.RigidBody> { hip }).Count);
        }

        // ---- derived values --------------------------------------------------------------------------------------

        [Test]
        public void SheetIsDecidedByBoxShapedBodies()
        {
            var m = Body();
            int p = 0;
            for (int i = 0; i < 4; i++) { int b = m.Bone("panel" + i, p, -i); m.Dyn(b, "Dress_" + i, 0.09f, 0f, 1f, 0.5f, 1f, 1 /*box*/); p = b; }
            Assert.IsTrue(m.Build()[0].Sheet, "box-shaped panels ⇒ a sheet (skirt ring)");

            var strand = Body();
            strand.Chain("Hair", Head, 4, 0f, 0f);
            Assert.IsFalse(strand.Build()[0].Sheet);
        }

        [Test]
        public void MassGradientIsMeasuredButNoLongerWithheldAsInertia()
        {
            var m = Body();
            m.Chain("Hair", Head, 6, 0f, 0f, 20f, 0.1f);
            var g = m.Build()[0];
            Assert.Greater(MmdClothChains.MassGradient(g.Stats), 5f, "root 200× the tip is still read off the model");
            // ...but it must NOT become depthInertia. That knob stands in for Bullet's TENSION (how much of the
            // anchor's motion reaches a particle), not for a mass ratio: derived from the gradient it measured as a
            // +59% over-swing and a 2.9x spin fling, and handing over the full motion measured best.
            Assert.AreEqual(1f, MmdClothChains.Derive(g.Stats).DepthInertia, 1e-6f);
            Assert.AreEqual(1f, MmdClothChains.Derive(g.Stats).WorldInertia, 1e-6f, "MMD imparts the full body motion");
        }

        [Test]
        public void AngleLimitFollowsTheAuthoredJointRange()
        {
            var m = Body();
            m.Chain("Hair", Head, 4, 20f * Mathf.Deg2Rad, 0f);
            Assert.AreEqual(26f, MmdClothChains.Derive(m.Build()[0].Stats).AngleLimitDeg, 0.01f, "free joints keep the loose guard");
        }

        [Test]
        public void LockedJointsGetALotLessSlackThanFreeOnes()
        {
            var locked = Body(); locked.Chain("Hair", Head, 8, 0f, 0f);          // 0/0 = the author locked it
            var free = Body(); free.Chain("Hair", Head, 8, 8f * Mathf.Deg2Rad, 0f);
            float lockedDeg = MmdClothChains.Derive(locked.Build()[0].Stats).AngleLimitDeg;
            float freeDeg = MmdClothChains.Derive(free.Build()[0].Stats).AngleLimitDeg;
            Assert.AreEqual(1.5f, lockedDeg, 0.01f);
            Assert.Greater(freeDeg, lockedDeg * 2f, "a chain the author left free must not be clamped like a locked one");
        }

        [Test]
        public void DampingConvertsBulletPerSecondToMagicaPerSubstep()
        {
            // one second of Bullet decay must survive 150 substeps of Magica's per-step multiply
            foreach (float d in new[] { 0.2f, 0.5f, 0.9f })
            {
                float m = MmdClothChains.BulletToMagicaDamping(d);
                float retained = Mathf.Pow(1f - m * 0.6f, 150f);
                Assert.AreEqual(1f - d, retained, 0.02f, "damping " + d);
            }
            Assert.AreEqual(0f, MmdClothChains.BulletToMagicaDamping(0f), 1e-6f);
            Assert.LessOrEqual(MmdClothChains.BulletToMagicaDamping(1f), 0.2f, "clamped, never a full stop");
        }

        [Test]
        public void AngularDampingOnlyCountsWhereItBeatsTheLinearOne()
        {
            // twintail-shaped: barely any linear damping, but authored angular 2.0. Above 1 is past Bullet's clamp =
            // pure "settle quickly" intent, so it counts fully; at half weight the twintails kept ringing.
            Assert.AreEqual(1f, MmdClothChains.EffectiveDamping(0.2f, 2f), 1e-4f);
            // an ORDINARY angular damping still counts half — a chained bone's rotation is constrained by its neighbours
            Assert.AreEqual(0.5f, MmdClothChains.EffectiveDamping(0.2f, 1f), 1e-4f);
            // skirt/tie/fringe-shaped: the linear term already dominates → unchanged
            Assert.AreEqual(0.999f, MmdClothChains.EffectiveDamping(0.999f, 1f), 1e-4f);
            Assert.AreEqual(0.537f, MmdClothChains.EffectiveDamping(0.537f, 0.995f), 1e-4f);
            Assert.AreEqual(0f, MmdClothChains.EffectiveDamping(0f, 0f), 1e-6f);
        }

        [Test]
        public void ModelWithoutPhysicsBodiesYieldsNothing()
        {
            var m = Body();
            Assert.IsEmpty(m.Build());
            Assert.IsEmpty(MmdClothChains.Build(null, null, null, null, null));
        }
    }
}
