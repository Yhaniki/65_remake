using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>Verifies the room walk logic (RoomMovement) against the decompiled StateRoom/Player constants.</summary>
    public class RoomMovementTests
    {
        [Test]
        public void MapKeyToDir_Arrows_Map_To_RE_Direction_Codes()
        {
            Assert.AreEqual(0, RoomMovement.MapKeyToDir(KeyCode.UpArrow));
            Assert.AreEqual(1, RoomMovement.MapKeyToDir(KeyCode.LeftArrow));
            Assert.AreEqual(2, RoomMovement.MapKeyToDir(KeyCode.DownArrow));
            Assert.AreEqual(3, RoomMovement.MapKeyToDir(KeyCode.RightArrow));
        }

        [Test]
        public void MapKeyToDir_NonArrow_Returns_Null()
        {
            Assert.IsNull(RoomMovement.MapKeyToDir(KeyCode.Space));
            Assert.IsNull(RoomMovement.MapKeyToDir(KeyCode.W));
            Assert.IsNull(RoomMovement.MapKeyToDir(KeyCode.Return));
        }

        [Test]
        public void Step_Moves_On_The_Correct_Signed_Axis()
        {
            var p = Vector3.zero;
            // 1000 ms * 0.02 * 3.0 = 60 units (walk)
            Assert.AreEqual(new Vector3(0f, 0f, 60f), RoomMovement.Step(p, 0, 1000f, RoomMovement.WalkSpeed));  // UP +Z
            Assert.AreEqual(new Vector3(-60f, 0f, 0f), RoomMovement.Step(p, 1, 1000f, RoomMovement.WalkSpeed)); // LEFT -X
            Assert.AreEqual(new Vector3(0f, 0f, -60f), RoomMovement.Step(p, 2, 1000f, RoomMovement.WalkSpeed)); // DOWN -Z
            Assert.AreEqual(new Vector3(60f, 0f, 0f), RoomMovement.Step(p, 3, 1000f, RoomMovement.WalkSpeed));  // RIGHT +X
        }

        [Test]
        public void Step_Run_Is_Faster_Than_Walk()
        {
            var walk = RoomMovement.Step(Vector3.zero, 3, 100f, RoomMovement.WalkSpeed).x; // 100*0.02*3 = 6
            var run = RoomMovement.Step(Vector3.zero, 3, 100f, RoomMovement.RunSpeed).x;   // 100*0.02*5 = 10
            Assert.AreEqual(6f, walk, 1e-4f);
            Assert.AreEqual(10f, run, 1e-4f);
        }

        [Test]
        public void Step_Leaves_Y_Untouched()
        {
            var p = new Vector3(1f, 12.5f, 2f);
            Assert.AreEqual(12.5f, RoomMovement.Step(p, 0, 500f, RoomMovement.WalkSpeed).y);
        }

        [Test]
        public void Clamp_Holds_Each_Edge_Of_The_Walk_Box()
        {
            Assert.AreEqual(RoomLayout.MinX, RoomMovement.Clamp(new Vector3(-9999f, 0f, 0f)).x);
            Assert.AreEqual(RoomLayout.MaxX, RoomMovement.Clamp(new Vector3(9999f, 0f, 0f)).x);
            Assert.AreEqual(RoomLayout.MinZ, RoomMovement.Clamp(new Vector3(0f, 0f, -9999f)).z);
            Assert.AreEqual(RoomLayout.MaxZ, RoomMovement.Clamp(new Vector3(0f, 0f, 9999f)).z);
        }

        [Test]
        public void Clamp_Inside_Box_Is_Unchanged_And_Leaves_Y_Free()
        {
            var p = new Vector3(-50f, 999f, -50f);
            Assert.AreEqual(p, RoomMovement.Clamp(p));
        }

        [Test]
        public void FacingDegrees_Match_RE_Table()
        {
            Assert.AreEqual(180f, RoomMovement.FacingDegrees(0));
            Assert.AreEqual(90f, RoomMovement.FacingDegrees(1));
            Assert.AreEqual(0f, RoomMovement.FacingDegrees(2));
            Assert.AreEqual(270f, RoomMovement.FacingDegrees(3));
        }
    }
}
