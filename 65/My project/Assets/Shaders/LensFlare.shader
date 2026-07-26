// SCN0004 海灘太陽的鏡頭光斑鏈。官方 (0x418990) 只設這幾個狀態：
//   RenderSys +0x54 (1,1) → ALPHABLENDENABLE=TRUE, SRCBLEND=D3DBLEND_ONE, DESTBLEND=D3DBLEND_ONE  → 純加法
//   RenderSys +0x9c (0)   → D3DRS_BLENDOP = D3DBLENDOP_ADD
//   未設 Z / alpha test / cull；TSS stage0 是 COLOROP=MODULATE(TEXTURE, DIFFUSE)
// 所以是 ONE/ONE（不是別處常見的 SrcAlpha One），顏色 = 貼圖 × 頂點色，而且 **diffuse 的 alpha
// 完全不進最終顏色**（ONE/ONE 不看 src alpha）—— 表裡的 A 欄在原版就是沒有作用的，別自作主張拿來乘。
// 貼圖 LENSFLARE.BMP 是 24bpp 無 alpha，黑色靠加法自然變成透明。
// 螢幕空間繪製：ZTest Always + ZWrite Off，永遠疊在場景之上（官方是在 3D 之後、2D HUD 之前畫）。
Shader "Sdo/LensFlare"
{
    Properties
    {
        _MainTex ("Flare atlas", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend One One
        BlendOp Add
        Cull Off
        ZWrite Off
        ZTest Always
        Lighting Off
        Fog { Mode Off }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 col : COLOR0; };

            sampler2D _MainTex;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.col = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed3 c = tex2D(_MainTex, i.uv).rgb * i.col.rgb;   // MODULATE(TEXTURE, DIFFUSE)
                return fixed4(c, 1);                                // ONE/ONE：alpha 不參與
            }
            ENDCG
        }
    }
    Fallback Off
}
