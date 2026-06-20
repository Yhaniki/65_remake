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
            Assert.AreEqual("Windowed", s.display.displayMode);
            Assert.AreEqual("zh-TW", s.language);
        }

        [Test]
        public void Preset_IndexOf_Finds_Known_And_Misses_Unknown()
        {
            Assert.GreaterOrEqual(ResolutionPreset.IndexOf(1280, 720), 0);
            Assert.AreEqual(-1, ResolutionPreset.IndexOf(1234, 567));
        }
    }
}
