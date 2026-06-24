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
        public readonly int SpawnDelay;             // ms from scene load; 0 = immediate
        public SceneEftPlacement(string eft, float x, float y, float z, float ex, float ey, float ez, float scale, int spawnDelay = 0)
        { Eft = eft; X = x; Y = y; Z = z; Ex = ex; Ey = ey; Ez = ez; Scale = scale; SpawnDelay = spawnDelay; }
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
                ["SCN0003"] = MainStageLights(),   // 主舞台 (BOX disco floor): 6 靜態 stage_3_light + 24 擺動聚光燈
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

        // SCN0003 主舞台 (BOX disco floor → StageMainScene class, scene-factory case 3). The lights are NOT in a
        // StageSceneNN_ctor — they belong to StageMainScene_ctor_004b2120 (6× stage_3_light, scale 2, table
        // DAT_005882c8) plus the per-frame StageScene_UpdateOscPlanes_004b2310 (24× light_left/light_right, scale 15,
        // table DAT_00588310, spawned in 4 waves and swept ±10° on Z). Because the remake's catalog was built from the
        // StageSceneNN ctors only, SCN0003 had zero effects and the whole stage was dark. We place all 30 statically
        // here (positions/scales verbatim from the exe); the ±10° Z sway is driven by Step1Game.OscLightZCo.
        // light_right = id 7 (the 3 beams on each band's stage-right half), light_left = id 6 (stage-left half).
        //
        // BEAM ORIENTATION: EFT emitter slot2 (invisible carrier) has InitRot already baked:
        //   light_right slot2 InitRot (15°,0°,190°) — 190° Z-flip points cone DOWN + 10° leftward + 15° forward tilt.
        //   light_left  slot2 InitRot (15°,0°,170°) — 170° Z-flip points cone DOWN + 10° rightward + 15° forward tilt.
        // The beam (slot0, attach=1) rides the carrier: in EftEffect StepParticle the carrier's p.rot is used as prot
        // and applied to the beam's localRotation directly. Placement euler must therefore be (0,0,0) — any non-zero
        // GO rotation stacks on top of carrier's InitRot and double-applies the tilt, flipping the beams back UP.
        // The official sweeps GO Z rotation ±10° (vel=0.5°/50ms, FUN_004b2310); Step1Game.OscLightZCo replicates that.
        private static SceneEftPlacement[] MainStageLights() => new[]
        {
            // 6 static stage_3_light (Effect_Play(4), scale 2.0) bracketing the dance spot at floor level
            new SceneEftPlacement("stage_3_light", -187.469f, 23.061f, 101.282f, 0, 0, 0, 2f),
            new SceneEftPlacement("stage_3_light", -135.930f, 23.061f, 150.378f, 0, 0, 0, 2f),
            new SceneEftPlacement("stage_3_light",  -83.290f, 23.061f, 202.200f, 0, 0, 0, 2f),
            new SceneEftPlacement("stage_3_light",  105.933f, 23.061f, 195.359f, 0, 0, 0, 2f),
            new SceneEftPlacement("stage_3_light",  157.303f, 23.061f, 143.204f, 0, 0, 0, 2f),
            new SceneEftPlacement("stage_3_light",  213.250f, 23.061f,  92.999f, 0, 0, 0, 2f),

            // 18 sweeping spotlights (Effect_Play(7/6), scale 15.0). Euler (0,0,0): carrier InitRot provides the tilt.
            // Original spawns 3 waves via FUN_004b2310 (2000ms apart), so bands start their 15s animation cycle
            // at t=0/2000/4000ms → staggered phase → different brightness/color at any given moment.
            // Band 1 (z≈342, spawns at t=0):
            new SceneEftPlacement("light_right", -217.764f, 223.500f, 341.680f, 0, 0, 0, 15f),
            new SceneEftPlacement("light_right", -144.458f, 221.015f, 341.680f, 0, 0, 0, 15f),
            new SceneEftPlacement("light_right",  -48.970f, 216.678f, 341.680f, 0, 0, 0, 15f),
            new SceneEftPlacement("light_left",    51.122f, 216.678f, 341.680f, 0, 0, 0, 15f),
            new SceneEftPlacement("light_left",   146.611f, 221.015f, 341.680f, 0, 0, 0, 15f),
            new SceneEftPlacement("light_left",   219.917f, 223.500f, 341.680f, 0, 0, 0, 15f),
            // Band 2 (z≈335, spawns at t=2000ms):
            new SceneEftPlacement("light_right", -187.481f, 170.494f, 335.229f, 0, 0, 0, 15f, 2000),
            new SceneEftPlacement("light_right", -118.646f, 171.518f, 335.229f, 0, 0, 0, 15f, 2000),
            new SceneEftPlacement("light_right",  -41.664f, 162.398f, 335.229f, 0, 0, 0, 15f, 2000),
            new SceneEftPlacement("light_left",    43.817f, 162.398f, 335.229f, 0, 0, 0, 15f, 2000),
            new SceneEftPlacement("light_left",   120.798f, 171.518f, 335.229f, 0, 0, 0, 15f, 2000),
            new SceneEftPlacement("light_left",   189.634f, 170.494f, 335.229f, 0, 0, 0, 15f, 2000),
            // Band 3 (z≈329, spawns at t=4000ms):
            new SceneEftPlacement("light_right", -158.608f, 127.588f, 329.097f, 0, 0, 0, 15f, 4000),
            new SceneEftPlacement("light_right",  -91.636f, 123.479f, 329.097f, 0, 0, 0, 15f, 4000),
            new SceneEftPlacement("light_right",  -30.072f, 112.305f, 329.097f, 0, 0, 0, 15f, 4000),
            new SceneEftPlacement("light_left",    32.225f, 112.305f, 329.097f, 0, 0, 0, 15f, 4000),
            new SceneEftPlacement("light_left",    93.789f, 123.479f, 329.097f, 0, 0, 0, 15f, 4000),
            new SceneEftPlacement("light_left",   160.761f, 127.588f, 329.097f, 0, 0, 0, 15f, 4000),
        };

        // 個人房 / 婚禮大廳: star light + two pillar glows (StageScene10 ctor)
        private static SceneEftPlacement[] PersonalRoom() => new[]
        {
            new SceneEftPlacement("star_light1", 126.69f, 36.56f, -60.95f, 0, 0, 0, 25f),
            new SceneEftPlacement("stage28_dengzhu", 293.92f, 219.36f, 3488.68f, 0, 0, 90, 90f),
            new SceneEftPlacement("stage_3_light", 714.1f, 219.36f, 3188.51f, 0, 0, 90, 90f),
        };
    }
}
