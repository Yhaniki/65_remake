using NUnit.Framework;
using Sdo.Net;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 外觀裡的 MMD 模型欄位(<see cref="NetAvatarLook.MmdPack"/>)。
    ///
    /// 這是「別人看得到我的模型」的**唯一宣告通道** —— 房間快照每次都帶著整份 look 廣播,
    /// 而 <see cref="NetAvatarLook.Key"/> 又是遠端角色「要不要重建」的判斷依據。所以這裡有兩件事
    /// 一定要對:① 別人送來的 id 不可信,格式不對要當成「沒有 MMD」而不是照收;
    /// ② 顯示用的名字**不能**進比較鍵,否則改個資料夾名就會讓每個看到你的人重建一隻角色。
    /// </summary>
    public class NetAvatarLookMmdTests
    {
        private static string GoodPack() => ModelPackId.Compute(new[] { new PackFileEntry("miku.pmx", 10, new string('a', 64)) });

        /// <summary>JSON 字串 → 可以丟給 Decode 的節點。</summary>
        private static object Node(string json)
        {
            object node;
            Assert.IsTrue(NetJson.TryParse(json, out node), "測試自己的 JSON 就壞了:" + json);
            return node;
        }

        [Test]
        public void MmdPack_RoundTrips_ThroughTheWire()
        {
            var look = new NetAvatarLook { Gender = 0, BodyIndex = 2, Parts = new[] { "hair", "coat" },
                                           MmdPack = GoodPack(), MmdName = "IkaHatunemiku2025" };
            var back = NetAvatarLook.Decode(Node(look.Encode().Json()));

            Assert.AreEqual(look.MmdPack, back.MmdPack);
            Assert.AreEqual("IkaHatunemiku2025", back.MmdName);
            Assert.AreEqual(2, back.BodyIndex);
            CollectionAssert.AreEqual(look.Parts, back.Parts);
        }

        [Test]
        public void NoMmd_StaysEmpty_AndCostsNothingOnTheWire()
        {
            // 沒有用 MMD 的人(多數人)不該因為這個功能而在每份房間快照裡多帶兩個空欄位。
            var look = new NetAvatarLook { Gender = 1, Parts = new[] { "coat" } };
            string json = look.Encode().Json();
            StringAssert.DoesNotContain("mmd", json);

            var back = NetAvatarLook.Decode(Node(json));
            Assert.AreEqual("", back.MmdPack);
            Assert.AreEqual("", back.MmdName);
        }

        [Test]
        public void MalformedPackId_IsTreatedAsNoMmd_NotPassedThrough()
        {
            // 不驗的話,一個亂填的 id 會讓每個收到的人都去跟 server 要一份不存在的模型 —— 而且是
            // 每進一次房間問一次。壞外觀的正確結果是「他看起來是 SDO 角色」,不是「大家一直重試」。
            foreach (var bad in new[] { "not-a-pack", "sha256:xyz", "sha256:" + new string('a', 31), "sha1:" + new string('a', 32) })
            {
                var look = NetAvatarLook.Decode(Node("{\"gender\":0,\"mmd\":\"" + bad + "\",\"mmdName\":\"x\"}"));
                Assert.AreEqual("", look.MmdPack, "壞的 packId 被放行了:" + bad);
                Assert.AreEqual("", look.MmdName, "packId 不合法時名字也不該留著");
            }
        }

        [Test]
        public void HostileMmdName_IsClipped()
        {
            // 名字是別人送來的字串而且會被畫在 UI 上。
            string huge = new string('x', 500);
            var look = NetAvatarLook.Decode(Node("{\"mmd\":\"" + GoodPack() + "\",\"mmdName\":\"" + huge + "\"}"));
            Assert.LessOrEqual(look.MmdName.Length, NetAvatarLook.MaxMmdNameLength);
        }

        // ---------------------------------------------------------------- 比較鍵 / SameAs
        [Test]
        public void ChangingTheModel_ForcesARemoteRebuild()
        {
            var a = new NetAvatarLook { Parts = new[] { "coat" }, MmdPack = GoodPack() };
            var b = new NetAvatarLook { Parts = new[] { "coat" } };                       // 同一套衣服,脫掉 MMD
            Assert.AreNotEqual(a.Key(), b.Key());
            Assert.IsFalse(a.SameAs(b));
            Assert.IsFalse(b.SameAs(a));
        }

        [Test]
        public void RenamingTheModelFolder_DoesNotRebuildAnything()
        {
            // 名字純粹顯示用。它變了畫面上什麼都不會不同,卻會白白重建一隻角色(要讀十幾個部件檔)。
            var a = new NetAvatarLook { Parts = new[] { "coat" }, MmdPack = GoodPack(), MmdName = "Miku" };
            var b = new NetAvatarLook { Parts = new[] { "coat" }, MmdPack = GoodPack(), MmdName = "初音未來" };
            Assert.AreEqual(a.Key(), b.Key());
            Assert.IsTrue(a.SameAs(b));
        }

        [Test]
        public void TwoDifferentModels_AreDifferentLooks()
        {
            var one = ModelPackId.Compute(new[] { new PackFileEntry("a.pmx", 10, new string('a', 64)) });
            var two = ModelPackId.Compute(new[] { new PackFileEntry("b.pmx", 10, new string('b', 64)) });
            Assert.AreNotEqual(one, two);

            var a = new NetAvatarLook { MmdPack = one };
            var b = new NetAvatarLook { MmdPack = two };
            Assert.AreNotEqual(a.Key(), b.Key());
            Assert.IsFalse(a.SameAs(b));
        }

        [Test]
        public void TheSdoOutfit_IsStillCarried_WhileWearingAModel()
        {
            // 這是「還沒下載完就先顯示他的穿搭」能成立的前提:穿搭一直都在 look 裡,
            // 不會因為他改用 MMD 就不送了。
            var look = new NetAvatarLook { Gender = 1, BodyIndex = 3, Parts = new[] { "hair", "coat", "shoes" },
                                           MmdPack = GoodPack() };
            var back = NetAvatarLook.Decode(Node(look.Encode().Json()));
            Assert.AreEqual(1, back.Gender);
            Assert.AreEqual(3, back.BodyIndex);
            CollectionAssert.AreEqual(new[] { "hair", "coat", "shoes" }, back.Parts);
        }
    }
}
