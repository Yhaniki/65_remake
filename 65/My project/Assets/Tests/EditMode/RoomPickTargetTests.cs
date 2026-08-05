using NUnit.Framework;
using Sdo.Net;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    /// <summary>
    /// 「右鍵房間裡的 3D 角色,挑到的是誰」——<see cref="RoomPickTarget"/> 的規則。
    ///
    /// 為什麼值得一整組測試:這條路徑壞掉的症狀是**右鍵完全沒有反應**(而不是彈出錯的選單),
    /// 在畫面上分不出是「挑選沒中」還是「選單規則判空」,而且要開兩台 client、其中一台按下「旁觀」
    /// 才重現得出來。使用者回報的原始 bug 就是這個:旁觀席上的人右鍵不出「玩家信息」。
    /// </summary>
    public class RoomPickTargetTests
    {
        private const string LocalProfileId = "00000001";
        private const int HostUserId = 5001;
        private const int GuestUserId = 5002;
        private const int LookerUserId = 5003;
        private const int OtherLookerUserId = 5004;

        /// <summary>房主 + 一名客人坐著,兩名旁觀者站著 —— 跟實機走同一條對映路徑。</summary>
        private static RoomInfo OnlineRoom()
        {
            var snap = new NetRoomSnapshot { Code = 47884, HostUserId = HostUserId };
            snap.Seats[0].State = SeatState.Taken;
            snap.Seats[0].UserId = HostUserId;
            snap.Seats[0].Name = "飄漂o";
            snap.Seats[2].State = SeatState.Taken;
            snap.Seats[2].UserId = GuestUserId;
            snap.Seats[2].Name = "客人";
            snap.Spectators = new[]
            {
                new NetSpectator { UserId = LookerUserId, Name = "看戲的", Level = 7, Guild = "夜貓子" },
                new NetSpectator { UserId = OtherLookerUserId, Name = "另一個" },
            };
            snap.Spectators[0].Look.Gender = 1;
            return NetRoomMapping.ToRoomInfo(snap);
        }

        /// <summary>離線房:沒有 userId、永遠沒有旁觀者。</summary>
        private static RoomInfo OfflineRoom()
        {
            var room = new RoomInfo { Id = 10001, Capacity = 6 };
            room.Seats.Add(new SeatInfo { Player = new PlayerProfile(LocalProfileId, "飄漂o", 11), IsHost = true });
            return room;
        }

        [Test]
        public void Seated_Player_Opens_The_Seat_Menu()
        {
            var r = RoomPickTarget.Resolve(OnlineRoom(), GuestUserId, HostUserId, 0, false);
            Assert.AreEqual(RoomPickKind.Seat, r.Kind);
            Assert.AreEqual(2, r.Index);
            Assert.IsFalse(r.IsSelf);
        }

        // ---- 這一組是那個 bug 的回歸鎖:旁觀者沒有座位,但他仍然是一個人 ----

        [Test]
        public void Spectator_Is_Pickable_And_Reports_His_List_Index()
        {
            var r = RoomPickTarget.Resolve(OnlineRoom(), OtherLookerUserId, HostUserId, 0, false);
            Assert.AreEqual(RoomPickKind.Spectator, r.Kind);
            Assert.AreEqual(1, r.Index);
            Assert.IsFalse(r.IsSelf);
        }

        [Test]
        public void Local_Player_On_A_Spectator_Slot_Can_Right_Click_Himself()
        {
            // 本機在 3D 挑選裡的 userId 是 0(不是他的 server userId),而他不在任何座位上(-1)。
            var r = RoomPickTarget.Resolve(OnlineRoom(), 0, LookerUserId, -1, true);
            Assert.AreEqual(RoomPickKind.Spectator, r.Kind);
            Assert.AreEqual(0, r.Index);
            Assert.IsTrue(r.IsSelf);
        }

        [Test]
        public void Just_Pressed_Spectate_Before_The_Snapshot_Caught_Up_Still_Resolves()
        {
            // 按下「旁觀」到 server 回快照之間:人已經站到旁觀席上,名單上還沒有他。
            var room = OnlineRoom();
            room.Spectators.Clear();
            var r = RoomPickTarget.Resolve(room, 0, LookerUserId, -1, true);
            Assert.AreEqual(RoomPickKind.Spectator, r.Kind);
            Assert.AreEqual(-1, r.Index);
            Assert.IsTrue(r.IsSelf);
        }

        [Test]
        public void Local_Player_On_A_Seat_Still_Opens_The_Seat_Menu_As_Self()
        {
            var r = RoomPickTarget.Resolve(OnlineRoom(), 0, HostUserId, 0, false);
            Assert.AreEqual(RoomPickKind.Seat, r.Kind);
            Assert.AreEqual(0, r.Index);
            Assert.IsTrue(r.IsSelf);
        }

        [Test]
        public void Offline_Local_Player_Resolves_By_Seat()
        {
            var r = RoomPickTarget.Resolve(OfflineRoom(), 0, 0, 0, false);
            Assert.AreEqual(RoomPickKind.Seat, r.Kind);
            Assert.AreEqual(0, r.Index);
            Assert.IsTrue(r.IsSelf);
        }

        [Test]
        public void Someone_Who_Left_The_Room_Resolves_To_Nothing()
        {
            // 快照還沒追上(那個人已經走了)→ 不彈選單,而不是彈一個空殼。
            var r = RoomPickTarget.Resolve(OnlineRoom(), 9999, HostUserId, 0, false);
            Assert.AreEqual(RoomPickKind.None, r.Kind);
        }

        [Test]
        public void No_Room_Resolves_To_Nothing()
        {
            Assert.AreEqual(RoomPickKind.None, RoomPickTarget.Resolve(null, GuestUserId, HostUserId, 0, false).Kind);
        }

        // ---- 對映:旁觀者的家族與性別要一起帶過來(玩家資訊視窗要用) ----

        [Test]
        public void Spectator_Mapping_Carries_Guild_And_Gender()
        {
            var room = OnlineRoom();
            Assert.AreEqual("夜貓子", room.Spectators[0].Guild);
            Assert.AreEqual(1, room.Spectators[0].Gender);
            Assert.AreEqual(7, room.Spectators[0].Level);
            Assert.AreEqual("看戲的", room.Spectators[0].DisplayName);
            // 沒報家族的人是空字串,不是 null(顯示端直接印它)。
            Assert.AreEqual("", room.Spectators[1].Guild);
            Assert.AreEqual(0, room.Spectators[1].Gender);
        }

        // ---- 旁觀者那份選單:社交三項,一個管理項都不能有 ----

        [Test]
        public void Spectator_Menu_Has_Social_Items_Only()
        {
            // 房主右鍵旁觀者:isHost 一律餵 false(旁觀者沒有座位可以踢/關)。
            var actions = Sdo.UI.Util.RoomSlotMenu.For(false, online: true, isSelf: false, taken: true,
                                                       closed: false, isFriend: false);
            CollectionAssert.AreEqual(new[]
            {
                Sdo.UI.Util.RoomSlotAction.PlayerInfo,
                Sdo.UI.Util.RoomSlotAction.Whisper,
                Sdo.UI.Util.RoomSlotAction.AddFriend,
            }, actions);
        }

        [Test]
        public void Own_Spectator_Menu_Is_Player_Info_Only()
        {
            var actions = Sdo.UI.Util.RoomSlotMenu.For(false, online: true, isSelf: true, taken: true,
                                                       closed: false, isFriend: false);
            CollectionAssert.AreEqual(new[] { Sdo.UI.Util.RoomSlotAction.PlayerInfo }, actions);
        }
    }
}
