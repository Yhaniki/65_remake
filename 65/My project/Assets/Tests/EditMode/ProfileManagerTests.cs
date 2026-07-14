using System.IO;
using System.Linq;
using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    public class ProfileManagerTests
    {
        [Test]
        public void FormatId_ZeroPads_To8()
        {
            Assert.AreEqual("00000000", ProfileManager.FormatId(0));
            Assert.AreEqual("00000001", ProfileManager.FormatId(1));
            Assert.AreEqual("00000042", ProfileManager.FormatId(42));
            Assert.AreEqual("12345678", ProfileManager.FormatId(12345678));
        }

        [Test]
        public void FormatId_ClampsNegativeToZero()
        {
            Assert.AreEqual("00000000", ProfileManager.FormatId(-5));
        }

        [Test]
        public void TryParseId_AcceptsNumeric_RejectsOther()
        {
            Assert.IsTrue(ProfileManager.TryParseId("00000001", out var v));
            Assert.AreEqual(1, v);
            Assert.IsTrue(ProfileManager.TryParseId("42", out var v2));
            Assert.AreEqual(42, v2);
            Assert.IsFalse(ProfileManager.TryParseId("abc", out _));
            Assert.IsFalse(ProfileManager.TryParseId("0000-1", out _));
            Assert.IsFalse(ProfileManager.TryParseId("12 34", out _));
            Assert.IsFalse(ProfileManager.TryParseId("", out _));
            Assert.IsFalse(ProfileManager.TryParseId(null, out _));
        }

        [Test]
        public void NextFreeId_PicksSmallestFreeSlot()
        {
            Assert.AreEqual("00000000", ProfileManager.NextFreeId(new string[0]));
            Assert.AreEqual("00000002", ProfileManager.NextFreeId(new[] { "00000000", "00000001" }));
            Assert.AreEqual("00000001", ProfileManager.NextFreeId(new[] { "00000000", "00000002" }));   // fills the gap
            Assert.AreEqual("00000000", ProfileManager.NextFreeId(new[] { "garbage", "00000001" }));    // non-numeric ignored
        }

        [Test]
        public void UserProfile_Sanitize_RepairsInvalid()
        {
            var p = new UserProfile { id = "", name = "", gender = 5, avatarId = -3 }.Sanitize();
            Assert.AreEqual("00000000", p.id);
            Assert.AreEqual("玩家001", p.name);
            Assert.AreEqual(0, p.gender);     // invalid gender → 女(0)
            Assert.AreEqual(0, p.avatarId);

            var m = new UserProfile("00000001", "阿明", 1).Sanitize();
            Assert.AreEqual(1, m.gender);     // 男 kept
            Assert.AreEqual("阿明", m.name);
        }

        [Test]
        public void Boot_MigratesLegacyPerUserFavorites_ToSharedProfileLayer()
        {
            // 舊版把 favorites.json 放在各 user 資料夾；現在收藏是 PROFILE 層全帳號共用（user 資料夾只放衣服）。
            // Boot() 要把殘留的 per-user 檔依 id 由小到大併入共用檔（去重、先出現先贏）並刪掉舊檔。
            string root = Path.Combine(Path.GetTempPath(), "sdo_profile_test_" + Path.GetRandomFileName());
            try
            {
                ProfileManager.Root = root;
                Directory.CreateDirectory(Path.Combine(root, "00000000"));
                Directory.CreateDirectory(Path.Combine(root, "00000001"));
                File.WriteAllText(Path.Combine(root, "00000000", Favorites.FileName), Favorites.Serialize(new[] { "sdom1.gn", "sdom2.gn" }));
                File.WriteAllText(Path.Combine(root, "00000001", Favorites.FileName), Favorites.Serialize(new[] { "sdom2.gn", "sdom3.gn" }));

                ProfileManager.Boot();

                var shared = Path.Combine(root, Favorites.FileName);
                Assert.IsTrue(File.Exists(shared), "共用檔應建立在 PROFILE 層");
                CollectionAssert.AreEqual(new[] { "sdom1.gn", "sdom2.gn", "sdom3.gn" },
                    Favorites.Parse(File.ReadAllText(shared)).ToList());
                Assert.IsFalse(File.Exists(Path.Combine(root, "00000000", Favorites.FileName)), "舊 per-user 檔應刪除");
                Assert.IsFalse(File.Exists(Path.Combine(root, "00000001", Favorites.FileName)), "舊 per-user 檔應刪除");
                Assert.IsTrue(Favorites.IsFav("sdom1.gn"));   // Boot 後記憶體集合 = 共用檔內容
                Assert.IsTrue(Favorites.IsFav("sdom3.gn"));
                Assert.AreEqual(3, Favorites.Count);
            }
            finally
            {
                ProfileManager.Root = null;    // 還原 lazy 解析，避免污染其他測試
                Favorites.ResetForTests();     // 解除對 temp 檔的綁定
                try { Directory.Delete(root, true); } catch { /* best effort */ }
            }
        }

        [Test]
        public void SeededIdForGender_MapsMaleAndFemaleToSeededProfiles()
        {
            // 單機開場男女選擇：女(0)→00000000、男(1)→00000001。與 EnsureSeeded 種下的兩帳號一致。
            Assert.AreEqual(ProfileManager.FemaleSeedId, ProfileManager.SeededIdForGender(0));
            Assert.AreEqual(ProfileManager.MaleSeedId, ProfileManager.SeededIdForGender(1));
            Assert.AreEqual("00000000", ProfileManager.SeededIdForGender(0));
            Assert.AreEqual("00000001", ProfileManager.SeededIdForGender(1));
            // 只有 1 算男；其它值(含非法)一律回退成女(00000000)，與 UserProfile.Sanitize 的 gender 夾法一致。
            Assert.AreEqual(ProfileManager.FemaleSeedId, ProfileManager.SeededIdForGender(2));
            Assert.AreEqual(ProfileManager.FemaleSeedId, ProfileManager.SeededIdForGender(-1));
        }
    }
}
