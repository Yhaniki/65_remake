using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>Pins the verbatim RoomLayout constants so an accidental edit to the RE'd tables is caught.</summary>
    public class RoomLayoutTests
    {
        [Test]
        public void Has_Six_Seat_Anchors_Matching_The_EXE_Table()
        {
            Assert.AreEqual(6, RoomLayout.SeatCount);
            Assert.AreEqual(6, RoomLayout.SeatAnchors.Length);
            Assert.AreEqual(new Vector3(-44f, -35f, -31f), RoomLayout.SeatAnchors[0]);
            Assert.AreEqual(new Vector3(-97f, -7f, -19f), RoomLayout.SeatAnchors[1]);
            Assert.AreEqual(new Vector3(19f, -15f, -19f), RoomLayout.SeatAnchors[2]);
            Assert.AreEqual(new Vector3(-80f, 19f, 41f), RoomLayout.SeatAnchors[3]);
            Assert.AreEqual(new Vector3(-15f, 22f, 57f), RoomLayout.SeatAnchors[4]);
            Assert.AreEqual(new Vector3(40f, 12f, 18f), RoomLayout.SeatAnchors[5]);
        }

        [Test]
        public void Walk_Bounds_Match_ClampCameraPos_Constants()
        {
            Assert.AreEqual(-278f, RoomLayout.MinX);
            Assert.AreEqual(100f, RoomLayout.MaxX);
            Assert.AreEqual(-279f, RoomLayout.MinZ);
            Assert.AreEqual(100f, RoomLayout.MaxZ);
            Assert.Less(RoomLayout.MinX, RoomLayout.MaxX);
            Assert.Less(RoomLayout.MinZ, RoomLayout.MaxZ);
        }

        [Test]
        public void Head_Slots_Are_Six_Left_To_Right()
        {
            Assert.AreEqual(6, RoomLayout.HeadSlotX.Length);
            for (int i = 1; i < RoomLayout.HeadSlotX.Length; i++)
                Assert.Greater(RoomLayout.HeadSlotX[i], RoomLayout.HeadSlotX[i - 1], "head slots must increase in X");
            Assert.AreEqual(63f, RoomLayout.HeadSlotX[0]);
            Assert.AreEqual(675f, RoomLayout.HeadSlotX[5]);
            Assert.AreEqual(56f, RoomLayout.HeadSlotY);
        }
    }
}
