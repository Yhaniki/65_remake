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
    /// kikkai_3.eft, etc.). Keyed by scene FOLDER (SceneMapobjCatalog's key). Spawned by ScreenGameplay.SpawnSceneEffects
    /// as persistent placed EftEffects. Bone-attached effects (SCN0015 booklight) live in
    /// <see cref="SceneAttachedEftCatalog"/>; data-table-positioned ones (SCN0028 niaochao, wedding rooms) are
    /// intentionally omitted for now.
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
                ["SCN0005"] = new[]   // 聖誕夜: snow + 天上輪流亮起的三支極光柱
                {
                    new SceneEftPlacement("snow", 0, 0, 0, 0, 0, 0, 30f),
                    // grav_1/2/3 (Effect_Play 0x10/0x11/0x12) scale 8.0 (0x41000000)。官方是每 2 秒接力播下一支、
                    // 而且無限循環;remake 這裡用既有的 SpawnDelay 把三支錯開 2 秒,再靠 EftEffect.Persistent
                    // 各自照 EFT 自身壽命循環 —— 相位差保留住「輪流亮」的節奏,但重播週期由 EFT 決定而非固定 6 秒。
                    new SceneEftPlacement("grav_1", -255, 230, 780, 0, 0, 0, 8f, 2000),
                    new SceneEftPlacement("grav_2", -140, 190, 829, 0, 0, 0, 8f, 4000),
                    new SceneEftPlacement("grav_3",  200, 237, 850, 0, 0, 0, 8f, 6000),
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
                ["SCN0015"] = new[]   // 魔法屋: hearth fire; window booklights attach to SHU1-4 bones
                {
                    // fire3 = Effect_Play(0x35) at decompiled pos (55.15, 339.83, 1237.664) scale=100
                    new SceneEftPlacement("fire3", 55.15f, 339.83f, 1237.66f, 0, 0, 0, 100f),
                },
                ["SCN0024"] = new[]   // 雪景: snow
                {
                    new SceneEftPlacement("snow", 0, 0, 0, 0, 0, 0, 30f),
                },
                ["SCN0028"] = Niaochao(),   // 北京之夜 (鸟巢): 遠景四道光柱 + 六團城市光暈
                ["SCN0029"] = Jiku(),   // 飛機場: 兩排共 10 支街燈光暈 + carnival glow
                ["SCN0037"] = PersonalRoom(),
                ["SCN0038"] = PersonalRoom(),
            };
        }

        // SCN0003 主舞台 (BOX disco floor → StageMainScene class, scene-factory case 3). The lights are NOT in a
        // StageSceneNN_ctor — they belong to StageMainScene_ctor_004b2120 (6× stage_3_light, scale 2, table
        // DAT_005882c8) plus the per-frame StageScene_UpdateOscPlanes_004b2310 (24× light_left/light_right, scale 15,
        // table DAT_00588310, spawned in 4 waves and swept ±10° on Z). Because the remake's catalog was built from the
        // StageSceneNN ctors only, SCN0003 had zero effects and the whole stage was dark. We place all 30 statically
        // here (positions/scales verbatim from the exe); the ±10° Z sway is driven by ScreenGameplay.OscLightZCo.
        // light_right = id 7 (the 3 beams on each band's stage-right half), light_left = id 6 (stage-left half).
        //
        // BEAM ORIENTATION: EFT emitter slot2 (invisible carrier) has InitRot already baked:
        //   light_right slot2 InitRot (15°,0°,190°) — 190° Z-flip points cone DOWN + 10° leftward + 15° forward tilt.
        //   light_left  slot2 InitRot (15°,0°,170°) — 170° Z-flip points cone DOWN + 10° rightward + 15° forward tilt.
        // The beam (slot0, attach=1) rides the carrier: in EftEffect StepParticle the carrier's p.rot is used as prot
        // and applied to the beam's localRotation directly. Placement euler must therefore be (0,0,0) — any non-zero
        // GO rotation stacks on top of carrier's InitRot and double-applies the tilt, flipping the beams back UP.
        // The official sweeps GO Z rotation ±10° (vel=0.5°/50ms, FUN_004b2310); ScreenGameplay.OscLightZCo replicates that.
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

        // SCN0028 北京之夜 (鸟巢)。特效 id 由 exe 的名稱表解出:0x5d = stage28_dengzhu.eft(名字就寫著
        // stage28)、0x5e = stage28_guangyun.eft。兩者都在 Scene_LoadBackground case 0x1c 起手播,
        // 座標/旋轉/縮放由 StageScene_UpdateBigBillboardSet_004b0fc0 每幀寫回(旋轉恆為 (0,0,90))。
        //
        //   光柱 dengzhu(scale 90):載入時先播 1 支並把計數器設成 1,之後更新函式在
        //   (t−載入時刻) > 2000 / 4000 / 6000 ms 時各再播一支,計數器 1→2→3→4;第 N 支的座標只在
        //   「計數器 > N−1」時才寫入 —— 所以是四支光柱每兩秒亮起一支,由遠而近排在長安街方向。
        //   光暈 guangyun(scale 1500):載入時一口氣播 6 支,座標每幀無條件寫入,是城市天際線上的
        //   六團大光暈(z 2100~3400,場景本體 z 一路到 6389,所以這幾團落在遠景建築上)。
        // 少了這一整組,北京之夜的遠景就是一片死黑,只剩貼圖 —— 使用者說的「沒有光線」的另一半。
        private static SceneEftPlacement[] Niaochao() => new[]
        {
            // 4 × 光柱,每 2 秒亮一支 (Effect_Play(0x5d) ×4)
            new SceneEftPlacement("stage28_dengzhu",  293.920f, 219.362f, 3488.677f, 0, 0, 90, 90f),
            new SceneEftPlacement("stage28_dengzhu",  714.099f, 219.362f, 3188.506f, 0, 0, 90, 90f, 2000),
            new SceneEftPlacement("stage28_dengzhu", 1135.853f, 219.362f, 2957.402f, 0, 0, 90, 90f, 4000),
            new SceneEftPlacement("stage28_dengzhu", 1509.139f, 219.362f, 3284.658f, 0, 0, 90, 90f, 6000),
            // 6 × 城市光暈 (Effect_Play(0x5e) ×6,載入時全部一起播)
            new SceneEftPlacement("stage28_guangyun",    7.095f, 329.066f, 3330.379f, 0, 0, 90, 1500f),
            new SceneEftPlacement("stage28_guangyun",  110.831f, 425.370f, 2948.400f, 0, 0, 90, 1500f),
            new SceneEftPlacement("stage28_guangyun",  373.760f, 425.370f, 2576.073f, 0, 0, 90, 1500f),
            new SceneEftPlacement("stage28_guangyun",  827.307f, 516.805f, 2233.711f, 0, 0, 90, 1500f),
            new SceneEftPlacement("stage28_guangyun", 1293.569f, 516.805f, 2161.466f, 0, 0, 90, 1500f),
            new SceneEftPlacement("stage28_guangyun", 1829.695f, 516.805f, 2380.157f, 0, 0, 90, 1500f),
        };

        // SCN0029 飛機場 (jiku)。和 SCN0003 主舞台同一種漏法:remake 當初只從 StageSceneNN_ctor 抽特效,
        // 所以只收到 StageScene09_ctor_004b3ae0 的 kuanghuan1/2 (Effect_Play 0x2c/0x2d),而 case body 裡
        // 那 10 支街燈光暈一支都沒收 —— 這就是使用者說的「路燈沒有光線」。
        // 官方 Scene_LoadBackground case 0x1d 先 operator_new(0x2fc) 配 10 個 0x4c bytes 的 effect slot,
        // 再 `do { Effect_Play(0x65,1,0,0,0,0); i += 0x4c; } while (i < 0x2f8)` = 10 支;0x65 = 101 =
        // dengguang.eft。座標由每幀更新 StageScene_UpdateFlashCycle_004b1890 從 VA 0x588d30..0x588da8
        // (10×vec3) 無條件寫回,旋轉恆 (0,0,0)、縮放 0x42c80000 = 100,沒有 SCN0028 光柱那種分批延遲。
        // 十支分成兩排(x −405..−205 一排五支、x 473..628 一排五支),全部 y = 251,夾著舞池。
        private static SceneEftPlacement[] Jiku() => new[]
        {
            new SceneEftPlacement("dengguang", -405f, 251f, 446f, 0, 0, 0, 100f),
            new SceneEftPlacement("dengguang", -356f, 251f, 485f, 0, 0, 0, 100f),
            new SceneEftPlacement("dengguang", -306f, 251f, 523f, 0, 0, 0, 100f),
            new SceneEftPlacement("dengguang", -254f, 251f, 562f, 0, 0, 0, 100f),
            new SceneEftPlacement("dengguang", -205f, 251f, 600f, 0, 0, 0, 100f),
            new SceneEftPlacement("dengguang",  473f, 251f, 219f, 0, 0, 0, 100f),
            new SceneEftPlacement("dengguang",  510f, 251f, 167f, 0, 0, 0, 100f),
            new SceneEftPlacement("dengguang",  547f, 251f, 116f, 0, 0, 0, 100f),
            new SceneEftPlacement("dengguang",  588f, 251f,  67f, 0, 0, 0, 100f),
            new SceneEftPlacement("dengguang",  628f, 251f,  16f, 0, 0, 0, 100f),
            // StageScene09_ctor 的兩支(官方順序:case body 的 dengguang 先播、ctor 的 kuanghuan 後播)
            new SceneEftPlacement("kuanghuan1", 0, 50, 0, 0, 0, 0, 10f),
            new SceneEftPlacement("kuanghuan2", 0, 50, 0, 0, 0, 0, 10f),
        };

        // 個人房 / 婚禮大廳: star light + two pillar glows (StageScene10 ctor)
        // 注意:只有第一筆 star_light1 有反編譯依據 (StageScene_SpawnPlacement4f_004ae0a0: Effect_Play(0x4f)
        // 於 (126.69, 36.56, −60.95) scale 25)。後兩筆的座標 (293.92,219.36,3488.68) 與 (714.1,219.36,3188.51)
        // 在整份反編譯裡只出現在 SCN0028 的 StageScene_UpdateBigBillboardSet_004b0fc0 —— 它們是北京之夜的
        // 前兩支 stage28_dengzhu 光柱,被誤掛到個人房。目前無害:SceneFolder() 取的是場景路徑最後一段
        // (SCNROOM / SCNMYHOUSE / SCNMERRYROOM),永遠不會是 "SCN0037"/"SCN0038",所以這兩個 key 從未命中。
        // 若日後真要接個人房特效,請先重新從 StageScene10 相關的 ctor 取座標,別沿用這兩筆。
        private static SceneEftPlacement[] PersonalRoom() => new[]
        {
            new SceneEftPlacement("star_light1", 126.69f, 36.56f, -60.95f, 0, 0, 0, 25f),
            new SceneEftPlacement("stage28_dengzhu", 293.92f, 219.36f, 3488.68f, 0, 0, 90, 90f),
            new SceneEftPlacement("stage_3_light", 714.1f, 219.36f, 3188.51f, 0, 0, 90, 90f),
        };
    }
}
