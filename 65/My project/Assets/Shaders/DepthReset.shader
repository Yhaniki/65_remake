// 把深度緩衝「推回無限遠」的全螢幕面片 —— 房間頭上聊天泡「只被人擋、不被場景擋」的第一步。
//
// 為什麼需要:泡與房間場景是同一台相機畫的,所以泡預設會被場景/家具的深度切掉(使用者回報)。
// 正確規則是只有「人」擋得住泡。做法是在**場景全部畫完之後、泡畫出來之前**插三步:
//
//   ① 這支 shader:整片寫入最遠的深度 → 場景的深度從此不存在(等同在一台相機中途清深度);
//   ② 每個角色的隱形分身(Sdo/DepthOnlyMask)把「人」的剪影寫回深度;
//   ③ 泡照常做深度測試 → 只可能輸給人。
//
// 三步的先後靠 sortingOrder(它比 renderQueue 優先,見 [[unity-sortingorder-outranks-renderqueue]]):
// 場景/衣物 0 → 這片 98 → 分身 99 → 泡 100+。
//
// 🔴 這片**不能寫顏色**(ColorMask 0),也不能被視錐剔除掉:它掛在相機底下、mesh 的 bounds 開很大,
// 頂點直接輸出裁剪空間座標(不吃 transform),所以永遠是整個畫面。
Shader "Sdo/DepthReset"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "IgnoreProjector"="True" }

        Pass
        {
            ColorMask 0
            ZWrite On
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f     { float4 pos : SV_POSITION; };

            // mesh 就是裁剪空間的一片大三角形(x,y ∈ [-1,3]),不經過任何矩陣 —— 相機怎麼動都蓋滿畫面。
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = float4(v.vertex.xy, UNITY_NEAR_CLIP_VALUE, 1.0);
                return o;
            }

            // 寫「最遠」。🔴 Unity 在多數平台是 reversed-Z(近 1、遠 0),寫死 1.0 會變成「整片貼在鏡頭上」,
            // 症狀是泡與人一起消失 —— 一定要看 UNITY_REVERSED_Z。
            float frag(v2f i) : SV_Depth
            {
            #if UNITY_REVERSED_Z
                return 0.0;
            #else
                return 1.0;
            #endif
            }
            ENDCG
        }
    }
}
