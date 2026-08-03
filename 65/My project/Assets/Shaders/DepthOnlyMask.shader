// 只寫深度、不寫任何顏色的「替身」材質。
//
// 用途:房間頭上聊天泡的遮擋規則是「**只被人擋,不被場景擋**」。做法是泡由一台疊加相機
// (RoomBubbleCameraRig)畫,那台相機**把深度清空**(場景的深度不進來),再讓每個角色的
// 隱形分身(RoomPeopleDepthProxy)用這支 shader 把人的剪影寫回深度 —— 於是泡的 ZTest
// 只可能輸給人。
//
// 兩個一定要保留的狀態:
//   • ColorMask 0:分身永遠不能被看見(它與本尊完全重疊,畫出來就是重複疊畫,紗質衣物會變濃)。
//   • Cull Off  :頭髮/裙擺是開放薄片,兩面都要進深度(與 Sdo/UnlitDoubleSided 同一個理由)。
Shader "Sdo/DepthOnlyMask"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        // 預設 0 = 完全不裁。頭髮/鏤空布料的材質自己帶 _Cutoff(建分身時抄過來),沒有的
        // 實心材質就維持 0 —— 這樣「貼圖 alpha 剛好是 0」的實心部位(DXT1 的 alpha 不可信,
        // 見 [[unity-dxt1-alpha-cutout-trap]])仍然照樣寫深度,不會在人身上開一個洞。
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "IgnoreProjector"="True" }

        Pass
        {
            ColorMask 0
            ZWrite On
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed _Cutoff;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                clip(tex2D(_MainTex, i.uv).a - _Cutoff);   // _Cutoff = 0 → a = 0 也留著(clip 只丟負值)
                return 0;
            }
            ENDCG
        }
    }
}
