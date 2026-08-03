using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Multi-dancer floor formations ("隊形"), reproduced VERBATIM from the decompiled offline client.
    ///
    /// The official layout is one hardcoded float table at EXE VA 0x582690 (.data, file offset 0x182690). It is a flat
    /// array of 0x48-byte entries; each entry = 6 × Vector3 (x,y,z), y always 0 (floor plane). A dancer's world spot is
    ///   pos = *(Vector3*)(0x582690 + (count + type*6)*0x48 + slotIndex*0x0c)
    /// i.e. the table is indexed by (dancerCount + formationType*6) to pick the entry, then by slotIndex for the dancer.
    ///
    /// Only formation TYPES 0..2 are user-selectable in the room dialog — it builds exactly 3 "formation%d" buttons
    /// (FORMATION1/2/3.AN), read from DAT_00674f04+0x64 (= game field +0x6a). Those are the clean 1-to-6-dancer
    /// families reproduced here (EXE entries 1..18). Higher table entries feed the special couple/versus/cinematic
    /// game modes (switch(DAT_00674f04+0x8c) cases 1..3 and the +0x87 couple flag) via different sub-tables and are
    /// intentionally not exposed here.
    ///
    /// SLOT ↔ RANK (decompiled FUN_00471f20 setup + FUN_0046dd20 per-frame):
    ///   • At setup, slot 0 = the room host (DAT_00674f04+0x81); the other dancers fill slots 1,2,3… in room-slot order.
    ///   • Every frame, FUN_0046dd20 scans each dancer's score (+0x48), finds the current #1, and slides them into
    ///     slot 0 — the center-front spot the camera anchors on (CameraMgr+0x340) — sending the displaced dancer back
    ///     to its own slot (a smooth *0.9 + target*0.1 LERP). So slot 0 is the LEADER / rank-1 spot, dynamically.
    /// Positions are floor offsets relative to the dance anchor (solo anchor = origin; entry for count=1 is (0,0,0)).
    /// </summary>
    public static class FormationCatalog
    {
        /// <summary>Number of user-selectable formation types (table modes 0,1,2 = the 3 room-dialog buttons).</summary>
        public const int TypeCount = 3;
        /// <summary>Max dancers a formation entry defines (each entry holds up to 6 slots).</summary>
        public const int MaxDancers = 6;

        // Verified verbatim from the EXE (entries 1..18 @ 0x582690, all coords are exact integers; y = 0).
        // Indexed [type][count-1] → the `count` slot positions. Slot 0 is the leader / center-front (rank-1) spot.
        private static readonly Vector3[][][] Table =
        {
            // ---- TYPE 0 : centre wedge, ±50/±100 x-spacing, back row at z=50 ----
            new[]
            {
                new[] { V(0, 0) },
                new[] { V(-25, 0), V(-50, 50) },
                new[] { V(0, 0), V(-50, 50), V(50, 50) },
                new[] { V(-25, 0), V(-50, 50), V(50, 50), V(0, 50) },
                new[] { V(-25, 0), V(-100, 50), V(100, 50), V(-50, 50), V(50, 50) },
                new[] { V(-25, 0), V(-100, 50), V(100, 50), V(-50, 50), V(50, 50), V(0, 50) },
            },
            // ---- TYPE 1 : leader forward at z=-25, staggered mid row at z=15, back row at z=50 ----
            new[]
            {
                new[] { V(-15, -25) },
                new[] { V(-15, -25), V(-55, 15) },
                new[] { V(-15, -25), V(-55, 15), V(35, 15) },
                new[] { V(-15, -25), V(-55, 15), V(35, 15), V(5, 50) },
                new[] { V(-15, -25), V(-55, 15), V(35, 15), V(5, 50), V(-90, 50) },
                new[] { V(-15, -25), V(-55, 15), V(35, 15), V(5, 50), V(-90, 50), V(85, 50) },
            },
            // ---- TYPE 2 : leader at z=15, back row z=50 widening right, extra front pair at z=-25 for 5/6 ----
            new[]
            {
                new[] { V(-5, 15) },
                new[] { V(-5, 15), V(-75, 50) },
                new[] { V(-5, 15), V(-75, 50), V(25, 50) },
                new[] { V(-5, 15), V(-75, 50), V(25, 50), V(95, 50) },
                new[] { V(-5, 15), V(-75, 50), V(25, 50), V(95, 50), V(-35, -25) },
                new[] { V(-5, 15), V(-75, 50), V(25, 50), V(95, 50), V(-35, -25), V(45, -25) },
            },
        };

        private static Vector3 V(float x, float z) => new Vector3(x, 0f, z);

        /// <summary>
        /// The `count` floor offsets for (formationType, dancerCount), relative to the dance anchor. Element [0] is the
        /// leader / center-front (rank-1, camera-anchor) slot; the rest are the other dancers. Clamps out-of-range
        /// arguments (type→0..2, count→1..6). Returns a fresh copy the caller may mutate.
        /// </summary>
        public static Vector3[] GetSlots(int type, int count)
        {
            type = Mathf.Clamp(type, 0, TypeCount - 1);
            count = Mathf.Clamp(count, 1, MaxDancers);
            var src = Table[type][count - 1];
            var outArr = new Vector3[src.Length];
            System.Array.Copy(src, outArr, src.Length);
            return outArr;
        }

        /// <summary>Human label for a formation type (matches the official FORMATION1/2/3 buttons, 1-based).</summary>
        public static string TypeName(int type) => "隊形 " + (Mathf.Clamp(type, 0, TypeCount - 1) + 1);
    }
}
