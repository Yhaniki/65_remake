using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>One animated texture overlay: a mapobj mesh whose material is driven through an ordered DDS frame
    /// sequence (the original's UIPicMap frame-swap) instead of by its single MSH material. Frames live in the
    /// mapobj group's own SCENE/MAPOBJ folder.</summary>
    public sealed class MapobjTexAnim
    {
        public readonly string MeshBase;     // mesh file base name (no extension), upper-invariant
        public readonly string[] Frames;     // DDS file names within the group's folder, in cycle order
        public readonly float IntervalMs;    // ms between frames
        public readonly bool Transparent;    // true -> alpha-blended overlay (cutout sprites: crowd/lights);
                                             // false -> keep the opaque material (a solid video screen)
        public readonly bool HoldLast;       // true -> play-once: after intervalMs, lock on last frame forever
        public MapobjTexAnim(string meshBase, string[] frames, float intervalMs, bool transparent, bool holdLast = false)
        { MeshBase = meshBase; Frames = frames; IntervalMs = intervalMs; Transparent = transparent; HoldLast = holdLast; }
    }

    /// <summary>
    /// Hand-authored companion to <see cref="SceneMapobjCatalog"/> for the props the original textures by a per-frame
    /// DDS sequence rather than by their MSH material. Decompiled from Scene_LoadBackground (FUN_004b43c0: the frame
    /// lists are loaded via UIPicMap_LoadEntry) + the scene updates (FUN_004ad250) that advance the frame index on a
    /// timer. The MSH of these props carries only a placeholder material (e.g. SEA_SCREEN = "s00.dds", which doesn't
    /// exist on disk), so the MSH-material path renders them white/untextured — that's why they looked "missing".
    ///
    ///   FIFA day (SCN0012) / night (SCN0013): crowd renqun (9 frames) + spotlight shanguang (4 frames), 300 ms,
    ///     alpha-cutout sprites (people / light beams on a transparent field) -> Transparent.
    ///   Sea (SCN0014): sea_screen video wall (28 frames), 250 ms, an opaque screen -> NOT transparent.
    ///
    /// Keyed by scene FOLDER (SceneMapobjCatalog's key) + mesh base name; frames resolve against the group's folder.
    /// </summary>
    public static class SceneMapobjTexAnimCatalog
    {
        // zero-padded "<prefix>NNN.dds" sequence, 1..count (e.g. Seq("sea_screen",28) -> sea_screen001.dds..028.dds)
        private static string[] Seq(string prefix, int count)
        {
            var a = new string[count];
            for (int i = 0; i < count; i++) a[i] = prefix + (i + 1).ToString("000") + ".dds";
            return a;
        }

        // 2-digit "<prefix>NN.dds" sequence, 1..count (e.g. Seq2("19_SUBWAY_VT6",24) -> 19_SUBWAY_VT601.dds..624.dds)
        private static string[] Seq2(string prefix, int count)
        {
            var a = new string[count];
            for (int i = 0; i < count; i++) a[i] = prefix + (i + 1).ToString("00") + ".dds";
            return a;
        }

        private static readonly MapobjTexAnim Shanguang =
            new MapobjTexAnim("FIFA_SHANGUANG", new[] { "s001_.dds", "s002_.dds", "s003_.dds", "s004_.dds" }, 300f, true);

        // The frame lists / intervals / transparency below are decompiled from Scene_LoadBackground (load) +
        // Scene_UpdateSceneObjects (timers) and grounded against the on-disk DDS sequences; Transparent matches each
        // sequence's measured alpha (opaque screens/water vs alpha cut-outs). See SDO_SCENE_MAPOBJ docs.
        private static readonly Dictionary<string, MapobjTexAnim[]> ByFolder =
            new Dictionary<string, MapobjTexAnim[]>
            {
                // SCN0003 disco floor is NOT here — its 256 tiles animate as a per-tile moving formation
                // (BoxFloorPattern / BoxFloorAnimator), not a single shared-material cycle.
                ["SCN0004"] = new[]
                {
                    // 海灘 water surface waves: sea_up = B001..B032, sea_down = A001..A032 @100ms, opaque.
                    new MapobjTexAnim("SEA_UP", Seq("B", 32), 100f, false),
                    new MapobjTexAnim("SEA_DOWN", Seq("A", 32), 100f, false),
                },
                ["SCN0005"] = new[]
                {
                    // Christmas reindeer billboard + the ground "Merry Christmas" decal: the MSH materials are
                    // placeholders (xunlu.dds / 001.dds, absent) so without these they rendered as beige boxes
                    // ("奇怪方塊在天上飛"). Frames CHRISTMAS001..004 / MERRYCHRISTMAS001..004 @500ms, alpha cut-outs.
                    new MapobjTexAnim("CHRISTMAS", Seq("CHRISTMAS", 4), 500f, true),
                    new MapobjTexAnim("MERRYCHRISTMAS", Seq("MERRYCHRISTMAS", 4), 500f, true),
                },
                ["SCN0011"] = new[]
                {
                    new MapobjTexAnim("JIGUANG", new[] { "01_.dds", "02_.dds", "03_.dds", "04_.dds", "05_.dds", "06_.dds", "07_.dds", "08_.dds", "09_.dds" }, 300f, true),
                    new MapobjTexAnim("DIDENG", new[] { "343.dds", "344.dds", "345.dds", "346.dds" }, 300f, false),   // opaque floor light
                    new MapobjTexAnim("DENGGUANG", new[] { "guangx1_.dds", "guangx11.dds" }, 600f, true),
                },
                ["SCN0012"] = new[]
                {
                    new MapobjTexAnim("FIFA_RENQUN", Seq("", 9), 300f, true),   // 001.dds..009.dds
                    Shanguang,
                },
                ["SCN0013"] = new[]
                {
                    new MapobjTexAnim("FIFA_RENQUN", Seq("fifanight_renqun", 9), 300f, true),
                    Shanguang,
                },
                ["SCN0014"] = new[]
                {
                    new MapobjTexAnim("SEA_SCREEN", Seq("sea_screen", 28), 250f, false),   // opaque video wall
                },
                ["SCN0017"] = new[]
                {
                    new MapobjTexAnim("DIANSHI", Seq("DIANSHI", 30), 150f, false),   // opaque subway TV wall
                },
                ["SCN0020"] = new[]
                {
                    // 19_subway TV6 video screen: 24 frames 19_SUBWAY_VT601..624 @80 ms, opaque (DXT3 but full alpha,
                    // a solid video wall like DIANSHI). FUN_004b09a0 cycles param_1[0x4f] every 0x50=80 ms, %0x18=24.
                    new MapobjTexAnim("TV6", Seq2("19_SUBWAY_VT6", 24), 80f, false),
                },
                ["SCN0018"] = new[]
                {
                    new MapobjTexAnim("NIHONG", Seq("NIHONG", 12), 500f, true),         // neon, alpha
                    new MapobjTexAnim("BOAT_SCREEN", Seq("BOAT_SCREEN", 4), 500f, false),// opaque screen
                    new MapobjTexAnim("SHUIMO", Seq("SHUIMO", 5), 125f, true),          // water-ink ripple, alpha
                    new MapobjTexAnim("WATER", Seq("WATER", 10), 150f, false),          // river surface, opaque
                },
            };

        /// <summary>The frame sequence for a (scene folder, mesh base) pair, or null if that prop isn't a sequence.</summary>
        public static MapobjTexAnim Find(string folder, string meshBase)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(meshBase)) return null;
            if (!ByFolder.TryGetValue(folder.ToUpperInvariant(), out var arr)) return null;
            string mb = meshBase.ToUpperInvariant();
            foreach (var a in arr) if (a.MeshBase == mb) return a;
            return null;
        }
    }
}
