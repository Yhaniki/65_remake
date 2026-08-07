using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 「模型放大了,頭上的名字要跟著往上」的那條規則(<see cref="MmdHeadroom"/>)。
    ///
    /// 回歸的畫面:MMD 顯示開著、模型縮放調大之後,頭上的名字牌 / 家族列 / combo 表情全部留在
    /// **SDO 骨架**的頭高上 —— 因為 MMD 身體只是疊上去畫的,SDO 那具驅動器不會跟著變大。
    /// 使用者的要求很明確:<b>「不用調低 只要高的要往上調」</b>,所以縮小時一步都不能動。
    /// </summary>
    public class MmdHeadroomTests
    {
        private const float SdoH = 100f;

        [Test]
        public void BiggerModel_PushesTheNamePlateUp_ByExactlyTheHeightItGained()
        {
            Assert.AreEqual(20f, MmdHeadroom.Rise(SdoH, 120f), 1e-4f);
            Assert.AreEqual(20f, MmdHeadroom.RiseForScale(SdoH, 1.2f), 1e-4f);
        }

        [Test]
        public void SmallerModel_LeavesTheNamePlateWhereItWas()
        {
            // 🔴 使用者明確要求:只往上,不往下。名字浮高一點只是留白,掉下來會蓋住臉。
            Assert.AreEqual(0f, MmdHeadroom.Rise(SdoH, 80f), 1e-6f);
            Assert.AreEqual(0f, MmdHeadroom.RiseForScale(SdoH, 0.8f), 1e-6f);
            Assert.AreEqual(0f, MmdHeadroom.RiseForScale(SdoH, MmdTuningPolicy.NeutralSize), 1e-6f);
        }

        [Test]
        public void TheTwoWaysOfAskingAgree()
        {
            foreach (float s in new[] { 0.3f, 0.75f, 1f, 1.4f, 3f })
                Assert.AreEqual(MmdHeadroom.Rise(SdoH, SdoH * s), MmdHeadroom.RiseForScale(SdoH, s), 1e-4f);
        }

        [Test]
        public void UnmeasurableHeights_MoveNothing()
        {
            // NaN/∞ 進了 transform.position 的話,整個名字牌會從畫面上消失而且完全看不出原因 ——
            // 量不到高度時的正確行為是「維持原本的位置」。
            Assert.AreEqual(0f, MmdHeadroom.Rise(float.NaN, 120f), 1e-6f);
            Assert.AreEqual(0f, MmdHeadroom.Rise(SdoH, float.NaN), 1e-6f);
            Assert.AreEqual(0f, MmdHeadroom.Rise(SdoH, float.PositiveInfinity), 1e-6f);
            Assert.AreEqual(0f, MmdHeadroom.RiseForScale(0f, 2f), 1e-6f);
            Assert.AreEqual(0f, MmdHeadroom.RiseForScale(-5f, 2f), 1e-6f);
        }
    }
}
