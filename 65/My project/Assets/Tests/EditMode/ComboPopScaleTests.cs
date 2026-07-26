using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 判定字 / COMBO 的「打中就彈一下」曲線（<see cref="ScreenGameplay.PopScale"/>）。
    /// 官方是 命中瞬間 ×1.6 → 線性收回 ×0.8（＝ peak 2.0 × 靜止 0.8）；peak 開放給玩家在 config.ini 調
    /// （comboTextPop / judgeTextPop），收回速度 rate 仍寫死（COMBO 9、判定字 6）。
    /// </summary>
    public class ComboPopScaleTests
    {
        [Test]
        public void Official_Peak_Two_Reproduces_The_Decompiled_Curve()
        {
            // 命中瞬間 = 靜止 ×2 = 1.6；收完 = 0.8。這兩個數字是照抄反編譯來的，改壞會整個手感跑掉。
            Assert.AreEqual(1.6f, ScreenGameplay.PopScale(0f, 9f, 2f), 1e-4f);
            Assert.AreEqual(0.8f, ScreenGameplay.PopScale(1f, 9f, 2f), 1e-4f);
            Assert.AreEqual(ScreenGameplay.PopRest, ScreenGameplay.PopScale(1f, 9f, 2f), 1e-4f);
        }

        [Test]
        public void Peak_Scales_Only_The_Amplitude_Not_The_Rest_Size()
        {
            // 調 peak 只動「彈多高」；靜止大小永遠是 PopRest（不然連段字平常就會整個變大/變小）。
            Assert.AreEqual(0.8f * 3f, ScreenGameplay.PopScale(0f, 9f, 3f), 1e-4f);
            Assert.AreEqual(0.8f, ScreenGameplay.PopScale(1f, 9f, 3f), 1e-4f, "收回後與 peak 無關");
            Assert.AreEqual(0.8f, ScreenGameplay.PopScale(0f, 9f, 1f), 1e-4f, "peak=1 → 從頭到尾都是靜止大小");
        }

        [Test]
        public void Rate_Sets_How_Long_The_Pop_Lasts_And_Never_Overshoots()
        {
            // rate = 1/持續秒數：COMBO 9 → 111ms、判定字 6 → 167ms。中點剛好在峰值與靜止的正中間。
            Assert.AreEqual(0.8f, ScreenGameplay.PopScale(1f / 9f, 9f, 2f), 1e-4f, "1/9 秒後 COMBO 收完");
            Assert.AreEqual(0.8f, ScreenGameplay.PopScale(1f / 6f, 6f, 2f), 1e-4f, "1/6 秒後判定字收完");
            Assert.AreEqual(1.2f, ScreenGameplay.PopScale(1f / 18f, 9f, 2f), 1e-4f, "半途 = 峰值與靜止的中間");

            // 收完之後不會繼續縮（Clamp01 的下限），負的 age（時鐘倒退/尚未觸發）也不會超過峰值。
            Assert.AreEqual(0.8f, ScreenGameplay.PopScale(10f, 9f, 2f), 1e-4f);
            Assert.AreEqual(1.6f, ScreenGameplay.PopScale(-5f, 9f, 2f), 1e-4f);
        }
    }
}
