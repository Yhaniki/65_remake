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
                return tex2D(_MainTex, i.uv) * _Color * i.col;   // × baked vertex lighting (SCN0008 night-dark props)
            }
            ENDCG
        }
    }
    Fallback "Unlit/Texture"
}
