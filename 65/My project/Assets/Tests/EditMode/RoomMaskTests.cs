using System.IO;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>Decodes the real SCNCHIRSROOM/MASK.MSK and pins the walkable-mask transform (validated against the
    /// SCENE.MSH floor geometry). Guards the 748×525 dims, the LSB-first bit packing and the world→cell mapping.</summary>
    public class RoomMaskTests
    {
        private static RoomMask Load()
        {
            var path = Path.Combine(SdoExtracted.Root, "SCENE", "SCNCHIRSROOM", "MASK.MSK");
            Assert.IsTrue(File.Exists(path), "Missing extracted MASK.MSK at " + path);
            var mask = RoomMask.Parse(File.ReadAllBytes(path));
            Assert.IsNotNull(mask, "MASK.MSK failed to parse");
            return mask;
        }

        [Test]
        public void Header_Decodes_To_748x525()
        {
            var m = Load();
            Assert.AreEqual(392700, m.CellCount);
            Assert.AreEqual(12272, m.WordCount);
            Assert.AreEqual(RoomMask.Width * RoomMask.Height, m.CellCount);   // 748*525
        }

        [Test]
        public void Walkable_Cell_Count_Matches_The_Decode()
        {
            // 59692 walkable cells — pins the bit order (LSB-first) and the 748×525 packing.
            Assert.AreEqual(59692, Load().WalkableCount());
        }

        [Test]
        public void Floor_Points_Are_Walkable_Dais_And_OutOfBounds_Are_Not()
        {
            var m = Load();
            // points verified to lie on the decoded floor polygon (coincides with the SCENE.MSH floor)
            Assert.IsTrue(m.IsWalkable(-44f, -31f), "floor point (-44,-31) should be walkable");
            Assert.IsTrue(m.IsWalkable(-97f, -19f), "floor point (-97,-19) should be walkable");
            // dance-spot origin sits on the raised dais (not floor) → not in the walk mask
            Assert.IsFalse(m.IsWalkable(0f, 0f), "dais centre (0,0) should not be floor-walkable");
            // far outside the grid → blocked
            Assert.IsFalse(m.IsWalkable(10000f, 10000f));
            Assert.IsFalse(m.IsWalkable(-10000f, -10000f));
        }

        [Test]
        public void Transform_Matches_The_RE_Constants()
        {
            Assert.AreEqual(444, RoomMask.ColumnX(0f));       // round(0 + 444.4)
            Assert.AreEqual(262, RoomMask.RowZ(0f));          // round(262.3 - 0)
            Assert.AreEqual(0, RoomMask.ColumnX(-444.4f));    // grid origin X
        }

        [Test]
        public void Walkable_Centroid_Is_On_The_Floor()
        {
            var m = Load();
            Assert.IsTrue(m.TryWalkableCentroid(out var c));
            Assert.IsTrue(m.IsWalkable(c.x, c.z), "the walkable centroid should itself be walkable");
        }
    }
}
