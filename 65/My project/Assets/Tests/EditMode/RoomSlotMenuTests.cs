using NUnit.Framework;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 房間座位右鍵選單的規則。
    ///
    /// 這些是 UX 規則(權限由 server 獨立驗,見 NetRoomRulesTests R7/R8),但錯了很傷:
    /// 把按不動的項目畫出來 → 玩家按了收到 error 卻不知道為什麼;漏掉該有的項目 → 功能像不存在。
    /// </summary>
    public class RoomSlotMenuTests
    {
        [Test]
        public void Non_Host_Gets_No_Menu_At_All()
        {
            // 所有座位操作都是 host-only。非房主右鍵不該彈出任何東西(不是彈出一個灰的)。
            foreach (bool taken in new[] { true, false })
                foreach (bool closed in new[] { true, false })
                    CollectionAssert.IsEmpty(RoomSlotMenu.For(isHost: false, online: true, isSelf: false, taken, closed));
        }

        [Test]
        public void Offline_Gets_No_Menu()
        {
            // 離線房只有自己一個人 —— 沒有人可以踢、也沒有必要關位子。
            CollectionAssert.IsEmpty(RoomSlotMenu.For(true, online: false, isSelf: false, taken: true, closed: false));
            CollectionAssert.IsEmpty(RoomSlotMenu.For(true, online: false, isSelf: false, taken: false, closed: false));
        }

        [Test]
        public void Own_Seat_Gets_No_Menu()
        {
            // 自己不能踢自己;關自己的位子 server 直接回 badSeat(R8)。
            CollectionAssert.IsEmpty(RoomSlotMenu.For(true, true, isSelf: true, taken: true, closed: false));
        }

        [Test]
        public void An_Occupied_Seat_Offers_Kick_And_Transfer_Host()
        {
            var a = RoomSlotMenu.For(true, true, false, taken: true, closed: false);
            CollectionAssert.AreEqual(new[] { RoomSlotAction.Kick, RoomSlotAction.TransferHost }, a);
            // 有人的位子刻意**不**提供「關閉位子」:那條路會連人一起踢掉(R8),
            // 放在選單裡看起來像不會影響到人。要連人清掉是雙擊鎖格的語意。
            CollectionAssert.DoesNotContain(a, RoomSlotAction.CloseSeat);
        }

        [Test]
        public void An_Empty_Open_Seat_Offers_Close_And_A_Closed_Seat_Offers_Open()
        {
            CollectionAssert.AreEqual(new[] { RoomSlotAction.CloseSeat },
                RoomSlotMenu.For(true, true, false, taken: false, closed: false));
            CollectionAssert.AreEqual(new[] { RoomSlotAction.OpenSeat },
                RoomSlotMenu.For(true, true, false, taken: false, closed: true));
        }

        [Test]
        public void Double_Click_Toggles_Closed_And_Follows_The_Same_Gate()
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
