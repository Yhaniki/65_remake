using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>Pins the verbatim RoomLayout constants so an accidental edit to the RE'd tables is caught.</summary>
    public class RoomLayoutTests
    {
        [Test]
        public void Host_Spawn_Is_The_Captured_Fixed_Floor_Position()
        {
            // (-100,0,-26): captured live (Frida) from the official EXE + confirmed in the decompile (the 6-dancer-slot
            // loop writes each player +4/+8/+0xc here). On the walkable floor (Y=0), NOT origin/the dais.
            Assert.AreEqual(6, RoomLayout.SeatCount);
            Assert.AreEqual(new Vector3(-100f, 0f, -26f), RoomLayout.HostSpawn);
            Assert.That(RoomLayout.HostSpawn.x, Is.InRange(-198f, 175f));
            Assert.That(RoomLayout.HostSpawn.z, Is.InRange(-234f, 43f));
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
        public void Has_Ten_Spectator_Anchors_Matching_The_EXE_Table()
        {
            Assert.AreEqual(10, RoomLayout.SpectatorAnchors.Length);
            Assert.AreEqual(16, RoomLayout.SlotCount);
            // verbatim from the EXE .data table @0x00583af0 (the open-room venue branch, entries 6..15, indexed by full slot)
            Assert.AreEqual(new Vector3(-132f, 0f, 31f), RoomLayout.SpectatorAnchors[0]);   // slot 6
            Assert.AreEqual(new Vector3(3f, 0f, 13f), RoomLayout.SpectatorAnchors[1]);      // slot 7
            Assert.AreEqual(new Vector3(-151f, 0f, -41f), RoomLayout.SpectatorAnchors[4]);  // slot 10
            Assert.AreEqual(new Vector3(-178f, 0f, -71f), RoomLayout.SpectatorAnchors[5]);  // slot 11
            Assert.AreEqual(new Vector3(85f, 0f, -62f), RoomLayout.SpectatorAnchors[9]);    // slot 15
            foreach (var s in RoomLayout.SpectatorAnchors) Assert.AreEqual(0f, s.y, "spectators stand on the Y=0 floor");
        }

        [Test]
        public void SlotAnchor_Dancers_To_HostSpawn_Then_Spectators()
        {
            for (int i = 0; i < RoomLayout.SeatCount; i++)
                Assert.AreEqual(RoomLayout.HostSpawn, RoomLayout.SlotAnchor(i), "dancer slots 0..5 map to the host spawn (server spreads 1..5)");
            for (int i = 0; i < 10; i++)
                Assert.AreEqual(RoomLayout.SpectatorAnchors[i], RoomLayout.SlotAnchor(RoomLayout.SeatCount + i), "slots 6..15 are the spectators");
        }

        [Test]
        public void All_Slots_Face_The_Default_Front()
        {
            // the open-room spawn path sets position only (never an euler), so every avatar keeps Player_Init's
            // default heading of 0 (param_1[0x1b]=0) — seated dancers and lookers all face front/toward the camera.
            for (int s = 0; s < RoomLayout.SlotCount; s++)
                Assert.AreEqual(0f, RoomLayout.SlotFacingDegrees(s), "slot " + s + " faces the default front (0deg)");
        }

        [Test]
        public void Slot_Motions_Seated_Rest_Spectators_Indexed_Waiting()
        {
            // seats 0..5 hold the cat-0 STANDBY lobby idle (NOT the in-game arena idle cat-0x15 WREST0072)
            for (int i = 0; i < RoomLayout.SeatCount; i++)
            {
                Assert.AreEqual("WREST0056", RoomLayout.SlotMotionName(i, female: true));
                Assert.AreEqual("MREST0067", RoomLayout.SlotMotionName(i, female: false));
            }
            // spectators 6..15 -> cat-0x21 WAITING bucket LOAD order (Motion_GetCategoryAt(0x21, slot-6)); NOT numeric
            Assert.AreEqual(12, RoomLayout.WaitingFemale.Length);
            Assert.AreEqual("WWAITING004", RoomLayout.SlotMotionName(6, female: true));   // index 0
            Assert.AreEqual("WWAITING007", RoomLayout.SlotMotionName(7, female: true));   // index 1
            Assert.AreEqual("WWAITING001", RoomLayout.SlotMotionName(13, female: true));  // index 7
            Assert.AreEqual("WWAITING009", RoomLayout.SlotMotionName(15, female: true));  // index 9 (last looker)
            Assert.AreEqual("MWAITING004", RoomLayout.SlotMotionName(6, female: false));
            // each of the ten lookers gets a DISTINCT pose (the point of the indexed cat-0x21 lookup)
            var seen = new System.Collections.Generic.HashSet<string>();
            for (int s = RoomLayout.SeatCount; s < RoomLayout.SlotCount; s++)
                Assert.IsTrue(seen.Add(RoomLayout.SlotMotionName(s, female: true)), "spectator motions must be distinct");
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

        [Test]
        public void Deng_Marquee_Pattern_Is_Verbatim()
        {
            // The eight GUANG waiting lights share one 24×8 on/off table (DAT_00552230), advanced one row every 150 ms
            // (RoomDengMarquee), NOT per-light cyclers. Verify the embedded table verbatim against the EXE.
            var lit = RoomDengPattern.Lit;
            Assert.AreEqual(24, RoomDengPattern.Rows);
            Assert.AreEqual(8, RoomDengPattern.Lights);
            Assert.AreEqual(24, lit.Length);
            Assert.AreEqual(150f, RoomDengPattern.IntervalMs);

            // rows 0..7 = a single light sweeping GUANG1 -> GUANG8 (left -> right).
            for (int r = 0; r < 8; r++)
            {
                int onCount = 0, onLight = -1;
                for (int g = 0; g < 8; g++) if (lit[r][g]) { onCount++; onLight = g; }
                Assert.AreEqual(1, onCount, "chase row " + r + " lights exactly one light");
                Assert.AreEqual(r, onLight, "the lit light sweeps GUANG1 -> GUANG8");
            }
            // rows 8,9,12,13 = all-on flash; rows 10,11,14,15 = all-off.
            foreach (int r in new[] { 8, 9, 12, 13 })
                for (int g = 0; g < 8; g++) Assert.IsTrue(lit[r][g], "flash row " + r + " is all-on");
            foreach (int r in new[] { 10, 11, 14, 15 })
                for (int g = 0; g < 8; g++) Assert.IsFalse(lit[r][g], "gap row " + r + " is all-off");
            // rows 16..23 = alternating every-other-light blink (even rows 0101..., odd rows 1010...).
            for (int r = 16; r < 24; r++)
                for (int g = 0; g < 8; g++)
                    Assert.AreEqual((g % 2 == 1) == (r % 2 == 0), lit[r][g], "alternating row " + r + " light " + g);

            int total = 0;
            foreach (var row in lit) foreach (var on in row) if (on) total++;
            Assert.AreEqual(72, total, "lit-cell count matches DAT_00552230");
        }
    }
}
