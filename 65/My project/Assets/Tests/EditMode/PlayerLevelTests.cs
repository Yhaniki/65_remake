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
        public void MaxLevel_Is200()
        {
            // 滿級是刻意選的數字(不是曲線推出來的),釘死在這裡：改它就是改遊戲的滿級。
            Assert.AreEqual(200, PlayerLevel.MaxLevel);
            Assert.AreEqual(200, PlayerLevel.Clamp(9999));
            Assert.AreEqual(19900, PlayerLevel.ExpToNext(PlayerLevel.MaxLevel - 1));   // LV199→200 是最後一段
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
        public void Percent_IsProgressWithinCurrentLevel_FullAtMaxLevel()
        {
            // 分母是「這一級要多少」＝ 100 × 等級，所以同樣的 exp 在越高的等級佔比越小。
            Assert.AreEqual(0f, PlayerLevel.Percent(1, 0), 0.001f);
            Assert.AreEqual(30f, PlayerLevel.Percent(1, 30), 0.001f);      // LV1 要 100
            Assert.AreEqual(15f, PlayerLevel.Percent(2, 30), 0.001f);      // LV2 要 200
            Assert.AreEqual(95f, PlayerLevel.Percent(10, 950), 0.001f);    // LV10 要 1000

            Assert.AreEqual(0f, PlayerLevel.Percent(3, -5), 0.001f);       // 壞資料 → 0
            Assert.AreEqual(100f, PlayerLevel.Percent(1, 500), 0.001f);    // 手改的存檔 → 夾在 100
            // 滿級沒有「下一級」可跑 → 條收滿；歸零會看起來像掉等。
            Assert.AreEqual(100f, PlayerLevel.Percent(PlayerLevel.MaxLevel, 0), 0.001f);
        }

        [Test]
        public void ProfileFields_ExpPercent_UsesTheSameLevelAsTheRestOfTheProfile()
        {
            int prevLevel = ProfileDefaults.level;
            try
            {
                ProfileDefaults.level = 10;   // 外層共用預設：LV10 → 這一級要 1000

                // 沒豎旗標 → 等級吃 default(10),分母就是 1000。若這裡誤讀 p.playerLevel("2"),
                // 分母會變成 200、百分比直接爆表 —— 大廳那條經驗條與實際進度對不起來。
                var noOverride = new UserProfile { playerLevel = "2", exp = 250 }.Sanitize();
                Assert.AreEqual(1000, ProfileFields.ExpToNext(noOverride));
                Assert.AreEqual(25f, ProfileFields.ExpPercent(noOverride), 0.001f);

                var own = new UserProfile { playerLevel = "2", exp = 50, hasProfileOverrides = true }.Sanitize();
                Assert.AreEqual(200, ProfileFields.ExpToNext(own));
                Assert.AreEqual(25f, ProfileFields.ExpPercent(own), 0.001f);

                Assert.AreEqual(0, ProfileFields.Exp(null));
                Assert.AreEqual(0f, ProfileFields.ExpPercent(null), 0.001f);   // 看別人時沒有 profile 可讀
            }
            finally { ProfileDefaults.level = prevLevel; }
        }

        [Test]
        public void Text_And_CleanText_NormalizeLevelStrings()
        {
            Assert.AreEqual("11", PlayerLevel.Text(11));
            Assert.AreEqual("", PlayerLevel.Text(0));        // 沒設定 → 空字串（＝不顯示等級）
            Assert.AreEqual("", PlayerLevel.Text(-3));
            Assert.AreEqual("7", PlayerLevel.CleanText(" 007 "));   // 寫回標準寫法
            Assert.AreEqual("", PlayerLevel.CleanText("   "));
            Assert.AreEqual("abc", PlayerLevel.CleanText("abc"));   // 手改的怪東西原樣留著，不吃掉
        }

        [Test]
        public void ProfileFields_PrefersOwnValues_OnlyWhenOverridden()
        {
            int prevLevel = ProfileDefaults.level;
            string prevFamily = ProfileDefaults.familyName, prevEmblem = ProfileDefaults.familyEmblem;
            try
            {
                ProfileDefaults.familyName = "天使之翼";
                ProfileDefaults.familyEmblem = "SMALL43";
                ProfileDefaults.level = 11;

                var noOverride = new UserProfile { familyName = "惡魔", playerLevel = "5" }.Sanitize();
                Assert.AreEqual("天使之翼", ProfileFields.FamilyName(noOverride), "沒豎旗標 → 整組吃外層 default");
                Assert.AreEqual("SMALL43", ProfileFields.FamilyEmblem(noOverride));
                Assert.AreEqual(11, ProfileFields.PlayerLevelValue(noOverride));
                Assert.AreEqual("LV:11", ProfileFields.LevelLabel(noOverride));

                var own = new UserProfile { familyName = "惡魔", familyEmblem = "SMALL7", playerLevel = "5", hasProfileOverrides = true }.Sanitize();
                Assert.AreEqual("惡魔", ProfileFields.FamilyName(own));
                Assert.AreEqual("SMALL7", ProfileFields.FamilyEmblem(own));
                Assert.AreEqual(5, ProfileFields.PlayerLevelValue(own));

                // 覆寫是整組的：刻意留白 → 這個角色就是不顯示家族/等級，不會被 default 蓋回去。
                var blank = new UserProfile { hasProfileOverrides = true }.Sanitize();
                Assert.AreEqual("", ProfileFields.FamilyName(blank));
                Assert.AreEqual("", ProfileFields.LevelLabel(blank));
                Assert.AreEqual(1, ProfileFields.PlayerLevelValue(blank), "顯示不顯示是一回事，送上線的數值一律 ≥1");
            }
            finally
            {
                ProfileDefaults.level = prevLevel;
                ProfileDefaults.familyName = prevFamily;
                ProfileDefaults.familyEmblem = prevEmblem;
            }
        }

        [Test]
        public void UserProfile_LevelAndExp_SanitizeAndRoundTripThroughJson()
        {
            var p = new UserProfile { playerLevel = "  007  ", exp = -20, familyName = "  家族  ", familyEmblem = "  SMALL7  " }.Sanitize();
            Assert.AreEqual("7", p.playerLevel);
            Assert.AreEqual(0, p.exp);          // 負的 → 0（經驗只會往上加）
            Assert.AreEqual("家族", p.familyName);
            Assert.AreEqual("SMALL7", p.familyEmblem);

            var src = new UserProfile("00000001", "阿明", 1)
            {
                playerLevel = "12", exp = 340, familyName = "惡魔", familyEmblem = "SMALL7", hasProfileOverrides = true,
            }.Sanitize();
            var back = UnityEngine.JsonUtility.FromJson<UserProfile>(UnityEngine.JsonUtility.ToJson(src)).Sanitize();
            Assert.AreEqual("12", back.playerLevel);
            Assert.AreEqual(340, back.exp);
            Assert.AreEqual("惡魔", back.familyName);
            Assert.IsTrue(back.hasProfileOverrides);
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

                Assert.AreEqual(3, ProfileManager.Level);                        // 角色沒設過 → 吃外層的預設
                Assert.IsFalse(ProfileManager.Active.hasProfileOverrides);       // 而且還沒落地成自己的
                Assert.AreEqual("LV:3", ProfileManager.LevelLabel);

                Assert.AreEqual(0, ProfileManager.AddExperience(250));   // LV3→4 要 300 → 還不夠
                Assert.IsTrue(ProfileManager.Active.hasProfileOverrides, "第一次加經驗就把等級落地成這個角色自己的");
                Assert.AreEqual("3", ProfileManager.Active.playerLevel);
                Assert.AreEqual(250, ProfileManager.Active.exp);

                Assert.AreEqual(1, ProfileManager.AddExperience(60));    // 310 ≥ 300 → 升上 LV4，餘 10
                Assert.AreEqual("4", ProfileManager.Active.playerLevel);
                Assert.AreEqual(10, ProfileManager.Active.exp);
                Assert.AreEqual(4, ProfileManager.Level);
                Assert.AreEqual("LV:4", ProfileManager.LevelLabel);

                // 真的寫進了這個角色的 profile.json（下次開遊戲讀得回來）
                var back = UnityEngine.JsonUtility.FromJson<UserProfile>(
                    File.ReadAllText(Path.Combine(root, "00000000", ProfileManager.ProfileFileName)));
                Assert.AreEqual("4", back.playerLevel);
                Assert.AreEqual(10, back.exp);
                Assert.IsTrue(back.hasProfileOverrides);

                // 另一個角色沒打過歌 → 仍吃共用預設，不會被這個角色的等級影響
                var other = UnityEngine.JsonUtility.FromJson<UserProfile>(
                    File.ReadAllText(Path.Combine(root, "00000001", ProfileManager.ProfileFileName)));
                Assert.IsFalse(other.hasProfileOverrides);
                Assert.AreEqual(3, ProfileFields.PlayerLevelValue(other));
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
