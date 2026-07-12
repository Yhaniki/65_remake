using System;
using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>
    /// The handful of avatar items that carry their OWN idle / walk animation (separate from a wing's own flap), each
    /// reverse-engineered from the offline client (sdo_stand_alone.exe). Two independent traits, both keyed on the
    /// equipped item's 6-digit MODEL id — the mesh filename prefix, which is the value the engine matches at
    /// Avatar_BuildCharacter_004a1110 / gameplay:4068:
    ///
    ///  • FLYING WINGS → a floating IDLE. Equipping one sets avatar flag <c>+0x86c</c> → render <c>+0x255</c>
    ///    (gameplay/023_gameplay_00482340.c:4068-4070); the idle motion then comes from rest-table category
    ///    <c>0x2c</c> (44) = FLYSTAY_NV / FLYSTAY_NAN instead of the normal idle (room cat 0 WREST0056/MREST0067,
    ///    arena cat 0x15 WREST0072/MREST0082). Pick sites: 023:4134-4138, state/022:2404, 028:2968. The wings are the
    ///    _CHIBANG meshes 008448/008449/008483/008484/020003.
    ///
    ///  • SPEED SHOES → a faster WALK. Equipping one sets flag <c>+0x86d</c> (ids 11106/11107) or <c>+0x86e</c>
    ///    (ids 11114/11115) → render <c>+0x256</c> / <c>+0x257</c>; Player_StepMovement_004abc20:2774-2780 then
    ///    integrates the walk at speed 5.0 instead of 3.0 — UNLESS a flying wing is also worn (flag 0x255 forces 3.0).
    ///    Same walk CLIP, faster. The shoes are the _SHOES meshes 011106/011107/011114/011115.
    ///
    /// fly_nan/fly_nv (cat 0x2b=43) and run_nan/run_nv (cat 0x30=48) exist in the motion table but the offline engine
    /// never invokes them, so the flying WALK stays the normal WWALK/MWALK clip (just at speed 3.0). Pure logic (no
    /// Unity dependency) so the id sets + mesh-path parsing are unit-tested.
    /// </summary>
    public static class SpecialMotionItems
    {
        // 飛行翅膀 model ids whose wing grants the floating (flystay) idle. woman 8448 / man 8449; man 8483 / woman 8484;
        // woman 20003. Verbatim from gameplay/023:4068 (0x2100/0x2101/0x2123/0x2124/0x4e23).
        private static readonly HashSet<int> FlyingWings = new HashSet<int> { 8448, 8449, 8483, 8484, 20003 };

        // 加速鞋 model ids that make the avatar walk at run speed (5.0). woman 11106 / man 11107 (flag +0x86d);
        // man 11114 / woman 11115 (flag +0x86e). Verbatim from gameplay/023:4072-4076 (0x2b62/0x2b63/0x2b6a/0x2b6b).
        private static readonly HashSet<int> FastWalkShoes = new HashSet<int> { 11106, 11107, 11114, 11115 };

        /// <summary>Flying-wing model ids (read-only view, for tests / diagnostics).</summary>
        public static IEnumerable<int> FlyingWingModelIds => FlyingWings;
        /// <summary>Speed-shoe model ids (read-only view, for tests / diagnostics).</summary>
        public static IEnumerable<int> FastWalkShoeModelIds => FastWalkShoes;

        // rest-table category 0x2c (44): the flying idle, one clip per gender.
        public const string FlyIdleFemale = "MOTION/FLYSTAY_NV.MOT";
        public const string FlyIdleMale = "MOTION/FLYSTAY_NAN.MOT";

        /// <summary>The flying-idle clip (rest cat 0x2c) for a gender — FLYSTAY_NAN (male) / FLYSTAY_NV (female).</summary>
        public static string FlyIdleMot(bool male) => male ? FlyIdleMale : FlyIdleFemale;

        /// <summary>True if <paramref name="modelId"/> is a flying wing (grants the flystay idle).</summary>
        public static bool IsFlyingWing(int modelId) => FlyingWings.Contains(modelId);

        /// <summary>True if <paramref name="modelId"/> is a speed shoe (grants the 5.0 walk).</summary>
        public static bool IsFastWalkShoe(int modelId) => FastWalkShoes.Contains(modelId);

        /// <summary>Parse the leading 6-digit MODEL id out of an AVATAR mesh path (e.g.
        /// <c>AVATAR/011106_WOMAN_SHOES.MSH</c> → 11106). Returns null when the filename doesn't start with digits.</summary>
        public static int? ModelIdFromMeshPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int slash = path.LastIndexOfAny(new[] { '/', '\\' });
            string file = slash >= 0 ? path.Substring(slash + 1) : path;
            int i = 0;
            while (i < file.Length && file[i] >= '0' && file[i] <= '9') i++;
            if (i == 0) return null;
            return int.TryParse(file.Substring(0, i), out var v) ? v : (int?)null;
        }

        /// <summary>True if any equipped mesh path is a flying wing (by the hardcoded id set only) → flystay idle.</summary>
        public static bool WearsFlyingWing(IEnumerable<string> equippedParts) => AnyMatch(equippedParts, IsFlyingWing);

        /// <summary>As <see cref="WearsFlyingWing(IEnumerable{string})"/> but ALSO treats any equipped 翅膀 that ships a
        /// <c>_CHIBANG_G</c> glide model (probed via <paramref name="meshExists"/>) as a flying wing. The offline exe only
        /// hardcodes 5 ids as a no-server fallback; the online server flags many more, and the client-side signal for
        /// "this wing can fly" is exactly the presence of its <c>_G</c> glide mesh — so the remake floats those too.</summary>
        public static bool WearsFlyingWing(IEnumerable<string> equippedParts, Func<string, bool> meshExists)
        {
            if (equippedParts == null) return false;
            foreach (var p in equippedParts)
            {
                var id = ModelIdFromMeshPath(p);
                if (id.HasValue && IsFlyingWing(id.Value)) return true;
                if (HasGlideVariant(p, meshExists)) return true;
            }
            return false;
        }

        /// <summary>True if <paramref name="chibangMeshPath"/> is a worn wing (<c>_CHIBANG.MSH</c>) whose <c>_CHIBANG_G</c>
        /// glide model exists on disk (<paramref name="meshExists"/>) — the client-side "flyable wing" marker.</summary>
        public static bool HasGlideVariant(string chibangMeshPath, Func<string, bool> meshExists)
        {
            if (string.IsNullOrEmpty(chibangMeshPath) || meshExists == null) return false;
            string up = chibangMeshPath.ToUpperInvariant();
            if (up.IndexOf("CHIBANG", StringComparison.Ordinal) < 0 || up.IndexOf("CHIBANG_G", StringComparison.Ordinal) >= 0)
                return false;   // only plain worn wings have a separate _G glide sibling
            int dot = chibangMeshPath.LastIndexOf('.');
            if (dot < 0) return false;
            string glide = chibangMeshPath.Substring(0, dot) + "_G" + chibangMeshPath.Substring(dot);   // …CHIBANG.MSH → …CHIBANG_G.MSH
            return meshExists(glide);
        }

        /// <summary>True if any equipped mesh path is a speed shoe → the avatar walks faster (subject to the wing gate).</summary>
        public static bool WearsFastWalkShoe(IEnumerable<string> equippedParts) => AnyMatch(equippedParts, IsFastWalkShoe);

        private static bool AnyMatch(IEnumerable<string> parts, Func<int, bool> pred)
        {
            if (parts == null) return false;
            foreach (var p in parts)
            {
                var id = ModelIdFromMeshPath(p);
                if (id.HasValue && pred(id.Value)) return true;
            }
            return false;
        }

        /// <summary>Walk speed multiplier per Player_StepMovement_004abc20:2774-2780: run speed (5.0) for a speed shoe,
        /// but a flying wing (or no speed shoe) forces the normal walk speed (3.0).</summary>
        public static float WalkSpeedMult(bool fastWalkShoe, bool flyingWing)
            => (fastWalkShoe && !flyingWing) ? RoomMovement.RunSpeed : RoomMovement.WalkSpeed;

        /// <summary>Resolve the AVATAR-relative idle clip for an equipped outfit: the flystay clip when a flying wing is
        /// worn, else the caller's normal idle (<paramref name="normalIdle"/>).</summary>
        public static string IdleMotFor(IEnumerable<string> equippedParts, bool male, string normalIdle)
            => WearsFlyingWing(equippedParts) ? FlyIdleMot(male) : normalIdle;

        /// <summary>As <see cref="IdleMotFor(IEnumerable{string},bool,string)"/> but also floats any wing with a
        /// <c>_CHIBANG_G</c> glide model on disk (<paramref name="meshExists"/>).</summary>
        public static string IdleMotFor(IEnumerable<string> equippedParts, bool male, string normalIdle, Func<string, bool> meshExists)
            => WearsFlyingWing(equippedParts, meshExists) ? FlyIdleMot(male) : normalIdle;
    }
}
