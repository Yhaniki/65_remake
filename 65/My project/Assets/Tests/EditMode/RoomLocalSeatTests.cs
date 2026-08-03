using NUnit.Framework;
using Sdo.Net;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    /// <summary>
    /// 「本機這個人按過準備了嗎」—— 右下角那顆球畫「準備」還是「取消」、按下去送 setReady(true) 還是 (false)，
    /// 都只看 <see cref="RoomLocalSeat.IsReady"/>。
    ///
    /// 為什麼值得一整組測試:這是兩個**不同的號碼系統**在對帳(server 的 userId ↔ 本機存檔的 profile id)，
    /// 對錯了不會報錯 —— 症狀只是「按了準備,球沒翻成取消,而且再按一次也取消不掉」，
    /// 而那要開兩個 client 連上 server 才重現得出來。
    /// </summary>
    public class RoomLocalSeatTests
    {
        private const string LocalProfileId = "00000001";   // DATA/PROFILE 資料夾名 —— 跟 userId 毫無關係
        private const int HostUserId = 4001;
        private const int GuestUserId = 4002;

        /// <summary>房主 + 一名客人的線上房間快照,對映成 UI 的 RoomInfo(跟實機走同一條路徑)。</summary>
        private static RoomInfo OnlineRoom(bool guestReady)
        {
            var snap = new NetRoomSnapshot { Code = 47884, HostUserId = HostUserId };
            snap.Seats[0].State = SeatState.Taken;
            snap.Seats[0].UserId = HostUserId;
            snap.Seats[0].Name = "飄漂o";
            snap.Seats[1].State = SeatState.Taken;
            snap.Seats[1].UserId = GuestUserId;
            snap.Seats[1].Name = "按黑青眼暴龍壽3";
            snap.Seats[1].Ready = guestReady;
            return NetRoomMapping.ToRoomInfo(snap);
        }

        /// <summary>離線(MockRoomService)的房間:沒有 userId,座位認的是 profile id。</summary>
        private static RoomInfo OfflineRoom(bool ready)
        {
            var room = new RoomInfo { Id = 10001, Capacity = 6 };
            room.Seats.Add(new SeatInfo
            {
                Player = new PlayerProfile(LocalProfileId, "飄漂o", 11),
                IsHost = true,
            });
            room.Seats.Add(new SeatInfo
            {
                Player = new PlayerProfile("00000002", "客人", 3),
                IsReady = ready,
            });
            return room;
        }

        // ---- 這一組是那個 bug 的回歸鎖 ----

        [Test]
        public void Online_Ready_Is_Read_By_UserId_Not_Profile_Id()
        {
            // 🔴 線上座位的 Player.Id 是 server 的 userId("4002"),不是本機的 profile id("00000001")。
            // 舊寫法拿 profile id 去比 → 永遠比不中 → 已經準備了還是讀成 false(球不翻、取消不掉)。
            var room = OnlineRoom(guestReady: true);
            Assert.IsTrue(RoomLocalSeat.IsReady(room, GuestUserId, LocalProfileId));
        }

        [Test]
        public void Online_Not_Ready_Is_False()
        {
            var room = OnlineRoom(guestReady: false);
            Assert.IsFalse(RoomLocalSeat.IsReady(room, GuestUserId, LocalProfileId));
        }

        [Test]
        public void Host_Is_Never_Ready_Even_Though_The_Mapping_Fills_It_True()
        {
            // NetRoomMapping 為了「全員準備了嗎」少一個特例,把房主的 IsReady 填成 true。
            // 直接讀它 → 房主那格會冒出「取消」鈕(而房主根本沒有準備這個狀態,server 也會回 BadState)。
            var room = OnlineRoom(guestReady: true);
            Assert.IsTrue(room.Seats[0].IsReady, "前提:mapping 確實把房主填成 ready");
            Assert.IsFalse(RoomLocalSeat.IsReady(room, HostUserId, LocalProfileId));
        }

        [Test]
        public void Spectator_Or_Not_Seated_Is_Not_Ready()
        {
            var room = OnlineRoom(guestReady: true);
            Assert.IsFalse(RoomLocalSeat.IsReady(room, 9999, LocalProfileId));   // 不在任何座位上
        }

        [Test]
        public void Null_Room_Is_Not_Ready()
        {
            Assert.IsFalse(RoomLocalSeat.IsReady(null, GuestUserId, LocalProfileId));
        }

        // ---- 離線:沒有 userId(0) 才退回 profile id 比對 ----

        [Test]
        public void Offline_Falls_Back_To_Profile_Id()
        {
            var room = OfflineRoom(ready: true);
            room.Seats[0].IsHost = false;        // 離線也可能不是房主(mock 的第二人)
            room.Seats[0].IsReady = true;
            Assert.IsTrue(RoomLocalSeat.IsReady(room, 0, LocalProfileId));
        }

        [Test]
        public void Offline_Host_Is_Not_Ready()
        {
            // 離線沒有 userId → 房主判定看座位旗標。本機是房主 → 畫的是「開始」,不是準備/取消。
            var room = OfflineRoom(ready: true);
            room.Seats[0].IsReady = true;
            Assert.IsFalse(RoomLocalSeat.IsReady(room, 0, LocalProfileId));
        }

        [Test]
        public void Unknown_Profile_Id_Is_Not_Ready()
        {
            var room = OfflineRoom(ready: true);
            Assert.IsFalse(RoomLocalSeat.IsReady(room, 0, "99999999"));
            Assert.IsFalse(RoomLocalSeat.IsReady(room, 0, null));
        }

        // ---- 座位索引(頭貼/名字/走位都靠它認人,與 ready 同一份規則) ----

        [Test]
        public void IndexOf_Online_Uses_UserId()
        {
            var room = OnlineRoom(guestReady: false);
            Assert.AreEqual(0, RoomLocalSeat.IndexOf(room, HostUserId, LocalProfileId));
            Assert.AreEqual(1, RoomLocalSeat.IndexOf(room, GuestUserId, LocalProfileId));
            Assert.AreEqual(-1, RoomLocalSeat.IndexOf(room, 9999, LocalProfileId));
        }

        [Test]
        public void IndexOf_Offline_Uses_Profile_Id()
        {
            var room = OfflineRoom(ready: false);
            Assert.AreEqual(0, RoomLocalSeat.IndexOf(room, 0, LocalProfileId));
            Assert.AreEqual(1, RoomLocalSeat.IndexOf(room, 0, "00000002"));
            Assert.AreEqual(-1, RoomLocalSeat.IndexOf(room, 0, "nobody"));
            Assert.AreEqual(-1, RoomLocalSeat.IndexOf(null, 0, LocalProfileId));
        }

        [Test]
        public void Of_Returns_The_Seat_Itself()
        {
            var room = OnlineRoom(guestReady: true);
            var seat = RoomLocalSeat.Of(room, GuestUserId, LocalProfileId);
            Assert.IsNotNull(seat);
            Assert.AreEqual(GuestUserId, seat.UserId);
            Assert.IsNull(RoomLocalSeat.Of(room, 9999, LocalProfileId));
        }
    }
}
