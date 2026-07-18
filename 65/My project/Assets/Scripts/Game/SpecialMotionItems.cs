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
        // 「Fly …」系列本來就是官方的飛行翅膀 — modelId 取自 iteminfo.dat(0x04 欄),商城名對照見注解。男女各一 modelId
        // (穿戴時 mesh 路徑帶各自 modelId → WearsFlyingWing 解出),男會拿 FLY_NAN/FLYSTAY_NAN、女拿 FLY_NV/FLYSTAY_NV。
        private static readonly int[] OnlineFlyingWings =
        {
            26394,   // Fly 甜心飛翼(合成 mesh-only 版 026394_WOMAN_CHIBANG,使用者實測會飛)
            23691,   // Fly 甜心飛翼(女)— iteminfo id 33691/83691/93691
            23692,   // Fly 甜心飛翼(男)— iteminfo id 33692/83692/93692
            23920,   // Fly 花雨飛翼(男)— iteminfo id 123920/223920/323920
            23921,   // Fly 花雨飛翼(女)— iteminfo id 123921/223921/323921
        };

        private static readonly HashSet<int> FlyingWings = BuildSet(DecompiledFlyingWings, OnlineFlyingWings);

        // 加速鞋 model ids that make the avatar walk at run speed (5.0). woman 11106 / man 11107 (flag +0x86d);
        // man 11114 / woman 11115 (flag +0x86e). Verbatim from gameplay/023:4072-4076 (0x2b62/0x2b63/0x2b6a/0x2b6b).
        private static readonly HashSet<int> FastWalkShoes = new HashSet<int> { 11106, 11107, 11114, 11115 };

        // 炫 (dazzle) hair "不斷變色": the standalone client hardcodes the MODEL-id band [40000,49999] as "animated"
        // items — three identical range checks in avatar/015_avatar_0042fe80.c (`if ((id < 40000) || (49999 < id))`)
        // set an anim flag that ordinary items never get, enabling texture-stage animation on the hair mesh.
        // GROUND TRUTH (Frida on the live online client sdo.bin, hooking FUN_0042cda0 = Mesh_applyRenderStates,
        // assets/閉撰敃氪/hook_xuan_hair.js → H:/65_remake/xuan_hair_log.txt): the ONLY flag set is 0x10000 (the
        // texture-coordinate transform), U stays 0, and V scrolls at a constant 1.999 ≈ 2.0 texture-units/sec (time-
        // based; measured identical across frame-rates). colA/B/C (the D3DRS_TEXTUREFACTOR sources) never change — so
        // it is NOT a colour animation. It is a UV V-scroll (the same updater as the SCN0011 running lights,
        // hook_uv_speed.js, +0x58/+0x5c). The 炫红/炫紫/炫白/炫黄 textures (model 40000-40003, cat 1) are a vertical
        // gradient of the item's own colour with a bright highlight band; scrolling V sweeps that sheen through the
        // hair → the "dazzle". Reproduced by scrolling the REAL texture's V (AvatarUvScroll) — no fabricated colours.
        public const int UvScrollModelIdMin = 40000;
        public const int UvScrollModelIdMax = 49999;
        /// <summary>Texture V units scrolled per second — measured 1.999 on the live client (2.0 exactly).</summary>
        public const float UvScrollUnitsPerSec = 2.0f;

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

        /// <summary>True if <paramref name="modelId"/> is a 炫 UV-scroll hair (model band [40000,49999] → its texture V
        /// scrolls, sweeping a bright band through the hair). The band is exactly 炫红/炫紫/炫白/炫黄 (40000-40003).</summary>
        public static bool IsUvScrollHair(int modelId) => modelId >= UvScrollModelIdMin && modelId <= UvScrollModelIdMax;

        /// <summary>The V texture-coordinate offset at <paramref name="elapsedSec"/> — the raw accumulator (V = rate·t)
        /// wrapped into [0,1) so the value never loses float precision over a long session (the texture sampler REPEATs,
        /// so wrapping is visually identical to the client's unbounded V=6.46→11.65…). Pure (no Unity/time) → the scroll
        /// is deterministic and unit-testable; <see cref="AvatarUvScroll"/> feeds it into the material's V offset.</summary>
        public static float ScrollOffsetV(double elapsedSec, float unitsPerSec)
        {
            double v = elapsedSec * unitsPerSec;
            v -= Math.Floor(v);           // wrap into [0,1) (Repeat sampler); also normalises negative elapsed
            return (float)v;
        }

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

        /// <summary>True if any equipped mesh path is a 炫 UV-scroll hair → its material should be driven by
        /// <see cref="AvatarUvScroll"/> (continuous V scroll).</summary>
        public static bool WearsUvScrollHair(IEnumerable<string> equippedParts) => AnyMatch(equippedParts, IsUvScrollHair);

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
