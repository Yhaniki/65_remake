using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Data-driven UV-scroll commands for stage render objects the original animates by writing texture-coordinate
    /// offsets into render states (0x58=U, 0x5c=V). Targets are structural: scene folder is optional, object key is
    /// SCENE or a mapobj base name, and material id is optional. No target depends on a DDS file name.
    /// </summary>
    public static class SceneMapobjUvScrollCatalog
    {
        public const string SceneObject = "SCENE";

        public enum RenderMode
        {
            KeepMaterial,
            AdditiveOverlay,
            // Force standard alpha-blend (SrcAlpha,OneMinusSrcAlpha) regardless of what the MSH loader assigned.
            // Needed when LooksLikeAdditiveGlow() misclassifies a texture that D3D9 confirmed uses DST=INVSRCALPHA.
            ForceAlphaBlend,
            // Like ForceAlphaBlend but TWO-SIDED and at FULL opacity (Sdo/UnlitOverlay) — for a glow prop the MSH loader
            // wrongly made additive (LooksLikeAdditiveGlow false positive) whose additive edge reads hard/opaque. Unlike
            // ForceAlphaBlend it doesn't cull (a sweeping beam mustn't vanish edge-on) or dim to 0.2 (keep the beam visible).
            AlphaBlendOverlay,
            // Soft searchlight beam: additive, but blur the texture along its width so the light spreads sideways
            // and the narrow hard alpha edge becomes a gradual soft falloff (SCN0016 JIGUANG spotlights).
            SpotGlow,
            // Let the prop's OFFICIAL per-material flag decide (MSH material record +0x194): a material the artist
            // marked transparent (flags & 0x3f != 0) renders as STANDARD alpha-blend, one marked 0 keeps whatever the
            // opaque path gave it. The engine has no alpha-test and no additive material mode, so this both undoes the
            // LooksLikeAdditiveGlow false positives (a bright soft-alpha glow blown out to solid white) and the
            // cutout heuristic. Unlike ForceAlphaBlend/AlphaBlendOverlay this is PER-MATERIAL, so a multi-material
            // prop (SCN0014 TV: screen + frame + projector opaque, only the light beam transparent) stays correct.
            OfficialMaterialAlpha,
        }

        public readonly struct Target
        {
            public readonly string Folder;     // null/empty = any scene
            public readonly string ObjectKey;
            public readonly int MaterialId;    // -1 = all materials on the object
            public readonly Vector2 Speed;
            public readonly RenderMode Mode;

            public Target(string folder, string objectKey, int materialId, Vector2 speed, RenderMode mode = RenderMode.KeepMaterial)
            {
                Folder = folder;
                ObjectKey = objectKey;
                MaterialId = materialId;
                Speed = speed;
                Mode = mode;
            }
        }

        // SCN0014 FUN_004b0330 writes SEVEN texture-coord targets every 50 ms, in three groups that line up exactly
        // with the scene's props: the 3 coral TREES get V = −t, the 3 coral BRANCHES get V = −2t, and the 7th — the
        // only one written in U, at 4× the rate — is the projector BEAM (t += _DAT_00589034 = 0.004 per 50 ms).
        // U is the beam's around-the-axis coordinate (its mesh is a 6-segment cone unwrapped U 0.018‥0.976 with only
        // two V rows), so scrolling U spins the light pattern about the beam axis — the "光自己也在轉" on top of the
        // whole prop orbiting the stage on its .mot. Coral UVs are not cylindrical, so U scroll only makes sense there.
        private static readonly Vector2 CoralV = new Vector2(0f, -0.08f);        // trees:    V += 0.004 per 50 ms
        private static readonly Vector2 CoralBranchV = new Vector2(0f, -0.16f);  // branches: V += 0.008 per 50 ms (2×)
        private static readonly Vector2 BeamSpinU = new Vector2(0.32f, 0f);      // beam:     U += 0.016 per 50 ms (4×)
        // D3D9 V += 0.003/50ms = +0.06/s. Test confirmed positive sign is correct (unlike CoralV).
        // Angular-edge issue tracked in decomp doc; suspect UV scale transform not yet captured.
        private static readonly Vector2 Scn0015WindowUv = new Vector2(0f, 0.06f);

        private static readonly Target[] Targets =
        {
            // SCN0011 StageScene_UpdateScrollLights: UV scroll V += 0.003/frame on Vector_at4b(0).
            // Vector_at4b[0] = CAIDAI — only uVar13==1 (CAIDAI) calls AvatarScene_Create(..., param3=1) which
            // registers it as the UV-scroll target; all others pass 0 and are skipped.
            // caidai.dds (32×128 DXT1) tiles V −1~2 (3× repeat) on the 彩帶 vertical light strip next to the speaker.
            // D3D9 positive V → Unity negative V (DDS raw-load flips V axis, same as CoralV convention).
            new Target("SCN0011", "CAIDAI", -1, new Vector2(0f, 1.775f)),   // measured: online sdo.bin @ 593fps → 0.003×593 = 1.775 UV/s
            // SCN0020 subway FUN_004b09a0: the TV1 filmstrip screen (TV01.dds 256×1024, dancers + "BROADWAY") is the
            // ONLY object registered with AvatarScene_Create(...,param3=1) → scroll object 0. Every 300 ms the update
            // sets render state +0x48|=0x10000 (texture transform), U=0, V += _DAT_00589040 (=0.03), wrapping at 1.0
            // ⇒ 0.1 UV/s in V. Sign confirmed by visual check (BROADWAY filmstrip scrolled the wrong way at -0.1) → +0.1.
            new Target("SCN0020", "TV1", -1, new Vector2(0f, 0.1f)),
            // SCN0015 FUN_004b0620: every 50 ms set U=0 and V=DAT_00678534, then DAT_00678534 += 0.003.
            // 15_UV is the only mapobj created with param3=1 in scene-load case 0xf; HUA/SHU1-4 pass 0.
            // The texture itself is diagonal, so a pure V scroll reads as the window beam sliding diagonally down.
            // D3D9 capture (hook onLeave after RenderObjPre): ABL=1 SRC=SRCALPHA(5) DST=INVSRCALPHA(6)
            // = STANDARD alpha blend (NOT additive). ZWrite=1, CULL=3, TTF0=COUNT2(2), ADDR=WRAP, FILTER=LINEAR.
            // ForceAlphaBlend overrides the MSH loader — LooksLikeAdditiveGlow returns true for GUANG1_.DDS
            // (it matches the "soft alpha, low opaque, mid lum" heuristic for radial glow sprites), so without
            // the override the material becomes Sdo/UnlitAdditiveOverlay, producing a hard bright mesh-edge band.
            new Target("SCN0015", "15_UV", -1, Scn0015WindowUv, RenderMode.ForceAlphaBlend),
            // SCN0016 spotlights (JIGUANG1/2/3): guang1_.dds has a narrow (~3-texel) alpha edge, so a plain additive
            // beam reads hard at its left/right. SpotGlow blurs the texture along its width to spread the light
            // sideways into a soft falloff. Speed=0 — these don't UV-scroll; the entry only carries the render mode.
            new Target("SCN0016", "JIGUANG1", -1, Vector2.zero, RenderMode.SpotGlow),
            new Target("SCN0016", "JIGUANG2", -1, Vector2.zero, RenderMode.SpotGlow),
            new Target("SCN0016", "JIGUANG3", -1, Vector2.zero, RenderMode.SpotGlow),
            // SCN0022 坟墓 射光 (sheguang1-3 = light-ray spotlights): the MSH loader makes these DXT3 glows ADDITIVE
            // (LooksLikeAdditiveGlow: bright RGB + all-soft alpha), but that's the SCN0015-窗光 false-positive — additive
            // reads as a hard, edgy beam with no transparency variation. Force TWO-SIDED standard alpha-blend so the ray
            // is a soft translucent shaft (the sweep must not cull edge-on). gui/gui2 (LABA11/12) are NOT here — they're
            // rebuilt as camera-facing billboards (SpawnSceneGhosts), which set their own alpha-blend material.
            new Target("SCN0022", "SHEGUANG",  -1, Vector2.zero, RenderMode.AlphaBlendOverlay),
            new Target("SCN0022", "SHEGUANG2", -1, Vector2.zero, RenderMode.AlphaBlendOverlay),
            new Target("SCN0022", "SHEGUANG3", -1, Vector2.zero, RenderMode.AlphaBlendOverlay),
            // SCN0014 海底 projector beams (TOUYINGGUANG_.DDS): the stage-centre GUANG prop and the beam material
            // inside the spinning TV prop. Their DXT3 is a bright (meanLum 186) mostly-soft-alpha glow, so
            // LooksLikeAdditiveGlow classes them additive → the overlapping beam quads saturate to a solid white
            // blob ("光沒去背"). The OFFICIAL material flags say otherwise: GUANG mat0 = 1 and TV mat1 = 1
            // (transparent batch = standard alpha blend), while TV's zhuanpan/gangjia/tv/touyingji_c are all 0
            // (opaque). Per-material so only the beam changes.
            // GUANG additionally SPINS: it is the 7th (U, 4×) target of FUN_004b0330 — see BeamSpinU. Its .mot only
            // carries ONE animated bone ('Box02', 550 keys of yaw) which orbits the whole prop around the stage, so
            // the light pattern turning about its own axis is this U scroll, nothing else.
            new Target("SCN0014", "GUANG", -1, BeamSpinU, RenderMode.OfficialMaterialAlpha),
            new Target("SCN0014", "TV", -1, Vector2.zero, RenderMode.OfficialMaterialAlpha),
            // SCN0025 春天 fountain water (CHUNTIANDONGHUA / SHUI_C_.DDS, official flags = 0x11 → transparent batch):
            // same additive false positive (meanLum 252, 98% soft alpha) painted the fountain as a solid white splash.
            // It also FLOWS: FUN_004b0d20's last block sets texture-transform U=0, V += _DAT_00589044 (=0.05) every
            // 50 ms ⇒ +1.0 UV/s in V, wrapping at 1.0. Positive sign copied verbatim (like SCN0015's window beam).
            new Target("SCN0025", "CHUNTIANDONGHUA", -1, new Vector2(0f, 1.0f), RenderMode.OfficialMaterialAlpha),
            // SCN0014 FUN_004b0330 coral glow: the three TREES scroll V at 1×, the three BRANCHES at 2× (verbatim
            // −t / −2t groups; the branches used to share the trees' rate).
            new Target(null, "SHANHU-BAI", -1, CoralV),
            new Target(null, "SHANHU-HONG", -1, CoralV),
            new Target(null, "SHANHU-LV", -1, CoralV),
            new Target(null, "SHANHUZHI-BAI", -1, CoralBranchV),
            new Target(null, "SHANHUZHI-HONG", -1, CoralBranchV),
            new Target(null, "SHANHUZHI-LV", -1, CoralBranchV),
        };

        /// <summary>UV-scroll speed (UV/s) for a scene object/material slot, or Vector2.zero if it does not scroll.</summary>
        public static Vector2 Find(string folder, string objectKey, int materialId = -1)
        {
            return TryFind(folder, objectKey, materialId, out var target) ? target.Speed : Vector2.zero;
        }

        public static RenderMode FindRenderMode(string folder, string objectKey, int materialId = -1)
        {
            return TryFind(folder, objectKey, materialId, out var target) ? target.Mode : RenderMode.KeepMaterial;
        }

        public static bool UsesAdditiveOverlay(string folder, string objectKey, int materialId = -1)
        {
            return FindRenderMode(folder, objectKey, materialId) == RenderMode.AdditiveOverlay;
        }

        private static bool TryFind(string folder, string objectKey, int materialId, out Target target)
        {
            target = default;
            if (string.IsNullOrEmpty(objectKey)) return false;
            for (int i = 0; i < Targets.Length; i++)
            {
                var t = Targets[i];
                if (!string.IsNullOrEmpty(t.Folder) &&
                    !string.Equals(t.Folder, folder, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(t.ObjectKey, objectKey, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (t.MaterialId >= 0 && materialId >= 0 && t.MaterialId != materialId) continue;
                if (t.MaterialId >= 0 && materialId < 0) continue;
                target = t;
                return true;
            }
            return false;
        }
    }
}
