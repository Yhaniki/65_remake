// Additive glow with a world-space RECT clip — the ShowTime energy-bar head glow. The official gauge renders its
// POWER_Y/B/R head-glow emitters (NAGA00 star + AEF_4_02 halo) through a dedicated camera whose viewport/scissor is
// the bar channel {22,14,488,15}, so the big flares are CROPPED to a thin horizontal wash. This shader reproduces
// that crop: same additive math as "Legacy Shaders/Particles/Additive" (col = 2·vertexColor·_TintColor·tex,
// Blend SrcAlpha One), plus clip() on world X AND Y against the channel rect.
// (No LightMode tag → URP renders it as SRPDefaultUnlit, same as Sdo/HpGlowClip.)
Shader "Sdo/GlowClipRect"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _TintColor ("Tint", Color) = (0.5,0.5,0.5,0.5)
        _ClipMinX ("Clip Min X (world)", Float) = -100000
        _ClipMaxX ("Clip Max X (world)", Float) =  100000
        _ClipMinY ("Clip Min Y (world)", Float) = -100000
        _ClipMaxY ("Clip Max Y (world)", Float) =  100000
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Blend SrcAlpha One
        Cull Off
        ZWrite Off
        Lighting Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; float2 wxy : TEXCOORD1; };
            sampler2D _MainTex; float4 _MainTex_ST; fixed4 _TintColor;
            float _ClipMinX; float _ClipMaxX; float _ClipMinY; float _ClipMaxY;
            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                float4 w = mul(unity_ObjectToWorld, v.vertex);
                o.wxy = w.xy;                          // world XY for the rect clip
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                clip(i.wxy.x - _ClipMinX);
                clip(_ClipMaxX - i.wxy.x);
                clip(i.wxy.y - _ClipMinY);
                clip(_ClipMaxY - i.wxy.y);
                return 2.0 * i.color * _TintColor * tex2D(_MainTex, i.uv);   // == Particles/Additive
            }
            ENDCG
        }
    }
}
