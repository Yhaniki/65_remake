// Solid cut-out for a SpriteRenderer (the 3D-note LONG end-cap). Same idea as Sdo/NoteCutout but sprite-compatible:
// _MainTex is [PerRendererData] so the SpriteRenderer feeds the (cropped) cap sprite's texture, and the sprite mesh's
// UVs already map to the cropped sub-rect. We clip texels below _Cutoff (the keyed background + the soft/keyed edge →
// no white fringe "白邊") and force the kept texels fully opaque so the cap reads as a SOLID object, matching the body.
Shader "Sdo/SpriteCutout"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0.4
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" "CanUseSpriteAtlas"="True" }
        Cull Off
        ZWrite Off
        Blend Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;
            fixed _Cutoff;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                clip(c.a - _Cutoff);   // drop the keyed background + soft edge → no white fringe
                c.a = 1;               // solid — the kept cap texels replace what's behind
                return c;
            }
            ENDCG
        }
    }
    Fallback "Sprites/Default"
}
