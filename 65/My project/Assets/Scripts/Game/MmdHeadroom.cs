namespace Sdo.Game
{
    /// <summary>
    /// 頭上那些東西(名字牌、家族列、聊天泡、combo 表情)要往上讓多少 —— 純數學,測試在
    /// <c>MmdHeadroomTests</c>。
    ///
    /// <b>為什麼需要它。</b>MMD 身體是**疊在 SDO 骨架上**畫的,而那具 SDO 骨架自始至終是原本的大小
    /// (它只是動作驅動器,見 <see cref="MmdAvatarSwap"/>)。頭上的名字/表情全都是釘在 SDO 的
    /// <c>Bip01_Head</c>(或角色腳下的站位)上,所以「模型縮放」(config.ini <c>mmdScale</c>)一旦把畫面上的人
    /// 放大,名字就留在原地被放大後的頭穿過去 —— 使用者回報的正是這個畫面。
    ///
    /// 🔴 <b>只准往上,不准往下。</b>模型比 SDO 矮的時候(<c>mmdScale &lt; 1</c>)名字維持原位:那是使用者
    /// 明確要求的(「不用調低 只要高的要往上調」)。名字浮高一點只是多一點留白,掉下來卻會蓋住臉。
    /// </summary>
    public static class MmdHeadroom
    {
        /// <param name="sdoHeight">驅動它的 SDO 身體從腳底到頭頂有多高。</param>
        /// <param name="mmdHeight">畫出來的那具 MMD 身體同一段有多高(＝已經乘過 <c>mmdScale</c>)。</param>
        /// <returns>頭上的東西要再往上移多少(與傳進來的兩個高度同一個單位);MMD 沒有比較高就是 0。</returns>
        public static float Rise(float sdoHeight, float mmdHeight)
        {
            // 量不到高度(避免把 NaN/∞ 傳進 transform:那會讓整個名字牌從畫面上消失,而且完全看不出原因)
            if (float.IsNaN(sdoHeight) || float.IsNaN(mmdHeight) ||
                float.IsInfinity(sdoHeight) || float.IsInfinity(mmdHeight)) return 0f;
            float d = mmdHeight - sdoHeight;
            return d > 0f ? d : 0f;
        }

        /// <summary>同一件事,但用「對齊身高之後再乘的倍率」(<c>mmdScale</c>)表示 —— 身體還沒建出來時也算得出來。</summary>
        public static float RiseForScale(float sdoHeight, float sizeMul)
            => sdoHeight > 0f ? Rise(sdoHeight, sdoHeight * sizeMul) : 0f;
    }
}
