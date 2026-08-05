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

        // ---- 旁觀席:人不能走,左右鍵改推相機錨點(RoomScene3D.Update 的旁觀分支) ----

        [Test]
        public void StepCameraPanX_Left_Right_Move_At_Walk_Speed()
        {
            // 同一條式子:1000 ms * 0.02 * 3.0 = 60
            Assert.AreEqual(-60f, RoomMovement.StepCameraPanX(0f, 1, 1000f, RoomMovement.WalkSpeed, -999f, 999f), 1e-4f);
            Assert.AreEqual(60f, RoomMovement.StepCameraPanX(0f, 3, 1000f, RoomMovement.WalkSpeed, -999f, 999f), 1e-4f);
        }

        [Test]
        public void StepCameraPanX_Up_Down_Do_Nothing()
        {
            Assert.AreEqual(12f, RoomMovement.StepCameraPanX(12f, 0, 1000f, RoomMovement.WalkSpeed, -999f, 999f), 1e-4f);
            Assert.AreEqual(12f, RoomMovement.StepCameraPanX(12f, 2, 1000f, RoomMovement.WalkSpeed, -999f, 999f), 1e-4f);
            Assert.AreEqual(12f, RoomMovement.StepCameraPanX(12f, -1, 1000f, RoomMovement.WalkSpeed, -999f, 999f), 1e-4f);
        }

        [Test]
        public void StepCameraPanX_Clamps_To_The_Camera_Stop_Box()
        {
            // 推到框外不會累積成「按了半天才回得來」:每一步都夾住
            Assert.AreEqual(-120f, RoomMovement.StepCameraPanX(-100f, 1, 5000f, RoomMovement.WalkSpeed, -120f, 100f), 1e-4f);
            Assert.AreEqual(100f, RoomMovement.StepCameraPanX(90f, 3, 5000f, RoomMovement.WalkSpeed, -120f, 100f), 1e-4f);
            // 起點就在框外(旁觀站位可以比相機停止框還外面,例如 x=-178)→ 第一步就被拉進框內
            Assert.AreEqual(-120f, RoomMovement.StepCameraPanX(-178f, 3, 1f, RoomMovement.WalkSpeed, -120f, 100f), 1e-4f);
        }

        [Test]
        public void StepCameraDollyZ_Up_Pulls_In_Down_Pulls_Out()
        {
            // 房間相機看 +Z:上=眼睛往前推(拉近)、下=往後退(拉遠),與走路的方向感一致
            Assert.AreEqual(-175f, RoomMovement.StepCameraDollyZ(-235f, 0, 1000f, RoomMovement.WalkSpeed, -999f, 999f), 1e-4f);
            Assert.AreEqual(-295f, RoomMovement.StepCameraDollyZ(-235f, 2, 1000f, RoomMovement.WalkSpeed, -999f, 999f), 1e-4f);
        }

        [Test]
        public void StepCameraDollyZ_Left_Right_Do_Nothing()
        {
            Assert.AreEqual(-235f, RoomMovement.StepCameraDollyZ(-235f, 1, 1000f, RoomMovement.WalkSpeed, -999f, 999f), 1e-4f);
            Assert.AreEqual(-235f, RoomMovement.StepCameraDollyZ(-235f, 3, 1000f, RoomMovement.WalkSpeed, -999f, 999f), 1e-4f);
            Assert.AreEqual(-235f, RoomMovement.StepCameraDollyZ(-235f, -1, 1000f, RoomMovement.WalkSpeed, -999f, 999f), 1e-4f);
        }

        [Test]
        public void StepCameraDollyZ_Clamps_Between_Back_Wall_And_Nearest()
        {
            // 遠端 = 後牆前(cameraEyeMinZ)、近端 = 錨點再往前一點(不推進人身上)
            Assert.AreEqual(-378f, RoomMovement.StepCameraDollyZ(-360f, 2, 5000f, RoomMovement.WalkSpeed, -378f, -60f), 1e-4f);
            Assert.AreEqual(-60f, RoomMovement.StepCameraDollyZ(-80f, 0, 5000f, RoomMovement.WalkSpeed, -378f, -60f), 1e-4f);
        }

        [Test]
        public void StepCameraDollyZ_Survives_An_Inverted_Range()
        {
            // 站位太靠後時近端可能算得比後牆還後面(near < min)—— 夾出來的區間不能翻過來變成 NaN/亂跳
            Assert.AreEqual(-378f, RoomMovement.StepCameraDollyZ(-300f, 0, 1000f, RoomMovement.WalkSpeed, -378f, -400f), 1e-4f);
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
