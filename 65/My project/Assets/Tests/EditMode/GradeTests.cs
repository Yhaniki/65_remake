using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    public class GradeTests
    {
        [Test]
        public void Bands_MapToLetters()
        {
            Assert.AreEqual("S", Grade.FromAccuracy(100.0));
            Assert.AreEqual("A+", Grade.FromAccuracy(99.9));
            Assert.AreEqual("A+", Grade.FromAccuracy(95.0));
            Assert.AreEqual("A", Grade.FromAccuracy(94.99));
            Assert.AreEqual("A", Grade.FromAccuracy(90.0));
            Assert.AreEqual("B", Grade.FromAccuracy(89.99));
            Assert.AreEqual("B", Grade.FromAccuracy(80.0));
            Assert.AreEqual("C", Grade.FromAccuracy(79.99));
            Assert.AreEqual("C", Grade.FromAccuracy(65.0));
            Assert.AreEqual("D", Grade.FromAccuracy(64.99));
            Assert.AreEqual("D", Grade.FromAccuracy(0.0));
        }
    }
}
