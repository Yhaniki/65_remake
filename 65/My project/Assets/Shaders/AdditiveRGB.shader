// Pure additive (Blend One One) RGB display of a texture — used to composite the ShowTime energy-gauge
// RenderTexture (the POWER_Y/B/R.EFT effect rendered by its own perspective camera, official-faithful) onto the
// bar channel: the RT background is black, so One-One addition shows only the bright plasma/glow and leaves the
// frame untouched, regardless of the RT's alpha channel.
Shader "Sdo/AdditiveRGB"
{
    Properties { _MainTex ("Texture", 2D) = "black" {} _Tint ("Tint", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend One One
        Cull Off ZWrite Off Lighting Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            sampler2D _MainTex; float4 _MainTex_ST; fixed4 _Tint;
            v2f vert (appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = TRANSFORM_TEX(v.uv, _MainTex); return o; }
            fixed4 frag (v2f i) : SV_Target { return tex2D(_MainTex, i.uv) * _Tint; }
            ENDCG
        }
    }
}
