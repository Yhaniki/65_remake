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
    ///
    /// Keyed by scene FOLDER (SceneMapobjCatalog's key). Spawned by ScreenGameplay.SpawnSceneFlames.
    /// </summary>
    public static class SceneFlameBillboardCatalog
    {
        private static readonly Dictionary<string, SceneFlameSet> ByFolder = new Dictionary<string, SceneFlameSet>
        {
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
        };

        /// <summary>The flame billboard set for a scene folder (e.g. "SCN0022"), or null if the scene has none.</summary>
        public static SceneFlameSet ForFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            return ByFolder.TryGetValue(folder.ToUpperInvariant(), out var s) ? s : null;
        }
    }
}
