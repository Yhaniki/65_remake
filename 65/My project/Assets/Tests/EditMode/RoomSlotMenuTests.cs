using NUnit.Framework;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 房間座位右鍵選單的規則。
    ///
    /// 這些是 UX 規則(管理項的權限由 server 獨立驗,見 NetRoomRulesTests R7/R8),但錯了很傷:
    /// 把按不動的項目畫出來 → 玩家按了收到 error 卻不知道為什麼;漏掉該有的項目 → 功能像不存在。
    ///
    /// 🔴 這一組測試曾經釘的是「非房主右鍵完全沒有選單」。那條規則本身就是錯的 ——
    /// 官方的座位右鍵選單本體是社交選單(玩家信息/私聊/加為好友),誰都能對別人用。
    /// 下面刻意把「非房主也看得到社交項」寫成第一個測試,免得哪天又有人把 host 檢查提回函式開頭。
    /// </summary>
    public class RoomSlotMenuTests
    {
        // 參數多,包一層讓每個測試只寫它在乎的那幾個。
        private static RoomSlotAction[] Menu(bool isHost = true, bool online = true, bool isSelf = false,
                                             bool taken = true, bool closed = false, bool isFriend = false)
            => RoomSlotMenu.For(isHost, online, isSelf, taken, closed, isFriend);

        [Test]
        public void Non_Host_Still_Gets_The_Social_Items()
        {
            // 官方 SP_PopMenu 的前三項跟房主沒有關係。非房主右鍵別人 → 玩家信息 / 私聊 / 加為好友。
            CollectionAssert.AreEqual(
                new[] { RoomSlotAction.PlayerInfo, RoomSlotAction.Whisper, RoomSlotAction.AddFriend },
                Menu(isHost: false));
        }

        [Test]
        public void Non_Host_Gets_No_Management_Items()
        {
            foreach (bool taken in new[] { true, false })
                foreach (bool closed in new[] { true, false })
                {
                    var a = Menu(isHost: false, taken: taken, closed: closed);
                    CollectionAssert.DoesNotContain(a, RoomSlotAction.Kick);
                    CollectionAssert.DoesNotContain(a, RoomSlotAction.TransferHost);
                    CollectionAssert.DoesNotContain(a, RoomSlotAction.CloseSeat);
                    CollectionAssert.DoesNotContain(a, RoomSlotAction.OpenSeat);
                }
        }

        [Test]
        public void An_Empty_Seat_Has_Nothing_Social_And_Nothing_At_All_For_A_Guest()
        {
            // 空位不是一個人:沒有資料可看、沒有人可以私聊/加好友。非房主右鍵空位 → 不要彈選單。
            CollectionAssert.IsEmpty(Menu(isHost: false, taken: false));
            CollectionAssert.IsEmpty(Menu(isHost: false, taken: false, closed: true));
        }

        [Test]
        public void Own_Seat_Only_Offers_Player_Info()
        {
            // 沒有「私聊自己」「加自己好友」;自己也不能踢自己 / 關自己的位子(server 回 badSeat,R8)。
            CollectionAssert.AreEqual(new[] { RoomSlotAction.PlayerInfo }, Menu(isSelf: true));
            CollectionAssert.AreEqual(new[] { RoomSlotAction.PlayerInfo }, Menu(isSelf: true, isFriend: true));
            CollectionAssert.AreEqual(new[] { RoomSlotAction.PlayerInfo }, Menu(isSelf: true, online: false));
        }

        [Test]
        public void Offline_Keeps_Player_Info_But_Drops_Everything_That_Needs_A_Server()
        {
            // 離線沒有 server:私聊送不出去、好友清單的對象也不存在、所有管理操作都是網路指令。
            // 但「玩家信息」是看本機資料,擋掉它單機就沒有辦法看自己的數據了。
            CollectionAssert.AreEqual(new[] { RoomSlotAction.PlayerInfo }, Menu(online: false));
            CollectionAssert.IsEmpty(Menu(online: false, taken: false));
            CollectionAssert.IsEmpty(Menu(online: false, taken: false, closed: true));
        }

        [Test]
        public void Friend_Items_Are_Mutually_Exclusive()
        {
            // 官方 PopBtn_AddFriend / PopBtn_DelFriend 疊在同一格 → 一次只出現一個。
            var stranger = Menu(isFriend: false);
            CollectionAssert.Contains(stranger, RoomSlotAction.AddFriend);
            CollectionAssert.DoesNotContain(stranger, RoomSlotAction.RemoveFriend);

            var friend = Menu(isFriend: true);
            CollectionAssert.Contains(friend, RoomSlotAction.RemoveFriend);
            CollectionAssert.DoesNotContain(friend, RoomSlotAction.AddFriend);

            Assert.AreEqual(stranger.Length, friend.Length, "兩態是取代不是新增,項目數要一樣");
        }

        [Test]
        public void Host_On_An_Occupied_Seat_Gets_Social_Then_Management_In_Official_Order()
        {
            // 順序照官方:玩家信息 → 私聊 → 加為好友 →(管理)切換房主 → 踢出玩家。
            CollectionAssert.AreEqual(
                new[]
                {
                    RoomSlotAction.PlayerInfo, RoomSlotAction.Whisper, RoomSlotAction.AddFriend,
                    RoomSlotAction.TransferHost, RoomSlotAction.Kick,
                },
                Menu());

            // 有人的位子刻意**不**提供「關閉位置」:那條路會連人一起踢掉(R8),
            // 放在選單裡看起來像不會影響到人。要連人清掉是雙擊鎖格的語意。
            CollectionAssert.DoesNotContain(Menu(), RoomSlotAction.CloseSeat);
        }

        [Test]
        public void Host_On_An_Empty_Seat_Only_Gets_The_Close_Open_Toggle()
        {
            CollectionAssert.AreEqual(new[] { RoomSlotAction.CloseSeat }, Menu(taken: false, closed: false));
            CollectionAssert.AreEqual(new[] { RoomSlotAction.OpenSeat }, Menu(taken: false, closed: true));
        }

        [Test]
        public void The_Menu_Never_Exceeds_Five_Rows()
        {
            // 繪製端把選單夾進 800×600(BuildSlotMenu),而列高是官方的 27px ——
            // 最長的組合就是這裡的 5 列 = 135px。這條釘住的是「別再默默加第 6 項」。
            foreach (bool isHost in new[] { true, false })
                foreach (bool online in new[] { true, false })
                    foreach (bool isSelf in new[] { true, false })
                        foreach (bool taken in new[] { true, false })
                            foreach (bool closed in new[] { true, false })
                                foreach (bool isFriend in new[] { true, false })
                                    Assert.LessOrEqual(
                                        RoomSlotMenu.For(isHost, online, isSelf, taken, closed, isFriend).Length, 5);
        }

        [Test]
        public void Double_Click_Toggles_Closed_And_Follows_The_Management_Gate()
        {
            Assert.IsTrue(RoomSlotMenu.DoubleClickClosesSeat(closed: false), "開放的位子 → 雙擊關閉(鎖格)");
            Assert.IsFalse(RoomSlotMenu.DoubleClickClosesSeat(closed: true), "關閉的位子 → 雙擊重新開放");

            Assert.IsTrue(RoomSlotMenu.DoubleClickAllowed(true, true, false));
            Assert.IsFalse(RoomSlotMenu.DoubleClickAllowed(false, true, false), "非房主不能鎖格");
            Assert.IsFalse(RoomSlotMenu.DoubleClickAllowed(true, false, false), "離線沒有鎖格這回事");
            Assert.IsFalse(RoomSlotMenu.DoubleClickAllowed(true, true, true), "不能鎖自己的位子(server 回 badSeat)");
        }
    }
}
