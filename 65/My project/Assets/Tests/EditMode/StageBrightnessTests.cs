using NUnit.Framework;
using Sdo.Game;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// 舞台背景亮度（遊玩畫面；opt_stageBrightness）：預設/夾值/落地/面板那一列，以及 StageBackdropDim 判斷
    /// 「哪種材質可以連 alpha 一起淡掉」的規則。純函式，不建場景。
    /// </summary>
    public class StageBrightnessTests
    {
        [Test]
        public void Default_Is_Untouched()
        {
            Assert.AreEqual(1f, new GameSettings().gameplay.stageBrightness, 1e-4f, "全新安裝＝舞台照原樣亮");
        }

        [Test]
        public void Sanitize_Clamps_To_Zero_One()
        {
            var s = new GameSettings();
            s.gameplay.stageBrightness = 5f;
            DisplaySettingsManager.Sanitize(s);
            Assert.AreEqual(1f, s.gameplay.stageBrightness, 1e-4f);

            s.gameplay.stageBrightness = -2f;
            DisplaySettingsManager.Sanitize(s);
            Assert.AreEqual(0f, s.gameplay.stageBrightness, 1e-4f, "0 是合法值＝全黑只剩人物");
        }

        [Test]
        public void RoundTrips_Through_Ini_Text()
        {
            var src = new GameSettings();
            src.gameplay.stageBrightness = 0.35f;
            RoomConfig.CaptureOptionFrom(src);
            string ini = RoomConfig.Serialize();
            StringAssert.Contains("opt_stageBrightness=", ini, "模板要帶這個鍵，玩家才手改得到");

            RoomConfig.optStageBrightness = 1f;
            RoomConfig.ParseInto(ini);
            var dst = new GameSettings();
            RoomConfig.ApplyOptionTo(dst);
            Assert.AreEqual(0.35f, dst.gameplay.stageBrightness, 1e-4f);

            RoomConfig.optStageBrightness = 1f;
            RoomConfig.Sanitize();
        }

        [Test]
        public void Old_Ini_Without_The_Key_Is_Flagged_For_Rewrite()
        {
            Assert.IsTrue(RoomConfig.IsMissingCurrentKey("[Option]\nopt_bgm=0.5\nopt_panelOpacity=1.4\n"));
        }

        [Test]
        public void Panel_Has_The_Row_In_The_Display_Tab()
        {
            var f = StartupConfigSchema.ByKey("opt_stageBrightness");
            Assert.IsNotNull(f, "舞台背景亮度要有 UI 入口，否則覆蓋率測試也會紅");
            Assert.AreEqual(StartupConfigSchema.CatText, f.Category);
            Assert.AreEqual(ConfigFieldKind.Slider, f.Kind);
            Assert.AreEqual(0f, f.Min, 1e-4f, "下限要能到 0＝全黑只剩人物");
            Assert.AreEqual(1f, f.Max, 1e-4f);
        }

        [Test]
        public void Panel_Row_Writes_The_Runtime_Working_Copy()
        {
            var f = StartupConfigSchema.ByKey("opt_stageBrightness");
            float before = DisplaySettingsManager.Settings.gameplay.stageBrightness;
            f.SetNumber(0f);
            Assert.AreEqual(0f, DisplaySettingsManager.Settings.gameplay.stageBrightness, 1e-4f);
            Assert.AreEqual(0f, f.GetNumber(), 1e-4f);
            DisplaySettingsManager.Settings.gameplay.stageBrightness = before;
        }

        // ---- 哪種材質可以連 alpha 一起收掉 ----

        [Test]
        public void AlphaBlended_Backdrop_Fades_Its_Alpha_Too()
        {
            // 只把 rgb 乘暗的話，全黑時這類會變成一塊「黑色半透明」，蓋在人物前面就把人物也壓暗了。
            Assert.IsTrue(StageBackdropDim.ShouldFadeAlpha("Sdo/UnlitInstancedAlpha"));
            Assert.IsTrue(StageBackdropDim.ShouldFadeAlpha("Sdo/UnlitInstancedAlphaCullBack"));
            Assert.IsTrue(StageBackdropDim.ShouldFadeAlpha("Sdo/SceneVertexAlpha"));
            Assert.IsTrue(StageBackdropDim.ShouldFadeAlpha("Sdo/UnlitOverlay"));
            Assert.IsTrue(StageBackdropDim.ShouldFadeAlpha("Unlit/Transparent"));
        }

        [Test]
        public void Cutout_And_Opaque_Keep_Their_Alpha()
        {
            // clip() 吃的就是 alpha：乘下去邊緣會被啃掉，甚至整片被裁光。
            Assert.IsFalse(StageBackdropDim.ShouldFadeAlpha("Sdo/UnlitInstancedCutout"));
            Assert.IsFalse(StageBackdropDim.ShouldFadeAlpha("Sdo/SceneVertexCutout"));
            Assert.IsFalse(StageBackdropDim.ShouldFadeAlpha("Sdo/UnlitInstanced"));
            Assert.IsFalse(StageBackdropDim.ShouldFadeAlpha("Unlit/Texture"));
        }

        [Test]
        public void Additive_Keeps_Its_Alpha()
        {
            // additive 只看 rgb，rgb 乘到 0 就不加光了；動 alpha 只會讓衰減變成平方。
            Assert.IsFalse(StageBackdropDim.ShouldFadeAlpha("Sdo/UnlitAdditiveOverlay"));
        }
    }
}
