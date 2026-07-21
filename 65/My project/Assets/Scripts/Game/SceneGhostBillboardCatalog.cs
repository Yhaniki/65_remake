using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>One .mot-driven, camera-facing sprite (SDO Billboard_AddEntry): a flat quad whose ANCHOR follows a
    /// looping .mot (fly + uniform scale-pulse) while the quad always faces the camera, with a 2-frame texture swing.
    /// Used for the SCN0022 坟墓 flying ghosts (gui/gui2).</summary>
    public sealed class GhostBillboardDef
    {
        public readonly string Dir;          // group folder (relative to Extracted root) holding hrc/mot/frames
        public readonly string Hrc, Mot;     // skeleton + looping motion (drives the flight path + scale)
        public readonly string[] Frames;     // texture-swing frames (cycle order)
        public readonly float IntervalMs;    // ms per texture frame
        public readonly float Size;          // quad size (mesh local extent; the .mot scale is applied on top)
        public GhostBillboardDef(string dir, string hrc, string mot, string[] frames, float intervalMs, float size)
        { Dir = dir; Hrc = hrc; Mot = mot; Frames = frames; IntervalMs = intervalMs; Size = size; }
    }

    /// <summary>
    /// Per-scene .mot-driven camera-facing sprites. The original loads these as AvatarScene meshes and registers them
    /// with <c>Billboard_AddEntry</c> (030 case 0x16) so the flat quad always faces the camera while its .mot animates
    /// position/scale. Baking the .mot into a FIXED-orientation quad (the normal mapobj path) makes the flat sprite
    /// foreshorten / go edge-on from the stage camera's angle — hard, "solid-division" look. Rendering it as a
    /// camera-facing billboard (like the 鬼火 flame) keeps it full-face and soft.
    ///
    ///   SCN0022 坟墓 gui (LABA11) / gui2 (LABA12): flying ghosts. The .mot flies them a wide path and pulses their
    ///     uniform scale (1.94→2.96); the texture swings GUI01↔GUI02 every 125 ms (StageScene_UpdateLightGroups, 0x14c
    ///     &1). Additive-glow DXT3 (already soft). Mesh local extent ≈ 20 (the .mot scale ~2.5× makes it ~50 on stage).
    ///
    /// Spawned by ScreenGameplay.SpawnSceneGhosts; the meshes are skipped in TryLoadMapobjs so they aren't double-drawn.
    /// (Directional sweeping beams like sheguang are NOT here — billboarding would drop their sweep; they stay meshes.)
    /// </summary>
    public static class SceneGhostBillboardCatalog
    {
        private static readonly GhostBillboardDef[] Empty = new GhostBillboardDef[0];
        private static readonly Dictionary<string, GhostBillboardDef[]> ByFolder = new Dictionary<string, GhostBillboardDef[]>
        {
            ["SCN0022"] = new[]
            {
                new GhostBillboardDef("SCENE/MAPOBJ/FENMU/GUI", "LABA11.HRC", "LABA11.MOT",
                    new[] { "FENMUOBJ_GUI_GUI01_.dds", "FENMUOBJ_GUI_GUI02_.dds" }, 125f, 20f),
                new GhostBillboardDef("SCENE/MAPOBJ/FENMU/GUI2", "LABA12.HRC", "LABA12.MOT",
                    new[] { "FENMUOBJ_GUI2_GUI01_.dds", "FENMUOBJ_GUI2_GUI02_.dds" }, 125f, 20f),
            },
        };

        /// <summary>The .mot-driven ghost billboards for a scene folder (e.g. "SCN0022"); empty if none.</summary>
        public static IReadOnlyList<GhostBillboardDef> ForFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return Empty;
            return ByFolder.TryGetValue(folder.ToUpperInvariant(), out var a) ? a : Empty;
        }
    }
}
