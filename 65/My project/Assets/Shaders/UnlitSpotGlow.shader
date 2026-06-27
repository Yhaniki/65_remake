// Soft searchlight beam (SCN0016 spotlights JIGUANG1/2/3). guang1_.dds has a NARROW alpha falloff (~3 texels)
// across the beam's width, so a plain additive draw gives the beam hard left/right edges. The official softens
// these with a screen-space glow we don't run. Instead, soften it locally: blur the texture along U (the beam's
// WIDTH axis — the long edges run along V) so the light spreads outward on both sides and the edge becomes a
// gradual falloff. Pure per-material effect — nothing else in the scene is touched. Additive (SrcAlpha One),
// ZWrite Off, Cull Off, like the EFT glow path. _Spread = how far (in UV) the light bleeds sideways.
Shader "Sdo/UnlitSpotGlow"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _Spread ("Sideways spread (UV)", Range(0,0.4)) = 0.2
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha One
        Cull Off
        ZWrite Off
        Lighting Off

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
                fixed4 color : COLOR;
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
            float _Spread;

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
                // Keep the INTERIOR untouched; only soften the hard EDGE. Take the original alpha and a blurred
                // (spread-sideways) alpha, then alpha = max(original, blurred): inside the beam the original wins
                // (the blur is dimmer there, so the bright core is unchanged); just OUTSIDE the beam, where the
                // original is 0 and cuts off sharply, the spread halo fills in a gradual falloff. The additive
                // core therefore renders identically to before — a soft glow is only ADDED at the edges.
                fixed4 orig = tex2D(_MainTex, i.uv);
                float blurA = 0.0;
                float wsum = 0.0;
                [unroll]
                for (int k = -6; k <= 6; k++)
                {
                    float fk = (float)k;
                    float w = exp(-fk * fk / 18.0);           // flat-ish: outer taps still contribute → wide spread
                    float2 uv = i.uv;
                    uv.x = clamp(uv.x + fk * (_Spread / 6.0), 0.0, 1.0);
                    blurA += tex2D(_MainTex, uv).a * w;
                    wsum += w;
                }
                blurA /= wsum;
                fixed4 c;
                c.rgb = orig.rgb;                              // interior colour unchanged
                c.a = max(orig.a, blurA);                     // core kept; soft halo only added where the edge cut off
                c *= _Color;
                c.rgb *= i.col.rgb;
                return c;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Transparent"
}
