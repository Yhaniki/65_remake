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
            // Soft searchlight beam: additive, but blur the texture along its width so the light spreads sideways
            // and the narrow hard alpha edge becomes a gradual soft falloff (SCN0016 JIGUANG spotlights).
            SpotGlow,
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

        private static readonly Vector2 CoralV = new Vector2(0f, -0.08f); // V += 0.004 per 50 ms
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
            // SCN0014 FUN_004b0330: coral glow scrolls V by 0.004 every 50 ms.
            new Target(null, "SHANHU-BAI", -1, CoralV),
            new Target(null, "SHANHU-HONG", -1, CoralV),
            new Target(null, "SHANHU-LV", -1, CoralV),
            new Target(null, "SHANHUZHI-BAI", -1, CoralV),
            new Target(null, "SHANHUZHI-HONG", -1, CoralV),
            new Target(null, "SHANHUZHI-LV", -1, CoralV),
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
