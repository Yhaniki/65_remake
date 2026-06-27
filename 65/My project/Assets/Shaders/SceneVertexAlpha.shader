// Scene mesh shader for DDS textures with real soft alpha.
// Keeps the SCENE.MSH vertex diffuse lighting, but alpha-blends instead of alpha-testing.
Shader "Sdo/SceneVertexAlpha"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _Cull ("Cull", Float) = 2   // 2=Back (default single-sided); 0=Off (double-sided thin props). Set by SceneLoader.
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 100
        Cull [_Cull]
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

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
                #ifdef UNITY_COLORSPACE_GAMMA
                c.rgb *= i.color.rgb * _Color.rgb;
                #else
                c.rgb = GammaToLinearSpace(LinearToGammaSpace(c.rgb) * i.color.rgb * _Color.rgb);
                #endif
                c.a *= _Color.a;
                return c;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Transparent"
}
