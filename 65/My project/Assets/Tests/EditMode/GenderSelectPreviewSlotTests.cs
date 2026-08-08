using NUnit.Framework;
using UnityEngine;
using Sdo.Game;
using Sdo.UI.Screens;

namespace Sdo.Tests
{
    /// <summary>
    /// 選男女畫面（<see cref="GenderSelectScreen"/>）那尊 3D 預覽的**槽位幾何**。
    ///
    /// 起因：官方 AvtShow 槽位只有 400 邏輯 px 寬，而那 400 px 就是預覽 RT 的取景邊界 —— 穿大型翅膀（天使翼那類）
    /// 時翅膀比人寬得多，左右各被切掉一截（使用者回報）。槽位改成「以官方那個中心為心、涵蓋整個 800 邏輯寬」。
    ///
    /// 這裡盯的是**那個加寬不能把人搬走也不能把人拉扁**：中心要留在官方的 x=350（角色中線恆在 RT 正中，見
    /// <see cref="GenderPreview3D"/> 的取景），高度要維持官方的 600（垂直 FOV 決定角色大小，動了高度＝動了人的大小）。
    /// 純算術，不畫 UI、不碰 Unity 執行期。
    /// </summary>
    public class GenderSelectPreviewSlotTests
    {
        private const float OfficialSlotW = 400f, OfficialSlotH = 600f;   // DDRLOBBYSEL.XML win5 的 AvtShow w/h

        [Test]
        public void Slot_Center_Stays_On_The_Official_AvtShow_Center()
        {
            float center = GenderSelectScreen.AvatarView.x + GenderSelectScreen.AvatarSize.x * 0.5f;
            Assert.That(center, Is.EqualTo(GenderSelectScreen.AvatarCenterX).Within(0.01f),
                        "角色中線恆在 RT 正中：槽位中心一偏，畫面上的人就跟著左右搬家");
            Assert.That(GenderSelectScreen.AvatarCenterX, Is.EqualTo(150f + OfficialSlotW * 0.5f).Within(0.01f),
                        "中心要留在官方 AvtShow 的 x=350");
        }

        [Test]
        public void Slot_Spans_The_Whole_Logical_Frame()
        {
            float left = GenderSelectScreen.AvatarView.x;
            float right = left + GenderSelectScreen.AvatarSize.x;
            Assert.That(left, Is.LessThanOrEqualTo(0f), "左緣沒蓋到畫面左邊 → 翅膀左半仍會被 RT 邊界切掉");
            Assert.That(right, Is.GreaterThanOrEqualTo(RtSizing.LogicalW), "右緣沒蓋到畫面右邊 → 翅膀右半仍會被切");
            Assert.That(GenderSelectScreen.AvatarSize.x, Is.GreaterThan(OfficialSlotW),
                        "這個測試檔的前提就是槽位比官方寬");
        }

        [Test]
        public void Slot_Height_Unchanged_So_The_Dancer_Keeps_Its_Size()
        {
            // 取景是用**垂直** FOV 算相機距離的（GenderPreview3D.FrameTo）→ 高度動了，人的大小就動了。
            Assert.That(GenderSelectScreen.AvatarSize.y, Is.EqualTo(OfficialSlotH).Within(0.01f));
            Assert.That(GenderSelectScreen.AvatarView.y, Is.EqualTo(0f).Within(0.01f));
        }

        [Test]
        public void Widened_Slot_Still_Sizes_A_Sane_Rt()
        {
            // RT 跟著視窗走（RtSizing）：加寬後仍要落在 MaxDim 的夾限內，且水平像素數不低於槽位本身的邏輯寬。
            foreach (var win in new[] { new Vector2Int(800, 600), new Vector2Int(1280, 720), new Vector2Int(1920, 1080) })
            {
                RtSizing.SlotRtSize(win.x, win.y, GenderSelectScreen.AvatarSize.x, GenderSelectScreen.AvatarSize.y,
                                    RtSizing.DefaultSupersample, out int w, out int h);
                Assert.That(w, Is.InRange(Mathf.RoundToInt(GenderSelectScreen.AvatarSize.x), RtSizing.MaxDim), "視窗 " + win);
                Assert.That(h, Is.InRange(Mathf.RoundToInt(GenderSelectScreen.AvatarSize.y), RtSizing.MaxDim), "視窗 " + win);
            }
        }
    }
}
