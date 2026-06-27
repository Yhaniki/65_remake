// Unlit, textured, GPU-INSTANCING capable. Same look as the built-in "Unlit/Texture"
// (Cull Back, _MainTex × _Color) but with the instancing macros, so many copies that
// share one mesh + one material (the mapobj prop instances — box ×256, deng ×72, the
// room/saloon prop walls) collapse into a handful of GPU-instanced draw calls instead
// of one draw per copy. The per-instance variation is the transform (unity_ObjectToWorld,
// instanced for free); material properties stay uniform across the batch. Legacy built-in
// Unlit/* shaders have no instancing variant under URP, which is why this exists.
Shader "Sdo/UnlitInstanced"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100
        Cull Back

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;           // per-vertex baked scene lighting (D3DCOLOR diffuse, FVF 0x142) — white when none
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 col : COLOR0;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);     // selects this instance's unity_ObjectToWorld
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.col = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // × baked vertex lighting (SCN0008 ZIMU rune A/B 5× contrast, night-dark props). The original D3D9
                // engine is gamma-unaware: it multiplies the sRGB texture bytes by the sRGB per-vertex diffuse bytes
                // and writes straight to the (gamma) framebuffer. This project renders LINEAR, so tex2D already returns
                // LINEAR and the output re-encodes linear→sRGB — which GAMMA-BRIGHTENS the dark baked diffuse and
                // crushes the A/B contrast (both panels read near-equal/too-bright). Replicate D3D9 exactly: do the
                // multiply in GAMMA space (tex back to sRGB × vertex × tint) then re-encode to linear. NO-OP for the
                // white-vertex props (box/deng/walls, i.col=1) so no other scene shifts; only baked-colour props move.
                fixed4 c = tex2D(_MainTex, i.uv);
                #ifdef UNITY_COLORSPACE_GAMMA
                c.rgb *= _Color.rgb * i.col.rgb;
                #else
                c.rgb = GammaToLinearSpace(LinearToGammaSpace(c.rgb) * _Color.rgb * i.col.rgb);
                #endif
                c.a *= _Color.a * i.col.a;
                return c;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Texture"
}
