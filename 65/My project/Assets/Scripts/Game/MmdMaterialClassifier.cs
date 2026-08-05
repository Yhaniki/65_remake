using UnityEngine;
using UnityEngine.Rendering;

namespace Sdo.Game
{
    public enum MmdMaterialRenderMode
    {
        Hidden,
        Opaque,
        Cutout,
        Blend,
    }

    /// <summary>Pure PMX material-alpha decision shared by runtime material construction and EditMode tests.</summary>
    public static class MmdMaterialClassifier
    {
        private const float HiddenAlpha = 0.05f;
        private const float AuthoredOpaqueAlpha = 0.999f;
        private const float MeaningfulAlphaFraction = 0.02f;
        private const float BroadTranslucencyFraction = 0.15f;

        public static MmdMaterialRenderMode Classify(
            float authoredAlpha,
            float translucentFraction,
            float transparentFraction,
            bool doubleSided)
        {
            if (authoredAlpha < HiddenAlpha) return MmdMaterialRenderMode.Hidden;

            // Texture alpha chooses HOW an authored-visible PMX material is drawn; it must never decide WHETHER the
            // material exists. The old code hid any texture with >=15% mid-alpha, which removed 34.6% of YYB's mesh.
            if (authoredAlpha < AuthoredOpaqueAlpha || translucentFraction >= BroadTranslucencyFraction)
                return MmdMaterialRenderMode.Blend;

            // A texture dominated by opaque/empty texels is a silhouette cutout. When it has no holes but does contain
            // a meaningful soft-alpha fringe, blending preserves that fringe instead of painting it fully opaque.
            if (transparentFraction >= MeaningfulAlphaFraction) return MmdMaterialRenderMode.Cutout;
            if (translucentFraction >= MeaningfulAlphaFraction) return MmdMaterialRenderMode.Blend;

            // Double-sided is a culling flag, not an alpha flag. It deliberately does not affect this result.
            return MmdMaterialRenderMode.Opaque;
        }

        /// <summary>Apply the render-state half of the classification to the shared MMD material.</summary>
        public static void Apply(Material material, MmdMaterialRenderMode mode)
        {
            if (material == null) return;

            switch (mode)
            {
                case MmdMaterialRenderMode.Cutout:
                    material.SetOverrideTag("RenderType", "TransparentCutout");
                    material.SetFloat("_AlphaClip", 1f);
                    material.SetFloat("_SrcBlend", (float)BlendMode.One);
                    material.SetFloat("_DstBlend", (float)BlendMode.Zero);
                    material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                    material.SetFloat("_DstBlendAlpha", (float)BlendMode.Zero);
                    material.SetFloat("_ZWrite", 1f);
                    material.renderQueue = (int)RenderQueue.AlphaTest;
                    break;

                case MmdMaterialRenderMode.Blend:
                    material.SetOverrideTag("RenderType", "Transparent");
                    material.SetFloat("_AlphaClip", 0f);
                    material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                    material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                    material.SetFloat("_ZWrite", 0f);
                    material.renderQueue = (int)RenderQueue.Transparent;
                    break;

                default:
                    material.SetOverrideTag("RenderType", "Opaque");
                    material.SetFloat("_AlphaClip", 0f);
                    material.SetFloat("_SrcBlend", (float)BlendMode.One);
                    material.SetFloat("_DstBlend", (float)BlendMode.Zero);
                    material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                    material.SetFloat("_DstBlendAlpha", (float)BlendMode.Zero);
                    material.SetFloat("_ZWrite", 1f);
                    material.renderQueue = (int)RenderQueue.Geometry;
                    break;
            }
        }
    }
}
