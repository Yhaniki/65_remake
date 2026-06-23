// Unlit, textured, GPU-INSTANCING capable, ALPHA-BLENDED. The transparent twin of Sdo/UnlitInstanced:
// same instancing (many shared-mesh prop copies batch into a few draws) but it respects the texture alpha
// so DXT3/DXT5 stage props with cut-out / transparent regions ("去背") composite correctly instead of
// painting their transparent areas solid. The original D3D path alpha-BLENDS these props (material flag
// 0x20000), not alpha-tests, so this uses SrcAlpha/OneMinusSrcAlpha. Cull Off (thin decals/billboards are
// viewed from either side as the camera orbits), ZWrite Off + Transparent queue (drawn over opaque stage).
Shader "Sdo/UnlitInstancedAlpha"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 100
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

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
                fixed4 c = tex2D(_MainTex, i.uv) * _Color;
                c.rgb *= i.col.rgb;             // × baked vertex lighting (RGB only; keep texture alpha for blending)
                return c;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Transparent"
}
