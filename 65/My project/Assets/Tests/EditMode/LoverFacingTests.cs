using System;
using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    /// <summary>
    /// Faithful-port tests for <see cref="LoverFacing"/> (sdo.bin.c:613983-613992, 469514-469517).
    /// See docs/reverse-engineering/SDO_COUPLE_MODE.md §4.
    /// </summary>
    public class LoverFacingTests
    {
        private const double Eps = 1e-6;

        [Test]
        public void Zero_Degrees_Is_Identity()
        {
            LoverFacing.YawQuaternion(0.0, out var x, out var y, out var z, out var w);
            Assert.AreEqual(0.0, x, Eps);
            Assert.AreEqual(0.0, y, Eps);
            Assert.AreEqual(0.0, z, Eps);
            Assert.AreEqual(1.0, w, Eps);
        }

        [Test]
        public void OneEighty_Degrees_Faces_Opposite()
        {
            LoverFacing.YawQuaternion(180.0, out var x, out var y, out var z, out var w);
            Assert.AreEqual(0.0, x, Eps);
            Assert.AreEqual(1.0, y, Eps);   // sin(90°)
            Assert.AreEqual(0.0, z, Eps);
            Assert.AreEqual(0.0, w, Eps);   // cos(90°)
        }

        [Test]
        public void Ninety_Degrees_Is_Quarter_Turn()
        {
            LoverFacing.YawQuaternion(90.0, out var x, out var y, out var z, out var w);
            double r = Math.Sqrt(0.5); // ~0.70710678
            Assert.AreEqual(0.0, x, Eps);
            Assert.AreEqual(r, y, Eps);
            Assert.AreEqual(0.0, z, Eps);
            Assert.AreEqual(r, w, Eps);
        }

        [Test]
        public void Yaw_Is_Always_Pure_Y()
        {
            foreach (var deg in new[] { -135.0, -30.0, 45.0, 200.0, 359.0 })
            {
                LoverFacing.YawQuaternion(deg, out var x, out var y, out var z, out var w);
                Assert.AreEqual(0.0, x, Eps, $"x@{deg}");
                Assert.AreEqual(0.0, z, Eps, $"z@{deg}");
                // unit quaternion
                Assert.AreEqual(1.0, x * x + y * y + z * z + w * w, Eps, $"norm@{deg}");
            }
        }

        [Test]
        public void DegToRad_Matches_Client_Constant()
        {
            Assert.AreEqual(0.017453292, LoverFacing.DegToRad, 1e-12);
        }

        [Test]
        public void TableIndex_GameType6_Uses_First_Bank()
        {
            Assert.AreEqual(0, LoverFacing.TableIndex(0, 6));
            Assert.AreEqual(1, LoverFacing.TableIndex(1, 6));
            Assert.AreEqual(5, LoverFacing.TableIndex(5, 6));
        }

        [Test]
        public void TableIndex_OtherType_Uses_Second_Bank()
        {
            Assert.AreEqual(6, LoverFacing.TableIndex(0, 0));
            Assert.AreEqual(7, LoverFacing.TableIndex(1, 2));
            Assert.AreEqual(11, LoverFacing.TableIndex(5, 12));
        }
    }
}
