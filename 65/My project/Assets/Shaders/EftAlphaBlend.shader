// ADDITIVE glow with a VISIBLE alpha PULSE for the SCN0008 kekkai DISC (tex69). The faithful Particles/Additive path
// does `2× tex × _TintColor`, and the kekkai texture is opaque (tex.a=1), so its SrcAlpha = 2×_TintColor.a clamps to 1
// for the whole pulse → the disc is stuck at full brightness (no "光變亮變暗", looks solid = "沒有透明度"). This shader
// drops the 2× and derives SrcAlpha from the texture LUMINANCE × the ch1 pulse, so the glow brightness actually rises
// and falls (dim→bright→dim) and the dark texture background contributes nothing (stays see-through). Still additive
// (SrcAlpha One) so the disc GLOWS cyan like the official, not a faint flat translucent blend. _TintColor set by SetCol.
Shader "Sdo/EftAlpha"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _TintColor ("Tint", Color) = (1,1,1,1)
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
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            sampler2D _MainTex; float4 _MainTex_ST; fixed4 _TintColor;
            v2f vert (appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = TRANSFORM_TEX(v.uv, _MainTex); return o; }
            fixed4 frag (v2f i) : SV_Target
            {
                // EXACT engine path (decompiled): COLOR = texture × diffuse(_TintColor.rgb); SRCALPHA = diffuse.a (the
                // kekkai texture is opaque RGB so tex.a=1 ⇒ alpha = ch1 only). Additive (SrcAlpha One): the contribution
                // = src.rgb × src.a = (tex × tint.rgb) × tint.a, so the ch1 pulse (128↔255) scales the glow brightness
                // 2×, and the BLACK background contributes nothing (src.rgb=0) = naturally see-through. No lum, no 2×.
                fixed3 t = tex2D(_MainTex, i.uv).rgb;
                return fixed4(t * _TintColor.rgb, _TintColor.a);
            }
            ENDCG
        }
    }
}
