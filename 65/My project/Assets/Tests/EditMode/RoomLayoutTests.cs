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
        public void Slot_Idle_Mot_Paths_Seated_Standby_Spectators_Waiting()
        {
            // 這條守的是「連線的旁觀者真的擺出官方那十種看戲姿勢」:動作表早就解出來了
            // (SlotMotionName),但遠端/本機生角色時要**經過 SlotIdleMot 這個橋**才會用到它 ——
            // 少了這一步,十個旁觀者會全部站著發呆(而且是靜默的:沒有任何報錯)。
            Assert.AreEqual(SdoRoomAvatar.IdleMot, RoomScene3D.SlotIdleMot(0, male: false));
            Assert.AreEqual(SdoRoomAvatar.MaleIdleMot, RoomScene3D.SlotIdleMot(5, male: true));
            Assert.AreEqual("MOTION/WWAITING004.MOT", RoomScene3D.SlotIdleMot(6, male: false));
            Assert.AreEqual("MOTION/MWAITING004.MOT", RoomScene3D.SlotIdleMot(6, male: true));
            Assert.AreEqual("MOTION/WWAITING009.MOT", RoomScene3D.SlotIdleMot(15, male: false));
            // 十個旁觀 slot 各一支,不重複(與 SlotMotionName 同一張表,這裡驗的是路徑組出來也還是十種)
            var seen = new System.Collections.Generic.HashSet<string>();
            for (int s = RoomLayout.SeatCount; s < RoomLayout.SlotCount; s++)
                Assert.IsTrue(seen.Add(RoomScene3D.SlotIdleMot(s, male: false)), "slot " + s + " needs its own pose");
            // 超出十個旁觀位(協定擋在 MaxSpectators=10,這是防禦)→ 退回站立待機,不是丟例外
            Assert.AreEqual(SdoRoomAvatar.IdleMot, RoomScene3D.SlotIdleMot(RoomLayout.SlotCount + 3, male: false));
        }

        [Test]
        public void Spectator_Slot_Beats_Flying_Wing_For_Idle_Walk_And_Hover()
        {
            // 使用者回報:「穿戴翅膀旁觀沒有做旁觀動作」—— 舊版一律讓道具贏(SpecialMotionItems.IdleMotFor),
            // 於是穿飛行翅膀的人站上旁觀席還是浮空 flystay,跟座位上的人長得一模一樣,誰在看戲分不出來。
            // 旁觀席改成看戲姿勢優先,而且是**整組**(idle / walk / 懸浮)—— 只擋 idle 會變成
            // 「浮在半空中做地面的看戲姿勢」。
            var wings = new[] { "AVATAR/008448_WOMAN_CHIBANG.MSH" };

            // 座位上:翅膀照舊贏(這是官方行為,不能被這次的修改弄壞)
            Assert.IsTrue(RoomScene3D.FlyingAt(0, wings));
            Assert.AreEqual(SpecialMotionItems.FlyIdleMot(false), RoomScene3D.ResolveIdleMot(0, male: false, parts: wings));
            Assert.AreEqual(SpecialMotionItems.FlyWalkMot(false), RoomScene3D.ResolveWalkMot(0, male: false, parts: wings));

            // 旁觀席:看戲姿勢贏,而且不飛不浮、走路也是一般走路
            for (int s = RoomLayout.SeatCount; s < RoomLayout.SlotCount; s++)
            {
                Assert.IsTrue(RoomScene3D.IsSpectatorSlot(s));
                Assert.IsFalse(RoomScene3D.FlyingAt(s, wings), "slot " + s + " 旁觀時不飛");
                Assert.AreEqual(0f, SpecialMotionItems.HoverY(RoomScene3D.FlyingAt(s, wings)), "旁觀不浮空");
                Assert.AreEqual(RoomScene3D.SlotIdleMot(s, male: false), RoomScene3D.ResolveIdleMot(s, male: false, parts: wings),
                                "slot " + s + " 要用自己那格的看戲姿勢,不是 flystay");
                Assert.AreEqual(SdoRoomAvatar.WalkMot, RoomScene3D.ResolveWalkMot(s, male: false, parts: wings));
                Assert.AreEqual(SdoRoomAvatar.MaleWalkMot, RoomScene3D.ResolveWalkMot(s, male: true, parts: wings));
            }

            // 沒穿翅膀的人不受影響:座位是大廳待機、旁觀是看戲姿勢(與 SlotIdleMot 同一個答案)
            Assert.AreEqual(SdoRoomAvatar.IdleMot, RoomScene3D.ResolveIdleMot(0, male: false, parts: null));
            Assert.AreEqual("MOTION/WWAITING004.MOT", RoomScene3D.ResolveIdleMot(6, male: false, parts: null));
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
