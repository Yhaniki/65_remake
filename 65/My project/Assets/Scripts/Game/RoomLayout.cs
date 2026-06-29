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

        /// <summary>The local HOST's fixed spawn — (-100, 0, -26). CAPTURED LIVE via Frida from the running official EXE
        /// (host = slot 0; its avatar object position) and then confirmed in the decompile: flat sdo_stand_alone.exe.c
        /// 99644-99660 loops the six dancer slots and writes each player +4/+8/+0xc = (-100, 0, -26). Offline only the
        /// host exists, so it stays here; the other dancers would be spread by the server's move packets — i.e. the
        /// offline client has NO per-dancer formation. (The static .data table-0..5 block (-44,-35,-31)… is dead — only
        /// slots 6..15 of those tables are read; (-44,-35,-31) is the camera anchor.) On the walkable floor (mask-OK).</summary>
        public static readonly Vector3 HostSpawn = new Vector3(-100f, 0f, -26f);

        /// <summary>The ten "looker"/spectator stand positions (slots 6..15), verbatim from the EXE .data table at
        /// <b>0x00583af0</b> — the table the OPEN ROOM actually uses. The avatar's scene byte +0x6af is -1 in the room,
        /// so Gameplay_OnAvatarSwap_00482bd0 takes the ELSE branch (the venue tables, NOT the df0 in-game-scene branch);
        /// and the venue byte (DAT_00674f04+0x82) defaults to 0, so the table is 0x583af0 — indexed by the FULL slot
        /// number ×0xc (slot 6 = entry 6 …). Pairs with the cat-0x21-indexed looker motions (same default-venue branch).
        /// They cluster around the dancers on the Y=0 floor. PE-parsed verbatim. (0x583970 was the venue-9/10/11 wide
        /// layout — wrong; 0x583df0 + per-slot SetEuler angles is the in-game-SCENE looker layout, not the room.)</summary>
        public static readonly Vector3[] SpectatorAnchors =
        {
            new Vector3(-132f, 0f,  31f),  // slot 6
            new Vector3(   3f, 0f,  13f),  // slot 7
            new Vector3( -20f, 0f,  34f),  // slot 8
            new Vector3( -51f, 0f,  46f),  // slot 9
            new Vector3(-151f, 0f, -41f),  // slot 10
            new Vector3(-178f, 0f, -71f),  // slot 11
            new Vector3(  49f, 0f, -70f),  // slot 12
            new Vector3(-195f, 0f, -95f),  // slot 13
            new Vector3(  98f, 0f, -99f),  // slot 14
            new Vector3(  85f, 0f, -62f),  // slot 15
        };

        /// <summary>Anchor for a slot: dancers (0..5) all map to <see cref="HostSpawn"/> (the EXE spawns every dancer
        /// there; the server then spreads 1..5 — the remake gives them random walkable spots, see RoomScene3D), lookers
        /// (6..15) use their <see cref="SpectatorAnchors"/> position.</summary>
        public static Vector3 SlotAnchor(int slot)
            => slot < SeatCount ? HostSpawn : SpectatorAnchors[slot - SeatCount];

        public const int SlotCount = SeatCount + 10;   // 6 seated dancers + 10 spectators

        /// <summary>Unity yaw (degrees) a slot faces at spawn. Every room avatar keeps Player_Init's default heading
        /// (param_1[0x1b] = 0 → 0°, front/toward the camera): the open-room spawn path (OnPlayerJoin / OnAvatarSwap ELSE
        /// branch) sets the POSITION only and never an euler, so seated dancers AND lookers all face front. (Only the
        /// in-game-SCENE looker branch — df0 — applies per-slot SetEuler angles, which is NOT the room.)</summary>
        public static float SlotFacingDegrees(int slot) => 0f;

        // ---- per-slot spawn motion (RE'd from Motion_LoadRestTable_004a3900: two '~'-delimited string-pointer arrays in
        // .data — male @VA 0x585668, female @VA 0x585988; bucket index == motion category int; PE-parsed verbatim). The
        // open room takes the DEFAULT venue branch (StateRoom_OnPlayerJoin @022_state:5304-5324): seated dancers idle on
        // cat 0x15, spectators each pose on cat 0x21 indexed by (slot-6). ----

        /// <summary>The room dancers' STANDBY idle = motion category 0 (the lobby/standby rest, a single-entry bucket).
        /// This is the LOBBY idle (module 027's cat-0), NOT the in-game arena idle cat-0x15 (WREST0072) — that one is for
        /// the dance lane (Gameplay_LoadLaneAvatar). The waiting room holds the standby pose. PE-parsed: cat0 male =
        /// mrest0067, female = wrest0056 (see [[sdo-ingame-idle-mot]]).</summary>
        public const string SeatedIdleFemale = "WREST0056";
        public const string SeatedIdleMale   = "MREST0067";

        /// <summary>Category 0x21 "WAITING" — the spectators' distinct watching poses, via Motion_GetCategoryAt(0x21,
        /// slot-6, gender). This is the BUCKET LOAD ORDER (NOT the filename's numeric order): index 0 = *WAITING004,
        /// 1 = *WAITING007, … (twelve entries; the open room's ten lookers consume indices 0..9).</summary>
        public static readonly string[] WaitingFemale =
        {
            "WWAITING004", "WWAITING007", "WWAITING006", "WWAITING005", "WWAITING003", "WWAITING002",
            "WWAITING008", "WWAITING001", "WWAITING010", "WWAITING009", "WWAITING012", "WWAITING011",
        };
        public static readonly string[] WaitingMale =
        {
            "MWAITING004", "MWAITING007", "MWAITING006", "MWAITING005", "MWAITING003", "MWAITING002",
            "MWAITING008", "MWAITING001", "MWAITING010", "MWAITING009", "MWAITING012", "MWAITING011",
        };

        /// <summary>Motion file stem (under MOTION/, no extension) a slot plays at spawn in the open room: seats 0..5 hold
        /// the cat-0x15 rest idle; spectators 6..15 hold their cat-0x21 WAITING pose (Motion_GetCategoryAt(0x21, slot-6)).
        /// <paramref name="female"/> picks the WOMAN variant (the lobby test fills all sixteen with the default WOMAN
        /// avatar). Out-of-range spectator indices fall back to the rest idle.</summary>
        public static string SlotMotionName(int slot, bool female)
        {
            if (slot < SeatCount) return female ? SeatedIdleFemale : SeatedIdleMale;
            int idx = slot - SeatCount;
            var tbl = female ? WaitingFemale : WaitingMale;
            return (idx >= 0 && idx < tbl.Length) ? tbl[idx] : (female ? SeatedIdleFemale : SeatedIdleMale);
        }

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
