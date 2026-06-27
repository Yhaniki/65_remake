using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Per-prop render-tuning for scene mapobj meshes. Keyed by (scene folder, prop base name).
    /// Applied to every material the prop's submeshes produce: _Color channel = _Color × ColorMul × Bright.
    /// Use this to adjust overall brightness or tint without touching the DDS files or vertex data.
    /// </summary>
    public readonly struct MapobjRenderTuning
    {
        /// <summary>Uniform RGB brightness multiplier (1 = unchanged). Does NOT affect alpha.</summary>
        public readonly float Bright;
        /// <summary>Per-channel tint/balance multiplier (white = unchanged). Applied on top of Bright.</summary>
        public readonly Color ColorMul;

        /// <summary>Adjust brightness only — all three RGB channels scaled equally.</summary>
        public MapobjRenderTuning(float bright)
        { Bright = bright; ColorMul = new Color(1f, 1f, 1f, 1f); }

        /// <summary>Adjust per-channel tint only — r/g/b multipliers, alpha optional.</summary>
        public MapobjRenderTuning(float r, float g, float b, float a = 1f)
        { Bright = 1f; ColorMul = new Color(r, g, b, a); }

        /// <summary>Adjust both brightness and per-channel tint.</summary>
        public MapobjRenderTuning(float bright, float r, float g, float b, float a = 1f)
        { Bright = bright; ColorMul = new Color(r, g, b, a); }

        public bool IsIdentity => Bright == 1f && ColorMul.r == 1f && ColorMul.g == 1f && ColorMul.b == 1f && ColorMul.a == 1f;
    }

    public static class SceneMapobjRenderCatalog
    {
        public static readonly MapobjRenderTuning Identity = new MapobjRenderTuning(1f, 1f, 1f);

        private readonly struct Entry
        {
            public readonly string Scene;   // e.g. "SCN0008"
            public readonly string Prop;    // baseName from AddMapobj, e.g. "ZIMU"
            public readonly MapobjRenderTuning Tuning;

            public Entry(string scene, string prop, MapobjRenderTuning tuning)
            { Scene = scene; Prop = prop; Tuning = tuning; }
        }

        private static readonly Entry[] Entries =
        {
            // SCN0008 ZIMU rotating rune panels.
            // Vertex baked lighting already encodes ~5× brightness between A (tiankonga2_) and B (tiankongb2_)
            // groups (lum 131 vs 27 for sub8). Bright scales all ZIMU panels uniformly; ColorMul adds tint.
            // Examples:
            //   new MapobjRenderTuning(bright: 2.0f)          // 2× 全體亮度
            //   new MapobjRenderTuning(bright: 1.5f, 0.9f, 1.1f, 1.2f)  // 1.5× + 偏青藍
            new Entry("SCN0008", "ZIMU", new MapobjRenderTuning(1f)),
        };

        public static MapobjRenderTuning Find(string scene, string prop)
        {
            if (string.IsNullOrEmpty(scene) || string.IsNullOrEmpty(prop)) return Identity;
            for (int i = 0; i < Entries.Length; i++)
            {
                var e = Entries[i];
                if (string.Equals(e.Scene, scene, System.StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Prop, prop, System.StringComparison.OrdinalIgnoreCase))
                    return e.Tuning;
            }
            return Identity;
        }
    }
}
