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
