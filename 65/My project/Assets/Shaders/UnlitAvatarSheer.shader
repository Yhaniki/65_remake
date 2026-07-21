// Translucent SHEER FABRIC shader for avatar garments — lace / mesh / organza dresses (e.g. Flower Lace Dress
// 024976_WOMAN_ONE) whose DDS alpha is an authored, roughly-uniform semi-transparent veil (mean α≈0.68) rather than
// crisp opaque-thread + clear-hole cut-outs. Plain alpha-blend of that veil reads TOO faint next to the official
// client (使用者:「比官方的透明」). The original engine draws these garments so the fabric reads DENSER — modelled here
// by a DENSITY power on alpha: α' = 1 - (1-α)^_Density. _Density=1 is faithful straight alpha-blend; the default 2.0
// is the density of the fabric drawn over itself twice (α 0.68 → 0.90), which matches the official look. Tune _Density
// up (denser) / down (sheerer) to taste — it's the single knob for "how see-through".
//   - Cull Back (single-sided): avatar garment meshes are authored outer-facing (same as the opaque Unlit/Texture cloth
//     path), so this gives ONE uniform layer — no back-through-front wash that made the skirt look see-through.
//   - ZWrite Off + Queue=Transparent: draw AFTER the opaque skin body so the fabric blends OVER the skin/arm behind it.
//   - Separate alpha blend (One OneMinusSrcAlpha): the shop card / left preview render into a TRANSPARENT-cleared
//     RenderTexture composited over the UI; where the opaque body backs the fabric the RT alpha stays 1 (the garment
//     shows over the body, not the UI), only fabric overhanging the silhouette composites translucently. Same trick as
//     Sdo/UnlitAvatarAlpha (glasses/wings), which stays SEPARATE so a lens/wing keeps its authored α (no density boost).
Shader "Sdo/UnlitAvatarSheer"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Density ("Fabric density (alpha power; 1=faithful, 2=default)", Range(1,4)) = 2.0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100
        Cull Back
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

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
            float _Density;

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
                c.a = saturate(1.0 - pow(saturate(1.0 - c.a), _Density));   // denser fabric; a=0 stays 0, a=1 stays 1
                return c;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Transparent"
}
