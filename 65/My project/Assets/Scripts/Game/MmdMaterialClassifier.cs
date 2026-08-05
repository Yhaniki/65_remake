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

        /// <summary>
        /// 一個 texel 要低於這個 alpha，才算得上「作者畫的半透明」。
        ///
        /// 這條線本來訂在 250（≒0.98），而 MMD 的貼圖幾乎每一張都會因此被誤判：作者存 PNG 時 alpha 通道
        /// 常常留著一整片 225~254 的雜訊/漸層（YYB 初音的 C.png 有 27% 落在那裡，而它連一個全透明像素都沒有），
        /// 那不是透明度，是沒清乾淨的通道。0.9 以上的 alpha 疊起來與不透明肉眼分不出來，卻會把整件衣服推進
        /// 半透明佇列（ZWrite 關掉 → 同一個 SkinnedMeshRenderer 裡改用材質順序畫 → 後面的頭髮蓋過前面的肩膀）。
        /// </summary>
        public const byte NearOpaqueAlpha = 229;   // 0.9

        /// <summary>低於這個 alpha 算「洞」（真的被裁掉的地方），不算半透明。</summary>
        public const byte HoleAlpha = 16;

        /// <summary>
        /// 半透明材質的 alpha 裁切線。<b>半透明也要寫深度</b>（見 <see cref="Apply"/>），所以全透明的 texel
        /// 必須丟掉，否則它們會在深度緩衝裡留下看不見的牆。取 1/255＝只丟真正的 0。
        /// </summary>
        public const float BlendClipCutoff = 0.004f;

        /// <summary>裁切（cutout）材質的 alpha 裁切線。</summary>
        public const float CutoutClipCutoff = 0.5f;

        /// <summary>這個 texel 是「洞」嗎（<see cref="HoleAlpha"/> 以下）。</summary>
        public static bool IsHole(byte alpha) => alpha < HoleAlpha;

        /// <summary>這個 texel 是「作者畫的半透明」嗎（洞以上、<see cref="NearOpaqueAlpha"/> 以下）。</summary>
        public static bool IsTranslucent(byte alpha) => alpha >= HoleAlpha && alpha < NearOpaqueAlpha;

        /// <param name="translucentFraction">這個材質**自己用到的那塊 UV** 裡，作者畫的半透明 texel 佔比
        /// （<see cref="IsTranslucent"/>）。整張貼圖的統計是不能用的：一張 atlas 是好幾個材質共用的，
        /// 別人那半邊的洞跟雜訊會被算到這個材質頭上。</param>
        /// <param name="transparentFraction">同一塊 UV 裡的「洞」佔比（<see cref="IsHole"/>）。</param>
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
                    material.SetFloat("_Cutoff", CutoutClipCutoff);
                    material.SetFloat("_SrcBlend", (float)BlendMode.One);
                    material.SetFloat("_DstBlend", (float)BlendMode.Zero);
                    material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                    material.SetFloat("_DstBlendAlpha", (float)BlendMode.Zero);
                    material.SetFloat("_ZWrite", 1f);
                    material.renderQueue = (int)RenderQueue.AlphaTest;
                    break;

                case MmdMaterialRenderMode.Blend:
                    // 🔴 半透明**也要寫深度** —— MMD 自己就是這樣畫的（固定管線全程 ZWRITEENABLE=TRUE，
                    // 只用一個 ALPHATEST 丟掉 alpha=0），所以 MMD 模型的材質順序本來就是照「會寫深度」來排的。
                    // 這裡關掉 ZWrite 會壞在一個很難看出根因的地方：整具身體是**一個** SkinnedMeshRenderer，
                    // Unity 對同一個 renderer 內的 submesh 不做距離排序，同一個 queue 就照材質順序畫。於是
                    // 「後面的材質」永遠蓋過「前面的材質」，與誰在前誰在後無關 —— YYB 初音的雙馬尾(mat 22)
                    // 因此蓋在袖子(mat 11~14)上（肩膀看起來透明），髮影平面(mat 21)蓋在瀏海(mat 19)上
                    // （頭頂一塊陰影）。寫了深度，這兩件都由深度測試自己解決。
                    // 代價是全透明的 texel 會留下看不見的牆 → 一定要配 clip（BlendClipCutoff）。
                    material.SetOverrideTag("RenderType", "Transparent");
                    material.SetFloat("_AlphaClip", 1f);
                    material.SetFloat("_Cutoff", BlendClipCutoff);
                    material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                    material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                    material.SetFloat("_ZWrite", 1f);
                    material.renderQueue = (int)RenderQueue.Transparent;
                    break;

                default:
                    material.SetOverrideTag("RenderType", "Opaque");
                    material.SetFloat("_AlphaClip", 0f);
                    material.SetFloat("_Cutoff", CutoutClipCutoff);
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
