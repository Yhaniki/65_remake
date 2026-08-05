using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Pure movement logic for the local player walking around the waiting room with the arrow keys. Reverse-engineered
    /// from the decompiled StateRoom_OnArrowKey_0047f450 (scancode → direction code) and Player_StepMovement_004abc20
    /// (per-frame position integration + facing). No Unity behaviour — every method is a pure function of its inputs so
    /// the direction mapping, the per-axis integration, the bounds clamp and the facing angle are all unit-tested.
    /// </summary>
    public static class RoomMovement
    {
        // Player_StepMovement_004abc20: delta = dt_ms * 0.02 * speedMult; speedMult = 3.0 (walk) or 5.0 (run).
        public const float MoveScale = 0.02f;
        public const float WalkSpeed = 3f, RunSpeed = 5f;

        /// <summary>Arrow key → direction code, matching StateRoom_OnArrowKey_0047f450 (UP=0, LEFT=1, DOWN=2, RIGHT=3).
        /// Returns null for any non-arrow key.</summary>
        public static int? MapKeyToDir(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.UpArrow: return 0;
                case KeyCode.LeftArrow: return 1;
                case KeyCode.DownArrow: return 2;
                case KeyCode.RightArrow: return 3;
                default: return null;
            }
        }

        /// <summary>Integrate one movement step in <paramref name="dir"/> for <paramref name="dtMs"/> milliseconds.
        /// Axis mapping (Player_StepMovement): dir0 UP → +Z, dir1 LEFT → −X, dir2 DOWN → −Z, dir3 RIGHT → +X. Y unchanged.
        /// Returns the new position; does not mutate the input.</summary>
        public static Vector3 Step(Vector3 pos, int dir, float dtMs, float speedMult)
        {
            float d = dtMs * MoveScale * speedMult;
            switch (dir)
            {
                case 0: pos.z += d; break;
                case 1: pos.x -= d; break;
                case 2: pos.z -= d; break;
                case 3: pos.x += d; break;
            }
            return pos;
        }

        /// <summary>
        /// 旁觀席專用的相機平移。旁觀者站在官方 looker 位置上不能走動(<see cref="RoomLayout.SpectatorAnchors"/>),
        /// 所以左右鍵改成推「相機錨點」的 X:人不動、視角橫移過去看房間裡的別人。
        /// 上/下(dir 0/2)不動 —— 房間相機的 Z 是跟著人走的,人不走就沒有前後。
        /// 速度與走路同一條式子(<see cref="Step"/>),夾在相機停止框的 X 範圍內,免得推到框外要按很久才回得來。
        /// </summary>
        public static float StepCameraPanX(float anchorX, int dir, float dtMs, float speedMult, float minX, float maxX)
        {
            float d = dtMs * MoveScale * speedMult;
            if (dir == 1) anchorX -= d;
            else if (dir == 3) anchorX += d;
            return Mathf.Clamp(anchorX, minX, maxX);
        }

        /// <summary>
        /// 旁觀席專用的相機推軌(拉遠/拉近)。房間相機看的方向就是 +Z,所以「拉近」＝把眼睛往 +Z 推向站位、
        /// 「拉遠」＝往 −Z 退開:上(dir 0)拉近、下(dir 2)拉遠,跟走路時上=往房間深處走的方向感一致。
        /// 左/右(dir 1/3)不動 —— 那是 <see cref="StepCameraPanX"/> 的橫移。
        /// <paramref name="minZ"/>=後牆前的極限(再退就穿牆)、<paramref name="maxZ"/>=最近距離(再近就穿進人身上)。
        /// </summary>
        public static float StepCameraDollyZ(float eyeZ, int dir, float dtMs, float speedMult, float minZ, float maxZ)
        {
            float d = dtMs * MoveScale * speedMult;
            if (dir == 0) eyeZ += d;         // UP → 眼睛往前推(拉近)
            else if (dir == 2) eyeZ -= d;    // DOWN → 眼睛往後退(拉遠)
            return Mathf.Clamp(eyeZ, minZ, Mathf.Max(minZ, maxZ));
        }

        /// <summary>Clamp X/Z to the room walk box (RoomLayout.Min/MaxX/Z); Y is left free (StateRoom_ClampCameraPos).</summary>
        public static Vector3 Clamp(Vector3 pos)
        {
            pos.x = Mathf.Clamp(pos.x, RoomLayout.MinX, RoomLayout.MaxX);
            pos.z = Mathf.Clamp(pos.z, RoomLayout.MinZ, RoomLayout.MaxZ);
            return pos;
        }

        /// <summary>Facing Y-rotation (degrees) for a direction, matching Player_SetFacingAngle_004aa840:
        /// UP=180, LEFT=90, DOWN=0, RIGHT=270.</summary>
        public static float FacingDegrees(int dir)
        {
            switch (dir)
            {
                case 0: return 180f;
                case 1: return 90f;
                case 2: return 0f;
                case 3: return 270f;
                default: return 0f;
            }
        }
    }
}
