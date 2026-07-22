using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// OPTION 對話框設定落地 config.ini 的 [Option]。驗證純函式的來回：GameSettings → RoomConfig 鏡像 →
    /// Serialize → ParseInto → ApplyOptionTo，值一致；且未帶 [Option] 的舊檔不會誤標 hasOption。不碰檔案。
    /// 鍵位已搬去 keymaps.ini（見 KeyMapTests），不再走這條路。
    /// </summary>
    public class RoomConfigOptionTests
    {
        [Test]
        public void Option_RoundTrips_Through_Ini_Text()
        {
            var src = new GameSettings();
            src.audio.bgm = 0.2f; src.audio.gameMusic = 0.9f; src.audio.sfx = 0.7f;
            src.display.width = 1280; src.display.height = 960; src.display.displayMode = "Fullscreen"; src.display.vsync = false;
            src.display.uiScale = 1.25f;
            src.language = "en";
            src.gameplay.fullscreenFill = true; src.gameplay.cameraAuto = false; src.gameplay.cameraFixed = 4;
            src.gameplay.playFullSong = true;
            src.gameplay.panelOpacity = 1.1f; src.gameplay.effectScene = false;

            RoomConfig.CaptureOptionFrom(src);
            string ini = RoomConfig.Serialize();

            // wipe mirrors to defaults, then parse the produced ini back
            RoomConfig.hasOption = false; RoomConfig.hasOptUiScale = false;
            RoomConfig.optBgm = RoomConfig.optMusic = RoomConfig.optSfx = 0.5f;
            RoomConfig.optDispW = 1024; RoomConfig.optLang = "zh-TW"; RoomConfig.optUiScale = 1f;
            RoomConfig.optFullscreenFill = false; RoomConfig.optCameraAuto = true; RoomConfig.optCameraFixed = 0;
            RoomConfig.optPlayFullSong = false;
            RoomConfig.ParseInto(ini);
            Assert.IsTrue(RoomConfig.hasOption, "[Option] 區應被辨識");
            Assert.IsTrue(RoomConfig.hasOptUiScale, "opt_uiScale 應被寫出且辨識得到");

            var dst = new GameSettings();
            RoomConfig.ApplyOptionTo(dst);

            Assert.AreEqual(0.2f, dst.audio.bgm, 1e-4f);
            Assert.AreEqual(0.9f, dst.audio.gameMusic, 1e-4f);
            Assert.AreEqual(0.7f, dst.audio.sfx, 1e-4f);
            Assert.AreEqual(1.25f, dst.display.uiScale, 1e-4f, "uiScale 以前只在 settings.json，併進來後不能掉");
            Assert.AreEqual(1280, dst.display.width);
            Assert.AreEqual(960, dst.display.height);
            Assert.AreEqual("Fullscreen", dst.display.displayMode);
            Assert.IsFalse(dst.display.vsync);
            Assert.AreEqual("en", dst.language);
            Assert.IsTrue(dst.gameplay.fullscreenFill);
            Assert.IsFalse(dst.gameplay.cameraAuto);
            Assert.AreEqual(4, dst.gameplay.cameraFixed, "F2 記住的固定鏡頭台號要跟著 config.ini 走");
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
        public void Keys_Are_No_Longer_Written_To_ConfigIni()
        {
            // 鍵位搬去 keymaps.ini：config.ini 不該再寫出 opt_keys（否則兩邊會各存一份、改了對不上）。
            var src = new GameSettings();
            src.keys.lane4 = new[] { "J", "K", "I", "L" };
            RoomConfig.CaptureOptionFrom(src);
            StringAssert.DoesNotContain("opt_keys", RoomConfig.Serialize());
        }

        [Test]
        public void Legacy_Keys_Still_Parse_For_Migration()
        {
            // 舊檔的 opt_keys 仍要讀得進來（開機時 KeyMap.Load 拿它種 keymaps.ini），只是不再寫回。
            RoomConfig.optKeys = "A,S,W,D";
            RoomConfig.ParseInto("[Option]\nopt_keys=J,K,I,L\nopt_keysAux=Keypad4,Keypad2,Keypad8,Keypad6\n");
            Assert.AreEqual("J,K,I,L", RoomConfig.optKeys);
            Assert.AreEqual("Keypad4,Keypad2,Keypad8,Keypad6", RoomConfig.optKeysAux);
        }

        // ---- [Profile] activeId：以前是獨立的 active.txt，現在併進 config.ini ----

        [Test]
        public void ActiveId_RoundTrips_Through_Ini_Text()
        {
            RoomConfig.activeId = "00000001";
            string ini = RoomConfig.Serialize();
            RoomConfig.activeId = "";
            RoomConfig.ParseInto(ini);
            Assert.AreEqual("00000001", RoomConfig.activeId);
        }

        [Test]
        public void ActiveId_Sanitize_Rejects_NonEightDigit()
        {
            Assert.AreEqual("", RoomConfig.SanitizeActiveId("1"), "非 8 位數 → 當沒設定");
            Assert.AreEqual("", RoomConfig.SanitizeActiveId("0000000a"), "非數字 → 當沒設定");
            Assert.AreEqual("", RoomConfig.SanitizeActiveId(null));
            Assert.AreEqual("00000000", RoomConfig.SanitizeActiveId(" 00000000 "), "前後空白要吃掉");
        }
    }
}
