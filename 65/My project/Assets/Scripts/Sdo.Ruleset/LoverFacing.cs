using System;

namespace Sdo.Ruleset
{
    /// <summary>
    /// 情侶模式 (LOVER) dancer facing — a faithful port of the CN online client rotation math.
    /// See docs/reverse-engineering/SDO_COUPLE_MODE.md §4.
    ///
    /// The client stores a per-dancer facing angle in DEGREES (avatar object +0x60) and turns it into a
    /// real Y-axis model rotation (FUN_009307c0 @ sdo.bin.c:613983-613992): axis = (0,1,0),
    /// angle = degrees × 0.017453292 (deg→rad), quaternion built by an axis-angle helper and written into
    /// the body node — so the two paired dancers physically turn to FACE each other (not a camera illusion).
    ///
    /// The angle value itself comes from a per-slot ushort table (DAT_00b86c64 for game-type 6,
    /// DAT_00b86c70 otherwise) indexed by <see cref="TableIndex"/>; those raw values are not present in the
    /// decompiled .c (no PE .rdata) — the remake feeds a tunable placeholder angle until a real EXE hexdump
    /// / Frida capture recovers them. This class owns only the pure, testable math.
    /// </summary>
    public static class LoverFacing
    {
        /// <summary>deg→rad constant used verbatim by the client (0.017453292). sdo.bin.c:613988.</summary>
        public const double DegToRad = 0.017453292;

        /// <summary>
        /// Convert a facing angle (degrees) into a pure-yaw quaternion (axis (0,1,0)); outputs (x,y,z,w).
        /// x and z are always 0. 0°→(0,0,0,1) identity; 180°→(0,1,0,0); 90°→(0,.7071,0,.7071).
        /// </summary>
        public static void YawQuaternion(double degrees, out double x, out double y, out double z, out double w)
        {
            double half = degrees * DegToRad * 0.5;
            x = 0.0;
            y = Math.Sin(half);
            z = 0.0;
            w = Math.Cos(half);
        }

        /// <summary>
        /// Per-slot facing-table index <c>slot + (gameType != 6 ? 6 : 0)</c> — the client reads
        /// <c>(&amp;DAT_00b86c64)[slot + (type!=6)*6]</c> (sdo.bin.c:469514-469517): game-type 6 uses the first
        /// 6-entry bank, everything else the second.
        /// </summary>
        public static int TableIndex(int slot, int gameType) => slot + (gameType != 6 ? 6 : 0);
    }
}
