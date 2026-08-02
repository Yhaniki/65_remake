using System.IO;
using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// 外層的 <c>DATA/PROFILE/profile.json</c>（登入哪個角色 + 家族/等級的共用預設值）：一次性從 config.ini 的舊
    /// <c>[Profile]</c> 區搬過來、之後 config.ini 不再有那區，以及「角色自己的 profile.json ＞ 外層這份」的優先序。
    /// </summary>
    public class ProfileDefaultsTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "sdo_pdef_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
            ProfileManager.Root = _root;
            ResetStatics();
        }

        [TearDown]
        public void TearDown()
        {
            ProfileManager.Root = null;   // 還原 lazy 解析，避免污染其他測試
            ResetStatics();
            Favorites.ResetForTests();
            try { Directory.Delete(_root, true); } catch { /* best effort */ }
        }

        private static void ResetStatics()
        {
            ProfileDefaults.activeId = ""; ProfileDefaults.familyName = "";
            ProfileDefaults.familyEmblem = "SMALL43"; ProfileDefaults.level = 0;
            RoomConfig.legacyActiveId = ""; RoomConfig.legacyFamilyName = "";
            RoomConfig.legacyFamilyEmblem = ""; RoomConfig.legacyPlayerLevel = "";
            RoomConfig.hasLegacyProfileKeys = false; RoomConfig.hasOption = false;
        }

        private string ConfigPath => Path.Combine(_root, RoomConfig.FileName);
        private string DefaultsPath => Path.Combine(_root, ProfileDefaults.FileName);

        [Test]
        public void DefaultsFile_SitsNextToConfigIni_WithTheSameNameAsAProfilesOwnFile()
        {
            // 外層這份跟角色資料夾裡那份「同檔名」是刻意的：外面＝default、裡面＝角色自己的。
            Assert.AreEqual("profile.json", ProfileDefaults.FileName);
            Assert.AreEqual(ProfileManager.ProfileFileName, ProfileDefaults.FileName);
            Assert.AreEqual(Path.GetFullPath(DefaultsPath), Path.GetFullPath(ProfileDefaults.FilePath));
        }

        [Test]
        public void Load_Migrates_LegacyProfileSection_OutOfConfigIni_IntoProfileJson()
        {
            // 舊佈局：整個 [Profile] 區還在 config.ini 裡。
            File.WriteAllText(ConfigPath,
                "[Profile]\nactiveId=00000001\nfamilyName=天使之翼\nfamilyEmblem=SMALL43\nplayerLevel=11\n"
                + "[Room]\ndefaultTeam=1\n[Option]\nopt_bgm=0.3\n");

            RoomConfig.Load();
            ProfileDefaults.Load();

            Assert.IsTrue(File.Exists(DefaultsPath), "應產生 DATA/PROFILE/profile.json");
            Assert.AreEqual("00000001", ProfileDefaults.activeId);
            Assert.AreEqual("天使之翼", ProfileDefaults.familyName);
            Assert.AreEqual("SMALL43", ProfileDefaults.familyEmblem);
            Assert.AreEqual(11, ProfileDefaults.level, "字串 playerLevel=11 → int level=11");

            // config.ini 被重寫過，那一區已經不在了（[Room]/[Option] 不受影響）。
            string ini = File.ReadAllText(ConfigPath);
            StringAssert.DoesNotContain("[Profile]", ini);
            StringAssert.DoesNotContain("familyName=", ini);
            StringAssert.DoesNotContain("playerLevel=", ini);
            StringAssert.DoesNotContain("activeId=", ini);
            Assert.AreEqual(1, RoomConfig.defaultTeam, "[Room] 的值不受影響");

            // 重開一次（config.ini 已無該區）值要從 profile.json 讀回來 —— 搬遷不是一次性有效而已。
            ResetStatics();
            RoomConfig.Load();
            ProfileDefaults.Load();
            Assert.AreEqual("00000001", ProfileDefaults.activeId);
            Assert.AreEqual("天使之翼", ProfileDefaults.familyName);
            Assert.AreEqual(11, ProfileDefaults.level);
        }

        [Test]
        public void Load_Migrates_LegacyActiveTxt_WhenConfigIniHasNoProfileSection()
        {
            // 更舊的佈局：登入的角色在獨立的 active.txt（連 [Profile] 區都還沒有）。
            File.WriteAllText(Path.Combine(_root, ProfileManager.ActiveFileName), "00000001");

            RoomConfig.Load();
            ProfileDefaults.Load();

            Assert.AreEqual("00000001", ProfileDefaults.activeId);
            Assert.IsFalse(File.Exists(Path.Combine(_root, ProfileManager.ActiveFileName)), "active.txt 搬完應被移除");
        }

        [Test]
        public void FreshInstall_WritesTemplate_WithBlankFamilyAndLevel()
        {
            RoomConfig.Load();
            ProfileDefaults.Load();

            Assert.IsTrue(File.Exists(DefaultsPath), "全新安裝要自動落地一份 profile.json");
            Assert.AreEqual("", ProfileDefaults.familyName, "預設不顯示家族");
            Assert.AreEqual(0, ProfileDefaults.level, "預設不顯示等級");
            Assert.AreEqual("SMALL43", ProfileDefaults.familyEmblem, "徽章保留內建預設（設了家族名就直接有徽章可用）");
            StringAssert.Contains("_readme", File.ReadAllText(DefaultsPath), "JSON 沒有註解語法 → 用 _readme 欄位帶說明");
        }

        [Test]
        public void ParseInto_And_Serialize_RoundTrip()
        {
            ProfileDefaults.activeId = "00000001";
            ProfileDefaults.familyName = "天使之翼";
            ProfileDefaults.familyEmblem = "SMALL7";
            ProfileDefaults.level = 11;

            string json = ProfileDefaults.Serialize();
            ResetStatics();
            ProfileDefaults.ParseInto(json);

            Assert.AreEqual("00000001", ProfileDefaults.activeId);
            Assert.AreEqual("天使之翼", ProfileDefaults.familyName);
            Assert.AreEqual("SMALL7", ProfileDefaults.familyEmblem);
            Assert.AreEqual(11, ProfileDefaults.level);
        }

        [Test]
        public void Sanitize_TrimsText_ClampsLevel_AndRejectsBadActiveId()
        {
            ProfileDefaults.activeId = " 00000000 ";
            ProfileDefaults.familyName = "  家族  ";
            ProfileDefaults.familyEmblem = "  ";
            ProfileDefaults.level = 999;
            ProfileDefaults.Sanitize();
            Assert.AreEqual("00000000", ProfileDefaults.activeId, "前後空白要吃掉");
            Assert.AreEqual("家族", ProfileDefaults.familyName);
            Assert.AreEqual("", ProfileDefaults.familyEmblem);
            Assert.AreEqual(PlayerLevel.MaxLevel, ProfileDefaults.level);

            Assert.AreEqual("", ProfileDefaults.SanitizeActiveId("1"), "非 8 位數 → 當沒設定");
            Assert.AreEqual("", ProfileDefaults.SanitizeActiveId("0000000a"), "非數字 → 當沒設定");
            Assert.AreEqual("", ProfileDefaults.SanitizeActiveId(null));

            ProfileDefaults.level = -5;
            ProfileDefaults.Sanitize();
            Assert.AreEqual(0, ProfileDefaults.level, "0/負 → 沒設定（合法）");
        }

        [Test]
        public void ParseInto_KeepsCurrentValues_WhenJsonIsGarbage()
        {
            ProfileDefaults.familyName = "天使之翼";
            ProfileDefaults.ParseInto("{ not json at all");
            Assert.AreEqual("天使之翼", ProfileDefaults.familyName, "壞檔不該把設定清掉");
        }

        [Test]
        public void OwnProfileOverridesOuterDefaults_PerCharacter()
        {
            // 外層 = default；00000001 自己設了家族/等級 → 它看自己的，00000000 沒設 → 看外層。
            ProfileDefaults.familyName = "天使之翼";
            ProfileDefaults.familyEmblem = "SMALL43";
            ProfileDefaults.level = 11;
            ProfileDefaults.activeId = "";
            ProfileManager.Boot();   // 種兩個角色，active = 00000000

            Assert.AreEqual("天使之翼", ProfileManager.FamilyName, "沒設過 → 吃外層的預設");
            Assert.AreEqual("SMALL43", ProfileManager.FamilyEmblem);
            Assert.AreEqual(11, ProfileManager.Level);
            Assert.AreEqual("LV:11", ProfileManager.LevelLabel);

            var male = new UserProfile(ProfileManager.MaleSeedId, "玩家002", 1)
            {
                familyName = "惡魔之翼", familyEmblem = "SMALL7", level = 5,
            }.Sanitize();
            File.WriteAllText(Path.Combine(_root, ProfileManager.MaleSeedId, ProfileManager.ProfileFileName),
                UnityEngine.JsonUtility.ToJson(male, true));

            ProfileManager.SetActive(ProfileManager.MaleSeedId);
            Assert.AreEqual("惡魔之翼", ProfileManager.FamilyName, "設過的角色 → 以自己的為準");
            Assert.AreEqual("SMALL7", ProfileManager.FamilyEmblem);
            Assert.AreEqual(5, ProfileManager.Level);
            Assert.AreEqual("LV:5", ProfileManager.LevelLabel);

            // 切回沒設過的角色 → 又是外層的預設（兩個角色互不影響）。
            ProfileManager.SetActive(ProfileManager.FemaleSeedId);
            Assert.AreEqual("天使之翼", ProfileManager.FamilyName);
            Assert.AreEqual(11, ProfileManager.Level);
        }

        [Test]
        public void SetActive_PersistsInto_ProfileJson_NotConfigIni()
        {
            ProfileDefaults.activeId = "";
            ProfileManager.Boot();
            ProfileManager.SetActive(ProfileManager.MaleSeedId);

            Assert.AreEqual("00000001", ProfileDefaults.activeId);
            StringAssert.Contains("\"activeId\": \"00000001\"", File.ReadAllText(DefaultsPath),
                "選角色要寫回 profile.json");
            if (File.Exists(ConfigPath))
                StringAssert.DoesNotContain("activeId=", File.ReadAllText(ConfigPath), "config.ini 不該再記角色");
        }
    }
}
