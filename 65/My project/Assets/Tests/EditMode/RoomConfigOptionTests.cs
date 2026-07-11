using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// OPTION 對話框設定落地 per-user config.ini（使用者要求）。驗證純函式的來回：GameSettings → RoomConfig 鏡像 →
    /// Serialize → ParseInto → ApplyOptionTo，值一致；且未帶 [Option] 的舊檔不會誤標 hasOption。不碰檔案。
    /// </summary>
    public class RoomConfigOptionTests
    {
        [Test]
        public void Option_RoundTrips_Through_Ini_Text()
        {
            var src = new GameSettings();
            src.audio.bgm = 0.2f; src.audio.gameMusic = 0.9f; src.audio.sfx = 0.7f;
            src.keys.lane4 = new[] { "J", "K", "I", "L" };
            src.keys.lane4aux = new[] { "Keypad4", "Keypad2", "Keypad8", "Keypad6" };
            src.display.width = 1280; src.display.height = 960; src.display.displayMode = "Fullscreen"; src.display.vsync = false;
            src.language = "en";
            src.gameplay.fullscreenFill = true; src.gameplay.cameraAuto = false; src.gameplay.playFullSong = true;
            src.gameplay.panelOpacity = 1.1f; src.gameplay.effectScene = false;

            RoomConfig.CaptureOptionFrom(src);
            string ini = RoomConfig.Serialize();

            // wipe mirrors to defaults, then parse the produced ini back
            RoomConfig.hasOption = false;
            RoomConfig.optBgm = RoomConfig.optMusic = RoomConfig.optSfx = 0.5f;
            RoomConfig.optKeys = "A,S,W,D"; RoomConfig.optDispW = 1024; RoomConfig.optLang = "zh-TW";
            RoomConfig.optFullscreenFill = false; RoomConfig.optCameraAuto = true; RoomConfig.optPlayFullSong = false;
            RoomConfig.ParseInto(ini);
            Assert.IsTrue(RoomConfig.hasOption, "[Option] 區應被辨識");

            var dst = new GameSettings();
            RoomConfig.ApplyOptionTo(dst);

            Assert.AreEqual(0.2f, dst.audio.bgm, 1e-4f);
            Assert.AreEqual(0.9f, dst.audio.gameMusic, 1e-4f);
            Assert.AreEqual(0.7f, dst.audio.sfx, 1e-4f);
            CollectionAssert.AreEqual(new[] { "J", "K", "I", "L" }, dst.keys.lane4);
            CollectionAssert.AreEqual(new[] { "Keypad4", "Keypad2", "Keypad8", "Keypad6" }, dst.keys.lane4aux);
            Assert.AreEqual(1280, dst.display.width);
            Assert.AreEqual(960, dst.display.height);
            Assert.AreEqual("Fullscreen", dst.display.displayMode);
            Assert.IsFalse(dst.display.vsync);
            Assert.AreEqual("en", dst.language);
            Assert.IsTrue(dst.gameplay.fullscreenFill);
            Assert.IsFalse(dst.gameplay.cameraAuto);
            Assert.IsTrue(dst.gameplay.playFullSong);
            Assert.IsFalse(dst.gameplay.effectScene);
            Assert.AreEqual(1.1f, dst.gameplay.panelOpacity, 1e-4f);
        }

        [Test]
        public void RoomOnly_Ini_Does_Not_Flag_Option()
        {
            RoomConfig.hasOption = false;
            RoomConfig.ParseInto("[Room]\ndefaultTeam=1\ndefaultScene=5\n");
            Assert.IsFalse(RoomConfig.hasOption, "只有 [Room] 的舊檔不應被當成帶 OPTION");
        }

        [Test]
        public void ClearedKeySlot_Is_Preserved_As_Empty()
        {
            var src = new GameSettings();
            src.keys.lane4 = new[] { "A", "", "W", "D" };   // 第二格刻意清空
            RoomConfig.CaptureOptionFrom(src);
            var dst = new GameSettings();
            RoomConfig.ApplyOptionTo(dst);
            Assert.AreEqual("", dst.keys.lane4[1], "清空的鍵格不應被還原成預設鍵");
        }
    }
}
