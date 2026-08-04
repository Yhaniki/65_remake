// MMD (.pmx) material shader — unlit port of MMD's fixed-function look:
//   Pass 1 (base): base texture × diffuse, optional SPHERE map (matcap by view normal: multiply=.sph sheen,
//     add=.spa glow), optional TOON ramp (N·L, fixed light), per-material alpha (opaque / alpha-test cutout /
//     alpha-blend) + cull.
//   Pass 2 (outline): MMD-style pencil edge — inverted hull, back-faces expanded along the view normal by a
//     screen-constant width (_OutlineWidth × per-material _EdgeSize), drawn in _EdgeColor; skipped when _EdgeSize≈0.
// One shader covers every PMX material class; the C# side sets the properties per material.
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
        _EdgeColor ("Edge Colour", Color) = (0,0,0,1)
        _EdgeSize ("Edge Size (per-material)", Float) = 0
        _OutlineWidth ("Outline Width (global)", Float) = 0.0018
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 0
        _ZWrite ("ZWrite", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }

        // ---- base ----
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
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 vnormal : TEXCOORD1; float3 wnormal : TEXCOORD2; };

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
                    float2 suv = normalize(i.vnormal).xy * 0.5 + 0.5;
                    fixed3 s = tex2D(_SphereTex, suv).rgb;
                    if (_SphereMode < 1.5) c.rgb *= s; else c.rgb += s;
                }
                if (_UseToon > 0.5)
                {
                    float3 L = normalize(float3(-0.4, -1.0, -0.5));
                    float d = saturate(dot(normalize(i.wnormal), -L));
                    c.rgb *= tex2D(_ToonTex, float2(0.5, d)).rgb;
                }
                return c;
            }
            ENDCG
        }

        // ---- outline (inverted hull) ----
        Pass
        {
            Name "Outline"
            Cull Front
            ZWrite On
            CGPROGRAM
            #pragma vertex vo
            #pragma fragment fo
            #include "UnityCG.cginc"

            struct ai { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
            struct vf { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex; float4 _MainTex_ST;
            float _EdgeSize, _OutlineWidth, _Cutoff, _AlphaClip;
            fixed4 _EdgeColor;

            vf vo (ai v)
            {
                vf o;
                float4 clip = UnityObjectToClipPos(v.vertex);
                float3 vn = normalize(mul((float3x3)UNITY_MATRIX_IT_MV, v.normal));   // view-space normal
                // expand in clip xy by a screen-constant amount (×clip.w survives the perspective divide); silhouettes
                // (normal ⟂ view → big xy) get the outline, front faces (small xy) barely move. The x is scaled by
                // height/width so the outline is PIXEL-uniform (not NDC-uniform → thicker sideways on a wide viewport).
                float2 aspect = float2(_ScreenParams.y / _ScreenParams.x, 1.0);
                clip.xy += vn.xy * aspect * (_OutlineWidth * max(_EdgeSize, 0.0)) * clip.w;
                o.pos = clip;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 fo (vf i) : SV_Target
            {
                if (_EdgeSize < 0.001) discard;                      // non-edge material → no outline
                if (_AlphaClip > 0.5) clip(tex2D(_MainTex, i.uv).a - _Cutoff);   // don't outline transparent (hair) holes
                return _EdgeColor;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Texture"
}
