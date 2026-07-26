using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>One camera-facing "billboard" quad: world position + quad size (world units).</summary>
    public struct FlameBillboard
    {
        public readonly float X, Y, Z, Size;
        public FlameBillboard(float x, float y, float z, float size) { X = x; Y = y; Z = z; Size = size; }
    }

    /// <summary>A set of camera-facing, texture-animated billboard sprites a scene draws — the SDO
    /// <c>BillboardSet</c> the original updates by frame-swapping a texture sequence from ONE shared frame counter
    /// (so every billboard in the set shows the same frame in sync).</summary>
    public sealed class SceneFlameSet
    {
        public readonly string FramesDir;        // folder holding the frames, relative to the Extracted root
        public readonly string[] Frames;         // ordered frame file names (cycle order)
        public readonly float IntervalMs;        // ms per frame
        public readonly FlameBillboard[] Billboards;
        public SceneFlameSet(string framesDir, string[] frames, float intervalMs, FlameBillboard[] billboards)
        { FramesDir = framesDir; Frames = frames; IntervalMs = intervalMs; Billboards = billboards; }
    }

    /// <summary>
    /// Per-scene camera-facing flame/glow billboards (SDO BillboardSet), decompiled from Scene_LoadBackground
    /// (030 FUN_004b43c0) + the per-frame scene update (029 FUN_004ad250). These are NOT mapobj meshes: the engine
    /// creates a BillboardSet (always faces the camera) and, every N ms, rebinds its texture slot to the next frame
    /// of a loaded sequence. The mapobj MESH the same asset ships (e.g. FENMU/LANHUO's SHAN.MSH) is loaded but not
    /// linked as a scene child, so it is never drawn — the visible sprite is this billboard.
    ///
    ///   SCN0022 坟墓 鬼火 / 蓝火 (StageScene_UpdateLightGroups_004b0b30): 3 blue-flame billboards at the decompiled
    ///     positions (param_1[0x56/0x57/0x58]), size 100 (0x42c80000). The lanhuo frame array (param_1[0x150]) is
    ///     advanced by a 0x96 = 150 ms timer, index = (i+1) % 3, and written to all three from one shared counter
    ///     (DAT_00678578) — so they flicker in sync. Frames are the smooth 32-bit TGA (the DXT3 twin's 4-bit alpha
    ///     bands the soft glow); render additively (Sdo/UnlitAdditiveOverlay = alpha-weighted additive, two-sided).
    ///   SCN0024 雪景 招牌光暈 (Scene_LoadBackground case 0x18): 3 static glow panels of the scene folder's own
    ///     GUANG_.TGA, size 200 (0x43480000), same +0x80/0x84/0x88 field values as SCN0022 — one frame, no cycle.
    ///
    /// Keyed by scene FOLDER (SceneMapobjCatalog's key). Spawned by ScreenGameplay.SpawnSceneFlames.
    /// </summary>
    public static class SceneFlameBillboardCatalog
    {
        private static readonly Dictionary<string, SceneFlameSet> ByFolder = new Dictionary<string, SceneFlameSet>
        {
            // SCN0001 新天地:街上兩盞路燈燈頭的光暈。Scene_LoadBackground case 1 切封裝到 Datas\Scene\scn0001.bin、
            // 載入 guangxiao_.tga 存 param_1[0x10],再 BillboardSet_Create ×2 → param_1[0xe]/[0xf],尺寸都是
            // 0x42c80000 = 100,旗標 +0x80=2 / +0x84=4 / +0x88=1 與 SCN0022/0024/0028 逐欄位相同。
            // 每幀更新的 switch 從 case 3 才開始、沒有 case 1 → 這兩顆是靜態光暈,不換幀。
            // 燈桿本體在 SCENE.MSH 裡(材質 ludeng.dds),少了這筆就只有桿子、燈頭不發光。
            ["SCN0001"] = new SceneFlameSet(
                "SCENE/SCN0001",
                new[] { "GUANGXIAO_.TGA" },
                1000f,   // 單張:間隔不影響畫面
                new[]
                {
                    new FlameBillboard(77.000f, 147.475f, 336.240f, 100f),
                    new FlameBillboard(-626.482f, 147.475f, 336.349f, 100f),
                }),

            // SCN0005 聖誕夜:同一張 guangxiao_.tga 的單顆光暈,100×100,同樣是靜態(case 5 的每幀更新
            // StageScene_UpdateDualBillboard_004afeb0 不碰它)。
            ["SCN0005"] = new SceneFlameSet(
                "SCENE/SCN0005",
                new[] { "GUANGXIAO_.TGA" },
                1000f,
                new[]
                {
                    new FlameBillboard(-97.7f, 115.0f, 222.4f, 100f),
                }),

            ["SCN0022"] = new SceneFlameSet(
                "SCENE/MAPOBJ/FENMU/LANHUO",
                new[] { "FENMUOBJ_LANHUO_01_.TGA", "FENMUOBJ_LANHUO_02_.TGA", "FENMUOBJ_LANHUO_03_.TGA" },
                150f,
                new[]
                {
                    new FlameBillboard(-163.43f, 40f, 135.51f, 100f),
                    new FlameBillboard(-11.6f, 30f, 200.11f, 100f),
                    new FlameBillboard(182.96f, 28f, 21.6f, 100f),
                }),

            // SCN0024 雪景街景:招牌上的三顆暖黃光暈。同一套 CBillboardSet,但只有「一張」貼圖 —— 場景 0x18 的
            // 每幀更新 (FUN_004b0cc0) 只換招牌那 5 張,完全沒碰這三顆,所以它們是靜態光暈,不是換幀動畫。
            // 官方 case 0x18 把當前封裝切到 Datas/Scene/scn0024,載入 guang_.tga,建三個物件
            // (param_1[0x56/0x57/0x58]),寫入座標 +0x28/0x2c/0x30、尺寸 200×200,並設 +0x80=2 / +0x84=4 /
            // +0x88=1 —— 與 SCN0022 鬼火那三顆逐欄位相同,所以照樣走加法混色的相機朝向 quad。
            // 貼圖是 256×256 32-bit TGA 的柔和放射狀光暈(alpha 最高只有 189,永遠不會全不透明),
            // 三顆都貼在 y≈210 的建築燈具上;真正會掃的那道光是 XUEJING/DONGHUA 的 mesh,不是這個。
            ["SCN0024"] = new SceneFlameSet(
                "SCENE/SCN0024",
                new[] { "GUANG_.TGA" },
                1000f,   // 單張:間隔不影響畫面(MapobjTexAnimator 的 idx % 1 永遠是 0)
                new[]
                {
                    new FlameBillboard(432.109f, 210.371f, -3.208f, 200f),
                    new FlameBillboard(-498.497f, 212.36f, 192.66f, 200f),
                    new FlameBillboard(75.41f, 213.7f, 314.22f, 200f),
                }),

            // SCN0028 北京之夜 (鸟巢):舞台四周四顆燈光暈(y 126~164,圍在舞台外圈,和遠處那四組
            // niaochao/dengN 光暈片是兩回事)。與 SCN0024 是同一條路子 —— Scene_LoadBackground
            // case 0x1c 把當前封裝切到 Datas/Scene/scn0028、載入 guang_.tga(param_1[0x10]),建四個
            // CBillboardSet(param_1[0x65..0x68]),寫入座標 +0x28/+0x2c/+0x30,尺寸 100×100(0x42c80000),
            // 四顆共用同一張貼圖,並設 +0x80=2 / +0x84=4 / +0x88=1 —— 和 SCN0022 鬼火、SCN0024 招牌光暈
            // 逐欄位相同,所以照樣是加法混色的相機朝向 quad。場景 0x1c 的每幀更新完全沒碰這四顆,
            // 只換遠處那四組光暈片的貼圖(見 SceneMapobjTexAnimCatalog 的 DENG1_~DENG4_),所以這是靜態光暈。
            // 少了這筆,場景裡的路燈桿(在 SCENE.MSH 裡,貼 LUDENG_.DDS)就只有燈桿、燈頭不發光。
            ["SCN0028"] = new SceneFlameSet(
                "SCENE/SCN0028",
                new[] { "GUANG_.TGA" },
                1000f,   // 單張:間隔不影響畫面(MapobjTexAnimator 的 idx % 1 永遠是 0)
                new[]
                {
                    new FlameBillboard(468.834f, 137.709f, 467.449f, 100f),
                    new FlameBillboard(-185.057f, 131.230f, 463.951f, 100f),
                    new FlameBillboard(-269.018f, 163.505f, -180.453f, 100f),
                    new FlameBillboard(243.408f, 125.984f, -48.953f, 100f),
                }),
        };

        /// <summary>The flame billboard set for a scene folder (e.g. "SCN0022"), or null if the scene has none.</summary>
        public static SceneFlameSet ForFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            return ByFolder.TryGetValue(folder.ToUpperInvariant(), out var s) ? s : null;
        }
    }
}
