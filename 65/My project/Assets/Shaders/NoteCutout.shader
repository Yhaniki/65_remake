// Solid cut-out for the 3D-note glyphs (NOTES/JUDGELINE/LONG). The official 3D notes are SOLID objects that OCCLUDE
// each other — NOT additive glow (which showed the long THROUGH the note). This renders the arrow's opaque texels
// (alpha >= _Cutoff) as fully-opaque pixels that REPLACE whatever is behind, and clips the transparent background +
// the anti-aliased/keyed edge (< _Cutoff) so there is no black box and no semi-transparent white fringe ("白邊").
// Kept in the TRANSPARENT queue with ZWrite Off so it sorts with the 2D play-field sprites by sortingOrder (note
// order 6 over long order 3 → the note covers the long) WITHOUT writing depth that would occlude the 2D HUD.
// Two-sided (Cull Off) so the flat arrow mesh shows regardless of winding after the flatten rotation.
Shader "Sdo/NoteCutout"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0.4
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100
        Cull Off
        ZWrite Off
        Blend Off

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
            fixed _Cutoff;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                clip(c.a - _Cutoff);   // drop the (keyed) background + soft edge → no box, no white fringe
                c.a = 1;               // solid — the kept arrow texels replace what's behind
                return c;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Transparent Cutout"
}
