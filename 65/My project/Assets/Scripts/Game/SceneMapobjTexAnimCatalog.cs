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
        public MapobjTexAnim(string meshBase, string[] frames, float intervalMs, bool transparent)
        { MeshBase = meshBase; Frames = frames; IntervalMs = intervalMs; Transparent = transparent; }
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

        private static readonly MapobjTexAnim Shanguang =
            new MapobjTexAnim("FIFA_SHANGUANG", new[] { "s001_.dds", "s002_.dds", "s003_.dds", "s004_.dds" }, 300f, true);

        private static readonly Dictionary<string, MapobjTexAnim[]> ByFolder =
            new Dictionary<string, MapobjTexAnim[]>
            {
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
