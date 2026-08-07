using NUnit.Framework;
using Sdo.Settings;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 畫面亮度（開場設定面板「顯示」頁的 opt_brightness）：預設/夾值/落地，以及 overlay 的乘法倍率換算。
    /// 全是純函式，不建 canvas、不碰檔案。
    /// </summary>
    public class BrightnessTests
    {
        [Test]
        public void Default_Is_Untouched()
        {
            Assert.AreEqual(1f, new GameSettings().display.brightness, 1e-4f, "全新安裝＝畫面原樣，不加任何 overlay");
            Assert.AreEqual(1f, DisplaySettings.DefaultBrightness, 1e-4f);
            Assert.Less(DisplaySettings.MinBrightness, 1f);
            Assert.Greater(DisplaySettings.MaxBrightness, 1f);
        }

        [Test]
        public void Sanitize_Clamps_And_Repairs_Zero()
        {
            var s = new GameSettings();
            s.display.brightness = 0f;                       // 舊檔沒有這個鍵 → JsonUtility/新欄位會是 0
            DisplaySettingsManager.Sanitize(s);
            Assert.AreEqual(1f, s.display.brightness, 1e-4f, "0 不能被當成「全黑」");

            s.display.brightness = 9f;
            DisplaySettingsManager.Sanitize(s);
            Assert.AreEqual(DisplaySettings.MaxBrightness, s.display.brightness, 1e-4f);

            s.display.brightness = -3f;
            DisplaySettingsManager.Sanitize(s);
            Assert.AreEqual(1f, s.display.brightness, 1e-4f, "負值同 0：回預設而不是夾到下限");
        }

        [Test]
        public void RoomConfig_Sanitize_Clamps_Mirror()
        {
            RoomConfig.optBrightness = 0f;
            RoomConfig.Sanitize();
            Assert.AreEqual(1f, RoomConfig.optBrightness, 1e-4f);

            RoomConfig.optBrightness = 99f;
            RoomConfig.Sanitize();
            Assert.AreEqual(DisplaySettings.MaxBrightness, RoomConfig.optBrightness, 1e-4f);
            RoomConfig.optBrightness = 1f;
        }

        [Test]
        public void Template_Carries_The_Key_And_Old_Ini_Is_Flagged_For_Rewrite()
        {
            StringAssert.Contains("opt_brightness=", RoomConfig.Serialize(), "模板要帶這個鍵，玩家才手改得到");
            Assert.IsTrue(RoomConfig.IsMissingCurrentKey("[Option]\nopt_bgm=0.5\nopt_uiScale=1\n"),
                "舊檔沒有 opt_brightness → Load 要補寫一次模板");
        }

        [Test]
        public void Panel_Has_The_Brightness_Row_In_The_Display_Tab()
        {
            var f = StartupConfigSchema.ByKey("opt_brightness");
            Assert.IsNotNull(f, "亮度要出現在開場設定面板上（不然 config.ini 沒有任何 UI 入口）");
            Assert.AreEqual(StartupConfigSchema.CatText, f.Category, "歸在「顯示」分頁");
            Assert.AreEqual(ConfigFieldKind.Slider, f.Kind);
            Assert.AreEqual(DisplaySettings.MinBrightness, f.Min, 1e-4f);
            Assert.AreEqual(DisplaySettings.MaxBrightness, f.Max, 1e-4f);
        }

        [Test]
        public void Panel_Row_Writes_The_Runtime_Working_Copy()
        {
            // 鐵則：opt_* 要改工作副本，不能改 RoomConfig 鏡像（存檔走 CaptureOptionFrom(Settings)，改鏡像會被蓋回去）。
            var f = StartupConfigSchema.ByKey("opt_brightness");
            float before = DisplaySettingsManager.Settings.display.brightness;
            f.SetNumber(1.3f);
            Assert.AreEqual(1.3f, DisplaySettingsManager.Settings.display.brightness, 1e-4f);
            Assert.AreEqual(1.3f, f.GetNumber(), 1e-4f);
            DisplaySettingsManager.Settings.display.brightness = before;
        }

        [Test]
        public void Panel_Row_Snaps_And_Clamps()
        {
            var f = StartupConfigSchema.ByKey("opt_brightness");
            Assert.AreEqual(DisplaySettings.MaxBrightness, f.SnapNumber(5f), 1e-4f);
            Assert.AreEqual(DisplaySettings.MinBrightness, f.SnapNumber(0f), 1e-4f);
            Assert.AreEqual(1.1f, f.SnapNumber(1.12f), 1e-4f, "0.05 一格");
        }

        // ---- overlay 的乘法倍率（BrightnessOverlay 的純函式）----

        [Test]
        public void One_Draws_Nothing()
        {
            Assert.IsTrue(BrightnessOverlay.IsIdentity(1f), "原樣＝整張 quad 不畫");
            Assert.IsFalse(BrightnessOverlay.IsIdentity(0.9f));
            Assert.IsFalse(BrightnessOverlay.IsIdentity(1.1f));
        }

        [Test]
        public void Below_One_Multiplies_The_Frame()
        {
            // Blend DstColor Zero → 畫面 × _Gain，所以 _Gain 就是亮度本身。
            Assert.IsFalse(BrightnessOverlay.IsBoost(0.5f));
            Assert.AreEqual(0.5f, BrightnessOverlay.GainFor(0.5f), 1e-4f);
            Assert.AreEqual(0.8f, BrightnessOverlay.GainFor(0.8f), 1e-4f);
        }

        [Test]
        public void Above_One_Adds_A_Second_Copy()
        {
            // Blend DstColor One → 畫面 ×(1+_Gain)，所以 _Gain = b−1。
            Assert.IsTrue(BrightnessOverlay.IsBoost(1.5f));
            Assert.AreEqual(0.5f, BrightnessOverlay.GainFor(1.5f), 1e-4f);
            Assert.AreEqual(1f, BrightnessOverlay.GainFor(2f), 1e-4f, "2× 剛好把 LDR 的 gain 用滿");
        }

        [Test]
        public void Gain_Never_Leaves_What_LDR_Can_Express()
        {
            // fragment 值在 LDR 下夾在 [0,1]：亮度上限就是「gain 剛好 1」那一點，設定的 Max 不能超過它。
            Assert.AreEqual(1f, BrightnessOverlay.GainFor(DisplaySettings.MaxBrightness), 1e-4f);
            for (float b = DisplaySettings.MinBrightness; b <= DisplaySettings.MaxBrightness; b += 0.05f)
                Assert.That(BrightnessOverlay.GainFor(b), Is.InRange(0f, 1f));
        }

        [Test]
        public void CurrentBrightness_Clamps_Whatever_Is_In_Settings()
        {
            float before = DisplaySettingsManager.Settings.display.brightness;
            DisplaySettingsManager.Settings.display.brightness = 0f;
            Assert.AreEqual(1f, BrightnessOverlay.CurrentBrightness(), 1e-4f, "壞值 → 原樣，不是全黑");
            DisplaySettingsManager.Settings.display.brightness = 99f;
            Assert.AreEqual(DisplaySettings.MaxBrightness, BrightnessOverlay.CurrentBrightness(), 1e-4f);
            DisplaySettingsManager.Settings.display.brightness = before;
        }
    }
}
