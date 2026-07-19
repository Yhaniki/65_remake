using NUnit.Framework;
using UnityEngine;
using Sdo.Settings;

namespace Sdo.Tests
{
    public class SettingsTests
    {
        [Test]
        public void Defaults_Are_Valid()
        {
            var s = new GameSettings();
            Assert.IsTrue(ResolutionPreset.IsValid(s.display.width, s.display.height));
        }

        [Test]
        public void Json_RoundTrip_Preserves_Fields()
        {
            var s = new GameSettings();
            s.display.width = 1920; s.display.height = 1080; s.display.displayMode = "Fullscreen";
            s.audio.bgm = 0.5f; s.language = "ja";
            var b = JsonUtility.FromJson<GameSettings>(JsonUtility.ToJson(s));
            Assert.AreEqual(1920, b.display.width);
            Assert.AreEqual(1080, b.display.height);
            Assert.AreEqual("Fullscreen", b.display.displayMode);
            Assert.AreEqual(0.5f, b.audio.bgm, 1e-4f);
            Assert.AreEqual("ja", b.language);
        }

        [Test]
        public void Clamp_OutOfRange_Snaps_To_Valid_Preset()
        {
            var c = ResolutionPreset.Clamp(100, 100);
            Assert.IsTrue(ResolutionPreset.IsValid(c.Width, c.Height));
        }

        [Test]
        public void Clamp_Valid_Is_Unchanged()
        {
            var c = ResolutionPreset.Clamp(1280, 720);
            Assert.AreEqual(1280, c.Width);
            Assert.AreEqual(720, c.Height);
        }

        [Test]
        public void DisplayMode_String_Mapping_RoundTrips()
        {
            Assert.AreEqual(FullScreenMode.Windowed, DisplaySettingsManager.ToMode("Windowed"));
            Assert.AreEqual(FullScreenMode.ExclusiveFullScreen, DisplaySettingsManager.ToMode("Fullscreen"));
            Assert.AreEqual(FullScreenMode.FullScreenWindow, DisplaySettingsManager.ToMode("Borderless"));
            Assert.AreEqual("Windowed", DisplaySettingsManager.FromMode(FullScreenMode.Windowed));
            Assert.AreEqual("Fullscreen", DisplaySettingsManager.FromMode(FullScreenMode.ExclusiveFullScreen));
            Assert.AreEqual("Borderless", DisplaySettingsManager.FromMode(FullScreenMode.FullScreenWindow));
        }

        [Test]
        public void Sanitize_Clamps_Invalid_Values()
        {
            var s = new GameSettings();
            s.display.width = 50; s.display.height = 50; s.display.uiScale = 0f;
            s.display.displayMode = ""; s.audio.bgm = 5f; s.audio.sfx = -1f; s.language = "";
            DisplaySettingsManager.Sanitize(s);
            Assert.IsTrue(ResolutionPreset.IsValid(s.display.width, s.display.height));
            Assert.AreEqual(1f, s.display.uiScale, 1e-4f);
            Assert.AreEqual(1f, s.audio.bgm, 1e-4f);
            Assert.AreEqual(0f, s.audio.sfx, 1e-4f);
            Assert.AreEqual("Borderless", s.display.displayMode);   // 預設全螢幕視窗化（同 GameSettings 的預設值）
            Assert.AreEqual("zh-TW", s.language);
        }

        [Test]
        public void Preset_IndexOf_Finds_Known_And_Misses_Unknown()
        {
            Assert.GreaterOrEqual(ResolutionPreset.IndexOf(1024, 768), 0);
            Assert.AreEqual(-1, ResolutionPreset.IndexOf(1234, 567));
        }

        [Test]
        public void All_Presets_Are_4_By_3()
        {
            foreach (var p in ResolutionPreset.Presets)
                Assert.AreEqual(4f / 3f, (float)p.Width / p.Height, 1e-3f, $"{p} is not 4:3");
        }

        // ---- GameplaySettings (OPTION 遊戲頁) ----

        [Test]
        public void Gameplay_Defaults_All_On_And_Official_Opacity()
        {
            var g = new GameSettings().gameplay;
            Assert.IsFalse(g.fullscreenFill);     // 窗口 (Pillarbox 左右黑邊) 預設
            Assert.IsTrue(g.bloom);
            Assert.IsTrue(g.notesPanelLeft);
            Assert.IsTrue(g.effectCharacter);
            Assert.IsTrue(g.effectScene);
            Assert.IsTrue(g.cameraAuto);          // 默認 (自動導播)
            Assert.IsTrue(g.callCardInGame);
            Assert.IsFalse(g.playFullSong);       // 完奏模式 預設關
            Assert.IsTrue(g.songSpeed);           // 歌曲變速 預設開
            Assert.AreEqual(1.4f, g.panelOpacity, 1e-4f);   // 官方預設
        }

        [Test]
        public void Sanitize_Clamps_PanelOpacity_To_0_1p6()
        {
            var over = new GameSettings(); over.gameplay.panelOpacity = 9f;
            DisplaySettingsManager.Sanitize(over);
            Assert.AreEqual(GameplaySettings.MaxPanelOpacity, over.gameplay.panelOpacity, 1e-4f);

            var under = new GameSettings(); under.gameplay.panelOpacity = -3f;
            DisplaySettingsManager.Sanitize(under);
            Assert.AreEqual(0f, under.gameplay.panelOpacity, 1e-4f);
        }

        [Test]
        public void Sanitize_Fills_Null_Gameplay()
        {
            var s = new GameSettings { gameplay = null };
            DisplaySettingsManager.Sanitize(s);
            Assert.IsNotNull(s.gameplay);
            Assert.AreEqual(1.4f, s.gameplay.panelOpacity, 1e-4f);
        }

        [Test]
        public void Gameplay_Survives_Json_RoundTrip()
        {
            var s = new GameSettings();
            s.gameplay.fullscreenFill = false;   // 黑邊 (Pillarbox)
            s.gameplay.effectCharacter = false;
            s.gameplay.effectScene = false;
            s.gameplay.cameraAuto = false;       // 固定
            s.gameplay.panelOpacity = 0.75f;
            var b = JsonUtility.FromJson<GameSettings>(JsonUtility.ToJson(s));
            Assert.IsFalse(b.gameplay.fullscreenFill);
            Assert.IsFalse(b.gameplay.effectCharacter);
            Assert.IsFalse(b.gameplay.effectScene);
            Assert.IsFalse(b.gameplay.cameraAuto);
            Assert.AreEqual(0.75f, b.gameplay.panelOpacity, 1e-4f);
        }
    }
}
