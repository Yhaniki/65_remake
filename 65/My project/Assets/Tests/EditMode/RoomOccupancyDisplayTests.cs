using NUnit.Framework;
using Sdo.Net;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    /// <summary>
    /// 大廳看到的「這間房現在幾個人」——房卡的 <c>1/6</c> 與「房間信息」的參與人數 / 觀戰兩格。
    ///
    /// 🔴 使用者回報的兩個症狀,兩個都在**房間外面**:
    ///    • 房裡有一個人在旁觀,房間信息的觀戰欄卻寫 0/10;
    ///    • 房主把房間關到剩兩格,外面還是寫 1/6(沒有變成 1/2)。
    ///
    /// 這兩件事在**房間裡面**看起來都是對的(房裡讀的是逐座位的 roomSnapshot),所以只有
    /// 「列表封包帶了什麼」這一層測得到 —— 用眼睛看要開兩個 client 才重現得了。
    /// </summary>
    public class RoomOccupancyDisplayTests
    {
        private static NetRoomListEntry Decode(string json)
        {
            object node;
            Assert.IsTrue(NetJson.TryParse(json, out node), "測試資料本身要是合法 JSON");
            return NetRoomListEntry.Decode(node);
        }

        // ------------------------------------------------------------ 房間列表(大廳)

        [Test]
        public void Closed_Seats_Shrink_The_Denominator_On_The_Room_Card()
        {
            // server 送的 capacity 是「開著的座位數」:6 格關掉 4 格 → 2。
            var room = NetRoomMapping.ToRoomInfo(Decode(
                "{\"code\":1,\"count\":1,\"capacity\":2,\"status\":\"open\"}"));

            Assert.AreEqual(2, room.Capacity, "🔴 房主關到剩兩格 → 外面要寫 1/2");
            Assert.AreEqual(1, room.Count);
            Assert.AreEqual(2, room.Seats.Count, "佔位座位跟著開著的格數,不是永遠 6");
            Assert.IsTrue(room.IsFull == false);
        }

        [Test]
        public void Spectator_Count_And_Limit_Reach_The_Room_Info_Dialog()
        {
            var room = NetRoomMapping.ToRoomInfo(Decode(
                "{\"code\":1,\"count\":1,\"capacity\":6,\"status\":\"open\"," +
                "\"spectators\":1,\"lookerCount\":10}"));

            Assert.AreEqual(1, room.SpectatorCount, "🔴 房裡有一個人在旁觀,那格不能寫 0");
            Assert.AreEqual(10, room.SpectatorCapacity);
            Assert.AreEqual(0, room.Spectators.Count, "列表送不到名單 —— 只有數字(見 RoomInfo.SpectatorCount)");
        }

        [Test]
        public void Host_Lowered_Spectator_Limit_Is_The_Denominator()
        {
            var room = NetRoomMapping.ToRoomInfo(Decode(
                "{\"code\":1,\"count\":1,\"capacity\":6,\"status\":\"open\"," +
                "\"spectators\":2,\"lookerCount\":4}"));

            Assert.AreEqual(2, room.SpectatorCount);
            Assert.AreEqual(4, room.SpectatorCapacity, "上限是房主設定的,不是寫死的 10");
        }

        [Test]
        public void Spectating_Turned_Off_Is_Zero_Not_The_Default_Ten()
        {
            // lookerCount 0 是合法設定(房主可以完全關掉旁觀)—— 不能被當成「沒帶這個欄位」。
            var e = Decode("{\"code\":1,\"count\":1,\"capacity\":6,\"status\":\"open\",\"lookerCount\":0}");
            Assert.AreEqual(0, e.LookerCount);
            Assert.AreEqual(0, NetRoomMapping.ToRoomInfo(e).SpectatorCapacity);
        }

        [Test]
        public void Old_Server_Without_The_New_Fields_Falls_Back_To_Ten_Spectator_Slots()
        {
            var e = Decode("{\"code\":1,\"count\":1,\"capacity\":6,\"status\":\"open\"}");
            Assert.AreEqual(NetLimits.MaxSpectators, e.LookerCount, "舊版 server 不送 → 退回官方預設的 10");
            Assert.AreEqual(0, e.Spectators);
        }

        [Test]
        public void Absurd_Spectator_Limit_Is_Clamped()
        {
            Assert.AreEqual(NetLimits.MaxSpectators,
                Decode("{\"code\":1,\"lookerCount\":999}").LookerCount);
            Assert.AreEqual(0, Decode("{\"code\":1,\"lookerCount\":-3}").LookerCount);
        }

        // ------------------------------------------------------------ 房間快照(房裡)

        [Test]
        public void Open_Seat_Count_Ignores_The_Seats_The_Host_Locked()
        {
            var snap = new NetRoomSnapshot();
            Assert.AreEqual(NetLimits.RoomCapacity, snap.OpenSeatCount, "沒關任何格子 → 全開");

            snap.Seats[0].State = SeatState.Taken;
            snap.Seats[0].UserId = 7;
            for (int i = 2; i < snap.Seats.Length; i++) snap.Seats[i].State = SeatState.Closed;

            Assert.AreEqual(2, snap.OpenSeatCount, "剩 座位0(有人)+ 座位1(空著)");
            Assert.AreEqual(1, snap.SeatedCount);
            Assert.AreEqual(NetLimits.RoomCapacity, snap.Capacity,
                "Capacity 仍然是座位**陣列長度** —— 房間畫面畫的是六格,不要跟著縮");
        }

        [Test]
        public void Room_Snapshot_Maps_To_The_Same_Numbers_As_The_Lobby()
        {
            // 同一間房,房裡與房外必須寫出同一組數字(不然一進房數字就跳)。
            var snap = new NetRoomSnapshot();
            snap.Seats[0].State = SeatState.Taken;
            snap.Seats[0].UserId = 7;
            snap.Seats[0].Name = "房主";
            snap.HostUserId = 7;
            for (int i = 2; i < snap.Seats.Length; i++) snap.Seats[i].State = SeatState.Closed;
            snap.Settings.LookerCount = 4;
            snap.Spectators = new[]
            {
                new NetSpectator { UserId = 9, Name = "路人" },
            };

            var room = NetRoomMapping.ToRoomInfo(snap);

            Assert.AreEqual(2, room.Capacity);
            Assert.AreEqual(1, room.Count);
            Assert.AreEqual(1, room.SpectatorCount, "房裡的人數 = 名單長度");
            Assert.AreEqual(1, room.Spectators.Count);
            Assert.AreEqual(4, room.SpectatorCapacity);
        }
    }
}
