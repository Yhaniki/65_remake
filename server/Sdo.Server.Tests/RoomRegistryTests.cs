using NUnit.Framework;
using Sdo.Net;
using Sdo.Net.Server;

namespace Sdo.Tests
{
    /// <summary>
    /// 房間集合 + 「玩家在哪間房」的索引。
    ///
    /// 這個檔的重點是**索引一致性**。一個玩家只能在一間房,而 <c>_userRoom</c> 是那個事實的
    /// 唯一真相。漏更新一次就會出現「玩家離開了但 server 還以為他在房裡」的幽靈狀態 ——
    /// 症狀是別人看到的頭貼永遠停在某個狀態,而且離原因很遠、很難查。
    /// 所以每個進出路徑(建房/加入/旁觀/離開/被踢/座位被關/旁觀被踢)後面都要驗索引。
    /// </summary>
    public class RoomRegistryTests
    {
        private const int Host = 1;
        private const int Bob = 2;
        private const int Cid = 3;

        private static NetJoinUser User(int id)
            => new NetJoinUser(id, "玩家" + id, "", 1, new NetAvatarLook());

        private static NetSongRef Song()
            => new NetSongRef { Official = true, Gn = "sdom1435k.gn", FileId = 11435 };

        private static RoomRegistry Reg(int maxRooms = 8) => new RoomRegistry(maxRooms, seed: 99);

        /// <summary>建一間房並回傳它。</summary>
        private static NetRoom Create(RoomRegistry reg, int hostId, string name = "房")
        {
            NetRoom room;
            LeaveResult left;
            Assert.AreEqual(NetRoomOp.Ok, reg.TryCreate(User(hostId), name, out room, out left));
            return room;
        }

        // ---- 建房 ----

        [Test]
        public void Create_Assigns_A_Five_Digit_Code_And_Indexes_The_Host()
        {
            var reg = Reg();
            var room = Create(reg, Host);

            Assert.GreaterOrEqual(room.Code, NetLimits.MinRoomCode);
            Assert.LessOrEqual(room.Code, NetLimits.MaxRoomCode);
            Assert.AreEqual(1, reg.RoomCount);
            Assert.AreSame(room, reg.Find(room.Code));
            Assert.AreSame(room, reg.RoomOf(Host), "索引要指到這間房");
            Assert.IsTrue(reg.IsInAnyRoom(Host));
        }

        [Test]
        public void Different_Rooms_Get_Different_Codes()
        {
            var reg = Reg();
            var a = Create(reg, Host);
            var b = Create(reg, Bob);
            Assert.AreNotEqual(a.Code, b.Code);
        }

        [Test]
        public void Max_Rooms_Is_Enforced()
        {
            var reg = Reg(maxRooms: 2);
            Create(reg, Host);
            Create(reg, Bob);

            NetRoom room;
            LeaveResult left;
            Assert.AreEqual(NetRoomOp.Full, reg.TryCreate(User(Cid), "第三間", out room, out left));
            Assert.AreEqual(2, reg.RoomCount);
            Assert.IsFalse(reg.IsInAnyRoom(Cid), "失敗的建房不該留下索引");
        }

        [Test]
        public void Creating_While_In_Another_Room_Leaves_The_Old_One_First()
        {
            // 玩家在房間裡直接按「建立房間」不該失敗 —— 該離開現在這間再開新的。
            var reg = Reg();
            var first = Create(reg, Host);
            int firstCode = first.Code;
            NetRoom joined; int seat; LeaveResult l;
            reg.TryJoin(firstCode, User(Bob), out joined, out seat, out l);

            NetRoom second;
            LeaveResult left;
            Assert.AreEqual(NetRoomOp.Ok, reg.TryCreate(User(Bob), "新房", out second, out left));

            Assert.AreSame(first, left.Room, "要回報離開了哪間房");
            Assert.AreEqual(second.Code, reg.RoomOf(Bob).Code, "索引指到新房");
            Assert.IsFalse(first.State.Contains(Bob), "舊房裡不該還有他");
            Assert.AreEqual(2, reg.RoomCount);
        }

        // ---- 加入 ----

        [Test]
        public void Join_Unknown_Code_Fails_Without_Side_Effects()
        {
            var reg = Reg();
            NetRoom room; int seat; LeaveResult left;
            Assert.AreEqual(NetRoomOp.NotInRoom, reg.TryJoin(55555, User(Bob), out room, out seat, out left));
            Assert.IsFalse(reg.IsInAnyRoom(Bob));
        }

