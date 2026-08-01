using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 大廳左側那隻 3D 角色**實際上有多大、落在哪裡**。
    ///
    /// 為什麼要用測試量而不是用眼睛看:這一格已經來回錯過兩次(先太小、改完又太大),而兩次都是
    /// 靠截圖目測判斷的 —— 截圖是 Free Aspect、有 Unity 工具列、還被 UI 蓋住半個身體,量不準。
    /// <see cref="GenderPreview3D"/> 的取景是純幾何(fillFrac / avatarYOffset / verticalBias 三個參數
    /// 決定相機距離與中心),所以把它渲一次、量 alpha 的邊界,就能得到「在 800×600 畫布上的頭頂 y、
    /// 腳底 y、身體寬度」——那才是能跟官方實機截圖對數字的東西。
    ///
    /// 🔴 這條測試**不是回歸測試,是量尺**。它印出實測值並斷言在一個寬鬆的範圍內;
    ///    要改角色大小時先跑它看現在是多少,再回頭調 <c>LobbyScreen.AvatarFillFrac</c> / <c>AvatarY</c>。
    ///    目標**逐點量自官方實機截圖**(800×630 視窗、標題列 26px):
    /// 頭頂 y≈4、腳底 y≈411(= 房卡列表框下緣)、高 ≈410、身體中線 x≈170。
    /// 前三版 599 / 571 / 546px 都太大,而且落點整個往下掉 80px —— 那三次都是「目測」而不是逐點量。
    /// </summary>
    public class LobbyAvatarFramingTests
    {
        // 與 LobbyScreen 的常數保持同步(那邊是 private const,測試碰不到 → 這裡複製一份並在失敗訊息裡點名)。
        private const float AvatarX = -30f, AvatarY = -91f, AvatarW = 400f, AvatarH = 600f;
        private const float AvatarFillFrac = 0.605f;

        private GenderPreview3D _preview;

        [TearDown]
        public void TearDown()
        {
            if (_preview != null) Object.DestroyImmediate(_preview.gameObject);
        }

        [UnityTest]
        public IEnumerator Lobby_Avatar_Fills_The_Official_Head_To_Toe_Box()
        {
            var go = new GameObject("LobbyAvatarProbe");
            _preview = go.AddComponent<GenderPreview3D>();
            // 與 LobbyScreen.ShowAvatar 完全相同的三個設定(順序也一樣:一定要在 Build 之前)。
            _preview.avatarYOffset = 0f;
            _preview.verticalBias = 0f;
            _preview.fillFrac = AvatarFillFrac;
            _preview.Build(0);   // 女角,預設穿搭

            // 等一幀讓 RT 配置好、角色 pose 好。
            yield return null;
            yield return null;

            var rt = _preview.PreviewTexture as RenderTexture;
            Assert.IsNotNull(rt, "預覽 RT 沒建起來(角色模型載入失敗?)");

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            var px = tex.GetPixels32();
            Object.DestroyImmediate(tex);

            // 量 alpha 邊界。RT 底是透明的(backgroundColor a=0),所以任何不透明像素都是角色。
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            for (int y = 0; y < rt.height; y++)
                for (int x = 0; x < rt.width; x++)
                {
                    if (px[y * rt.width + x].a < 24) continue;   // 24 = 濾掉邊緣的半透明羽化
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            Assert.Less(minX, int.MaxValue, "RT 裡一個不透明像素都沒有 —— 角色沒被畫出來");

            // RT 座標(左下原點)→ 800×600 畫布座標(左上原點)。RawImage 把整張 RT 攤在 AvatarW×AvatarH 上。
            float sx = AvatarW / rt.width, sy = AvatarH / rt.height;
            float headY = AvatarY + (rt.height - 1 - maxY) * sy;
            float feetY = AvatarY + (rt.height - 1 - minY) * sy;
            float leftX = AvatarX + minX * sx;
            float rightX = AvatarX + maxX * sx;
            float centerX = (leftX + rightX) * 0.5f;
            float height = feetY - headY;

            Debug.Log($"[lobby-avatar] RT {rt.width}×{rt.height} → 畫布 頭頂 y={headY:F1} 腳底 y={feetY:F1} " +
                      $"高={height:F1} 左 x={leftX:F1} 右 x={rightX:F1} 中線 x={centerX:F1}");

            // 官方基準:頭頂 30 / 腳底 578 / 高 548 / 中線 205。容差給得寬 —— 不同 idle 動作與髮型會差幾 px,
            // 這條測試要抓的是「整個人跑掉一大截」那種錯,不是像素級校正。
            Assert.That(height, Is.EqualTo(410f).Within(40f),
                $"角色高度 {height:F0} 偏離官方的 410 太多 → 調 LobbyScreen.AvatarFillFrac(高度 = AvatarH × fillFrac)");
            Assert.That(headY, Is.EqualTo(4f).Within(35f),
                $"頭頂 y={headY:F0} 偏離官方的 4 太多 → 調 LobbyScreen.AvatarY(1:1 px)");
            Assert.That(feetY, Is.LessThan(600f),
                $"腳底 y={feetY:F0} 掉出畫面下緣了(600) → 角色會被切掉半截");
            // 🔴 身體中線驗的是 **RT 中心**(= AvatarX + AvatarW/2),不是 alpha bounding box 的中心:
            //    相機正對角色原點,所以中線恆在 RT 正中;而 bounding box 的中心會隨當下抽到的 idle 姿勢
            //    (手臂張開、抬腳、甩裙擺)左右跳三四十 px。照 bounding box 校 AvatarX 只會越校越偏。
            float geoCenterX = AvatarX + AvatarW * 0.5f;
            Assert.That(geoCenterX, Is.EqualTo(170f).Within(8f),
                $"身體中線 x={geoCenterX:F0} 偏離官方的 170 → 調 LobbyScreen.AvatarX(= 205 - AvatarW/2)");
            // 人整個跑出畫布左右緣就是版位錯了(姿勢再誇張也不該發生)。
            Assert.That(leftX, Is.GreaterThan(-20f), $"角色左緣 x={leftX:F0} 跑出畫面外");
            Assert.That(rightX, Is.LessThan(430f), $"角色右緣 x={rightX:F0} 侵入房卡列表區(列表底板從 x=286 起)");
        }
    }
}
