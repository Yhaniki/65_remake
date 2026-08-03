using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// 「外層的 DATA/PROFILE/profile.json 是 Default,角色自己設過就以角色的為準」這條規則。
    ///
    /// 最值得守的是**留空的語意**:現行約定是「familyName 留空 = 不顯示家族」,而 JsonUtility 對 string
    /// 一律給 ""(分不出「這個 key 不存在」與「使用者刻意清空」)。如果哪天有人把整組覆寫改成逐欄 fallback,
    /// 「我就是不想顯示家族」就會被外層的預設值悄悄蓋回來 —— 那個 bug 只有玩家會發現。
    /// </summary>
    public class ProfileFieldsTests
    {
        private string _savedFamily, _savedEmblem;
        private int _savedLevel;

        [SetUp]
        public void SetUp()
        {
            _savedFamily = ProfileDefaults.familyName;
            _savedEmblem = ProfileDefaults.familyEmblem;
            _savedLevel = ProfileDefaults.level;
            ProfileDefaults.familyName = "天使之翼";
            ProfileDefaults.familyEmblem = "SMALL43";
            ProfileDefaults.level = 11;
        }

        [TearDown]
        public void TearDown()
        {
            ProfileDefaults.familyName = _savedFamily;
            ProfileDefaults.familyEmblem = _savedEmblem;
            ProfileDefaults.level = _savedLevel;
        }

        [Test]
        public void Profile_Without_Overrides_Uses_Outer_Defaults()
        {
            var p = new UserProfile("00000000", "飄漂o", 0);
            Assert.IsFalse(p.hasProfileOverrides);
            Assert.AreEqual("天使之翼", ProfileFields.FamilyName(p));
            Assert.AreEqual("SMALL43", ProfileFields.FamilyEmblem(p));
            Assert.AreEqual("11", ProfileFields.PlayerLevel(p));
            Assert.AreEqual(11, ProfileFields.PlayerLevelValue(p));
        }

        [Test]
        public void Profile_With_Overrides_Wins()
        {
            var p = new UserProfile("00000001", "阿偉", 1);
            ProfileFields.SetOverrides(p, "夜貓子", "SMALL01", "72");
            Assert.IsTrue(p.hasProfileOverrides);
            Assert.AreEqual("夜貓子", ProfileFields.FamilyName(p));
            Assert.AreEqual("SMALL01", ProfileFields.FamilyEmblem(p));
            Assert.AreEqual(72, ProfileFields.PlayerLevelValue(p));
        }

        [Test]
        public void Deliberately_Blank_Override_Is_Not_Refilled_By_Default()
        {
            // 這條就是 hasProfileOverrides 這個旗標存在的理由。
            var p = new UserProfile("00000001", "阿偉", 1);
            ProfileFields.SetOverrides(p, "", "", "");
            Assert.AreEqual("", ProfileFields.FamilyName(p), "刻意清空的家族名被外層的預設值蓋回來了");
            Assert.AreEqual("", ProfileFields.PlayerLevel(p), "刻意清空的等級被外層的預設值蓋回來了");
        }

        [Test]
        public void ClearOverrides_Falls_Back_To_Outer_Defaults()
        {
            var p = new UserProfile("00000001", "阿偉", 1);
            ProfileFields.SetOverrides(p, "夜貓子", "SMALL01", "72");
            ProfileFields.ClearOverrides(p);
            Assert.IsFalse(p.hasProfileOverrides);
            Assert.AreEqual("天使之翼", ProfileFields.FamilyName(p));
            Assert.AreEqual("11", ProfileFields.PlayerLevel(p));
        }

        [Test]
        public void Null_Profile_Falls_Back_To_Outer_Defaults()
        {
            // ProfileManager.Active 在極早期(或測試情境)可能是 null —— 不能因此炸掉。
            Assert.AreEqual("天使之翼", ProfileFields.FamilyName(null));
            Assert.AreEqual(11, ProfileFields.PlayerLevelValue(null));
        }

        [Test]
        public void Unset_Level_Falls_Back_To_One()
        {
            ProfileDefaults.level = 0;    // 沒設定 → 顯示端不畫等級,但送上線的數值一律 ≥1
            Assert.AreEqual("", ProfileFields.PlayerLevel(null));
            Assert.AreEqual(1, ProfileFields.PlayerLevelValue(null));
            ProfileDefaults.level = -3;   // 壞值同理
            Assert.AreEqual(1, ProfileFields.PlayerLevelValue(null));
        }

        [Test]
        public void Overrides_Survive_A_Json_Roundtrip()
        {
            // profile.json 走 Unity JsonUtility —— 新欄位必須真的存得下來(bool latch 尤其容易被漏掉)。
            var p = new UserProfile("00000000", "飄漂o", 0);
            ProfileFields.SetOverrides(p, "夜貓子", "SMALL01", "72");
            p.stats.AddPlay(100, 20, 5, 3);
            p.stats.AddResult(true);

            var json = UnityEngine.JsonUtility.ToJson(p);
            var back = UnityEngine.JsonUtility.FromJson<UserProfile>(json).Sanitize();

            Assert.IsTrue(back.hasProfileOverrides);
            Assert.AreEqual("夜貓子", ProfileFields.FamilyName(back));
            Assert.AreEqual(72, ProfileFields.PlayerLevelValue(back));
            Assert.AreEqual(128, back.stats.Judged);
            Assert.AreEqual(1, back.stats.wins);
            Assert.AreEqual(1, back.stats.plays);
        }
    }
}
