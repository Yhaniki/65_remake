using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 「頭上的東西進 3D 相機」的定位數學(純函式,可單元測試)。
    ///
    /// 🔴 名字取自它的第一個用途(房間頭上聊天泡),但**房間的泡已經不走這裡了** ——
    /// 泡要蓋過整張房間畫面(前面的人也擋不住它),所以它整個回到 UI 層;泡與泡之間照站位排前後
    /// 由 <see cref="RoomBubbleDrawOrder"/> 做。現在的使用者是兩處「要被人擋住」的頭上物件:
    /// gameplay 舞台的頭部標記(<c>HeadMarker.SolveWorldPlane</c>)與房間的頭上**名字牌**
    /// (<c>RoomNamePlateAnchor</c>)。
    ///
    /// 要的是:東西不可以隨距離變大變小、螢幕位置要與畫在 UI 上時逐像素一樣。做法是把它放在一個
    /// **正對相機的平面**上,並讓平面的縮放隨距離補償:
    ///
    ///   ① 位置:沿「相機 → 錨點」那條視線推,所以投影點完全不動(距離只影響深度,不影響投影)。
    ///   ② 縮放:2·d / (600·m11) 世界單位 / 設計 px —— 相機在距離 d 處看得到的世界高度是
    ///      2·d·tan(fovV/2) = 2d/m11,而設計框的高度是 600 px,所以一個設計 px 就是這麼多世界單位。
    ///      於是遠處那個人的牌子與近處那個人的牌子在螢幕上**同樣大**。
    ///
    /// 這兩件加起來 ⇒ 螢幕位置與大小與畫在 UI 上時相同,唯一的差別就是它現在會被前面的人擋住。
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
        /// <param name="anchorWorld">錨點(掛的那根骨頭)的世界座標。</param>
        /// <param name="depthBias">
        /// 把平面往相機拉近多少世界單位。用意是**別讓那個人自己的身體/頭髮把牌子切掉** ——
        /// 錨點在肩膀/頭上,而頭髮與胸口比它更靠近相機。值要由那個人自己的 renderer bounds
        /// 沿 camFwd 的半徑算出來,不要用魔術常數。
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
        /// 「內容層」相對於平面原點的位移(設計 px)。
        ///
        /// 平面的原點就落在錨點的投影點上,但掛在上面的名字/家族列**還是用 800×600 的絕對設計座標**
        /// 在排版(PlaceFollow / RoomFamilyRow.Place 一行都不用改)。所以中間夾一層,把整組往回推
        /// 「錨點的設計座標」——(0,0) 的子物件於是正好落在錨點上,而 (x,−y) 的子物件落在它該在的絕對位置。
        ///
        /// 座標系:子物件是左上錨(0,1),anchoredPosition 往下為負;<paramref name="vp"/> 是 y 朝上的
        /// viewport,所以錨點的設計座標是 (vp.x·W, (1−vp.y)·H),而這裡回的是它的相反數
        /// (x 取負、y 取正 —— 差一個負號的症狀是名字牌上下鏡射地飄到頭的另一邊)。
        /// </summary>
        public static Vector2 ContentOffset(Vector2 vp, float designWidth, float designHeight)
            => new Vector2(-vp.x * designWidth, (1f - vp.y) * designHeight);
    }
}
