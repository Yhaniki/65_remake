// Legacy TextMesh normally uses GUI/Text Shader, whose ZTest Always makes a projected HUD name draw through
// every dancer. Gameplay nameplates use this material inside SceneCam instead: alpha remains blended (and does
// not write depth, so the 16 outline copies can coexist), but opaque avatar depth can reject rear-name pixels.
Shader "Sdo/DepthText"
{
    Properties
    {
        [PerRendererData] _MainTex ("Font / Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _UseTextureRgb ("Use Texture RGB", Float) = 1
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" }
        Lighting Off
        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            sampler2D _MainTex;
            fixed _UseTextureRgb;
            float4 _MainTex_ST;
            fixed4 _Color;

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.color = input.color * _Color;
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 sample = tex2D(_MainTex, input.uv);
                fixed3 sourceRgb = lerp(fixed3(1, 1, 1), sample.rgb, saturate(_UseTextureRgb));
                return fixed4(sourceRgb * input.color.rgb, sample.a * input.color.a);
            }
            ENDCG
        }
    }
}
