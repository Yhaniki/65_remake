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
        // 光錐貼圖的 alpha 在 quad 邊界仍有殘值(4-bit 的一階 = 17/255)，加法下就是一條硬階。
        // 抬黑點把它壓到 0，核心幾乎不受影響。0.09 略高於一個 4-bit 階(0.067)。
        _AlphaFloor ("Alpha black point", Range(0,0.3)) = 0.09
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
            float _AlphaFloor;

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
                // ALPHA FLOOR — this is what actually kills the razor edge. The beam texture is a DXT3 atlas whose
                // cone runs flush to the quad's UV rect: SCN0019's dengzhu_ still has alpha 17 (= one 4-bit step,
                // 6.7%) in the very column the quad ends on. Additive over a dark stage that is a ~45-luminance
                // jump in ONE pixel — measured on a capture — and no amount of sideways _Spread can fix it,
                // because the quad has no spare area for the halo to live in. Lifting the black point maps that
                // residual step to 0 and turns the neighbouring 34/51/68 texels into a ramp that starts at zero,
                // while the bright core (0.4→0.35, 0.87→0.86) is barely touched.
                c.a = saturate((c.a - _AlphaFloor) / max(1e-4, 1.0 - _AlphaFloor));
                c *= _Color;
                c.rgb *= i.col.rgb;
                return c;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Transparent"
}
