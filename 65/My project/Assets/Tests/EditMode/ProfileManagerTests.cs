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
    }
}
