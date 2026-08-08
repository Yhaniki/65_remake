// 半透明衣物/翅膀的「只寫深度」分身 —— 它畫不出任何顏色,存在的唯一意義是讓**畫在它後面的東西**
// (頭上名字牌)知道這裡有一件衣服。
//
// 為什麼需要:alpha-blend 的衣物與翅膀 ZWrite Off(那是它們能透光的原因),於是它們在深度緩衝裡
// 根本不存在 —— 站在前面的人穿一對半透明翅膀,後面那個人的名字就從翅膀裡透出來(使用者回報)。
//
// 🔴 為什麼不是「在原本的 shader 加一個 depth prepass pass」:試過兩次,兩次都讓**每一件**
//    alpha-blend 衣服整件消失(見 UnlitAvatarSheer.shader 檔頭的警告與 GarmentFlickerFixTests:
//    那支 shader 的 passCount 被釘死在 1)。所以深度改由一個**獨立的分身 renderer** 寫。
//
// 🔴 Queue = Transparent+500 (3500):比所有衣物的色彩批都晚。
//    衣物是 3000..3400(SdoAvatarBuilder.TransparentGarmentQueue),分身排在它們**全部畫完之後**
//    才寫深度 ⇒ 衣物之間的疊色、紗料的層層累積密度、自己的背面…一個像素都不會變。
//    這就是這個做法比「把 ZWrite 打開」安全的地方:ZWrite On 會讓後畫的那層被前面那層裁掉。
//    名字牌是 sortingOrder 1(sortingOrder 比 renderQueue 優先),永遠排在這之後 → 吃得到這份深度。
Shader "Sdo/AvatarDepthOnly"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        // 多透明才算「這裡有衣服」。0.5 = 比半透明更實的地方才擋名字;紗的邊緣羽化處讓名字透出來,
        // 與「透過它看得到房間」一致。調低 → 連最淡的羽化邊都擋;調高 → 只有接近不透明處才擋。
        _Cutoff ("Depth cutoff (alpha above this writes depth)", Range(0,1)) = 0.5
    }
    SubShader
    {
        Tags { "Queue"="Transparent+500" "RenderType"="Transparent" "IgnoreProjector"="True" "ForceNoShadowCasting"="True" }
        LOD 100

        ColorMask 0     // 畫不出顏色 —— 這是「外觀不可能改變」的保證
        ZWrite On
        ZTest LEqual
        Cull Off        // 衣物/翅膀可能是單面片,從背面看也要擋

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
            float _Cutoff;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 去背處(a≈0)絕對不能寫深度 —— 翅膀的鏤空、裙子的開衩會在名字上切出方形的洞。
                clip(tex2D(_MainTex, i.uv).a - _Cutoff);
                return 0;   // ColorMask 0,寫不出去
            }
            ENDCG
        }
    }
}