        [Test]
        public void Join_Indexes_The_Player()
        {
            var reg = Reg();
            var room = Create(reg, Host);

            NetRoom joined; int seat; LeaveResult left;
            Assert.AreEqual(NetRoomOp.Ok, reg.TryJoin(room.Code, User(Bob), out joined, out seat, out left));
            Assert.AreEqual(1, seat);
            Assert.AreSame(room, reg.RoomOf(Bob));
        }

        [Test]
        public void Joining_The_Same_Room_Twice_Is_Rejected()
        {
            var reg = Reg();
            var room = Create(reg, Host);
            NetRoom j; int seat; LeaveResult left;
            reg.TryJoin(room.Code, User(Bob), out j, out seat, out left);

            Assert.AreEqual(NetRoomOp.BadState, reg.TryJoin(room.Code, User(Bob), out j, out seat, out left));
            Assert.AreSame(room, reg.RoomOf(Bob), "索引不該被破壞");
        }

        [Test]
        public void Joining_Another_Room_Leaves_The_First()
        {
            var reg = Reg();
            var a = Create(reg, Host);
            var b = Create(reg, Bob);

            NetRoom joined; int seat; LeaveResult left;
            Assert.AreEqual(NetRoomOp.Ok, reg.TryJoin(b.Code, User(Cid), out joined, out seat, out left));
            Assert.AreEqual(NetRoomOp.Ok, reg.TryJoin(a.Code, User(Cid), out joined, out seat, out left));

            Assert.AreSame(b, left.Room);
            Assert.AreSame(a, reg.RoomOf(Cid));
            Assert.IsFalse(b.State.Contains(Cid));
        }

        [Test]
        public void Implicit_Leave_That_Closes_The_Target_Room_Is_Handled()
        {
            // 邊界情況:Bob 一個人在 A 房(所以他一走 A 就關),他要加入 B 房。
            // 隱式離房會關掉 A —— 實作必須在那之後重新查 B,不能用失效的參照。
            var reg = Reg();
            var a = Create(reg, Bob);      // Bob 獨自在 A
            var b = Create(reg, Host);     // 目標房

            NetRoom joined; int seat; LeaveResult left;
            Assert.AreEqual(NetRoomOp.Ok, reg.TryJoin(b.Code, User(Bob), out joined, out seat, out left));

            Assert.IsTrue(left.RoomClosed, "A 房該關了");
            Assert.IsNull(reg.Find(a.Code));
            Assert.AreSame(b, reg.RoomOf(Bob));
            Assert.AreEqual(1, reg.RoomCount);
        }

        // ---- 離開 / 關房 ----

        [Test]
        public void Leaving_Clears_The_Index()
        {
            var reg = Reg();
            var room = Create(reg, Host);
            NetRoom j; int seat; LeaveResult l;
            reg.TryJoin(room.Code, User(Bob), out j, out seat, out l);

            var left = reg.Leave(Bob);
            Assert.AreSame(room, left.Room);
            Assert.IsFalse(left.RoomClosed);
            Assert.IsFalse(reg.IsInAnyRoom(Bob));
            Assert.IsNull(reg.RoomOf(Bob));
        }

        [Test]
        public void Leaving_Reports_A_Host_Change()
        {
            var reg = Reg();
            var room = Create(reg, Host);
            NetRoom j; int seat; LeaveResult l;
            reg.TryJoin(room.Code, User(Bob), out j, out seat, out l);

            var left = reg.Leave(Host);
            Assert.AreEqual(Bob, left.NewHostUserId, "Hub 要據此廣播房主換人");
            Assert.AreEqual(Bob, room.HostUserId);
        }

        [Test]
        public void Last_Seated_Player_Leaving_Closes_The_Room_And_Recycles_The_Code()
        {
            var reg = Reg();
            var room = Create(reg, Host);
            int code = room.Code;

            var left = reg.Leave(Host);

            Assert.IsTrue(left.RoomClosed);
            Assert.AreEqual(0, reg.RoomCount);
            Assert.IsNull(reg.Find(code));
            Assert.IsFalse(reg.IsInAnyRoom(Host));
        }

