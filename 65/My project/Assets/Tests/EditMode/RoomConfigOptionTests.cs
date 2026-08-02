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
            src.gameplay.songBombs = false;   // 非預設值（預設是 true＝照譜面原樣有雷）才驗得出有沒有落地
            src.gameplay.panelOpacity = 1.1f; src.gameplay.effectScene = false;

            RoomConfig.CaptureOptionFrom(src);
            string ini = RoomConfig.Serialize();

            // wipe mirrors to defaults, then parse the produced ini back
            RoomConfig.hasOption = false; RoomConfig.hasOptUiScale = false;
            RoomConfig.optBgm = RoomConfig.optMusic = RoomConfig.optSfx = 0.5f;
            RoomConfig.optDispW = 1024; RoomConfig.optLang = "zh-TW"; RoomConfig.optUiScale = 1f;
            RoomConfig.optFullscreenFill = false; RoomConfig.optCameraAuto = true; RoomConfig.optCameraFixed = 0;
            RoomConfig.optPlayFullSong = false; RoomConfig.optSongBombs = true;
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
            Assert.IsFalse(dst.gameplay.songBombs, "進階「歌曲炸彈」要跟著 config.ini 走");
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
            // 比 "opt_keys=" 而非 "opt_keys"：檔頭那行「鍵位已搬到 keymaps.ini」的註解本來就會提到這個名字。
            StringAssert.DoesNotContain("opt_keys=", RoomConfig.Serialize());
            StringAssert.DoesNotContain("opt_keysAux=", RoomConfig.Serialize());
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

        // ---- 「停用炸彈」→「歌曲炸彈」：改名同時語意反過來（開＝有雷），舊鍵要搬得過來 ----

        [Test]
        public void SongBombs_Defaults_To_On_And_RoundTrips()
        {
            RoomConfig.optSongBombs = true;
            Assert.IsTrue(new GameSettings().gameplay.songBombs, "預設開＝照譜面原樣有雷");

            RoomConfig.optSongBombs = false;
            RoomConfig.ParseInto(RoomConfig.Serialize());
            Assert.IsFalse(RoomConfig.optSongBombs);
            Assert.IsTrue(RoomConfig.hasSongBombsKey, "自己寫出來的模板一定帶新鍵");
            StringAssert.Contains("opt_songBombs=", RoomConfig.Serialize());
            StringAssert.DoesNotContain("opt_disableBombs=", RoomConfig.Serialize(), "舊鍵不再寫回");
        }

        [Test]
        public void Legacy_DisableBombs_Key_Migrates_Inverted()
        {
            // 舊鍵語意相反：opt_disableBombs=1（把炸彈拿掉）→ opt_songBombs=0。
            RoomConfig.optSongBombs = true; RoomConfig.hasSongBombsKey = false;
            RoomConfig.ParseInto("[Option]\nopt_disableBombs=1\n");
            Assert.IsFalse(RoomConfig.optSongBombs, "舊檔說要拿掉炸彈 → 新鍵是關");
            Assert.IsFalse(RoomConfig.hasSongBombsKey, "只有舊鍵 → Load 要補寫一次模板換成新鍵");

            RoomConfig.optSongBombs = false;
            RoomConfig.ParseInto("[Option]\nopt_disableBombs=0\n");
            Assert.IsTrue(RoomConfig.optSongBombs, "舊檔說照譜面原樣 → 新鍵是開");
        }

        // activeId 已經從 config.ini 拉去 DATA/PROFILE/profile.json（見 ProfileDefaultsTests）。
    }
}
