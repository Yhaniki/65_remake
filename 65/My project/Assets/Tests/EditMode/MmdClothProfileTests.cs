using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Unit tests for <see cref="MmdClothProfile"/> — the per-model physics.ini that overrides the cloth values
    /// converted from the .pmx. The contract under test: <b>no file → pure conversion; a file overrides only the keys
    /// it actually lists.</b>
    /// </summary>
    public class MmdClothProfileTests
    {
        // A stand-in for what MmdMagicaCloth converts out of the model's rigid bodies/joints.
        private static MmdClothPart Converted() => new MmdClothPart
        {
            GravityMul = 1f,
            GravityFalloff = 0.25f,
            UseAngleRestoration = true,
            AngleStiffness = 0.9f,
            AngleStiffTipScale = 0.8f,
            RootWeighted = true,
            AngleLimitDeg = 12.5f,
            AngleLimitStiffness = 0.2f,
            DampingRoot = 0.01f,
            DampingTip = 0.02f,
            RadiusMul = 1f,
            RadiusRootScale = 0.35f,
            WorldInertia = 1f,
            DepthInertia = 0.4f,
            InertiaSmoothing = 0.15f,
            Connection = MmdClothConnection.Auto,
            MaxDistanceMul = 1f,
            MaxDistanceRootScale = 0.5f,
            Friction = 0.15f,
            ParticleSpeedLimitMps = 8f,
        };

        [Test]
        public void AKeyTheFileDoesNotMention_KeepsTheConvertedValue()
        {
            var profile = MmdClothProfile.Parse("[skirt]\nangleStiffness = 0.4\n");

            var p = profile.Apply(MmdClothPartId.Skirt, Converted());

            Assert.AreEqual(0.4f, p.AngleStiffness, 1e-5f);          // overridden
            Assert.AreEqual(12.5f, p.AngleLimitDeg, 1e-5f);          // untouched → still the converted value
            Assert.AreEqual(0.35f, p.RadiusRootScale, 1e-5f);
            Assert.IsTrue(p.RootWeighted);
        }

        [Test]
        public void APartTheFileDoesNotMention_IsEntirelyTheConvertedValue()
        {
            var profile = MmdClothProfile.Parse("[skirt]\ngravityMul = 2\n");

            var hair = profile.Apply(MmdClothPartId.Hair, Converted());

            Assert.AreEqual(Converted().GravityMul, hair.GravityMul, 1e-5f);
            Assert.IsFalse(profile.Has(MmdClothPartId.Hair));
            Assert.IsTrue(profile.Has(MmdClothPartId.Skirt));
        }

        [Test]
        public void EveryKnob_IsOverridable()
        {
            var profile = MmdClothProfile.Parse(@"
[hair]
gravityMul = 1.5
gravityFalloff = 0.9
useAngleRestoration = false
angleStiffness = 0.31
angleStiffTipScale = 0.6
rootWeighted = false
angleLimitDeg = 30
angleLimitStiffness = 0.7
dampingRoot = 0.05
dampingTip = 0.06
radiusMul = 2
radiusRootScale = 0.5
worldInertia = 0.8
depthInertia = 0.2
inertiaSmoothing = 0.05
connection = mesh
maxDistanceMul = 0.5
maxDistanceRootScale = 0.25
friction = 0.9
particleSpeedLimitMps = 12
");
            var p = profile.Apply(MmdClothPartId.Hair, Converted());

            Assert.AreEqual(1.5f, p.GravityMul, 1e-5f);
            Assert.AreEqual(0.9f, p.GravityFalloff, 1e-5f);
            Assert.IsFalse(p.UseAngleRestoration);
            Assert.AreEqual(0.31f, p.AngleStiffness, 1e-5f);
            Assert.AreEqual(0.6f, p.AngleStiffTipScale, 1e-5f);
            Assert.IsFalse(p.RootWeighted);
            Assert.AreEqual(30f, p.AngleLimitDeg, 1e-5f);
            Assert.AreEqual(0.7f, p.AngleLimitStiffness, 1e-5f);
            Assert.AreEqual(0.05f, p.DampingRoot, 1e-5f);
            Assert.AreEqual(0.06f, p.DampingTip, 1e-5f);
            Assert.AreEqual(2f, p.RadiusMul, 1e-5f);
            Assert.AreEqual(0.5f, p.RadiusRootScale, 1e-5f);
            Assert.AreEqual(0.8f, p.WorldInertia, 1e-5f);
            Assert.AreEqual(0.2f, p.DepthInertia, 1e-5f);
            Assert.AreEqual(0.05f, p.InertiaSmoothing, 1e-5f);
            Assert.AreEqual(MmdClothConnection.Mesh, p.Connection);
            Assert.AreEqual(0.5f, p.MaxDistanceMul, 1e-5f);
            Assert.AreEqual(0.25f, p.MaxDistanceRootScale, 1e-5f);
            Assert.AreEqual(0.9f, p.Friction, 1e-5f);
            Assert.AreEqual(12f, p.ParticleSpeedLimitMps, 1e-5f);
        }

        [Test]
        public void CommentsBlankLinesTrailingCommentsAndCaseAreTolerated()
        {
            var profile = MmdClothProfile.Parse(@"
# 這行是註解
; 這行也是

[ SKIRT ]
  AngleStiffness  =  0.5   # 裙子軟一點
  bogusKeyFromANewerVersion = 7
[unknown-part]
angleStiffness = 0.1
");
            var p = profile.Apply(MmdClothPartId.Skirt, Converted());

            Assert.AreEqual(0.5f, p.AngleStiffness, 1e-5f);   // section + key are case-insensitive, value comment stripped
            Assert.AreEqual(0.9f, profile.Apply(MmdClothPartId.Hair, Converted()).AngleStiffness, 1e-5f);   // unknown section ignored
        }

        [Test]
        public void GlobalsFallBackWhenAbsent()
        {
            var empty = MmdClothProfile.Parse("");
            Assert.AreEqual(150f, empty.SimulationFrequency(150f), 1e-5f);
            Assert.AreEqual(1f, empty.ColliderRadiusMul(1f), 1e-5f);

            var set = MmdClothProfile.Parse("[global]\nsimulationFrequency = 90\ncolliderRadiusMul = 1.2\n");
            Assert.AreEqual(90f, set.SimulationFrequency(150f), 1e-5f);
            Assert.AreEqual(1.2f, set.ColliderRadiusMul(1f), 1e-5f);
        }

        [Test]
        public void GarbageValueKeepsTheConvertedValue()   // a typo in the file must not zero a knob
        {
            var profile = MmdClothProfile.Parse("[skirt]\nangleStiffness = 很硬\ngravityMul =\n");

            var p = profile.Apply(MmdClothPartId.Skirt, Converted());

            Assert.AreEqual(0.9f, p.AngleStiffness, 1e-5f);
            Assert.AreEqual(1f, p.GravityMul, 1e-5f);
        }

        [Test]
        public void SaveThenLoad_ReproducesTheSameTuning()   // the 「存成 physics.ini」 button's contract
        {
            var tuned = Converted();
            tuned.GravityMul = 1.4f;          // as if the panel's 重+ had been pressed
            tuned.AngleStiffness = 0.62f;     // …and 硬-
            tuned.Connection = MmdClothConnection.Line;

            string ini = MmdClothProfile.Serialize(150f, 1.18f, new[]
            {
                new KeyValuePair<MmdClothPartId, MmdClothPart>(MmdClothPartId.Skirt, tuned),
            });

            var reloaded = MmdClothProfile.Parse(ini);
            var p = reloaded.Apply(MmdClothPartId.Skirt, Converted());

            Assert.AreEqual(1.4f, p.GravityMul, 1e-4f);
            Assert.AreEqual(0.62f, p.AngleStiffness, 1e-4f);
            Assert.AreEqual(tuned.DepthInertia, p.DepthInertia, 1e-4f);
            Assert.AreEqual(MmdClothConnection.Line, p.Connection);
            Assert.AreEqual(1.18f, reloaded.ColliderRadiusMul(1f), 1e-4f);
        }

        [Test]
        public void NumbersAreCultureInvariant()   // a comma-decimal locale must not turn 0.9 into 9
        {
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");   // decimal separator = ','
                string ini = MmdClothProfile.Serialize(150f, 1f, new[]
                {
                    new KeyValuePair<MmdClothPartId, MmdClothPart>(MmdClothPartId.Hair, Converted()),
                });
                StringAssert.Contains("angleStiffness = 0.9", ini);

                var p = MmdClothProfile.Parse(ini).Apply(MmdClothPartId.Hair, Converted());
                Assert.AreEqual(0.9f, p.AngleStiffness, 1e-5f);
            }
            finally { Thread.CurrentThread.CurrentCulture = prev; }
        }

        [Test]
        public void LoadFromAFolderWithoutTheFile_IsNull()   // = "no file → pure conversion"
        {
            Assert.IsNull(MmdClothProfile.Load(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-such-mmd-model-dir")));
        }
    }
}
