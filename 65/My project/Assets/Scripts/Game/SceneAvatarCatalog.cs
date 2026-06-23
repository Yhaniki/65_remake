using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>One scene NPC ("場景的人"): a full skinned avatar (MSH skin + HRC skeleton + optional MOT) placed in
    /// the stage. Mesh+skeleton live in AVATAR/, motion in MOTION/. Mot == null → posed at the HRC bind once, frozen.</summary>
    public struct SceneAvatarPlacement
    {
        public readonly string Msh, Hrc, Mot;
        public readonly Vector3 Pos;        // native SDO world coords
        public readonly Vector3 EulerDeg;   // facing (Y), already converted from the native radians
        public SceneAvatarPlacement(string msh, string hrc, string mot, Vector3 pos, Vector3 eulerDeg)
        { Msh = msh; Hrc = hrc; Mot = mot; Pos = pos; EulerDeg = eulerDeg; }
    }

    /// <summary>
    /// Per-scene background NPCs ("場景的人"), decompiled from <c>StageScene_LoadAvatarsAndMotions_004b3ef0(dst, count,
    /// typeTable, posTable, eulerTable)</c> (029_scene_004ad250.c:3862). Each scene that calls it lists, per NPC, a
    /// model TYPE byte + a world position + a Y-euler (radians). The five "changjing" (場景) models are a shared
    /// skeleton with five skins (msh list @exe 0x588214, hrc list @0x588200):
    ///   0 nan1, 1 nan2 → mdance0001 (male);  2 nv1, 3 nv2 → wdance0001 (female);  4 dj → mdance0001 + a dance .mot.
    /// Types 0–3 carry NO motion in the original (posed standing); only the DJ animates. Keyed by scene FOLDER, like
    /// <see cref="SceneMapobjCatalog"/>. (The mapobj catalog intentionally omitted these data-table avatars before.)
    /// </summary>
    public static class SceneAvatarCatalog
    {
        private static readonly SceneAvatarPlacement[] Empty = new SceneAvatarPlacement[0];

        // type 0..4 -> (skin msh, skeleton hrc, idle/dance motion). All under AVATAR/ (mot under MOTION/).
        // Motions per type from the exe: males (0/1) sway with mdance0002, females (2/3) with wdance0001, the DJ (4)
        // dances mdance0001 — applied every ~1s by StageScene_LoadMotionsCycle_004ae160 (idle table @0x588228) + the
        // DJ's own LoadAvatarsAndMotions entry. So ALL changjing NPCs animate (not just the DJ).
        private static readonly (string msh, string hrc, string mot)[] Changjing =
        {
            ("CHANGJINGNAN1.MSH", "MDANCE0001_CHANGJING.HRC", "MDANCE0002_CHANGJING.MOT"),
            ("CHANGJINGNAN2.MSH", "MDANCE0001_CHANGJING.HRC", "MDANCE0002_CHANGJING.MOT"),
            ("CHANGJINGNV1.MSH",  "WDANCE0001_CHANGJING.HRC", "WDANCE0001_CHANGJING.MOT"),
            ("CHANGJINGNV2.MSH",  "WDANCE0001_CHANGJING.HRC", "WDANCE0001_CHANGJING.MOT"),
            ("CHANGJINGDJ.MSH",   "MDANCE0001_CHANGJING.HRC", "MDANCE0001_CHANGJING.MOT"),
        };

        // one changjing NPC: type + native pos + native Y-euler (RADIANS → degrees here)
        private static SceneAvatarPlacement Cj(int type, float x, float y, float z, float eulerYRad)
        {
            var m = Changjing[type];
            return new SceneAvatarPlacement(m.msh, m.hrc, m.mot, new Vector3(x, y, z),
                new Vector3(0f, eulerYRad * Mathf.Rad2Deg, 0f));
        }

        private static readonly Dictionary<string, SceneAvatarPlacement[]> ByFolder =
            new Dictionary<string, SceneAvatarPlacement[]>
            {
                // SCN0017 地鐵: 10 platform passengers (StageScene_LoadAvatarsAndMotions(.,10,DAT_005518cc,0x588b60,0x588bd8)).
                // type[10]={0,2,1,3,0,1,2,1,3,4}; pos/euler from the exe tables (euler in radians, Y only).
                ["SCN0017"] = new[]
                {
                    Cj(0, -110.50f, -2f, 176f, -0.35f),
                    Cj(2, -140.00f, -2f,  30f, -1.40f),
                    Cj(1, -123.00f, -2f, 140f, -0.52f),
                    Cj(3,  -81.00f, -2f, 160f, -0.44f),
                    Cj(0,   71.00f, -2f, 178f,  0.44f),
                    Cj(1,  100.00f, -2f, 181f,  0.52f),
                    Cj(2,  132.00f, -2f, 132f,  0.52f),
                    Cj(1,  141.00f, -2f,  95f,  0.87f),
                    Cj(3,  150.00f, -2f,  45f,  0.61f),
                    Cj(4, -164.80f, -2f, 107f, -0.99f),   // the DJ (animated)
                },
            };

        /// <summary>Background NPCs for a scene folder (e.g. "SCN0017"); empty if none.</summary>
        public static IReadOnlyList<SceneAvatarPlacement> ForFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return Empty;
            return ByFolder.TryGetValue(folder.ToUpperInvariant(), out var a) ? a : Empty;
        }
    }
}
