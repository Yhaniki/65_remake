// Unlit, textured, TWO-SIDED, ALPHA-BLENDED overlay — for the FIFA crowd / spotlight props whose textures are
// alpha-cutout sprites (people on a transparent/black background) cycled as a frame sequence (see MapobjTexAnimator).
// The opaque Sdo/UnlitInstanced renders those sprites' transparent regions as solid, so the stands looked empty /
// black at night; this respects the texture alpha so only the people (or light beams) show. Cull Off (billboards are
// thin, viewed from either side), ZWrite Off + Transparent queue (composited over the opaque stage).
Shader "Sdo/UnlitOverlay"
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
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv) * _Color;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Transparent"
}
