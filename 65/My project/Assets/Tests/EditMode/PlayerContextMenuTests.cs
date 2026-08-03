using NUnit.Framework;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 大廳側「右鍵某個玩家」的選單規則(玩家名單 / 房間信息的參與者列表共用)。
    ///
    /// 與 <see cref="RoomSlotMenuTests"/> 是姐妹:那邊管的是**座位**(多一整組房主管理項),
    /// 這邊管的是**名單上的一個人**,只有社交項。兩份規則刻意分開,測試也分開釘。
    /// </summary>
    public class PlayerContextMenuTests
    {
        private static PlayerAction[] Menu(bool online = true, bool isSelf = false, bool isFriend = false,
                                           bool isBlocked = false)
            => PlayerContextMenu.For(online, isSelf, isFriend, isBlocked);

        [Test]
        public void A_Stranger_Gets_The_Four_Official_Items_In_Order()
        {
            // 官方順序:玩家信息 → 私聊 → 加為好友 → 加入黑名單(設置阻止)。
            CollectionAssert.AreEqual(
                new[] { PlayerAction.PlayerInfo, PlayerAction.Whisper, PlayerAction.AddFriend, PlayerAction.Block },
                Menu());
        }

        [Test]
        public void Own_Row_Only_Offers_Player_Info()
        {
            // 沒有「私聊自己」「加自己好友」「封鎖自己」——BlockList/FriendList 本身也會拒絕。
            CollectionAssert.AreEqual(new[] { PlayerAction.PlayerInfo }, Menu(isSelf: true));
            CollectionAssert.AreEqual(new[] { PlayerAction.PlayerInfo }, Menu(isSelf: true, isFriend: true));
            CollectionAssert.AreEqual(new[] { PlayerAction.PlayerInfo }, Menu(isSelf: true, isBlocked: true));
        }

        [Test]
        public void Offline_Keeps_Player_Info_Only()
        {
            // 離線沒有 server:私聊送不出去、名單上也不會有別人。但「玩家信息」看的是本機資料 → 留著。
            CollectionAssert.AreEqual(new[] { PlayerAction.PlayerInfo }, Menu(online: false));
            CollectionAssert.AreEqual(new[] { PlayerAction.PlayerInfo }, Menu(online: false, isFriend: true));
        }

        [Test]
        public void Friend_Items_Are_Mutually_Exclusive()
        {
            var stranger = Menu(isFriend: false);
            CollectionAssert.Contains(stranger, PlayerAction.AddFriend);
            CollectionAssert.DoesNotContain(stranger, PlayerAction.RemoveFriend);

            var friend = Menu(isFriend: true);
            CollectionAssert.Contains(friend, PlayerAction.RemoveFriend);
            CollectionAssert.DoesNotContain(friend, PlayerAction.AddFriend);

            Assert.AreEqual(stranger.Length, friend.Length, "兩態是取代不是新增,項目數要一樣");
        }

        [Test]
        public void Block_Items_Are_Mutually_Exclusive()
        {
            var normal = Menu(isBlocked: false);
            CollectionAssert.Contains(normal, PlayerAction.Block);
            CollectionAssert.DoesNotContain(normal, PlayerAction.Unblock);

            var blocked = Menu(isBlocked: true);
            CollectionAssert.Contains(blocked, PlayerAction.Unblock);
            CollectionAssert.DoesNotContain(blocked, PlayerAction.Block);
        }

        [Test]
        public void A_Blocked_Player_Cannot_Be_Added_As_A_Friend_From_The_Menu()
        {
            // 兩件事互斥(BlockList.Add 會把他從好友移除)—— 兩項並排會讓人以為可以同時成立。
            var blocked = Menu(isBlocked: true);
            CollectionAssert.DoesNotContain(blocked, PlayerAction.AddFriend);
            CollectionAssert.DoesNotContain(blocked, PlayerAction.RemoveFriend);
            CollectionAssert.AreEqual(
                new[] { PlayerAction.PlayerInfo, PlayerAction.Whisper, PlayerAction.Unblock }, blocked);
        }

        [Test]
        public void Player_Info_Is_Always_There_And_Always_First()
        {
            // 這一項是唯一「離線也成立」的入口(看本機資料)。任何組合都不該把它擠掉或擠到後面。
            foreach (bool online in new[] { true, false })
                foreach (bool isSelf in new[] { true, false })
                    foreach (bool isFriend in new[] { true, false })
                        foreach (bool isBlocked in new[] { true, false })
                        {
                            var a = PlayerContextMenu.For(online, isSelf, isFriend, isBlocked);
                            Assert.Greater(a.Length, 0);
                            Assert.AreEqual(PlayerAction.PlayerInfo, a[0]);
                            Assert.LessOrEqual(a.Length, 4, "繪製端沒有為第 5 列留高度");
                        }
        }
    }
}
