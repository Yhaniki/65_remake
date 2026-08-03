using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// 本機好友清單。
    ///
    /// 這裡最容易寫錯的是**鍵**:清單是以顯示名字比對的,不是某種 id ——
    /// 在這套連線裡名字就是身分(私聊是照名字全服尋人),而座位快照根本沒帶穩定的 playerId,
    /// 只有 server 每次連線重發的 userId(拿它當鍵,好友隔天全部失效)。見 <see cref="FriendList"/>。
    /// </summary>
    public class FriendListTests
    {
        private const string Now = "2026-07-31T00:00:00.0000000Z";

        private static UserProfile Me() => new UserProfile("00000000", "飄漂o", 0).Sanitize();

        [Test]
        public void Add_Then_IsFriend()
        {
            var me = Me();
            Assert.IsTrue(FriendList.Add(me, "A-wei", "00000001", Now));
            Assert.IsTrue(FriendList.IsFriend(me, "A-wei"));
            Assert.AreEqual(1, me.friends.Length);
            Assert.AreEqual("A-wei", me.friends[0].name);
        }

        [Test]
        public void Cannot_Add_Twice()
        {
            var me = Me();
            Assert.IsTrue(FriendList.Add(me, "A-wei", "", Now));
            Assert.IsFalse(FriendList.Add(me, "A-wei", "", Now));
            Assert.AreEqual(1, me.friends.Length);
        }

        [Test]
        public void Cannot_Add_Self()
        {
            var me = Me();
            Assert.IsFalse(FriendList.Add(me, "飄漂o", "00000000", Now));
            Assert.AreEqual(0, me.friends.Length);
        }

        [Test]
        public void Cannot_Add_Blank_Name()
        {
            var me = Me();
            Assert.IsFalse(FriendList.Add(me, "", "00000001", Now));
            Assert.IsFalse(FriendList.Add(me, "   ", "00000001", Now));
            Assert.IsFalse(FriendList.Add(me, null, "00000001", Now));
            Assert.AreEqual(0, me.friends.Length);
        }

        [Test]
        public void Name_Match_Is_Case_Insensitive()
        {
            var me = Me();
            FriendList.Add(me, "A-wei", "", Now);
            Assert.IsTrue(FriendList.IsFriend(me, "a-WEI"));
            Assert.IsFalse(FriendList.Add(me, "a-WEI", "", Now));
        }

        [Test]
        public void Remove_Works_And_Is_Idempotent()
        {
            var me = Me();
            FriendList.Add(me, "A-wei", "", Now);
            Assert.IsTrue(FriendList.Remove(me, "A-wei"));
            Assert.IsFalse(FriendList.IsFriend(me, "A-wei"));
            Assert.IsFalse(FriendList.Remove(me, "A-wei"));
            Assert.AreEqual(0, me.friends.Length);
        }

        [Test]
        public void Names_Preserves_Insertion_Order()
        {
            var me = Me();
            FriendList.Add(me, "甲", "", Now);
            FriendList.Add(me, "乙", "", Now);
            FriendList.Add(me, "丙", "", Now);
            CollectionAssert.AreEqual(new[] { "甲", "乙", "丙" }, FriendList.Names(me));
        }

        [Test]
        public void Sanitize_Drops_Nameless_And_Dedupes()
        {
            var me = Me();
            me.friends = new[]
            {
                new FriendEntry { name = "甲", id = "1" },
                new FriendEntry { name = "", id = "2" },      // 沒名字的殘骸
                new FriendEntry { name = "甲", id = "3" },     // 重複
                null,
            };
            me.Sanitize();
            Assert.AreEqual(1, me.friends.Length);
            Assert.AreEqual("甲", me.friends[0].name);
            Assert.AreEqual("1", me.friends[0].id, "去重應該留先加進來的那筆");
        }

        [Test]
        public void Friends_Survive_A_Json_Roundtrip()
        {
            var me = Me();
            FriendList.Add(me, "A-wei", "00000001", Now);
            var back = UnityEngine.JsonUtility.FromJson<UserProfile>(UnityEngine.JsonUtility.ToJson(me)).Sanitize();
            Assert.IsTrue(FriendList.IsFriend(back, "A-wei"));
            Assert.AreEqual("00000001", back.friends[0].id);
        }

        [Test]
        public void Null_Owner_Is_Safe()
        {
            Assert.IsFalse(FriendList.Add(null, "A-wei", "", Now));
            Assert.IsFalse(FriendList.Remove(null, "A-wei"));
            Assert.IsFalse(FriendList.IsFriend(null, "A-wei"));
            Assert.AreEqual(0, FriendList.Names(null).Length);
        }
    }
}
