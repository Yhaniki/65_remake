using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// MinaCalc 模式下「原始 MSD → 螢幕上那個難度數字」的換算(<see cref="ManiaMsd.ToLevel"/>):
    /// round(max(MSD^1.9 × 0.1, MSD)),上限 <see cref="ManiaMsd.LevelMax"/>(999,與 osu 星數等級同一個天花板)。
    /// </summary>
    public class ManiaMsdLevelTests
    {
        [Test]
        public void ToLevel_Zero_Or_Negative_Msd_Is_Zero()
        {
            Assert.AreEqual(0, ManiaMsd.ToLevel(0f), "沒算出 MSD → 0(呼叫端會退回星數等級)");
            Assert.AreEqual(0, ManiaMsd.ToLevel(-3f));
        }

        [Test]
        public void ToLevel_Uses_The_Curve_Above_The_Raw_Msd()
        {
            Assert.AreEqual(30, ManiaMsd.ToLevel(20f));   // 20^1.9 × 0.1 = 29.64 → 30
            Assert.AreEqual(64, ManiaMsd.ToLevel(30f));   // 30^1.9 × 0.1 = 64.0
        }

        [Test]
        public void ToLevel_Floors_At_The_Raw_Msd_Where_The_Curve_Compresses()
        {
            // 低 MSD 時曲線值比原始 MSD 還小 → 取原始 MSD,免得簡單譜顯示得比自己的 MSD 更低。
            Assert.AreEqual(10, ManiaMsd.ToLevel(10f));   // 曲線只有 7.94
            Assert.AreEqual(5, ManiaMsd.ToLevel(5f));     // 曲線只有 2.13
        }

        [Test]
        public void ToLevel_Caps_At_999()
        {
            Assert.AreEqual(892, ManiaMsd.ToLevel(120f), "上限以下照實顯示");
            Assert.AreEqual(ManiaMsd.LevelMax, ManiaMsd.ToLevel(200f));      // 曲線 2357 → 封在 999
            Assert.AreEqual(ManiaMsd.LevelMax, ManiaMsd.ToLevel(100000f));   // 爆掉的評分也不會溢位成怪數字
        }
    }
}
