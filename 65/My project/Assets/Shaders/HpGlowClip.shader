// HP-bar leading-edge glow (HpEft) with a horizontal world-space CLIP. Same additive look as the stock
// "Legacy Shaders/Particles/Additive" (col = 2·vertexColor·_TintColor·tex, Blend SrcAlpha One) so brightness is
// unchanged — the ONLY addition is clip(): fragments whose WORLD X falls outside [_ClipMinX,_ClipMaxX] are
// discarded, so when HP runs low the glow can no longer spill past the bar frame's left/right ends.
// (No LightMode tag → URP renders it as SRPDefaultUnlit, same as Sdo/EftAdditiveLum.)
Shader "Sdo/HpGlowClip"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _TintColor ("Tint", Color) = (0.5,0.5,0.5,0.5)
        _ClipMinX ("Clip Min X (world)", Float) = -100000
        _ClipMaxX ("Clip Max X (world)", Float) =  100000
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
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; float wx : TEXCOORD1; };
            sampler2D _MainTex; float4 _MainTex_ST; fixed4 _TintColor; float _ClipMinX; float _ClipMaxX;
            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                o.wx = mul(unity_ObjectToWorld, v.vertex).x;   // world X for the clip test
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                clip(i.wx - _ClipMinX);   // cut everything left of the frame
                clip(_ClipMaxX - i.wx);   // cut everything right of the frame
                return 2.0 * i.color * _TintColor * tex2D(_MainTex, i.uv);   // == Particles/Additive
            }
            ENDCG
        }
    }
}
