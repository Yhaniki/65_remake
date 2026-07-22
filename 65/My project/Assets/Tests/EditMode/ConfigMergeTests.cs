using System.IO;
using NUnit.Framework;
using UnityEngine;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// 「把 active.txt 和 settings.json 合併進 config.ini」的一次性搬遷（使用者要求）。用一個 temp 的 PROFILE 根
    /// 種舊檔 → RoomConfig.Load() → 驗值進了 config.ini、舊檔被刪、且鍵位落到同層的 keymaps.ini。
    /// </summary>
    public class ConfigMergeTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "sdo_merge_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
            ProfileManager.Root = _root;
            RoomConfig.hasOption = false; RoomConfig.hasOptUiScale = false; RoomConfig.activeId = "";
        }

        [TearDown]
        public void TearDown()
        {
            ProfileManager.Root = null;   // 還原 lazy 解析，避免污染其他測試
            RoomConfig.activeId = "";
            try { Directory.Delete(_root, true); } catch { /* best effort */ }
        }

        private string ConfigPath => Path.Combine(_root, RoomConfig.FileName);
        private string KeymapPath => Path.Combine(_root, KeyMap.FileName);

        [Test]
        public void Load_Merges_ActiveTxt_And_SettingsJson_Into_ConfigIni()
        {
            // 舊佈局：active.txt + settings.json + （這次故意沒有）config.ini
            File.WriteAllText(Path.Combine(_root, ProfileManager.ActiveFileName), "00000001");
            var legacy = new GameSettings();
            legacy.audio.bgm = 0.25f; legacy.audio.gameMusic = 0.75f;
            legacy.display.width = 1280; legacy.display.height = 960; legacy.display.uiScale = 1.25f;
            legacy.keys.lane4 = new[] { "J", "K", "I", "L" };
            legacy.gameplay.playFullSong = true;
            File.WriteAllText(Path.Combine(_root, DisplaySettingsManager.LegacyFileName), JsonUtility.ToJson(legacy, true));

            RoomConfig.Load();
            KeyMap.Load();

            Assert.IsTrue(File.Exists(ConfigPath), "應產生合併後的 config.ini");
            Assert.AreEqual("00000001", RoomConfig.activeId, "active.txt 的角色要進 [Profile] activeId");
            Assert.AreEqual(0.25f, RoomConfig.optBgm, 1e-4f, "settings.json 的音量要進 [Option]");
            Assert.AreEqual(0.75f, RoomConfig.optMusic, 1e-4f);
            Assert.AreEqual(1280, RoomConfig.optDispW);
            Assert.AreEqual(1.25f, RoomConfig.optUiScale, 1e-4f, "uiScale 以前只存在 settings.json，不能在搬遷中掉了");
            Assert.IsTrue(RoomConfig.optPlayFullSong);
            CollectionAssert.AreEqual(new[] { "J", "K", "I", "L" }, KeyMap.lane4, "鍵位要落到 keymaps.ini");

            // 合併完舊檔就該消失 —— 以後只有 config.ini + keymaps.ini 兩個檔。
            Assert.IsFalse(File.Exists(Path.Combine(_root, ProfileManager.ActiveFileName)), "active.txt 應被移除");
            Assert.IsFalse(File.Exists(Path.Combine(_root, DisplaySettingsManager.LegacyFileName)), "settings.json 應被移除");
            Assert.IsTrue(File.Exists(KeymapPath), "應產生 keymaps.ini");

            // 重開一次（只剩 config.ini）值要一樣 —— 搬遷不是一次性有效而已。
            RoomConfig.hasOption = false; RoomConfig.activeId = ""; RoomConfig.optBgm = 0.5f; RoomConfig.optDispW = 800;
            RoomConfig.Load();
            Assert.AreEqual("00000001", RoomConfig.activeId);
            Assert.AreEqual(0.25f, RoomConfig.optBgm, 1e-4f);
            Assert.AreEqual(1280, RoomConfig.optDispW);
        }

        [Test]
        public void Load_Moves_Legacy_ConfigIni_Keys_Into_Keymaps()
        {
            // 上一版的 config.ini 把 4 鍵放在 [Option]：搬進 keymaps.ini，且新寫出的 config.ini 不再有 opt_keys。
            File.WriteAllText(ConfigPath,
                "[Room]\ndefaultTeam=1\n[Option]\nopt_bgm=0.3\nopt_keys=J,K,I,L\nopt_keysAux=Keypad4,Keypad2,Keypad8,Keypad6\n");

            RoomConfig.Load();
            KeyMap.Load();

            CollectionAssert.AreEqual(new[] { "J", "K", "I", "L" }, KeyMap.lane4);
            CollectionAssert.AreEqual(new[] { "Keypad4", "Keypad2", "Keypad8", "Keypad6" }, KeyMap.lane4aux);
            RoomConfig.Save();
            // 比 "opt_keys=" 而非 "opt_keys"：檔頭那行「鍵位已搬到 keymaps.ini」的註解本來就會提到這個名字。
            StringAssert.DoesNotContain("opt_keys=", File.ReadAllText(ConfigPath), "新的 config.ini 不該再寫鍵位");
            Assert.AreEqual(1, RoomConfig.defaultTeam, "[Room] 的值不受影響");
        }

        [Test]
        public void Fresh_Install_Writes_Both_Files_With_Defaults()
        {
            RoomConfig.Load();
            KeyMap.Load();

            Assert.IsTrue(File.Exists(ConfigPath), "全新安裝要自動落地一份 config.ini 範本");
            Assert.IsTrue(File.Exists(KeymapPath), "全新安裝要自動落地一份 keymaps.ini 範本");
            var km = File.ReadAllText(KeymapPath);
            StringAssert.Contains("speedUp=F5", km, "預設功能鍵要寫進範本，玩家才知道能改");
            StringAssert.Contains("speedDown=F6", km);
            StringAssert.Contains("camera=F2", km);
        }
    }
}
