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

        public readonly struct Target
        {
            public readonly string Folder;     // null/empty = any scene
            public readonly string ObjectKey;
            public readonly int MaterialId;    // -1 = all materials on the object
            public readonly Vector2 Speed;

            public Target(string folder, string objectKey, int materialId, Vector2 speed)
            {
                Folder = folder;
                ObjectKey = objectKey;
                MaterialId = materialId;
                Speed = speed;
            }
        }

        private static readonly Vector2 CoralV = new Vector2(0f, -0.08f); // V += 0.004 per 50 ms

        private static readonly Target[] Targets =
        {
            // SCN0011 StageScene_UpdateScrollLights: UV scroll V += 0.003/frame on Vector_at4b(0).
            // Vector_at4b[0] = CAIDAI — only uVar13==1 (CAIDAI) calls AvatarScene_Create(..., param3=1) which
            // registers it as the UV-scroll target; all others pass 0 and are skipped.
            // caidai.dds (32×128 DXT1) tiles V −1~2 (3× repeat) on the 彩帶 vertical light strip next to the speaker.
            // D3D9 positive V → Unity negative V (DDS raw-load flips V axis, same as CoralV convention).
            new Target("SCN0011", "CAIDAI", -1, new Vector2(0f, -0.18f)),  // 0.003 × 60fps = 0.18/s, negated
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
            if (string.IsNullOrEmpty(objectKey)) return Vector2.zero;
            for (int i = 0; i < Targets.Length; i++)
            {
                var t = Targets[i];
                if (!string.IsNullOrEmpty(t.Folder) &&
                    !string.Equals(t.Folder, folder, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(t.ObjectKey, objectKey, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (t.MaterialId >= 0 && materialId >= 0 && t.MaterialId != materialId) continue;
                if (t.MaterialId >= 0 && materialId < 0) continue;
                return t.Speed;
            }
            return Vector2.zero;
        }
    }
}
