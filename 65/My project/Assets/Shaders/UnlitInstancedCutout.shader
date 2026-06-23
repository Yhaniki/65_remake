// Unlit, textured, GPU-INSTANCING capable, ALPHA-TEST (cutout), SOLID. For VOLUMETRIC mapobj props whose alpha is
// a hard cut-out on a 3-D body (e.g. the SCN0006 carousel carriages): the alpha-BLENDED twin (Sdo/UnlitInstancedAlpha,
// Cull Off + ZWrite Off) made them see-through — back faces showed through the front and nothing wrote depth
// ("穿透 / 雙面材質"). This clips transparent texels (no depth-write artefact), Cull Back (drop back faces) and
// ZWrite On (the solid body occludes itself) — matching the original's solid alpha props. Flat billboards/decals
// keep the blended shader.
Shader "Sdo/UnlitInstancedCutout"
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
        Cull Off
        ZWrite On

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
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 col : COLOR0; };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed _Cutoff;

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.col = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * _Color;
                clip(c.a - _Cutoff);            // clip on TEXTURE alpha (vertex alpha is 255, RGB-only lighting)
                c.rgb *= i.col.rgb;             // × baked vertex lighting (SCN0008 night-dark props)
                return c;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Transparent Cutout"
}