        [Test]
        public void Room_With_Only_Spectators_Stays_Open_And_Hostless()
        {
            // 使用者的規則:「只要房間有人 旁觀也算 房間就不會被關閉,
            // 6 個遊戲的位置全空也是合法的」;而「座位全空的時候就是沒有 host」。
            var reg = Reg();
            var room = Create(reg, Host);
            NetRoom j; int seat; LeaveResult l;
            reg.TryJoin(room.Code, User(Bob), out j, out seat, out l);

            NetRoom sr;
            LeaveResult sl;
            Assert.AreEqual(NetRoomOp.Ok, reg.TrySpectate(room.Code, User(Bob), out sr, out sl));

            var left = reg.Leave(Host);

            Assert.IsFalse(left.RoomClosed, "還有旁觀者 → 房間不關");
            Assert.AreEqual(1, reg.RoomCount);
            Assert.IsTrue(reg.IsInAnyRoom(Bob), "旁觀者還在房裡,索引要留著");
            Assert.AreEqual(0, room.State.SeatedCount);
            Assert.IsFalse(room.HasHost, "座位全空 → 沒有房主");
        }

        [Test]
        public void Room_Closes_And_Recycles_Its_Code_Only_When_Everyone_Left()
        {
            var reg = Reg();
            var room = Create(reg, Host);
            int code = room.Code;
            NetRoom j; int seat; LeaveResult l;
            reg.TryJoin(room.Code, User(Bob), out j, out seat, out l);

            NetRoom sr; LeaveResult sl;
            reg.TrySpectate(room.Code, User(Bob), out sr, out sl);

            reg.Leave(Host);                    // 座位空了,旁觀者還在 → 不關
            Assert.AreEqual(1, reg.RoomCount);

            var left = reg.Leave(Bob);          // 最後一個人也走了 → 關
            Assert.IsTrue(left.RoomClosed);
            Assert.AreEqual(0, reg.RoomCount);
            Assert.IsNull(reg.Find(code));
            Assert.IsFalse(reg.IsInAnyRoom(Bob));
        }

        [Test]
        public void Seating_Into_A_Hostless_Room_Claims_The_Host_Role()
        {
            // 「上來座位的人會變成 host」。
            var reg = Reg();
            var room = Create(reg, Host);
            NetRoom j; int seat; LeaveResult l;
            reg.TryJoin(room.Code, User(Bob), out j, out seat, out l);
            NetRoom sr; LeaveResult sl;
            reg.TrySpectate(room.Code, User(Bob), out sr, out sl);
            reg.Leave(Host);
            Assert.IsFalse(room.HasHost);

            NetRoom jr; int newSeat; LeaveResult jl;
            Assert.AreEqual(NetRoomOp.Ok, reg.TryJoin(room.Code, User(Cid), out jr, out newSeat, out jl));
            Assert.AreEqual(Cid, room.State.HostUserId, "第一個坐下的人接手房主");
        }

        [Test]
        public void Leaving_When_Not_In_A_Room_Is_Harmless()
        {
            var reg = Reg();
            var left = reg.Leave(Cid);
            Assert.IsNull(left.Room);
            Assert.IsFalse(left.RoomClosed);
        }

        [Test]
        public void Recycled_Code_Can_Be_Used_Again_Eventually()
        {
            // 房號回收後池子還能運作(FIFO 所以不會馬上重發,這裡只驗不會漏掉)。
            var reg = Reg(maxRooms: 2);
            var a = Create(reg, Host);
            reg.Leave(Host);
            Assert.AreEqual(0, reg.RoomCount);

            var b = Create(reg, Host);
            Assert.AreEqual(1, reg.RoomCount);
            Assert.AreNotEqual(a.Code, b.Code, "FIFO 回收 → 不會立刻重用同一個號");
        }

        // ---- 旁觀 ----

        [Test]
        public void Spectate_Indexes_The_Player()
        {
            var reg = Reg();
            var room = Create(reg, Host);

            NetRoom sr; LeaveResult sl;
            Assert.AreEqual(NetRoomOp.Ok, reg.TrySpectate(room.Code, User(Bob), out sr, out sl));
            Assert.AreSame(room, reg.RoomOf(Bob));
            Assert.AreEqual(1, room.State.Spectators.Length);
        }

        [Test]
        public void Unspectate_Takes_A_Seat_Back()
        {
            var reg = Reg();
            var room = Create(reg, Host);
            NetRoom sr; LeaveResult sl;
            reg.TrySpectate(room.Code, User(Bob), out sr, out sl);

            NetRoom ur; int seat;
            Assert.AreEqual(NetRoomOp.Ok, reg.TryUnspectate(User(Bob), out ur, out seat));
            Assert.AreEqual(1, seat);
            Assert.AreSame(room, reg.RoomOf(Bob), "還在同一間房");
        }

        // ---- 踢人 / 關座位 / 縮旁觀上限:都要維護索引 ----

