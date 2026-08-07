// 全螢幕亮度 overlay（BrightnessOverlay）：把已經畫好的畫面**乘上**一個倍率，＝顯示器亮度的行為
// （暗的地方保持暗，不會像加法白幕那樣把純黑抬成灰）。
//
// 一張 quad 兩種用法，靠 _SrcBlend/_DstBlend 切（材質上設，見 BrightnessOverlay.Apply）：
//   變暗 (亮度 b ≤ 1)：Blend DstColor Zero → result = dst × _Gain，_Gain = b
//   變亮 (亮度 b > 1)：Blend DstColor One  → result = dst × _Gain + dst = dst × (1 + _Gain)，_Gain = b − 1
// 專案是 Linear color space、backbuffer 是 sRGB 格式 → 混合由 GPU 在 linear 光量空間做，所以這個乘法就是
// 「光量 ×b」，刻度精準。_Gain 走材質 float（不是 Image.color）：頂點色在 linear 專案會被 sRGB→linear 轉一次，
// 拿它當倍率會歪掉。
//
// LDR backbuffer 下 fragment 值夾在 [0,1] → 變亮最多到 ×2（＝ GameplaySettings 那邊亮度上限 2.0 的由來）。
Shader "Sdo/UiScreenGain"
{
    Properties
    {
        _Gain ("Gain", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 2   // DstColor
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 0   // Zero
    }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Blend [_SrcBlend] [_DstBlend]
        Cull Off ZWrite Off ZTest Always Lighting Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; };
            float _Gain;
            v2f vert (appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); return o; }
            // alpha 也走同一個乘法（backbuffer 的 alpha 沒有人讀，只是別讓它變成隨機值）。
            fixed4 frag (v2f i) : SV_Target { return fixed4(_Gain, _Gain, _Gain, _Gain); }
            ENDCG
        }
    }
}
