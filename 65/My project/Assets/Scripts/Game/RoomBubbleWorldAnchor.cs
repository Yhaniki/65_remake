using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 把「頭上聊天泡」從 UI 層搬進**房間相機**時的定位數學(純函式,可單元測試)。
    ///
    /// 為什麼要搬:泡原本畫在 UI 上(疊在房間 RT 之上),所以永遠蓋在整張 3D 圖前面 ——
    /// 站在說話者前面的人擋不住它(使用者回報)。要有真正的前後景就得讓泡進到房間相機裡,
    /// 由 GPU 的深度測試逐像素裁掉。
    ///
    /// 難的不是搬進去,是**搬進去之後不能有任何其他變化**:泡不可以隨距離變大變小、
    /// 螢幕位置要跟今天逐像素一樣。做法是把泡放在一個**正對相機的平面**上,
    /// 並讓平面的縮放隨距離補償:
    ///
    ///   ① 位置:沿「相機 → 錨點」那條視線推,所以投影點完全不動(距離只影響深度,不影響投影)。
    ///   ② 縮放:2·d / (600·m11) 世界單位 / 設計 px —— 相機在距離 d 處看得到的世界高度是
    ///      2·d·tan(fovV/2) = 2d/m11,而設計框的高度是 600 px,所以一個設計 px 就是這麼多世界單位。
    ///      於是遠處的人的泡與近處的人的泡在螢幕上**同樣大**,字還是 13 設計 px。
    ///
    /// 這兩件加起來 ⇒ 泡的螢幕位置與大小與搬家前相同,唯一的差別就是它現在會被前面的人擋住。
    /// </summary>
    public static class RoomBubbleWorldAnchor
    {
        /// <summary>泡要擺的那個正對相機的平面。<see cref="Valid"/> 為 false 表示這一幀不該畫(錨點在相機後面或太近)。</summary>
        public struct BubblePlane
        {
            public bool Valid;
            public Vector3 Position;   // 平面原點的世界座標(投影點與錨點相同)
            public float Scale;        // 世界單位 / 設計 px
        }

        /// <summary>
        /// 解出泡的平面。
        /// </summary>
        /// <param name="camPos">相機世界位置。</param>
        /// <param name="camFwd">相機朝向(單位向量)。</param>
        /// <param name="m11">
        /// <c>cam.projectionMatrix.m11</c> = 1/tan(fovV/2)。**用矩陣而不是 fieldOfView** ——
        /// 房間相機每幀把 aspect 釘成 4:3,矩陣才是真正在用的那組數。
        /// </param>
        /// <param name="nearClip">相機近裁面。</param>
        /// <param name="anchorWorld">錨點(說話者的肩膀骨)的世界座標。</param>
        /// <param name="depthBias">
        /// 把平面往相機拉近多少世界單位。用意是**別讓說話者自己的身體/頭髮把泡切掉** ——
        /// 泡的錨點在肩膀,而頭髮/胸口比肩膀骨更靠近相機。值應該由說話者自己的 renderer bounds
        /// 沿 camFwd 的半徑算出來(見 <c>RoomScene3D.OwnerDepthExtent</c>),不要用魔術常數。
        /// </param>
        /// <param name="designHeight">設計框高度(800×600 的 600)。</param>
        public static BubblePlane Solve(Vector3 camPos, Vector3 camFwd, float m11, float nearClip,
                                       Vector3 anchorWorld, float depthBias, float designHeight)
        {
            var result = default(BubblePlane);
            if (m11 <= 0f || designHeight <= 0f) return result;

            Vector3 toAnchor = anchorWorld - camPos;
            float dAnchor = Vector3.Dot(toAnchor, camFwd);

            // 🔴 這條守衛是正確性條件,不是防禦性程式碼:d 變成 0 或負數的話 Scale 會變成負的,
            // 泡就會以鏡像的形式出現在相機後面(而且是一團亂圖)。錨點在相機後面/貼在近裁面上就別畫。
            float minD = nearClip + 1f;
            if (!(dAnchor > minD)) return result;   // 寫成 !(>) 是為了讓 NaN 也走到這裡

            float d = Mathf.Max(minD, dAnchor - depthBias);

            result.Valid = true;
            result.Position = camPos + toAnchor * (d / dAnchor);   // 同一條視線 → 投影點不動
            result.Scale = 2f * d / (designHeight * m11);
            return result;
        }

        /// <summary>
        /// 錨點在設計座標系裡的位置(UI 慣例:原點在設計框左上、y 往下為負)。
        ///
        /// 這一點就是泡的 world canvas 原點所投影到的地方(<see cref="Solve"/> 保證投影點 = 錨點),
        /// 所以「泡的畫」在 canvas 裡的相對位移一律是 `泡的絕對設計位置 − 這一點`。
        ///
        /// 🔴 這裡減的是**錨點骨頭的投影點**,不是那條泡鏈的錨點(RoomScreen.BubbleRootFromVisible)。
        /// 兩者刻意不同:鏈的錨點還帶了「泡身中心相對肩膀的位移」(+80, +10)與「泡在 171×111 畫布裡的
        /// 中心」(85.5, 56.5)。減錯的話整條鏈會固定往右下偏 (5.5, 46.5) 設計 px —— 泡的位置看起來
        /// 「就是設計得有點低」,不會有人想到是座標基準減錯了。
        /// </summary>
        public static Vector2 AnchorDesignPoint(Vector2 viewport, float designWidth, float designHeight)
            => new Vector2(viewport.x * designWidth, -(1f - viewport.y) * designHeight);
    }
}
