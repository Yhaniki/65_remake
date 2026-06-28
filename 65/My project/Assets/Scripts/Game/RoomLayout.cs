using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Verbatim layout constants for the waiting-room (開房間的大廳, SCNCHIRSROOM / scene id 0x27), reverse-engineered
    /// from the decompiled CStateRoom + Player (assets/sdox_offline/named/modules/state/022, gameplay/028). The room
    /// holds up to six dancers on the dais (slots 0..5, fixed seat anchors) plus a free-walking local "looker" that the
    /// arrow keys move around the floor. SCENE.HRC carries NO anchor data (it is a degenerate 1-bone stub), so these
    /// positions are the only authoritative source. Pure data (no Unity behaviour) so it is unit-tested directly.
    /// </summary>
    public static class RoomLayout
    {
        public const int SeatCount = 6;

        /// <summary>The six seated-dancer anchor positions on the dais (native SDO scene units). Identical across all
        /// five game-mode tables in the EXE .data (StateRoom_OnPlayerJoin / Player position table) — the canonical seat
        /// layout. Non-zero Y = the raised platform sections.</summary>
        public static readonly Vector3[] SeatAnchors =
        {
            new Vector3(-44f, -35f, -31f),
            new Vector3(-97f,  -7f, -19f),
            new Vector3( 19f, -15f, -19f),
            new Vector3(-80f,  19f,  41f),
            new Vector3(-15f,  22f,  57f),
            new Vector3( 40f,  12f,  18f),
        };

        // free-walking movement bounds (StateRoom_ClampCameraPos_0047dee0, integer .data constants): X∈[-278,100],
        // Z∈[-279,100]; Y is not clamped. The local looker (and the camera) are clamped to this box each step.
        public const float MinX = -278f, MaxX = 100f;
        public const float MinZ = -279f, MaxZ = 100f;

        /// <summary>Floor plane the free-walking local player stands on. The EXE looker tables (slots 6..15) all use
        /// Y=0, so the local avatar walks the Y=0 plane (the dais seats above use their own per-seat Y).</summary>
        public const float FloorY = 0f;

        // ---- ROOM UI head-portrait slot screen rects (DDRROOM.XML <win1> AvatarView1..6) ----
        // win1 rests at TransForm target (0,1); each AvatarView child is at (x,55) size 96×76, so the on-screen
        // top-left = (x, 56). Six evenly-pitched slots across the top head panel.
        public static readonly float[] HeadSlotX = { 63f, 184f, 306f, 430f, 549f, 675f };
        public const float HeadSlotY = 56f;
        public const float HeadSlotW = 96f, HeadSlotH = 76f;
    }
}
