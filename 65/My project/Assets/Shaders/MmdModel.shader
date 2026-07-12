// MMD (.pmx) material shader — faithful-ish unlit port of MMD's fixed-function look:
//   base texture × diffuse, optional SPHERE map (matcap sampled by the VIEW-space normal: multiply = .sph sheen,
//   add = .spa glow), optional TOON ramp (N·L, fixed light), and per-material alpha (opaque / alpha-test cutout /
//   alpha-blend) + cull, all driven by material properties so one shader covers every PMX material class.
// Sphere is the defining MMD texture feature (eye highlights, skin/metal sheen); it needs no scene light, so it's on
// by default. Toon needs a light direction (approximated) and is opt-in.
Shader "Sdo/MmdModel"
{
    Properties
    {
        _Color ("Diffuse", Color) = (1,1,1,1)
        _MainTex ("Base (RGBA)", 2D) = "white" {}
        [NoScaleOffset] _SphereTex ("Sphere", 2D) = "black" {}
        [NoScaleOffset] _ToonTex ("Toon", 2D) = "white" {}
        _SphereMode ("Sphere Mode (0 none,1 mul,2 add)", Float) = 0
        _UseToon ("Use Toon", Float) = 0
        _Cutoff ("Alpha Cutoff", Float) = 0.5
        _AlphaClip ("Alpha Clip", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 0
        _ZWrite ("ZWrite", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Cull [_Cull]
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 vnormal : TEXCOORD1;   // view-space normal (sphere sampling)
                float3 wnormal : TEXCOORD2;   // world normal (toon)
            };

            sampler2D _MainTex; float4 _MainTex_ST;
            sampler2D _SphereTex; sampler2D _ToonTex;
            float _SphereMode, _UseToon, _Cutoff, _AlphaClip;
            fixed4 _Color;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.wnormal = UnityObjectToWorldNormal(v.normal);
                o.vnormal = mul((float3x3)UNITY_MATRIX_V, o.wnormal);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * _Color;
                if (_AlphaClip > 0.5) clip(c.a - _Cutoff);

                if (_SphereMode > 0.5)
                {
                    float2 suv = normalize(i.vnormal).xy * 0.5 + 0.5;   // MMD spherical matcap coords
                    fixed3 s = tex2D(_SphereTex, suv).rgb;
                    if (_SphereMode < 1.5) c.rgb *= s;                  // .sph multiply (sheen/shading)
                    else c.rgb += s;                                    // .spa add (highlight/glow)
                }
                if (_UseToon > 0.5)
                {
                    float3 L = normalize(float3(-0.4, -1.0, -0.5));     // approximate MMD default key light
                    float d = saturate(dot(normalize(i.wnormal), -L));
                    c.rgb *= tex2D(_ToonTex, float2(0.5, d)).rgb;       // toon ramp (vertical)
                }
                return c;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Texture"
}
