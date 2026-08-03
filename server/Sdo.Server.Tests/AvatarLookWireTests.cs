using NUnit.Framework;
using Sdo.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// 外觀(<see cref="NetAvatarLook"/>)的 wire round-trip 與比較規則。
    ///
    /// 為什麼這個檔重要:遠端玩家在別人畫面上長什麼樣,100% 靠這條路 ——
    /// 而它斷掉的症狀是「大家都是預設的女角」,看起來像美術問題而不是協定問題(實測繞了很久)。
    /// <see cref="NetAvatarLook.SameAs"/> 更是兩處共用:送出去重、以及「遠端角色要不要重建」;
    /// 判錯的後果分別是「洗爆房間廣播」與「換裝永遠不生效」。
    /// </summary>
    public class AvatarLookWireTests
    {
        private static NetAvatarLook RoundTrip(NetAvatarLook src)
        {
            object node;
            Assert.IsTrue(NetJson.TryParse(src.Encode().Json(), out node), "編出來的 JSON 要合法");
            return NetAvatarLook.Decode(node);
        }

        [Test]
        public void Parts_Survive_The_Wire_In_Order()
        {
            // 順序有意義:部件的疊圖順序會影響外觀,所以不能排序、不能去重。
            var src = new NetAvatarLook
            {
                Gender = 1,
                BodyIndex = 3,
                Parts = new[]
                {
                    "AVATAR/900001_MAN_FACE.MSH", "AVATAR/900002_MAN_HAIR.MSH",
                    "AVATAR/024976_MAN_COAT.MSH", "skinonly:AVATAR/024976_MAN_ONE.MSH",
                },
            };
            var got = RoundTrip(src);
            Assert.AreEqual(1, got.Gender);
            Assert.AreEqual(3, got.BodyIndex);
            CollectionAssert.AreEqual(src.Parts, got.Parts, "部件要逐項且照順序穿過 wire");
        }

        [Test]
        public void Null_Parts_Stays_Null_Not_Empty_Array()
        {
            // 下游(SdoRoomAvatar.NormalizeParts)靠 null/空 判斷「用預設整套」,
            // 兩者語意相同,但這裡明確釘住:不要變成一個「有 0 件衣服」的角色(那會是裸的)。
            var got = RoundTrip(new NetAvatarLook { Gender = 0 });
            Assert.IsTrue(got.Parts == null || got.Parts.Length == 0);
        }

        [Test]
        public void Over_Long_Names_And_Too_Many_Parts_Are_Clipped_Not_Rejected()
        {
            // 外觀資料壞掉最糟就是角色長得怪,不值得為它斷線 —— 所以是截斷而不是拒絕。
            var many = new string[NetAvatarLook.MaxParts + 5];
            for (int i = 0; i < many.Length; i++) many[i] = "AVATAR/P" + i + ".MSH";
            var got = RoundTrip(new NetAvatarLook { Parts = many });
            Assert.AreEqual(NetAvatarLook.MaxParts, got.Parts.Length);

            string longName = new string('A', NetAvatarLook.MaxPartNameLength + 30);
            var got2 = RoundTrip(new NetAvatarLook { Parts = new[] { longName } });
            Assert.AreEqual(NetAvatarLook.MaxPartNameLength, got2.Parts[0].Length);
        }

        [Test]
        public void BodyIndex_Is_Clamped_To_The_Five_Body_Shapes()
        {
            Assert.AreEqual(4, RoundTrip(new NetAvatarLook { BodyIndex = 99 }).BodyIndex);
            Assert.AreEqual(0, RoundTrip(new NetAvatarLook { BodyIndex = -3 }).BodyIndex);
        }

        // ---- SameAs / Key ----

        [Test]
        public void SameAs_Treats_Null_And_Empty_Parts_As_Equal()
        {
            // 兩者都代表「用預設整套」。判成不同的話,hello(null)與第一次 setLook(空)
            // 會多送一次無意義的全房廣播。
            var a = new NetAvatarLook { Gender = 0 };
            var b = new NetAvatarLook { Gender = 0, Parts = new string[0] };
            Assert.IsTrue(a.SameAs(b));
            Assert.IsTrue(b.SameAs(a));
            Assert.AreEqual(a.Key(), b.Key());
        }

        [Test]
        public void SameAs_Is_Order_Sensitive()
        {
            var a = new NetAvatarLook { Parts = new[] { "A", "B" } };
            var b = new NetAvatarLook { Parts = new[] { "B", "A" } };
            Assert.IsFalse(a.SameAs(b), "疊圖順序不同就是不同的外觀");
            Assert.AreNotEqual(a.Key(), b.Key());
        }

        [Test]
        public void SameAs_Notices_Gender_And_Body_Changes()
        {
            var baseLook = new NetAvatarLook { Gender = 0, BodyIndex = 1, Parts = new[] { "A" } };
            Assert.IsFalse(baseLook.SameAs(new NetAvatarLook { Gender = 1, BodyIndex = 1, Parts = new[] { "A" } }));
            Assert.IsFalse(baseLook.SameAs(new NetAvatarLook { Gender = 0, BodyIndex = 2, Parts = new[] { "A" } }));
            Assert.IsTrue(baseLook.SameAs(new NetAvatarLook { Gender = 0, BodyIndex = 1, Parts = new[] { "A" } }));
            Assert.IsFalse(baseLook.SameAs(null));
        }

        [Test]
        public void Key_Round_Trips_Through_The_Wire()
        {
            // 🔴 這條是「換裝要不要重建遠端角色」的判斷基礎:client 手上的 Key 是從
            // **收到的快照**算的,所以它必須在 wire round-trip 之後保持相同,
            // 否則每一份快照都會被判定成「外觀變了」→ 每次 rev 變動都重建一次角色(50-100ms hitch)。
            var src = new NetAvatarLook { Gender = 1, BodyIndex = 2, Parts = new[] { "AVATAR/X.MSH", "AVATAR/Y.MSH" } };
            Assert.AreEqual(src.Key(), RoundTrip(src).Key());
        }
    }
}
