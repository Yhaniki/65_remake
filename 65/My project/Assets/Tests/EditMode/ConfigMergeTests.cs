using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
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
            RoomConfig.serverTls = false; RoomConfig.serverCertFingerprint = "";   // TLS 是連線總開關的一部分,不要留給別人
            // 🔴 還原成 **false**,不是 true。
            // 還原成 true 的話,後面任何一條測試呼叫 RoomConfig.Save() 都會寫進**玩家真正的**
            // config.ini(ProfileManager.Root 已經被還原成 null → 解析到真實 DataRoot),
            // 而測試裡的靜態欄位是被各自 SetUp 塞過的值 —— 實測後果:serverAddress 被寫成空字串,
            // 玩家下次開遊戲就默默變回單機模式,而且完全看不出是測試幹的。
            // false 是安全的方向:真的要寫檔的測試自己會先 Load()(那時旗標就會變 true)。
            RoomConfig.LoadedForTests = false;
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
        public void A_Pasted_Certificate_Fingerprint_Is_Normalized_On_Load()
        {
            // 玩家是**複製貼上**的:openssl 印冒號、Windows 憑證管理員印空白、大小寫也不一定。
            // 三種都要能用 —— 為了一個冒號就說「憑證指紋不符」只會讓人以為 TLS 壞了。
            const string Hex = "6473b40678340324aa73a7cd6144d2168cdbc24f3d3a04ef469a672cc2c92e22";
            File.WriteAllText(ConfigPath,
                "[Net]\nserverAddress=192.168.1.10\nserverTls=1\n" +
                "serverCertFingerprint=64:73:B4:06:78:34:03:24:AA:73:A7:CD:61:44:D2:16" +
                ":8C:DB:C2:4F:3D:3A:04:EF:46:9A:67:2C:C2:C9:2E:22\n");
            RoomConfig.LoadedForTests = false;
            RoomConfig.Load();

            Assert.IsTrue(RoomConfig.serverTls, "serverTls=1 要讀進來");
            Assert.AreEqual(Hex, RoomConfig.serverCertFingerprint, "冒號與大寫都要被正規化掉");

            // 🔴 貼錯東西 → 當成沒填。那會讓連線在握手時明確失敗(自簽憑證過不了一般驗證),
            // 而**不是**靜默放行一張不對的憑證 —— 後者才是漏洞,而且症狀是「一切正常」。
            File.WriteAllText(ConfigPath,
                "[Net]\nserverAddress=192.168.1.10\nserverCertFingerprint=(我不小心貼了整段說明)\n");
            RoomConfig.LoadedForTests = false;
            RoomConfig.Load();
            Assert.AreEqual("", RoomConfig.serverCertFingerprint, "格式不對就當沒填");
        }

        [Test]
        public void Save_Before_Load_Refuses_To_Overwrite_The_Players_File()
        {
            // 🔴 RoomConfig 的欄位是 static,而 Save() 是「把現在的欄位值整份寫出去」——
            // 在 Load() 之前呼叫就會把玩家的 config.ini 換成一份內建預設值,設定全部消失。
            // 實際踩過:serverAddress 被寫回空字串 → 遊戲默默退回單機模式,
            // 而症狀(「兩台怎麼看不到彼此」)完全指不到根因。所以寧可不存也不要存錯。
            File.WriteAllText(ConfigPath, "[Net]\nserverAddress=192.168.1.10\nserverPort=27015\n");
            RoomConfig.LoadedForTests = false;      // 模擬「這個 process 還沒讀過檔」
            RoomConfig.serverAddress = "";          // 欄位停在預設值

            LogAssert.Expect(LogType.Warning, new Regex(@"Save\(\) 在 Load\(\) 之前"));
            RoomConfig.Save();

            StringAssert.Contains("serverAddress=192.168.1.10", File.ReadAllText(ConfigPath),
                "沒 Load 過就不該覆寫玩家的檔");

            // Load 過之後才允許寫 —— 守門不能把正常的保存也一起擋掉。
            RoomConfig.Load();
            Assert.AreEqual("192.168.1.10", RoomConfig.serverAddress, "Load 要真的讀到值");
            RoomConfig.serverAddress = "10.0.0.7";
            RoomConfig.Save();
            StringAssert.Contains("serverAddress=10.0.0.7", File.ReadAllText(ConfigPath));
        }

        [Test]
        public void Save_Refuses_To_Write_A_Different_File_Than_Load_Read()
        {
            // 🔴 這是「Save() 只准寫回 Load() 讀進來的那個檔」那條不變式。
            //
            // 為什麼需要它:欄位是 static,而 FilePath 跟著 ProfileManager.Root 跑。
            // 任何「Root 指到暫存目錄讀一份 → 之後把 Root 還原」的流程(每個測試都這樣做)
            // 都會讓後面某一次 Save() 把**暫存那份的值**寫進玩家真正的 config.ini。
            // 實測後果:玩家的 serverAddress 被抹成空字串 → 遊戲默默退回單機模式,
            // 而且房號一樣是 5 位數、畫面一模一樣,完全看不出來。踩過兩次,所以用不變式關掉整個 class。
            var otherRoot = Path.Combine(Path.GetTempPath(), "sdo_other_" + Path.GetRandomFileName());
            Directory.CreateDirectory(otherRoot);
            try
            {
                // ① 在 _root 讀一份(serverAddress = A)
                File.WriteAllText(ConfigPath, "[Net]\nserverAddress=10.0.0.1\n");
                RoomConfig.Load();
                Assert.AreEqual("10.0.0.1", RoomConfig.serverAddress);

                // ② 「別人的」設定檔:內容不該被動到
                var otherPath = Path.Combine(otherRoot, RoomConfig.FileName);
                const string otherText = "[Net]\nserverAddress=192.168.9.9\n";
                File.WriteAllText(otherPath, otherText);

                // ③ 把 Root 換過去(= 測試把 Root 還原成 null 的那個瞬間)再 Save
                ProfileManager.Root = otherRoot;
                LogAssert.Expect(LogType.Warning, new Regex(@"讀進來的"));
                RoomConfig.Save();

                Assert.AreEqual(otherText, File.ReadAllText(otherPath),
                    "Save() 不該寫到「不是它讀進來的那一份」設定檔");

                // ④ 在新根 Load 過之後就允許寫了 —— 守門不能把正常的換根流程也擋掉
                RoomConfig.Load();
                RoomConfig.serverAddress = "10.9.9.9";
                RoomConfig.Save();
                StringAssert.Contains("serverAddress=10.9.9.9", File.ReadAllText(otherPath));
            }
            finally
            {
                ProfileManager.Root = _root;   // 交還給 TearDown 收(它會刪 _root)
                try { Directory.Delete(otherRoot, true); } catch { /* best effort */ }
            }
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
