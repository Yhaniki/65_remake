// Alpha-BLEND garment shader — for a material whose official .msh flag marks it transparent (flags & 0x3f != 0; see
// MshLoader.MatFlagTransparentMask / SdoAvatarBuilder.OfficialAlphaMode).
//
// WHAT THE RETAIL CLIENT DOES (sdo.bin FUN_0042cda0 + the device state it never touches):
//   • ALPHABLENDENABLE = TRUE, alpha = texture.α × TFACTOR.α (TFACTOR is opaque in normal play) → straight alpha blend.
//   • ZWRITEENABLE is never disabled, CULLMODE stays single-sided (CCW), and flagged materials are collected into a
//     DEFERRED list drawn after the opaque batch — sorted, i.e. BACK-TO-FRONT. That combination is why the client both
//     accumulates its stacked sheer layers (a garment ships several: 024976 金姬兰/Flower Lace = four lace materials
//     over a liner, which is where its dense look comes from) AND never shows a garment through its own far side.
//
// ⚠ SINGLE PASS ONLY — DO NOT ADD A DEPTH PREPASS.
//   A ColorMask-0 depth prepass pass was added twice to stop a garment showing its own far side. Both times it made
//   EVERY blended garment vanish completely: with the 2-pass shader nothing is drawn at all. Verified by rendering the
//   same dress with identical geometry/textures under both shaders — Unlit/Texture drew the full dress, the 2-pass
//   sheer shader drew zero pixels (GarmentRendersVisibleTests.Bisect_WhyBlendedDressVanishes writes the PNGs).
//   That one change produced a long chain of confusing reports: 「紅色不羈牛仔 是透明的」, a shop grid full of empty
//   cards, and 「身體透明」 (the dress gone, so only bare skin showed). GarmentFlickerFixTests pins passCount == 1.
//   ZWrite stays Off so a garment's stacked lace layers ACCUMULATE (that is where the official density comes from);
//   if self-see-through ever needs fixing, solve it WITHOUT a second pass.
//
//   Separate alpha blend (One OneMinusSrcAlpha) is a remake-only necessity: the shop card / preview render into a
//   TRANSPARENT-cleared RenderTexture composited over the UI, so the RT's alpha must stay meaningful. The colour result
//   over an opaque background is identical to the client's.
Shader "Sdo/UnlitAvatarSheer"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        // α' = 1-(1-α)^_Density is exactly the alpha of the fabric drawn over ITSELF _Density times, so 2.0 = "two
        // layers" (the dress's translucent weave: mean α 0.58 → 0.73). The client stacks real layers; we approximate.
        // 1.0 (raw single layer) reads far too sheer against the client — verified twice by the user on 024976.
        _Density ("Fabric density (alpha power; 2=official stacked look, 1=single raw layer)", Range(1,4)) = 2.0
        // Is this material REAL SHEER FABRIC (lace/organza — a mid-alpha weave) or just SOLID cloth whose official flag
        // happens to mark it transparent (an alpha channel that only cuts the garment's silhouette out)? The shader
        // itself never reads it; the transparent-RT SHOP/WARDROBE CARD does (ShopScreen.ApplyCardCutoutShader): a card
        // hides the body, so single-sided solid cloth leaves a HOLE where the neckline should show the garment's own
        // far side (使用者 001766 Skirt Suit:「領口後面應該是有衣服的」) — those swap to the two-sided cutout shader,
        // while real sheer stays here so the card keeps its translucency. Set by the builders on EVERY sheer material;
        // the default 1 (= treat as sheer, leave alone) keeps any path that forgets to set it on today's behaviour.
        _SheerFabric ("Real sheer fabric (1=lace/organza weave, 0=solid cut-out cloth)", Float) = 1
        // Per-material render state — THIS is how a solid cut-out garment avoids showing its own far side, instead of
        // a second pass (see the warning above). SdoAvatarBuilder.ApplySheerMaterialState sets both from _SheerFabric:
        //   • real sheer weave  → ZWrite 0 + Cull Back: the garment's stacked lace layers accumulate (density).
        //   • solid cut-out cloth → ZWrite 1 + Cull Off: it behaves like the opaque cloth it visually is — the nearest
        //     surface wins so a low neckline can't show the garment's inside as see-through
        //     (使用者:「低領口衣服背面會變成透明穿透」), and two-sided drawing fills that neckline with the garment's
        //     own far side instead of a hole.
        [Enum(Off,0,Front,1,Back,2)] _CullMode ("Cull", Float) = 2
        [Enum(Off,0,On,1)] _ZWriteMode ("ZWrite", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100
        Cull [_CullMode]

        ZWrite [_ZWriteMode]
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Density;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                // Fully-transparent texels must NOT reach the depth buffer. With _ZWriteMode on they would still write
                // depth and occlude everything behind — a skirt slit then punched a hole straight to the background
                // (使用者:「腳上破一個洞」). Clipping only the true zeros keeps every authored translucency intact.
                clip(c.a - 0.004);
                c.a = saturate(1.0 - pow(saturate(1.0 - c.a), _Density));   // denser fabric; a=0 stays 0, a=1 stays 1
                return c;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Transparent"
}
