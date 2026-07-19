// PREMULTIPLIED-ALPHA sprite. Pair with a texture whose RGB is premultiplied by alpha
// (SdoExtracted.LoadAnSoloPremultiplied). A straight-alpha sprite smears a pale 白邊 halo when MAGNIFIED because
// bilinear interpolates colour and coverage (alpha) SEPARATELY across each glyph's opaque→transparent edge: the
// transparent matte keeps a bright RGB while its alpha drops, so the bright candy bevel / white matte leak outward as
// a semi-transparent fringe (worst on flat letter tops like "U"). With premultiplied colour the transparent texels are
// (0,0,0,0), so interpolation only fades COVERAGE — the edge stays the glyph colour and the halo can't form, while the
// edge stays smooth at any scale (unlike point filtering). Used by the result YOU WIN / YOU LOSE banner, which zooms
// screen-width→1 and is stretched from the 800×600 design to the window.
Shader "Sdo/SpritePremultiply"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="True" }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha       // premultiplied-alpha OVER (src already carries rgb*a)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata_t { float4 vertex : POSITION; float4 color : COLOR; float2 texcoord : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; fixed4 color : COLOR; float2 texcoord : TEXCOORD0; };
            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            v2f vert (appdata_t IN)
            {
                v2f OUT;
                OUT.vertex = UnityObjectToClipPos(IN.vertex);
                OUT.texcoord = TRANSFORM_TEX(IN.texcoord, _MainTex);
                OUT.color = IN.color * _Color;
                return OUT;
            }
            fixed4 frag (v2f IN) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, IN.texcoord);   // premultiplied (rgb already × a)
                c.rgb *= IN.color.rgb;                      // colour tint (default white = no-op)
                c *= IN.color.a;                            // vertex/tint alpha scales premult rgb AND coverage
                return c;
            }
            ENDCG
        }
    }
}
