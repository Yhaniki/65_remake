using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>One persistent background EFT a scene spawns on load (native SDO world coords, Euler°, scale).</summary>
    public struct SceneEftPlacement
    {
        public readonly string Eft;                 // 3DEFT/<Eft>.EFT (no extension; case-insensitive on disk)
        public readonly float X, Y, Z;              // position (native SDO coords)
        public readonly float Ex, Ey, Ez;           // Euler rotation (degrees)
        public readonly float Scale;                // uniform scale (decompiled Effect_SetTransformAnimated)
        public SceneEftPlacement(string eft, float x, float y, float z, float ex, float ey, float ez, float scale)
        { Eft = eft; X = x; Y = y; Z = z; Ex = ex; Ey = ey; Ez = ez; Scale = scale; }
    }

    /// <summary>
    /// Per-scene background particle effects (EFT), decompiled from the StageScene controllers' constructors
    /// (029_scene_004ad250.c StageSceneNN_ctor → Effect_Play(id) + Effect_SetTransformAnimated). The original plays
    /// these once when the stage loads and they run the whole song (snow, aurora, the SCN0008 magic circle "結界",
    /// carnival lights, sea bubbles…). Effect ids resolve to names via the exe's effect-name table (id 31 =
    /// kikkai_3.eft, etc.). Keyed by scene FOLDER (SceneMapobjCatalog's key). Spawned by Step1Game.SpawnSceneEffects
    /// as persistent placed EftEffects. Bone-attached effects (SCN0015 booklight) and data-table-positioned ones
    /// (SCN0028 niaochao, wedding rooms) are intentionally omitted for now.
    /// </summary>
    public static class SceneEftCatalog
    {
        private static readonly IReadOnlyList<SceneEftPlacement> Empty = new SceneEftPlacement[0];
        private static readonly Dictionary<string, SceneEftPlacement[]> ByFolder = Build();

        /// <summary>Background EFTs for a scene folder (e.g. "SCN0008"); empty if none.</summary>
        public static IReadOnlyList<SceneEftPlacement> ForFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return Empty;
            return ByFolder.TryGetValue(folder.ToUpperInvariant(), out var a) ? a : Empty;
        }

        private static Dictionary<string, SceneEftPlacement[]> Build()
        {
            return new Dictionary<string, SceneEftPlacement[]>
            {
                ["SCN0005"] = new[]   // 聖誕夜: snow
                {
                    new SceneEftPlacement("snow", 0, 0, 0, 0, 0, 0, 30f),
                },
                ["SCN0007"] = new[]   // 極地花園: aurora + petals
                {
                    new SceneEftPlacement("aurora_s4", -380, 200, 380, 0, 90, 30, 500f),
                    new SceneEftPlacement("hanabira", 0, 50, 0, 0, 0, 0, 10f),
                },
                ["SCN0008"] = new[]   // 埃及古墓: the ground magic circle (結界)
                {
                    new SceneEftPlacement("kikkai_3", 0, 0, 0, 0, 180, 0, 40f),
                },
                ["SCN0010"] = new[]   // 花車: carnival glow
                {
                    new SceneEftPlacement("kuanghuan", 0, 50, 0, 0, 0, 0, 10f),
                    new SceneEftPlacement("kuanghuan1", 0, 50, 0, 0, 0, 0, 10f),
                    new SceneEftPlacement("kuanghuan2", 0, 50, 0, 0, 0, 0, 10f),
                    new SceneEftPlacement("huacheguang", -174, 160, 191, 0, 0, 0, 44f),
                },
                ["SCN0011"] = new[]   // 舞林大會: stage lights
                {
                    new SceneEftPlacement("bgl", 90, 0, 350, 0, 0, -80, 30f),
                    new SceneEftPlacement("bgl", -82, 0, 350, 0, 0, 60, 30f),
                    new SceneEftPlacement("gravcolor_r", -250, 0, 167, 0, 0, 0, 80f),
                    new SceneEftPlacement("gravcolor_b", 250, 0, 167, 0, 0, 0, 80f),
                    new SceneEftPlacement("stagelightb", 112, 197, 63, 0, 0, 0, 15f),
                    new SceneEftPlacement("stagelightb", 0, 197, 180, 0, 0, 0, 15f),
                    new SceneEftPlacement("stagelightb", -120, 197, 65, 0, 0, 0, 15f),
                    new SceneEftPlacement("stagelightb", 0, 197, -58, 0, 0, 0, 15f),
                },
                ["SCN0014"] = new[]   // 海底: aurora curtain + bubbles
                {
                    new SceneEftPlacement("aurora s5", 90, 300, 150, 0, 0, 0, 800f),
                    new SceneEftPlacement("bubble", 171, -58, 205, 0, 0, 0, 40f),
                    new SceneEftPlacement("bubble", -171, -124, 498, 0, 0, 0, 40f),
                    new SceneEftPlacement("bubble", -555, -90, 200, 0, 0, 0, 40f),
                    new SceneEftPlacement("bubble", 54, -41, 1548, 0, 0, 0, 40f),
                },
                ["SCN0015"] = new[]   // 魔法屋: hearth fire
                {
                    new SceneEftPlacement("fire3", 55.15f, 339.83f, 1237.66f, 0, 0, 0, 100f),
                },
                ["SCN0024"] = new[]   // 雪景: snow
                {
                    new SceneEftPlacement("snow", 0, 0, 0, 0, 0, 0, 30f),
                },
                ["SCN0029"] = new[]   // 激舞酒吧: carnival glow
                {
                    new SceneEftPlacement("kuanghuan1", 0, 50, 0, 0, 0, 0, 10f),
                    new SceneEftPlacement("kuanghuan2", 0, 50, 0, 0, 0, 0, 10f),
                },
                ["SCN0037"] = PersonalRoom(),
                ["SCN0038"] = PersonalRoom(),
            };
        }

        // 個人房 / 婚禮大廳: star light + two pillar glows (StageScene10 ctor)
        private static SceneEftPlacement[] PersonalRoom() => new[]
        {
            new SceneEftPlacement("star_light1", 126.69f, 36.56f, -60.95f, 0, 0, 0, 25f),
            new SceneEftPlacement("stage28_dengzhu", 293.92f, 219.36f, 3488.68f, 0, 0, 90, 90f),
            new SceneEftPlacement("stage_3_light", 714.1f, 219.36f, 3188.51f, 0, 0, 90, 90f),
        };
    }
}
