using NUnit.Framework;
using Sdo.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// 同族的判定規則(<see cref="GuildIdentity"/>)—— **家族名 + 徽章兩者都一樣才算同族**。
    ///
    /// 為什麼這條規則值得一整組測試:它同時決定了三件使用者看得到的事 ——
    /// 家族頻道的話送給誰(server 的 <c>Hub.OnChatSay</c>)、大廳「家族」分頁列出誰
    /// (<c>LobbyUserPanel</c>)、以及未來任何「同族才做的事」。三邊吃同一份,
    /// 這裡放寬一格,三個地方會一起放寬。
    /// </summary>
    public class GuildIdentityTests
    {
        [Test]
        public void Same_Name_And_Emblem_Is_Same_Guild()
        {
            Assert.IsTrue(GuildIdentity.Same("熱舞家族", "SMALL43", "熱舞家族", "SMALL43"));
        }

        [Test]
        public void Same_Name_But_Different_Emblem_Is_Not_Same_Guild()
        {
            // 使用者的要求:「家族一樣、**符號也一樣**才可以用家族頻道溝通」。
            // 這套連線沒有家族註冊表,家族名是自己填的字串 —— 徽章是唯一的第二道。
            Assert.IsFalse(GuildIdentity.Same("熱舞家族", "SMALL43", "熱舞家族", "SMALL7"));
        }

        [Test]
        public void One_Side_Without_Emblem_Is_Not_Same_Guild()
        {
            // 一邊設了徽章、一邊沒設 = 真的設得不一樣,不是「舊 client 沒送」。
            Assert.IsFalse(GuildIdentity.Same("熱舞家族", "SMALL43", "熱舞家族", ""));
            Assert.IsFalse(GuildIdentity.Same("熱舞家族", "", "熱舞家族", "SMALL43"));
        }

        [Test]
        public void Both_Without_Emblem_Falls_Back_To_Name_Only()
        {
            // 舊 client 完全不送 guildEmblem —— 那時的家族頻道不能因為升級就整個斷掉。
            Assert.IsTrue(GuildIdentity.Same("熱舞家族", "", "熱舞家族", null));
        }

        [Test]
        public void No_Guild_Never_Matches()
        {
            // 「都沒家族」不能變成一個大家族 —— 否則全服沒設家族的人會在同一個頻道裡。
            Assert.IsFalse(GuildIdentity.Same("", "", "", ""));
            Assert.IsFalse(GuildIdentity.Same(null, "SMALL43", null, "SMALL43"));
            Assert.IsFalse(GuildIdentity.Same("熱舞家族", "SMALL43", "", "SMALL43"));
        }

        [Test]
        public void Name_Comparison_Ignores_Case_And_Surrounding_Space()
        {
            // 大小寫/前後空白是打字的差異,不是不同的家族(顯示仍保留原樣)。
            Assert.IsTrue(GuildIdentity.Same("DanceCrew", "SMALL1", " dancecrew ", "SMALL1"));
        }

        [Test]
        public void Emblem_Spellings_Normalise_To_The_Same_Key()
        {
            // config / profile 裡同一個徽章有好幾種寫法,不該害兩個人變成不同家族。
            Assert.AreEqual("SMALL43", GuildIdentity.NormalizeEmblem("SMALL43"));
            Assert.AreEqual("SMALL43", GuildIdentity.NormalizeEmblem("small43"));
            Assert.AreEqual("SMALL43", GuildIdentity.NormalizeEmblem("43"));
            Assert.AreEqual("SMALL43", GuildIdentity.NormalizeEmblem(" SMALL43.PNG "));
            Assert.AreEqual("", GuildIdentity.NormalizeEmblem(null));

            Assert.IsTrue(GuildIdentity.Same("家族", "43", "家族", "small43.png"));
        }

        [Test]
        public void Has_Requires_A_Name()
        {
            Assert.IsTrue(GuildIdentity.Has("熱舞家族"));
            Assert.IsFalse(GuildIdentity.Has("   "));
            Assert.IsFalse(GuildIdentity.Has(null));
        }
    }
}
