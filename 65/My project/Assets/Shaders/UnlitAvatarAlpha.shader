// Translucent, TWO-SIDED avatar accessory shader — for glasses (眼鏡) whose lens must be see-through.
// Official glasses textures are DXT3 (BC2) with a REAL alpha channel: opaque frame/stars (a=1), a TRANSLUCENT
// tinted LENS (a≈0.3-0.6, you see the eyes through it), and a cut-out surround (a=0). Rendering them with plain
// "Unlit/Texture" (opaque, alpha ignored) turns the lens into a solid colour blob covering the eyes — the
// "紫色星光" 眼鏡「去背/透明度」bug. This shader alpha-blends so the lens tints the face behind it and the
// a=0 surround stays invisible (proper 去背).
//   - Cull Off: lens/temple geometry is thin & open → draw both faces (same reason hair uses UnlitDoubleSided).
//   - ZWrite Off + Queue=Transparent: draw AFTER the opaque head so the lens blends OVER the face.
//   - Separate alpha blend (One OneMinusSrcAlpha): the shop / portrait preview renders into a TRANSPARENT-cleared
//     RenderTexture composited over the UI. Keeping the alpha channel at max preserves the opaque head backing
//     (dstA=1 behind the lens → result stays 1); only lens texels that overhang the face composite translucently.
// DXT1 glasses (no real alpha → decoded opaque a=1) render identically to the old opaque path, so they don't
// regress. Verbatim-faithful: the original D3D9 glasses drew alpha-blended from their DXT3 art.
Shader "Sdo/UnlitAvatarAlpha"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100
        Cull Off
        ZWrite Off
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

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv);   // rgb + real DXT3 alpha → blended over the face by the Blend state
            }
            ENDCG
        }
    }
    Fallback "Unlit/Transparent"
}
