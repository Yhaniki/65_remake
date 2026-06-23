using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>Unit tests for MotLoader.SampleScale / ScaleVaries — the scale track that drives the SCN0008
    /// delta_line bars' "extend" (scale.Y 0→2 over the clip). Pure logic, no Unity scene.</summary>
    public class MotLoaderTests
    {
        // a 3-keyframe node whose scale.Y ramps 0→1→2 (x/z constant 1); Scl layout = (x,y,z,time) per key.
        private static MotLoader.Node RampY()
        {
            return new MotLoader.Node
            {
                Sc = 3,
                Scl = new float[] {
                    1f, 0f, 1f, 0f,
                    1f, 1f, 1f, 15f,
                    1f, 2f, 1f, 30f,
                },
            };
        }

        [Test]
        public void SampleScale_HitsKeyframes()
        {
            var n = RampY();
            Assert.AreEqual(new Vector3(1f, 0f, 1f), MotLoader.SampleScale(n, 0f));
            Assert.AreEqual(new Vector3(1f, 1f, 1f), MotLoader.SampleScale(n, 15f));
            Assert.AreEqual(new Vector3(1f, 2f, 1f), MotLoader.SampleScale(n, 30f));
        }

        [Test]
        public void SampleScale_InterpolatesBetweenKeyframes()
        {
            var n = RampY();
            Assert.AreEqual(0.5f, MotLoader.SampleScale(n, 7.5f).y, 1e-4f);   // midway 0→1
            Assert.AreEqual(1.5f, MotLoader.SampleScale(n, 22.5f).y, 1e-4f);  // midway 1→2
        }

        [Test]
        public void SampleScale_SingleKeyframe_ReturnsThatValue()
        {
            var n = new MotLoader.Node { Sc = 1, Scl = new float[] { 1f, 3f, 1f, 0f } };
            Assert.AreEqual(new Vector3(1f, 3f, 1f), MotLoader.SampleScale(n, 99f));
        }

        [Test]
        public void ScaleVaries_TrueForRamp_FalseForConstant()
        {
            Assert.IsTrue(MotLoader.ScaleVaries(RampY()));
            var flat = new MotLoader.Node
            {
                Sc = 2,
                Scl = new float[] { 1f, 1f, 1f, 0f, 1f, 1f, 1f, 30f },
            };
            Assert.IsFalse(MotLoader.ScaleVaries(flat));
            Assert.IsFalse(MotLoader.ScaleVaries(new MotLoader.Node { Sc = 1, Scl = new float[] { 1f, 1f, 1f, 0f } }));
            Assert.IsFalse(MotLoader.ScaleVaries(null));
        }
    }
}
