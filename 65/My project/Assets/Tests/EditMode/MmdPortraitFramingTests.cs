using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// MMD 頭貼的取景(<see cref="MmdAvatar.FramePortrait"/>)。重點不只是「這格自己好看」,而是
    /// **它算出來的距離跟 SDO 那條的距離不是同一種量**:
    ///
    ///   MmdAvatar.FramePortrait → **世界單位**(頭框已經含了 avatar 的 scale)
    ///   HeadBoneFraming        → **模型單位**,用的時候才 ×avatarScale
    ///
    /// 2026-08-05 使用者回報「結算畫面男生的頭特別大」:UpdateHeadPortraitCam 的 MMD 分支把世界距離
    /// **寫回共用欄位** ScreenGameplay.headPortraitDist(模型單位),而 SyncResultHeadPortraitTuning 每幀
    /// 把那個欄位推給結算列其他人那幾格的 boneDistModel → 旁邊 SDO 那格的相機被拉近一截、頭就大一圈。
    /// 只要自己換上 MMD 就會發生,全 SDO 時那條分支根本不跑,所以「只有有 MMD 角色的時候才這樣」。
    ///
    /// 修法是結構性的:MMD 取景改成沒有 ScreenGameplay 參數的純函式(下面這些測試),距離只落在區域變數,
    /// 共用欄位永遠保持模型單位。
    /// </summary>
    public class MmdPortraitFramingTests
    {
        // 實測:MMD 的「純頭」框(頭骨 tail 算的,不含髮/角/帽)約 9.0 模型單位;結算頭貼 avatarScale 1.05
        // → 世界高 ~9.45。中心點取頭骨上方一點,絕對位置不影響任何一條斷言。
        private const float HeadWorldH = 9.45f;
        private static Bounds Head(float h = HeadWorldH)
            => new Bounds(new Vector3(5000f, 55f, 5000f), new Vector3(h * 0.75f, h, h * 0.75f));

        [Test]
        public void Dist_ScalesWithTheHeadBox_SoEveryModelsHeadFillsTheSameFraction()
        {
            MmdAvatar.FramePortrait(Head(9f), 1f, 0f, out _, out float d1);
            MmdAvatar.FramePortrait(Head(18f), 1f, 0f, out _, out float d2);
            Assert.AreEqual(2f * d1, d2, 1e-3f);
            Assert.AreEqual(9f * MmdAvatar.PortraitFrameDist, d1, 1e-3f);
        }

        [Test]
        public void Zoom_OnlyMovesTheCameraBack_CompositionCentreUnchanged()
        {
            MmdAvatar.FramePortrait(Head(), 1f, 0f, out var a1, out float d1);
            MmdAvatar.FramePortrait(Head(), 1.5f, 0f, out var a2, out float d2);
            Assert.AreEqual(1.5f * d1, d2, 1e-3f);
            Assert.AreEqual(a1, a2);
        }

        [Test]
        public void Aim_SitsBelowTheHeadCentre_AndAimXOnlyShiftsSideways()
        {
            var box = Head();
            MmdAvatar.FramePortrait(box, 1f, -2f, out var aim, out _);
            Assert.AreEqual(box.center.y - MmdAvatar.PortraitAimUp * box.size.y, aim.y, 1e-3f);   // 臉落框內偏下
            Assert.AreEqual(box.center.x - 2f, aim.x, 1e-3f);
            Assert.AreEqual(box.center.z, aim.z, 1e-3f);
        }

        [Test]
        public void DegenerateInputs_DoNotProduceADegenerateCamera()
        {
            MmdAvatar.FramePortrait(new Bounds(Vector3.zero, Vector3.zero), 0f, 0f, out _, out float dist);
            Assert.Greater(dist, 0f);
        }

        /// <summary>回歸:MMD 那格算出來的世界距離,若被當成 SDO 那條的「模型單位」推給結算列其他人,
        /// 那幾格的相機會比正確值近一截 —— 這就是使用者看到的「男生的頭特別大」。這裡把那個差距釘住,
        /// 提醒兩種量永遠不可以互相指派。</summary>
        [Test]
        public void MmdWorldDist_IsNotAModelUnitDist_MixingThemShrinksTheOtherRowsCamera()
        {
            const float scale = 1.05f;
            var bone = new Vector3(5000f, 55.12f, 5000f);
            var aimOffset = new Vector3(-2f, HeadBoneFraming.AimUpModel, 0f);

            MmdAvatar.FramePortrait(Head(), 1f, aimOffset.x, out _, out float mmdWorldDist);
            HeadBoneFraming.Compute(bone, scale, 1f, aimOffset, out _, out float correct);
            HeadBoneFraming.Compute(bone, scale, 1f, aimOffset, mmdWorldDist, out _, out float polluted);

            Assert.AreEqual(25.17f, correct, 0.05f);                 // SDO 那格該有的世界距離
            Assert.Less(polluted, correct * 0.9f);                   // 被 MMD 的值蓋掉 → 近 >10% → 頭明顯變大
        }

        [Test]
        public void TheSharedRowDist_StaysInModelUnits()   // 共用欄位的語意:模型單位,不是誰的世界距離
        {
            var go = new GameObject("t");
            try
            {
                var s = go.AddComponent<ScreenGameplay>();
                Assert.AreEqual(HeadBoneFraming.DistModel, s.headPortraitDist, 1e-4f);
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
