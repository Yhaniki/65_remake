// GAMMA-CORRECT ADDITIVE glow for the SCN0008 kekkai DISC (tex69) + MW runes (tex117). Used ONLY by these two
// (EftEffect tex69/117 path), so it's safe to make D3D9-faithful here without touching other effects.
//
// Root cause of the "太亮/過曝白團": the original D3D9 is gamma-unaware — it adds (sRGB texel × diffuse × ch1) straight
// into the GAMMA framebuffer. This project renders LINEAR: tex2D returns LINEAR(texel) and `Blend SrcAlpha One` mixes
// with srcAlpha=ch1 in LINEAR space, then the output re-encodes linear→sRGB — which gamma-BRIGHTENS the mid pulse
// (ch1=0.5, texel 0.8 → displays 0.58 vs the original's 0.40) and blows the overlapping rings white.
//
// Fix: PREMULTIPLIED additive (Blend One One) with the texel×tint×ch1 product done in GAMMA space, then re-encoded to
// linear. Single layer over black now displays exactly s_gamma×tint×ch1 = the original. The ch1 pulse (128↔255) and the
// black texture background (→0, stays see-through) behave verbatim; cyan glows, no flat translucent blend. SetCol drives
// _TintColor (rgb=ch2/3/4, a=ch1). Gamma builds skip the round-trip (already gamma).
Shader "Sdo/EftAlpha"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _TintColor ("Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend One One          // PREMULTIPLIED additive: ch1 (srcAlpha) is folded into rgb below, in gamma space
        Cull Off
        ZWrite Off
        Lighting Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            sampler2D _MainTex; float4 _MainTex_ST; fixed4 _TintColor;
            v2f vert (appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = TRANSFORM_TEX(v.uv, _MainTex); return o; }
            fixed4 frag (v2f i) : SV_Target
            {
                // D3D9 path (decompiled): additive contribution = (texel × diffuse.rgb) × srcAlpha, where srcAlpha = ch1
                // (kekkai/MW textures are opaque RGB, tex.a=1). Premultiply ch1 into rgb and add via Blend One One, doing
                // the whole product in GAMMA space so it matches the gamma-framebuffer original (no mid-pulse brighten).
                fixed3 t = tex2D(_MainTex, i.uv).rgb;
                fixed3 o;
                #ifdef UNITY_COLORSPACE_GAMMA
                o = t * _TintColor.rgb * _TintColor.a;
                #else
                o = GammaToLinearSpace(LinearToGammaSpace(t) * _TintColor.rgb * _TintColor.a);
                #endif
                return fixed4(o, _TintColor.a);
            }
            ENDCG
        }
    }
}
