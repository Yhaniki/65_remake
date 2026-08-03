using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// 本機黑名單(官方「設置阻止 / 加入黑名單」)。
    ///
    /// 與 <see cref="FriendListTests"/> 同一套規則(名字為鍵、不能加自己、不重複、上限、不自己存檔),
    /// 外加一條它獨有的:**加黑名單會把那個人從好友清單移掉**(官方按下去之後好友清單就沒有他了)。
    /// </summary>
    public class BlockListTests
    {
        private const string Now = "2026-08-04T00:00:00.0000000Z";

        private static UserProfile Me() => new UserProfile("00000000", "飄漂o", 0).Sanitize();

        [Test]
        public void Add_Then_IsBlocked()
        {
            var me = Me();
            Assert.IsTrue(BlockList.Add(me, "A-wei", "00000001", Now));
            Assert.IsTrue(BlockList.IsBlocked(me, "A-wei"));
            Assert.AreEqual(1, me.blocked.Length);
            Assert.AreEqual("A-wei", me.blocked[0].name);
        }

        [Test]
        public void Cannot_Add_Twice_Or_Self()
        {
            var me = Me();
            Assert.IsTrue(BlockList.Add(me, "A-wei", "", Now));
            Assert.IsFalse(BlockList.Add(me, "A-wei", "", Now));
            Assert.IsFalse(BlockList.Add(me, "飄漂o", "", Now), "不能封鎖自己");
            Assert.AreEqual(1, me.blocked.Length);
        }

        [Test]
        public void Name_Match_Ignores_Case_And_Padding()
        {
            var me = Me();
            Assert.IsTrue(BlockList.Add(me, "  A-wei  ", "", Now));
            Assert.AreEqual("A-wei", me.blocked[0].name, "存進去的是修過頭尾空白的名字");
            Assert.IsTrue(BlockList.IsBlocked(me, "a-WEI"));
        }

        [Test]
        public void Blocking_Drops_The_Friendship()
        {
            // 官方按下「設置阻止」之後,那個人就不在好友清單上了 —— 兩份清單不該同時有他。
            var me = Me();
            Assert.IsTrue(FriendList.Add(me, "A-wei", "", Now));
            Assert.IsTrue(BlockList.Add(me, "A-wei", "", Now));
            Assert.IsFalse(FriendList.IsFriend(me, "A-wei"));
            Assert.IsTrue(BlockList.IsBlocked(me, "A-wei"));
        }

        [Test]
        public void Unblocking_Does_Not_Restore_The_Friendship()
        {
            // 解除封鎖只是解除封鎖:封鎖前是不是好友沒有記錄,自動加回去等於憑空捏造。
            var me = Me();
            FriendList.Add(me, "A-wei", "", Now);
            BlockList.Add(me, "A-wei", "", Now);
            Assert.IsTrue(BlockList.Remove(me, "A-wei"));
            Assert.IsFalse(BlockList.IsBlocked(me, "A-wei"));
            Assert.IsFalse(FriendList.IsFriend(me, "A-wei"));
        }

        [Test]
        public void Remove_Reports_Whether_Anything_Changed()
        {
            var me = Me();
            Assert.IsFalse(BlockList.Remove(me, "nobody"));
            BlockList.Add(me, "A-wei", "", Now);
            Assert.IsTrue(BlockList.Remove(me, "A-wei"));
            Assert.AreEqual(0, me.blocked.Length);
        }

        [Test]
        public void Names_Comes_Back_In_Insert_Order()
        {
            var me = Me();
            BlockList.Add(me, "A", "", Now);
            BlockList.Add(me, "B", "", Now);
            CollectionAssert.AreEqual(new[] { "A", "B" }, BlockList.Names(me));
        }

        [Test]
        public void Sanitize_Dedupes_And_Drops_Junk()
        {
            // 手改壞的 profile.json 不該讓名單長出空白列或重複列(與 friends 走同一個清洗函式)。
            var me = Me();
            me.blocked = new[]
            {
                new FriendEntry { name = "A-wei" },
                new FriendEntry { name = "a-wei" },   // 同一個人(大小寫不同)
                new FriendEntry { name = "  " },      // 沒有名字的殘骸
                null,
            };
            me.Sanitize();
            Assert.AreEqual(1, me.blocked.Length);
            Assert.AreEqual("A-wei", me.blocked[0].name);
        }

        [Test]
        public void Null_Owner_Is_Never_A_Crash()
        {
            // ProfileManager.Active 在開機早期可能還沒建好 —— 選單的 gate 會先呼叫這些。
            Assert.IsFalse(BlockList.IsBlocked(null, "A-wei"));
            Assert.IsFalse(BlockList.Add(null, "A-wei", "", Now));
            Assert.IsFalse(BlockList.Remove(null, "A-wei"));
            CollectionAssert.IsEmpty(BlockList.Names(null));
        }
    }
}
