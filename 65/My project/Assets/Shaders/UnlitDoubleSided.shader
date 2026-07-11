// Unlit, textured, TWO-SIDED (Cull Off), OPAQUE-CUTOUT. Draws both faces — needed for open/thin
// avatar geometry like hair, whose inward-facing polygons get culled by default (Cull Back) and vanish.
// Official (RE render/008 Mesh_applyRenderStates): avatars draw with ZWrite ON, no alpha-blend, single
// closed shells — the back of the head is occluded by depth, never blended-through.
// Why cutout + forced-opaque alpha (not plain "Unlit/Texture"): the shop / portrait preview renders the
// avatar into a TRANSPARENT-cleared RenderTexture (clear alpha=0) that a RawImage composites over the UI.
// A hair DDS carries a cutout alpha channel; writing that alpha straight into the RT makes the strands
// semi-transparent → the shop background shows THROUGH the hair (the "頭髮後面透明" bug). So: clip the gap
// texels (they stay transparent = cleared bg) and force every DRAWN texel to alpha=1 (fully opaque in the
// RT). In the opaque in-scene framebuffer (dancer/lobby) alpha is ignored, so this is a no-op there and
// only removes the dark cutout halos — strictly an improvement, never a regression.
Shader "Sdo/UnlitDoubleSided"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0.3
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100
        Cull Off
        ZWrite On

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
                clip(c.a - _Cutoff);   // drop hair-strand gaps (stay transparent in the RT)
                c.a = 1;               // everything drawn is FULLY OPAQUE → no bg shows through in the RT
                return c;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Texture"
}
