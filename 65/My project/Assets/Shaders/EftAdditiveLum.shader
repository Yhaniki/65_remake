// Additive particle shader that colours by the DIFFUSE tint, using the TEXTURE only as a luminance/shape mask.
// Used for the 200/300COMBO "burst" world-quad (tex4 = aef_1_07): the texture is an ORANGE radial-spike sprite but
// its emitter DIFFUSE is BLUE (154,159,255). The engine's MODULATE (texture×diffuse) would muddy it to brown; the
// official reads as a BLUE radial burst, so here colour = tint.rgb × luminance(texture) (shape from the sprite, hue
// from the diffuse). Additive, ZWrite off, double-sided — same blend as the faithful particle path otherwise.
Shader "Sdo/EftAdditiveLum"
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
                fixed3 t = tex2D(_MainTex, i.uv).rgb;
                fixed lum = max(t.r, max(t.g, t.b));          // texture = shape/intensity only
                fixed3 col = _TintColor.rgb * lum;            // hue from the DIFFUSE tint (blue), not the texture
                return fixed4(col, lum * _TintColor.a);
            }
            ENDCG
        }
    }
}
