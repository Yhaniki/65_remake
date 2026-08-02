using System.IO;
using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// 個人資料的「兩層」設定（角色自己的 profile.json ＞ config.ini [Profile] 的共用預設）與等級/經驗值曲線。
    /// 曲線與解析都是純函式；最後一個測試才真的落地到 temp 資料夾，驗經驗值有寫進 profile.json。
    /// </summary>
    public class PlayerLevelTests
    {
        [Test]
        public void ExpToNext_Is100TimesLevel_AndZeroAtMax()
        {
            Assert.AreEqual(100, PlayerLevel.ExpToNext(1));      // LV1→2
            Assert.AreEqual(1100, PlayerLevel.ExpToNext(11));    // LV11→12
            Assert.AreEqual(0, PlayerLevel.ExpToNext(PlayerLevel.MaxLevel));   // 滿級 → 不再升
            Assert.AreEqual(100, PlayerLevel.ExpToNext(0));      // 非法等級先夾成 1
            Assert.AreEqual(100, PlayerLevel.ExpToNext(-7));
        }

        [Test]
        public void ParseOptional_ReadsLegacyConfigString_EmptyMeansUnset()
        {
            // 舊 config.ini 的 playerLevel 字串 → profile.json 的 int。留空＝沒設定(0)，不是 LV1 ——
            // 「留空＝不顯示等級」的語意要能原封不動搬過去。
            Assert.AreEqual(11, PlayerLevel.ParseOptional("11"));
            Assert.AreEqual(11, PlayerLevel.ParseOptional("  11  "));
            Assert.AreEqual(0, PlayerLevel.ParseOptional(""));
            Assert.AreEqual(0, PlayerLevel.ParseOptional(null));
            Assert.AreEqual(0, PlayerLevel.ParseOptional("abc"));
            Assert.AreEqual(0, PlayerLevel.ParseOptional("-3"));
            Assert.AreEqual(PlayerLevel.MaxLevel, PlayerLevel.ParseOptional("999"));   // 夾進上限
        }

        [Test]
        public void Label_Formats_Level_And_BlankWhenUnset()
        {
            Assert.AreEqual("LV:11", PlayerLevel.Label(11));
            Assert.AreEqual("LV:1", PlayerLevel.Label(1));
            Assert.AreEqual("", PlayerLevel.Label(0));    // 沒設定 → 不顯示等級
            Assert.AreEqual("", PlayerLevel.Label(-4));
        }

        [Test]
        public void Grant_AccumulatesWithinLevel_ThenLevelsUp()
        {
            var (lv, exp) = PlayerLevel.Grant(1, 0, 30);          // 一局約 30~120 點
            Assert.AreEqual(1, lv);
            Assert.AreEqual(30, exp);

            (lv, exp) = PlayerLevel.Grant(1, 30, 70);             // 剛好 100 → 升級，餘 0
            Assert.AreEqual(2, lv);
            Assert.AreEqual(0, exp);

            (lv, exp) = PlayerLevel.Grant(2, 190, 20);            // 200 門檻，超過 10 → 帶到下一級
            Assert.AreEqual(3, lv);
            Assert.AreEqual(10, exp);
        }

        [Test]
        public void Grant_CanCrossSeveralLevelsAtOnce()
        {
            // 100 + 200 + 300 = 600 → LV1 拿 650 點直接到 LV4，餘 50。
            var (lv, exp) = PlayerLevel.Grant(1, 0, 650);
            Assert.AreEqual(4, lv);
            Assert.AreEqual(50, exp);
        }

        [Test]
        public void Grant_IgnoresNonPositiveGain_AndStopsAtMaxLevel()
        {
            var (lv, exp) = PlayerLevel.Grant(5, 42, 0);          // 沒拿到經驗 → 原封不動
            Assert.AreEqual(5, lv);
            Assert.AreEqual(42, exp);

            (lv, exp) = PlayerLevel.Grant(5, 42, -100);           // 結算只會加、不會扣
            Assert.AreEqual(5, lv);
            Assert.AreEqual(42, exp);

            (lv, exp) = PlayerLevel.Grant(PlayerLevel.MaxLevel, 0, 999999);   // 滿級 → 經驗條收掉
            Assert.AreEqual(PlayerLevel.MaxLevel, lv);
            Assert.AreEqual(0, exp);
        }

        [Test]
        public void ResolveText_PrefersOwnValue_FallsBackToDefault()
        {
            Assert.AreEqual("惡魔", ProfileManager.ResolveText("惡魔", "天使之翼"));   // 角色自己設過 → 用自己的
            Assert.AreEqual("天使之翼", ProfileManager.ResolveText("", "天使之翼"));   // 沒設過 → 共用預設
            Assert.AreEqual("天使之翼", ProfileManager.ResolveText("   ", "天使之翼")); // 只有空白也算沒設過
            Assert.AreEqual("天使之翼", ProfileManager.ResolveText(null, "  天使之翼  "));
            Assert.AreEqual("", ProfileManager.ResolveText("", ""));                    // 兩層都空 → 不顯示
        }

        [Test]
        public void ResolveLevel_PrefersOwnLevel_FallsBackToOuterDefault()
        {
            Assert.AreEqual(7, ProfileManager.ResolveLevel(7, 11));    // 角色升過等 → 看自己的
            Assert.AreEqual(11, ProfileManager.ResolveLevel(0, 11));   // 0 = 沒設過 → 吃外層 profile.json 的預設
            Assert.AreEqual(1, ProfileManager.ResolveLevel(0, 0));     // 兩層都沒設 → LV1（獎勵公式以前寫死的值）
            Assert.AreEqual(PlayerLevel.MaxLevel, ProfileManager.ResolveLevel(500, 0));   // 壞存檔夾回上限
        }

        [Test]
        public void UserProfile_LevelAndExp_SanitizeAndRoundTripThroughJson()
        {
            var p = new UserProfile { level = -5, exp = -20, familyName = "  家族  ", familyEmblem = "  SMALL7  " }.Sanitize();
            Assert.AreEqual(0, p.level);        // 負的 → 0（＝沒設過，吃預設）
            Assert.AreEqual(0, p.exp);
            Assert.AreEqual("家族", p.familyName);
            Assert.AreEqual("SMALL7", p.familyEmblem);

            var src = new UserProfile("00000001", "阿明", 1) { level = 12, exp = 340, familyName = "惡魔", familyEmblem = "SMALL7" }.Sanitize();
            var back = UnityEngine.JsonUtility.FromJson<UserProfile>(UnityEngine.JsonUtility.ToJson(src)).Sanitize();
            Assert.AreEqual(12, back.level);
            Assert.AreEqual(340, back.exp);
            Assert.AreEqual("惡魔", back.familyName);
            Assert.AreEqual("SMALL7", back.familyEmblem);
        }

        [Test]
        public void AddExperience_LevelsUpFromConfigDefault_AndPersistsToProfileJson()
        {
            string root = Path.Combine(Path.GetTempPath(), "sdo_level_test_" + Path.GetRandomFileName());
            int prevLevel = ProfileDefaults.level;
            string prevActive = ProfileDefaults.activeId;
            try
            {
                ProfileManager.Root = root;
                ProfileDefaults.level = 3;      // 外層 profile.json 的共用預設：所有角色從 LV3 起跳
                ProfileDefaults.activeId = "";
                ProfileManager.Boot();          // 種 00000000(女)/00000001(男)，active = 00000000

                Assert.AreEqual(3, ProfileManager.Level);          // 角色沒設過 → 吃外層的預設
                Assert.AreEqual(0, ProfileManager.Active.level);   // 而且還沒落地成自己的
                Assert.AreEqual("LV:3", ProfileManager.LevelLabel);

                Assert.AreEqual(0, ProfileManager.AddExperience(250));   // LV3→4 要 300 → 還不夠
                Assert.AreEqual(3, ProfileManager.Active.level);         // 第一次加經驗就把預設等級落地成自己的
                Assert.AreEqual(250, ProfileManager.Active.exp);

                Assert.AreEqual(1, ProfileManager.AddExperience(60));    // 310 ≥ 300 → 升上 LV4，餘 10
                Assert.AreEqual(4, ProfileManager.Active.level);
                Assert.AreEqual(10, ProfileManager.Active.exp);
                Assert.AreEqual(4, ProfileManager.Level);
                Assert.AreEqual("LV:4", ProfileManager.LevelLabel);

                // 真的寫進了這個角色的 profile.json（下次開遊戲讀得回來）
                var back = UnityEngine.JsonUtility.FromJson<UserProfile>(
                    File.ReadAllText(Path.Combine(root, "00000000", ProfileManager.ProfileFileName)));
                Assert.AreEqual(4, back.level);
                Assert.AreEqual(10, back.exp);

                // 另一個角色沒打過歌 → 仍吃共用預設，不會被這個角色的等級影響
                var other = UnityEngine.JsonUtility.FromJson<UserProfile>(
                    File.ReadAllText(Path.Combine(root, "00000001", ProfileManager.ProfileFileName)));
                Assert.AreEqual(0, other.level);
                Assert.AreEqual(3, ProfileManager.ResolveLevel(other.level, ProfileDefaults.level));
            }
            finally
            {
                ProfileDefaults.level = prevLevel;
                ProfileDefaults.activeId = prevActive;
                ProfileManager.Root = null;    // 還原 lazy 解析，避免污染其他測試
                Favorites.ResetForTests();     // Boot() 綁到了 temp 的收藏檔
                try { Directory.Delete(root, true); } catch { /* best effort */ }
            }
        }
    }
}
