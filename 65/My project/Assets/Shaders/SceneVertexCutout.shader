// Unlit, textured, × VERTEX COLOR, alpha-cutout — for the stage SCENE.MSH. The SDO scene mesh is FVF 0x142
// (pos + DIFFUSE + uv): its per-vertex diffuse colour is BAKED LIGHTING (e.g. SCN0008 Egyptian tomb darkened to
// ~0.8 with black patches; SCN0005 christmas tinted blue for night). The old path used "Unlit/Transparent Cutout"
// which ignores vertex colour → every scene rendered at full texture brightness ("太亮"). This multiplies the
// texture by the vertex colour so the baked darkening/tint shows. Alpha-cutout (clip) keeps the DXT3 audience
// billboards' transparent background out.
//
// Cull Back (SINGLE-SIDED) — matches the original D3D backface cull and the SceneLoader's verbatim winding
// (floor faces +Y up, ceiling -Y down, walls face inward — all verified). The earlier `Cull Off` here silently
// DEFEATED that single-sided fix: it re-rendered the back faces of walls/columns, so any camera passing BEHIND
// an inward-facing column saw its back face and was BLOCKED (e.g. SCN0017 railway's opening dolly threading the
// platform pillars, and fixed cam5 behind the back columns). The original culls those back faces -> sees through.
Shader "Sdo/SceneVertexCutout"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0.5
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        LOD 100
        Cull Back

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed _Cutoff;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                clip(c.a - _Cutoff);
                // baked vertex lighting/tint. The original D3D9 engine is gamma-unaware: it multiplies the sRGB texture
                // bytes by the sRGB baked-diffuse bytes and writes the result straight to the (gamma) framebuffer. This
                // project renders in LINEAR colour space, so tex2D already returns LINEAR and the framebuffer re-encodes
                // linear->sRGB on output — which GAMMA-BRIGHTENS the very dark baked diffuse (e.g. SCN0008's near-black
                // floor (2,20,10)) into a visible dark GREEN. Replicate D3D9 exactly: do the multiply in GAMMA space
                // (tex back to sRGB × vertex × tint) then re-encode to linear so the framebuffer's output equals the
                // original gamma product (near-black, not green). Gamma builds skip this (the multiply is already gamma).
                #ifdef UNITY_COLORSPACE_GAMMA
                c.rgb *= i.color.rgb * _Color.rgb;
                #else
                c.rgb = GammaToLinearSpace(LinearToGammaSpace(c.rgb) * i.color.rgb * _Color.rgb);
                #endif
                return c;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Transparent Cutout"
}
