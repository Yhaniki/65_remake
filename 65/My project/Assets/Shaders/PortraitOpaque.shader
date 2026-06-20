// Result-screen head-portrait shader. Renders the isolated idle avatar into a TRANSPARENT-cleared RenderTexture so
// the portrait composites cleanly over the panel: every DRAWN pixel is forced fully opaque (alpha = 1), and only the
// hair/cutout gaps (texture alpha below _Cutoff) are discarded — so they stay transparent (the cleared background)
// instead of bleeding semi-transparent body/hair into the panel. Two-sided (Cull Off) for open hair geometry.
Shader "Sdo/PortraitOpaque"
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
                clip(c.a - _Cutoff);   // drop hair gaps / cutout (keeps them transparent in the RT)
                c.a = 1;               // everything else is FULLY OPAQUE → clean alpha for the transparent quad
                return c;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Texture"
}