        [Test]
        public void Kick_Clears_The_Target_Index()
        {
            var reg = Reg();
            var room = Create(reg, Host);
            NetRoom j; int seat; LeaveResult l;
            reg.TryJoin(room.Code, User(Bob), out j, out seat, out l);

            NetRoom kr; LeaveResult left;
            Assert.AreEqual(NetRoomOp.Ok, reg.KickUser(Host, Bob, out kr, out left));
            Assert.IsFalse(reg.IsInAnyRoom(Bob));
            Assert.IsFalse(room.State.Contains(Bob));
        }

        [Test]
        public void Kick_By_Non_Host_Leaves_Everything_Untouched()
        {
            // 🔴 權限檢查必須在改動索引**之前** —— 否則非 host 的請求會先把人的索引清掉。
            var reg = Reg();
            var room = Create(reg, Host);
            NetRoom j; int seat; LeaveResult l;
            reg.TryJoin(room.Code, User(Bob), out j, out seat, out l);
            reg.TryJoin(room.Code, User(Cid), out j, out seat, out l);

            NetRoom kr; LeaveResult left;
            Assert.AreEqual(NetRoomOp.NotHost, reg.KickUser(Bob, Cid, out kr, out left));
            Assert.IsTrue(reg.IsInAnyRoom(Cid), "被鎖定的人還在房裡");
            Assert.IsTrue(room.State.Contains(Cid));
        }

        [Test]
        public void Closing_An_Occupied_Seat_Clears_That_Players_Index()
        {
            var reg = Reg();
            var room = Create(reg, Host);
            NetRoom j; int seat; LeaveResult l;
            reg.TryJoin(room.Code, User(Bob), out j, out seat, out l);

            NetRoom sr; int kicked;
            Assert.AreEqual(NetRoomOp.Ok, reg.SetSeatClosed(Host, 1, true, out sr, out kicked));
            Assert.AreEqual(Bob, kicked);
            Assert.IsFalse(reg.IsInAnyRoom(Bob), "被鎖格踢掉的人索引也要清");
        }

        [Test]
        public void Shrinking_Looker_Count_Clears_Kicked_Spectator_Indexes()
        {
            var reg = Reg();
            var room = Create(reg, Host);
            NetRoom sr; LeaveResult sl;
            reg.TrySpectate(room.Code, User(Bob), out sr, out sl);
            reg.TrySpectate(room.Code, User(Cid), out sr, out sl);

            object patch;
            Assert.IsTrue(NetJson.TryParse("{\"lookerCount\":1}", out patch));

            NetRoom rr; int[] kicked;
            Assert.AreEqual(NetRoomOp.Ok, reg.SetRoomSettings(Host, patch, out rr, out kicked));
            Assert.AreEqual(1, kicked.Length);
            Assert.IsFalse(reg.IsInAnyRoom(kicked[0]));
        }

        // ---- Tick / 列表 ----

        [Test]
        public void TickAll_Reports_Nothing_When_Rooms_Are_Idle()
        {
            var reg = Reg();
            Create(reg, Host);
            Create(reg, Bob);
            Assert.AreEqual(0, reg.TickAll(1000).Count, "沒事發生就不該產生工作");
        }

        [Test]
        public void TickAll_Reports_The_Room_That_Started_Gameplay()
        {
            var reg = Reg();
            var a = Create(reg, Host);
            Create(reg, Bob);   // 另一間閒著的房

            a.SetSong(Host, Song());
            a.SetAvailability(Host, "sdom1435k.gn", Availability.Have, 0f);
            NetMatchInfo m;
            a.RequestStart(Host, false, new NetResolvedRound { SceneId = 9, FormationType = 0 }, 0, out m);
            a.SetPlayState(Host, PlayState.Loaded, m.MatchId);

            var results = reg.TickAll(100);
            Assert.AreEqual(1, results.Count, "只有 A 房有事");
            Assert.AreSame(a, results[0].Room);
            Assert.IsTrue(results[0].Tick.GameplayStarted);
        }

        [Test]
        public void ListOpenRooms_Is_Sorted_By_Code()
        {
            var reg = Reg();
            Create(reg, Host);
            Create(reg, Bob);
            Create(reg, Cid);

            var list = reg.ListOpenRooms();
            Assert.AreEqual(3, list.Count);
            for (int i = 1; i < list.Count; i++)
                Assert.Less(list[i - 1].Code, list[i].Code, "輸出要穩定,否則房間列表每次刷新都在跳");
        }
    }
}
