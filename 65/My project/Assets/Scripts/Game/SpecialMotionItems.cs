using System;
using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>
    /// The avatar items that carry their OWN idle / walk behaviour, reverse-engineered from the offline client
    /// (sdo_stand_alone.exe) and verified by a multi-agent decompile dig. Everything keys on the equipped item's 6-digit
    /// MODEL id (the mesh filename prefix — the value the engine matches at Avatar_BuildCharacter / gameplay:4068).
    ///
    /// FLYING WINGS → a floating IDLE (flystay) + a forward-lean GLIDE while moving + a +10 hover.
    ///   Which wings fly is NOT derivable from any item property — the offline exe hardcodes exactly 5 ids and otherwise
    ///   trusts a server packet (state/022:3157). There is NO client data rule (verified: +0x86c is written only by the
    ///   literal id compare at gameplay/023:4068-4070 or the network byte). So the flying set is a CURATED list:
    ///   the 5 decompiled ids + online-confirmed additions we add by hand. (My earlier "wing has a _CHIBANG_G glide mesh"
    ///   guess was WRONG — 891/1139 wings ship a _G mesh, incl. ones that do NOT fly, e.g. 036937.)
    ///   • Idle: rest cat 0x2c (44) = FLYSTAY_NV/FLYSTAY_NAN instead of the normal idle (023:4138 / 022:2404).
    ///   • Move: the OFFLINE exe keeps the normal walk clip (cat 6) — but the OFFICIAL/online client (what the user sees)
    ///     plays the forward-lean glide cat 0x2b (43) = FLY_NV/FLY_NAN. We use the glide (the clip ships in the data) so
    ///     it matches the game the user knows; speed is forced slow (3.0) and world-Y hovers +10 while moving (028:2852).
    ///
    /// SPEED SHOES → a faster WALK (5.0 vs 3.0), same clip. Ids {11106,11107}(flag +0x86d) / {11114,11115}(flag +0x86e);
    ///   a flying wing forces 3.0 (028:2774). Pure logic (no Unity dependency) → unit-tested.
    ///
    /// NOTE the offline exe never animates the wing itself (the _chibang_g rig is sprintf'd but never consumed) — plain
    /// wings are static skin on the BODY skeleton. So "each wing's own flap" is an online-only feature, not modelled here.
    /// </summary>
    public static class SpecialMotionItems
    {
        // 反編譯確定會飛的 5 個翅膀(離線 exe 硬編:gameplay/023:4068 = 0x2100/0x2101/0x2123/0x2124/0x4e23)。
        private static readonly int[] DecompiledFlyingWings = { 8448, 8449, 8483, 8484, 20003 };

        // 官方線上額外會飛、但離線資料推不出來的翅膀(伺服器決定;使用者實測補上)。要讓更多翅膀會飛就把 modelId 加在這裡。
        private static readonly int[] OnlineFlyingWings =
        {
            26394,   // Fly 甜心飛翼(使用者實測會飛)
        };

        private static readonly HashSet<int> FlyingWings = BuildSet(DecompiledFlyingWings, OnlineFlyingWings);

        // 加速鞋 model ids that make the avatar walk at run speed (5.0). woman 11106 / man 11107 (flag +0x86d);
        // man 11114 / woman 11115 (flag +0x86e). Verbatim from gameplay/023:4072-4076 (0x2b62/0x2b63/0x2b6a/0x2b6b).
        private static readonly HashSet<int> FastWalkShoes = new HashSet<int> { 11106, 11107, 11114, 11115 };

        private static HashSet<int> BuildSet(params int[][] groups)
        {
            var s = new HashSet<int>();
            foreach (var g in groups) foreach (var id in g) s.Add(id);
            return s;
        }

        /// <summary>Flying-wing model ids (read-only view, for tests / diagnostics).</summary>
        public static IEnumerable<int> FlyingWingModelIds => FlyingWings;
        /// <summary>Speed-shoe model ids (read-only view, for tests / diagnostics).</summary>
        public static IEnumerable<int> FastWalkShoeModelIds => FastWalkShoes;

        // rest cat 0x2c (44): flying IDLE (flystay). rest cat 0x2b (43): flying MOVE (fly = 前傾滑動 forward-lean glide).
        public const string FlyIdleFemale = "MOTION/FLYSTAY_NV.MOT";
        public const string FlyIdleMale = "MOTION/FLYSTAY_NAN.MOT";
        public const string FlyWalkFemale = "MOTION/FLY_NV.MOT";
        public const string FlyWalkMale = "MOTION/FLY_NAN.MOT";

        /// <summary>World-Y hover added to a flying avatar while moving (Player_StepMovement 028:2852: <c>y += 10</c>).</summary>
        public const float FlyHoverY = 10f;

        /// <summary>The flying-IDLE clip (rest cat 0x2c) for a gender — FLYSTAY_NAN (male) / FLYSTAY_NV (female).</summary>
        public static string FlyIdleMot(bool male) => male ? FlyIdleMale : FlyIdleFemale;

        /// <summary>The flying-MOVE glide clip (rest cat 0x2b) for a gender — FLY_NAN (male) / FLY_NV (female).</summary>
        public static string FlyWalkMot(bool male) => male ? FlyWalkMale : FlyWalkFemale;

        /// <summary>True if <paramref name="modelId"/> is a flying wing (grants the flystay idle + glide walk).</summary>
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

        /// <summary>True if any equipped mesh path is a flying wing → the avatar should float (flystay idle + glide walk).</summary>
        public static bool WearsFlyingWing(IEnumerable<string> equippedParts) => AnyMatch(equippedParts, IsFlyingWing);

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

        /// <summary>Resolve the AVATAR-relative IDLE clip for an equipped outfit: the flystay clip when a flying wing is
        /// worn, else the caller's normal idle (<paramref name="normalIdle"/>).</summary>
        public static string IdleMotFor(IEnumerable<string> equippedParts, bool male, string normalIdle)
            => WearsFlyingWing(equippedParts) ? FlyIdleMot(male) : normalIdle;

        /// <summary>Resolve the AVATAR-relative WALK clip for an equipped outfit: the forward-lean glide when a flying
        /// wing is worn, else the caller's normal walk (<paramref name="normalWalk"/>).</summary>
        public static string WalkMotFor(IEnumerable<string> equippedParts, bool male, string normalWalk)
            => WearsFlyingWing(equippedParts) ? FlyWalkMot(male) : normalWalk;
    }
}
