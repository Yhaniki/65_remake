using NUnit.Framework;
using Sdo.Net;
using Sdo.Net.Server;

namespace Sdo.Tests
{
    /// <summary>
    /// 房間狀態機的規則 —— **計畫裡的 R1..R20,每條至少一個測試**。
    ///
    /// 這是整個連線功能最重要的測試檔:狀態機錯了的症狀通常離原因很遠
    /// (「為什麼有時候開始遊戲會卡住」、「為什麼房主走了大家就被踢光」),
    /// 而且在真實網路上很難重現。全部在這裡用純邏輯釘死。
    ///
    /// 時間一律注入,所以逾時行為可以精準驗證,不用真的等 30 秒。
    /// </summary>
    public class NetRoomRulesTests
    {
        private const int RoomCode = 12345;
        private const int Host = 1;
        private const int Bob = 2;
        private const int Cid = 3;
        private const int Dan = 4;
        private const int Eve = 5;
        private const int Fay = 6;
        private const int Gus = 7;

        private static NetJoinUser User(int id)
            => new NetJoinUser(id, "玩家" + id, "", 1, new NetAvatarLook());

        private static NetRoom MakeRoom()
            => new NetRoom(RoomCode, User(Host), "測試房");

        private static NetSongRef OfficialSong()
            => new NetSongRef { Official = true, Gn = "sdom1435k.gn", FileId = 11435, Title = "測試歌" };

        private static NetResolvedRound Resolved(TeamLayout layout = TeamLayout.None)
            => new NetResolvedRound { SceneId = 9, FormationType = 0, TeamLayout = layout };

        /// <summary>加入 n 個人(Bob 開始)。</summary>
        private static void JoinMany(NetRoom r, params int[] ids)
        {
            foreach (var id in ids)
            {
                int seat;
                Assert.AreEqual(NetRoomOp.Ok, r.TryJoin(User(id), out seat), "加入 " + id + " 應該成功");
            }
        }

        /// <summary>選官方歌,並讓指定的人都「有歌」。</summary>
        private static void SetSongAndHave(NetRoom r, params int[] userIds)
        {
            Assert.AreEqual(NetRoomOp.Ok, r.SetSong(Host, OfficialSong()));
            foreach (var id in userIds)
                Assert.AreEqual(NetRoomOp.Ok, r.SetAvailability(id, "sdom1435k.gn", Availability.Have, 0f));
        }

        /// <summary>
        /// 把房間切成「普通模式」。
        ///
        /// 新房預設是**自由模式**(<c>NetRoomSettings.GameMode</c> 預設 0),而
        /// <see cref="TeamLayoutRules.TeamsAllowedIn"/> 規定只有普通模式可以組隊 ——
        /// 所以每一個要碰隊伍的測試都得先做這一步,否則 setOwnTeam / assignTeams 會被擋成 BadTeams。
        /// </summary>
        private static void NormalMode(NetRoom r)
        {
            int[] kicked;
            Assert.AreEqual(NetRoomOp.Ok,
                r.SetRoomSettings(Host, ParseSettings("{\"gameMode\":" + TeamLayoutRules.TeamGameMode + "}"), out kicked),
                "切成普通模式");
        }

        /// <summary>讓所有座位玩家都準備好(房主本來就 ready)。</summary>
        private static void ReadyAll(NetRoom r)
        {
            for (int i = 0; i < r.State.Seats.Length; i++)
            {
                var s = r.State.Seats[i];
                if (!s.IsTaken || s.UserId == r.HostUserId) continue;
                Assert.AreEqual(NetRoomOp.Ok, r.SetReady(s.UserId, true), "準備 " + s.UserId);
            }
        }

        // ==================== R2 / R3:座位配置 ====================

        [Test]
        public void R2_Room_Has_Six_Seats_And_Host_Sits_First()
        {
            var r = MakeRoom();
            Assert.AreEqual(NetLimits.RoomCapacity, r.State.Seats.Length);
            Assert.AreEqual(6, r.State.Seats.Length);
            Assert.IsTrue(r.State.Seats[0].IsTaken);
            Assert.AreEqual(Host, r.State.Seats[0].UserId);
            Assert.AreEqual(RoomCode, r.Code);
            Assert.AreEqual(RoomStatus.Open, r.Status);
        }

        [Test]
        public void R3_Join_Takes_The_First_Open_Seat_By_Index()
        {
            var r = MakeRoom();
            int seat;
            Assert.AreEqual(NetRoomOp.Ok, r.TryJoin(User(Bob), out seat));
            Assert.AreEqual(1, seat, "第一個空位是索引 1");

            Assert.AreEqual(NetRoomOp.Ok, r.TryJoin(User(Cid), out seat));
            Assert.AreEqual(2, seat);
        }

        [Test]
        public void R3_Join_Reuses_A_Vacated_Seat_At_The_Lowest_Index()
        {
            var r = MakeRoom();
            JoinMany(r, Bob, Cid, Dan);
            r.Leave(Bob);   // 空出索引 1

            int seat;
            Assert.AreEqual(NetRoomOp.Ok, r.TryJoin(User(Eve), out seat));
            Assert.AreEqual(1, seat, "應該回填最小的空位而不是接在後面");
        }

        [Test]
        public void R3_Full_Room_Rejects_Join()
        {
            var r = MakeRoom();
            JoinMany(r, Bob, Cid, Dan, Eve, Fay);   // 共 6 人
            Assert.AreEqual(6, r.State.SeatedCount);

            int seat;
            Assert.AreEqual(NetRoomOp.Full, r.TryJoin(User(Gus), out seat));
        }

        [Test]
        public void R3_Closed_Seats_Do_Not_Count_As_Open()
        {
            var r = MakeRoom();
            int kicked;
            Assert.AreEqual(NetRoomOp.Ok, r.SetSeatClosed(Host, 1, true, out kicked));
            Assert.AreEqual(NetRoomOp.Ok, r.SetSeatClosed(Host, 2, true, out kicked));

            int seat;
            Assert.AreEqual(NetRoomOp.Ok, r.TryJoin(User(Bob), out seat));
            Assert.AreEqual(3, seat, "應該跳過被關閉的 1 和 2");
        }

        [Test]
        public void Joining_Twice_Is_Rejected()
        {
            var r = MakeRoom();
            int seat;
            Assert.AreEqual(NetRoomOp.Ok, r.TryJoin(User(Bob), out seat));
            Assert.AreEqual(NetRoomOp.BadState, r.TryJoin(User(Bob), out seat));
        }

        // ==================== R4:房主身分跟 userId 不跟座位 ====================

        [Test]
        public void R4_Host_Is_Tracked_By_UserId_Not_Seat_Index()
        {
            // 🔴 這條決定 client 端的房主徽章要怎麼畫:必須跟著 hostUserId,不能假設「座位 0 是房主」。
            var r = MakeRoom();
            JoinMany(r, Bob);

            Assert.AreEqual(NetRoomOp.Ok, r.TransferHost(Host, Bob));

            Assert.AreEqual(Bob, r.HostUserId);
            Assert.AreEqual(Host, r.State.Seats[0].UserId, "轉移房主不搬座位");
            Assert.AreEqual(Bob, r.State.Seats[1].UserId);
            Assert.IsFalse(r.State.IsHost(Host));
            Assert.IsTrue(r.State.IsHost(Bob));
        }

        [Test]
        public void R4_Host_Has_No_Ready_State_At_All()
        {
            // 🔴 房主**沒有**準備這個狀態。它的頭貼顯示 host 徽章(官方 master.an),
            // 而它按的是「開始」不是「準備」。所以 Ready 一律留 false ——
            // 不是「恆為 true」,那會讓 Ready 這個欄位同時承載兩種語意,
            // 於是每個讀它的地方都要記得排除房主,忘掉一處就出錯。
            var r = MakeRoom();
            Assert.IsFalse(r.State.Seats[0].Ready, "房主的 Ready 應該是 false");

            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            Assert.AreEqual(NetRoomOp.Ok, r.SetReady(Bob, true));
            Assert.IsTrue(r.State.Seats[1].Ready);
            Assert.AreEqual(PlayState.Ready, r.State.Seats[1].PlayState);

            Assert.AreEqual(NetRoomOp.Ok, r.TransferHost(Host, Bob));
            Assert.IsFalse(r.State.Seats[1].Ready, "新房主也不需要 Ready");
            Assert.AreEqual(PlayState.Idle, r.State.Seats[1].PlayState);
        }

        [Test]
        public void R4_Host_Can_Start_Without_Being_Ready()
        {
            // 「它可以直接按開始」—— 房主不需要先準備。
            var r = MakeRoom();
            SetSongAndHave(r, Host);
            Assert.IsFalse(r.State.SeatOf(Host).Ready);

            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.Ok, r.RequestStart(Host, false, Resolved(), 0, out m));
            Assert.Contains(Host, m.ParticipantUserIds, "房主自己要算參與者");
        }

        [Test]
        public void R4_New_Host_Can_Start_Immediately_After_Transfer()
        {
            // 轉移房主後不該出現「新房主自己沒準備所以開不了場」的死結。
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            r.TransferHost(Host, Bob);

            NetMatchInfo m;
            // 舊房主 Host 現在是一般玩家,它沒準備 → 非強制開始應該被擋。
            Assert.AreEqual(NetRoomOp.BadState, r.RequestStart(Bob, false, Resolved(), 0, out m));
            // 舊房主準備後就能開。
            Assert.AreEqual(NetRoomOp.Ok, r.SetReady(Host, true));
            Assert.AreEqual(NetRoomOp.Ok, r.RequestStart(Bob, false, Resolved(), 0, out m));
        }

        [Test]
        public void R4_Host_Can_Change_Team_Even_When_Everyone_Else_Is_Ready()
        {
            // 「它還是要可以選隊」—— 這是舊模型(房主恆 Ready)最直接的受害者:
            // 換隊的條件是「還沒準備」,房主恆 Ready 就永遠通不過,
            // 於是組隊模式(要求所有參與者都選隊)永遠開不了場。
            var r = MakeRoom();
            NormalMode(r);   // 只有普通模式可以組隊
            JoinMany(r, Bob, Cid, Dan);
            SetSongAndHave(r, Host, Bob, Cid, Dan);
            ReadyAll(r);

            Assert.AreEqual(NetRoomOp.Ok, r.SetOwnTeam(Host, (int)TeamTag.A));
            Assert.AreEqual((int)TeamTag.A, r.State.SeatOf(Host).Team);
        }

        [Test]
        public void TransferHost_Requires_A_Seated_Target()
        {
            var r = MakeRoom();
            Assert.AreEqual(NetRoomOp.NotInRoom, r.TransferHost(Host, Bob), "Bob 不在房裡");

            // 旁觀者不能當房主 —— 它沒有座位,沒辦法開始遊戲。
            JoinMany(r, Bob);
            Assert.AreEqual(NetRoomOp.Ok, r.TrySpectate(User(Bob)));
            Assert.AreEqual(NetRoomOp.NotInRoom, r.TransferHost(Host, Bob));
        }

        [Test]
        public void TransferHost_To_Self_Is_Rejected()
        {
            var r = MakeRoom();
            Assert.AreEqual(NetRoomOp.BadState, r.TransferHost(Host, Host));
        }

        // ==================== R5:房主離開自動轉移 ====================

        [Test]
        public void R5_Host_Leaving_Promotes_The_Lowest_Seat_Index()
        {
            // 🔴 這是與離線 MockRoomService(host 走 = 整房解散)的**刻意分歧**。
            // 需求 12 要有「切換房主」,線上就該自動轉移而不是把大家踢光。
            var r = MakeRoom();
            JoinMany(r, Bob, Cid);

            bool close = r.Leave(Host);

            Assert.IsFalse(close, "還有人在,房間不該關");
            Assert.AreEqual(Bob, r.HostUserId, "應該給座位索引最小的那個人");
        }

        [Test]
        public void R5_Ready_Guest_Promoted_When_Host_Leaves_Can_Spectate()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            Assert.AreEqual(NetRoomOp.Ok, r.SetReady(Bob, true));
            Assert.AreEqual(PlayState.Ready, r.State.SeatOf(Bob).PlayState);

            Assert.IsFalse(r.Leave(Host), "Bob 還在房間裡");
            Assert.AreEqual(Bob, r.HostUserId);
            Assert.IsFalse(r.State.SeatOf(Bob).Ready, "接任房主後不能保留一般玩家的 Ready");
            Assert.AreEqual(PlayState.Idle, r.State.SeatOf(Bob).PlayState);
            Assert.AreEqual(NetRoomOp.Ok, r.TrySpectate(User(Bob)),
                "新房主應可立即切旁觀，不可被接任前的 Ready 卡住");
            Assert.GreaterOrEqual(r.State.SpectatorIndexOf(Bob), 0);
        }

        [Test]
        public void R5_Host_Leaving_Skips_Vacated_Seats_When_Promoting()
        {
            var r = MakeRoom();
            JoinMany(r, Bob, Cid);
            r.Leave(Bob);            // 座位 1 空了

            r.Leave(Host);
            Assert.AreEqual(Cid, r.HostUserId, "座位 1 已空,應該給座位 2 的人");
        }

        [Test]
        public void R5_Room_Closes_Only_When_Nobody_Is_Left()
        {
            var r = MakeRoom();
            Assert.IsTrue(r.Leave(Host), "一個人都不剩 → 關房");
            Assert.AreEqual(RoomStatus.Closed, r.Status);
        }

        [Test]
        public void R5_Room_Stays_Open_With_Only_Spectators()
        {
            // 使用者的規則:「只要房間有人 旁觀也算 房間就不會被關閉,
            // 6 個遊戲的位置全空也是合法的」。
            var r = MakeRoom();
            JoinMany(r, Bob);
            Assert.AreEqual(NetRoomOp.Ok, r.TrySpectate(User(Bob)));
            Assert.AreEqual(1, r.State.Spectators.Length);

            Assert.IsFalse(r.Leave(Host), "還有旁觀者 → 房間不關");
            Assert.AreEqual(RoomStatus.Open, r.Status);
            Assert.AreEqual(0, r.State.SeatedCount, "六個座位全空是合法狀態");
            Assert.IsFalse(r.IsEmpty);
            Assert.AreEqual(1, r.TotalOccupants);
        }

        [Test]
        public void R5_No_Seated_Players_Means_No_Host()
        {
            // 使用者的規則:「座位全空的時候就是沒有 host」—— 旁觀者不當房主。
            var r = MakeRoom();
            JoinMany(r, Bob);
            r.TrySpectate(User(Bob));
            r.Leave(Host);

            Assert.IsFalse(r.HasHost, "座位全空 → 沒有房主");
            Assert.AreEqual(0, r.State.HostUserId);
            Assert.IsFalse(r.State.IsHost(Bob), "旁觀者不是房主");
        }

        [Test]
        public void R5_First_Player_To_Take_A_Seat_Becomes_Host()
        {
            // 「上來座位的人會變成 host」。
            var r = MakeRoom();
            JoinMany(r, Bob);
            r.TrySpectate(User(Bob));
            r.Leave(Host);
            Assert.IsFalse(r.HasHost);

            // 旁觀者坐回座位 → 它成為房主。
            int seat;
            Assert.AreEqual(NetRoomOp.Ok, r.TryUnspectate(User(Bob), out seat));
            Assert.IsTrue(r.HasHost);
            Assert.AreEqual(Bob, r.State.HostUserId);
        }

        [Test]
        public void R5_A_New_Joiner_Becomes_Host_When_Seats_Were_Empty()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            r.TrySpectate(User(Bob));
            r.Leave(Host);
            Assert.IsFalse(r.HasHost);

            int seat;
            Assert.AreEqual(NetRoomOp.Ok, r.TryJoin(User(Cid), out seat));
            Assert.AreEqual(Cid, r.State.HostUserId, "第一個坐下的人接手房主");
        }

        [Test]
        public void R5_Host_Only_Operations_Are_Refused_While_There_Is_No_Host()
        {
            // 沒有房主的房間沒有舞者,本來就不需要選歌或開始 —— 全部拒絕是正確的。
            var r = MakeRoom();
            JoinMany(r, Bob);
            r.TrySpectate(User(Bob));
            r.Leave(Host);

            Assert.AreEqual(NetRoomOp.NotHost, r.SetSong(Bob, OfficialSong()));
            Assert.AreEqual(NetRoomOp.NotHost, r.SetRoomName(Bob, "旁觀者的房"));

            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.NotHost, r.RequestStart(Bob, true, Resolved(), 0, out m));
        }

        // ==================== R6:離開 idempotent ====================

        [Test]
        public void R6_Leaving_Twice_Is_Harmless()
        {
            // 斷線與主動離開可能同時發生(玩家按離開的瞬間網路也斷了)。
            var r = MakeRoom();
            JoinMany(r, Bob);

            Assert.IsFalse(r.Leave(Bob));
            Assert.IsFalse(r.Leave(Bob), "第二次應該什麼都不做");
            Assert.AreEqual(1, r.State.SeatedCount);
        }

        [Test]
        public void R6_Leaving_A_Room_You_Are_Not_In_Is_Harmless()
        {
            var r = MakeRoom();
            Assert.IsFalse(r.Leave(Gus));
            Assert.AreEqual(1, r.State.SeatedCount);
        }

        // ==================== R7:host-only 操作 ====================

        [Test]
        public void R7_Non_Host_Cannot_Perform_Host_Operations()
        {
            // 🔴 每一個都要在 server 端擋。client 隱藏按鈕只是 UX ——
            // 改過的 client 照樣能把這些訊息送上來。
            var r = MakeRoom();
            NormalMode(r);   // 只有普通模式可以組隊
            JoinMany(r, Bob);

            int kicked;
            int[] kickedSpecs;

            Assert.AreEqual(NetRoomOp.NotHost, r.SetSong(Bob, OfficialSong()));
            Assert.AreEqual(NetRoomOp.NotHost, r.SetRoomName(Bob, "壞人的房"));
            Assert.AreEqual(NetRoomOp.NotHost, r.SetRoomSettings(Bob, null, out kickedSpecs));
            Assert.AreEqual(NetRoomOp.NotHost, r.SetSeatClosed(Bob, 2, true, out kicked));
            Assert.AreEqual(NetRoomOp.NotHost, r.TransferHost(Bob, Bob));
            Assert.AreEqual(NetRoomOp.NotHost, r.AssignTeams(Bob, TeamLayout.V2v2));

            bool close;
            Assert.AreEqual(NetRoomOp.NotHost, r.KickUser(Bob, Host, out close));

            NetMatchInfo match;
            Assert.AreEqual(NetRoomOp.NotHost, r.RequestStart(Bob, false, Resolved(), 0, out match));
        }

        [Test]
        public void R7_Host_Operations_Do_Not_Silently_Succeed_For_Outsiders()
        {
            // 完全不在房裡的人送 host 操作,也要被擋(而不是靜默忽略)。
            var r = MakeRoom();
            int kicked;
            Assert.AreEqual(NetRoomOp.NotHost, r.SetSeatClosed(Gus, 1, true, out kicked));
        }

        [Test]
        public void Kick_Removes_The_Target()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);

            bool close;
            Assert.AreEqual(NetRoomOp.Ok, r.KickUser(Host, Bob, out close));
            Assert.IsFalse(close);
            Assert.AreEqual(1, r.State.SeatedCount);
            Assert.IsFalse(r.State.Contains(Bob));
        }

        [Test]
        public void Host_Cannot_Kick_Itself()
        {
            var r = MakeRoom();
            bool close;
            Assert.AreEqual(NetRoomOp.BadSeat, r.KickUser(Host, Host, out close), "要離開請用 leaveRoom");
        }

        [Test]
        public void Kicking_Someone_Not_In_The_Room_Fails()
        {
            var r = MakeRoom();
            bool close;
            Assert.AreEqual(NetRoomOp.NotInRoom, r.KickUser(Host, Gus, out close));
        }

        // ==================== R8:座位鎖 ====================

        [Test]
        public void R8_Closing_An_Occupied_Seat_Kicks_The_Player_First()
        {
            // 這就是需求 12:「host 點大頭貼兩下可以鎖上面大頭貼的格子,
            // 如果原本那格有玩家就會把那個玩家踢出去」。
            var r = MakeRoom();
            JoinMany(r, Bob);

            int kicked;
            Assert.AreEqual(NetRoomOp.Ok, r.SetSeatClosed(Host, 1, true, out kicked));

            Assert.AreEqual(Bob, kicked, "要回報被踢的是誰(Hub 要發 kicked{seatClosed})");
            Assert.AreEqual(SeatState.Closed, r.State.Seats[1].State);
            Assert.IsFalse(r.State.Contains(Bob));
            Assert.AreEqual(0, r.State.Seats[1].UserId, "座位資料要清乾淨");
        }

        [Test]
        public void R8_Closing_An_Empty_Seat_Kicks_Nobody()
        {
            var r = MakeRoom();
            int kicked;
            Assert.AreEqual(NetRoomOp.Ok, r.SetSeatClosed(Host, 3, true, out kicked));
            Assert.AreEqual(0, kicked);
            Assert.AreEqual(SeatState.Closed, r.State.Seats[3].State);
        }

        [Test]
        public void R8_Host_Cannot_Close_Its_Own_Seat()
        {
            // 關了就沒人能管這間房了。
            var r = MakeRoom();
            int kicked;
            Assert.AreEqual(NetRoomOp.BadSeat, r.SetSeatClosed(Host, 0, true, out kicked));
            Assert.AreEqual(SeatState.Taken, r.State.Seats[0].State);
        }

        [Test]
        public void R8_Host_Cannot_Close_Its_Own_Seat_After_Transfer_Either()
        {
            // 房主轉移之後,「房主的座位」也跟著換 —— 檢查要看 hostUserId 而不是索引 0。
            var r = MakeRoom();
            JoinMany(r, Bob);
            r.TransferHost(Host, Bob);   // Bob 在座位 1

            int kicked;
            Assert.AreEqual(NetRoomOp.BadSeat, r.SetSeatClosed(Bob, 1, true, out kicked), "新房主的座位");
            Assert.AreEqual(NetRoomOp.Ok, r.SetSeatClosed(Bob, 0, true, out kicked), "舊房主的座位現在可以關");
            Assert.AreEqual(Host, kicked);
        }

        [Test]
        public void R8_Reopening_A_Closed_Seat_Works()
        {
            var r = MakeRoom();
            int kicked;
            r.SetSeatClosed(Host, 2, true, out kicked);
            Assert.AreEqual(NetRoomOp.Ok, r.SetSeatClosed(Host, 2, false, out kicked));
            Assert.AreEqual(SeatState.Open, r.State.Seats[2].State);
        }

        [Test]
        public void R8_Bad_Seat_Index_Is_Rejected()
        {
            var r = MakeRoom();
            int kicked;
            Assert.AreEqual(NetRoomOp.BadSeat, r.SetSeatClosed(Host, -1, true, out kicked));
            Assert.AreEqual(NetRoomOp.BadSeat, r.SetSeatClosed(Host, 6, true, out kicked));
            Assert.AreEqual(NetRoomOp.BadSeat, r.SetSeatClosed(Host, 999, true, out kicked));
        }

        // ==================== R9:換歌保留準備、重設歌曲狀態 ====================

        [Test]
        public void R9_Changing_The_Song_Preserves_Ready_And_Resets_Availability()
        {
            // Ready 是跨換歌的意願；availability 必須針對新歌重新確認。
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            Assert.AreEqual(NetRoomOp.Ok, r.SetReady(Bob, true));
            Assert.IsTrue(r.State.Seats[1].Ready);

            // 換另一首歌
            Assert.AreEqual(NetRoomOp.Ok, r.SetSong(Host, new NetSongRef { Official = true, Gn = "sdom0001k.gn" }));

            Assert.IsTrue(r.State.Seats[1].Ready, "換歌要保留準備狀態");
            Assert.AreEqual(PlayState.Ready, r.State.Seats[1].PlayState);
            Assert.AreEqual(Availability.Unknown, r.State.Seats[1].Avail);
            Assert.AreEqual(Availability.Unknown, r.State.Seats[0].Avail);
            Assert.IsFalse(r.State.Seats[0].Ready, "房主沒有準備狀態,一律 false");

            var stickyStates = new[] { Availability.Missing, Availability.Downloading, Availability.Importing, Availability.Have };
            for (int i = 0; i < stickyStates.Length; i++)
            {
                r.SetAvailability(Bob, "sdom0001k.gn", stickyStates[i], 0.5f);
                Assert.IsTrue(r.State.SeatOf(Bob).Ready, "availability 變化不可清掉準備意願");
                Assert.AreEqual(PlayState.Ready, r.State.SeatOf(Bob).PlayState);
            }
        }

        [Test]
        public void R9_Rev_Increases_On_Every_Change()
        {
            // client 靠 rev 丟掉過期快照。
            var r = MakeRoom();
            int rev0 = r.State.Rev;

            int seat;
            r.TryJoin(User(Bob), out seat);
            Assert.Greater(r.State.Rev, rev0);

            int rev1 = r.State.Rev;
            r.SetSong(Host, OfficialSong());
            Assert.Greater(r.State.Rev, rev1);
        }

        [Test]
        public void Song_Cannot_Be_Changed_While_Playing()
        {
            var r = MakeRoom();
            SetSongAndHave(r, Host);
            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.Ok, r.RequestStart(Host, false, Resolved(), 0, out m));

            Assert.AreEqual(NetRoomOp.BadState, r.SetSong(Host, new NetSongRef { Official = true, Gn = "x.gn" }));
        }

        // ==================== R10a:自己換隊 ====================

        [Test]
        public void R10a_Own_Team_Can_Be_Changed_While_Idle()
        {
            // 使用者的要求:「其它玩家如果不是在準備狀態下的話能在自己換組隊」
            var r = MakeRoom();
            NormalMode(r);   // 只有普通模式可以組隊
            JoinMany(r, Bob);

            Assert.AreEqual(NetRoomOp.Ok, r.SetOwnTeam(Bob, (int)TeamTag.A));
            Assert.AreEqual((int)TeamTag.A, r.State.Seats[1].Team);
        }

        [Test]
        public void R10a_Own_Team_Cannot_Be_Changed_After_Readying_Up()
        {
            var r = MakeRoom();
            NormalMode(r);   // 只有普通模式可以組隊
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            Assert.AreEqual(NetRoomOp.Ok, r.SetReady(Bob, true));

            Assert.AreEqual(NetRoomOp.BadState, r.SetOwnTeam(Bob, (int)TeamTag.B),
                "按了準備就不能再換隊");

            // 取消準備之後又可以了。
            Assert.AreEqual(NetRoomOp.Ok, r.SetReady(Bob, false));
            Assert.AreEqual(NetRoomOp.Ok, r.SetOwnTeam(Bob, (int)TeamTag.B));
        }

        [Test]
        public void R10a_Invalid_Team_Value_Is_Rejected()
        {
            var r = MakeRoom();
            NormalMode(r);   // 只有普通模式可以組隊
            JoinMany(r, Bob);
            Assert.AreEqual(NetRoomOp.BadState, r.SetOwnTeam(Bob, 4));
            Assert.AreEqual(NetRoomOp.BadState, r.SetOwnTeam(Bob, -1));
        }

        [Test]
        public void R10a_Spectators_Cannot_Set_A_Team()
        {
            var r = MakeRoom();
            NormalMode(r);   // 只有普通模式可以組隊
            JoinMany(r, Bob);
            r.TrySpectate(User(Bob));
            Assert.AreEqual(NetRoomOp.NotInRoom, r.SetOwnTeam(Bob, (int)TeamTag.A));
        }

        // ==================== R10b:房主一鍵分隊 ====================

        [Test]
        public void R10b_AssignTeams_Requires_An_Exact_Player_Count()
        {
            var r = MakeRoom();
            NormalMode(r);   // 只有普通模式可以組隊
            JoinMany(r, Bob);   // 共 2 人

            Assert.AreEqual(NetRoomOp.BadTeams, r.AssignTeams(Host, TeamLayout.V2v2), "2 個人不能 2v2");

            JoinMany(r, Cid, Dan);   // 共 4 人
            Assert.AreEqual(NetRoomOp.Ok, r.AssignTeams(Host, TeamLayout.V2v2));
            Assert.AreEqual(NetRoomOp.BadTeams, r.AssignTeams(Host, TeamLayout.V3v3), "4 個人不能 3v3");
        }

        [Test]
        public void R10b_AssignTeams_Deals_Round_Robin_Over_Seated_Players()
        {
            var r = MakeRoom();
            NormalMode(r);   // 只有普通模式可以組隊
            JoinMany(r, Bob, Cid, Dan);   // 座位 0,1,2,3

            Assert.AreEqual(NetRoomOp.Ok, r.AssignTeams(Host, TeamLayout.V2v2));

            Assert.AreEqual(0, r.State.Seats[0].Team);
            Assert.AreEqual(1, r.State.Seats[1].Team);
            Assert.AreEqual(0, r.State.Seats[2].Team);
            Assert.AreEqual(1, r.State.Seats[3].Team);
        }

        [Test]
        public void R10b_AssignTeams_Skips_Empty_Seats()
        {
            var r = MakeRoom();
            NormalMode(r);   // 只有普通模式可以組隊
            JoinMany(r, Bob, Cid, Dan, Eve);   // 6 人滿
            r.Leave(Bob);                       // 座位 1 空 → 剩 5 人... 再走一個湊 4
            r.Leave(Cid);                       // 剩 Host, Dan, Eve = 3 人

            Assert.AreEqual(3, r.State.SeatedCount);
            Assert.AreEqual(NetRoomOp.BadTeams, r.AssignTeams(Host, TeamLayout.V2v2), "3 個人湊不出 2v2");
        }

        // ==================== R10c:開場的組隊版型驗證 ====================

        [Test]
        public void R10c_Team_Mode_With_A_Legal_Layout_Can_Start()
        {
            var r = MakeRoom();
            NormalMode(r);   // 只有普通模式可以組隊
            JoinMany(r, Bob, Cid, Dan);
            SetSongAndHave(r, Host, Bob, Cid, Dan);
            r.AssignTeams(Host, TeamLayout.V2v2);
            ReadyAll(r);

            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.Ok, r.RequestStart(Host, false, Resolved(TeamLayout.V2v2), 0, out m));
            Assert.AreEqual(TeamLayout.V2v2, m.Resolved.TeamLayout);
        }

        [Test]
        public void R10c_Uneven_Teams_Cannot_Start()
        {
            // 🔴 使用者的決定:湊不出官方座標表有的版型就**不能開始遊戲**
            // (而不是退回個人隊形 —— 那會讓玩家以為分隊生效了卻看到單人站位)。
            var r = MakeRoom();
            NormalMode(r);   // 只有普通模式可以組隊
            JoinMany(r, Bob, Cid, Dan, Eve);   // 5 人
            SetSongAndHave(r, Host, Bob, Cid, Dan, Eve);

            // 手動分成 3v2 —— 沒有這張座標表。
            r.SetOwnTeam(Bob, (int)TeamTag.A);
            r.SetOwnTeam(Cid, (int)TeamTag.A);
            r.SetOwnTeam(Dan, (int)TeamTag.B);
            r.SetOwnTeam(Eve, (int)TeamTag.B);
            Assert.AreEqual(NetRoomOp.Ok, r.SetOwnTeam(Host, (int)TeamTag.A));   // A=3, B=2
            ReadyAll(r);

            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.BadTeams, r.RequestStart(Host, false, Resolved(TeamLayout.V2v2), 0, out m));
            Assert.AreEqual(RoomStatus.Open, r.Status, "被擋下來,房間狀態不該變");
        }

        [Test]
        public void R10c_Force_Start_Does_Not_Bypass_The_Team_Check()
        {
            // 強制開始不能繞過「站位表不存在」這件事。
            var r = MakeRoom();
            NormalMode(r);   // 只有普通模式可以組隊
            JoinMany(r, Bob, Cid);
            SetSongAndHave(r, Host, Bob, Cid);
            r.SetOwnTeam(Bob, (int)TeamTag.A);
            r.SetOwnTeam(Cid, (int)TeamTag.B);
            r.SetOwnTeam(Host, (int)TeamTag.A);   // A=2, B=1
            ReadyAll(r);

            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.BadTeams, r.RequestStart(Host, true, Resolved(TeamLayout.V2v2), 0, out m));
        }

        [Test]
        public void R10c_Mixed_Free_And_Team_Participants_Cannot_Start()
        {
            // 組隊模式下所有參與者都必須選了隊 —— 有人是「自由」就不知道他該站哪。
            var r = MakeRoom();
            NormalMode(r);   // 只有普通模式可以組隊
            JoinMany(r, Bob, Cid, Dan);
            SetSongAndHave(r, Host, Bob, Cid, Dan);
            r.SetOwnTeam(Bob, (int)TeamTag.A);
            r.SetOwnTeam(Cid, (int)TeamTag.A);
            r.SetOwnTeam(Dan, (int)TeamTag.B);
            // Host 保持「自由」
            ReadyAll(r);

            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.BadTeams, r.RequestStart(Host, false, Resolved(TeamLayout.V2v2), 0, out m));
        }

        [Test]
        public void R10c_Server_Recomputes_The_Layout_And_Rejects_A_Lying_Host()
        {
            // host 送來的 teamLayout 不可信 —— server 用自己手上的參與者名單重算。
            var r = MakeRoom();
            NormalMode(r);   // 只有普通模式可以組隊
            JoinMany(r, Bob, Cid, Dan);
            SetSongAndHave(r, Host, Bob, Cid, Dan);
            r.AssignTeams(Host, TeamLayout.V2v2);   // 實際是 2v2
            ReadyAll(r);

            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.BadTeams, r.RequestStart(Host, false, Resolved(TeamLayout.V3v3), 0, out m),
                "host 謊報 3v3,server 應該拒絕");
        }

        [Test]
        public void R10c_Non_Team_Mode_Requires_Layout_None()
        {
            var r = MakeRoom();
            SetSongAndHave(r, Host);

            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.BadTeams, r.RequestStart(Host, false, Resolved(TeamLayout.V2v2), 0, out m),
                "沒人組隊卻送了組隊版型");
            Assert.AreEqual(NetRoomOp.Ok, r.RequestStart(Host, false, Resolved(TeamLayout.None), 0, out m));
        }

        // ==================== R10d:只有普通模式可以組隊 ====================
        // 使用者的規則:「只有普通模式才能組隊」。自由模式是各玩各的(連難度都各挑各的),
        // ShowTime 是集氣表演 —— 兩者都沒有隊伍的意義。client 也會擋,但那只是為了說得出原因;
        // 這裡這幾條才是真的:改過的 client 不能靠直接送 setOwnTeam / assignTeams 繞過去。

        [Test]
        public void R10d_Free_Mode_Rejects_Setting_A_Team()
        {
            var r = MakeRoom();          // 新房預設就是自由模式
            JoinMany(r, Bob);

            Assert.AreEqual(NetRoomOp.BadTeams, r.SetOwnTeam(Bob, (int)TeamTag.A), "自由模式不能選隊");
            Assert.AreEqual((int)TeamTag.Free, r.State.Seats[1].Team);
        }

        [Test]
        public void R10d_Showtime_Mode_Rejects_Setting_A_Team()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            int[] kicked;
            Assert.AreEqual(NetRoomOp.Ok, r.SetRoomSettings(Host, ParseSettings("{\"gameMode\":2}"), out kicked));

            Assert.AreEqual(NetRoomOp.BadTeams, r.SetOwnTeam(Bob, (int)TeamTag.B), "ShowTime 模式也不能選隊");
        }

        [Test]
        public void R10d_Free_Mode_Rejects_AssignTeams()
        {
            var r = MakeRoom();
            JoinMany(r, Bob, Cid, Dan);

            Assert.AreEqual(NetRoomOp.BadTeams, r.AssignTeams(Host, TeamLayout.V2v2), "自由模式不能一鍵分隊");
        }

        [Test]
        public void R10d_Going_Back_To_Free_Is_Always_Allowed()
        {
            // 「改回自由」永遠放行 —— 否則模式切走之後,已經在某一隊的人會被鎖在那一隊裡出不來。
            var r = MakeRoom();
            NormalMode(r);
            JoinMany(r, Bob);
            Assert.AreEqual(NetRoomOp.Ok, r.SetOwnTeam(Bob, (int)TeamTag.A));

            int[] kicked;
            Assert.AreEqual(NetRoomOp.Ok, r.SetRoomSettings(Host, ParseSettings("{\"gameMode\":0}"), out kicked));
            Assert.AreEqual(NetRoomOp.Ok, r.SetOwnTeam(Bob, (int)TeamTag.Free), "改回自由不受模式限制");
        }

        [Test]
        public void R10d_Leaving_Normal_Mode_Clears_Everyones_Team()
        {
            // 不清的話會留下一組看不見也改不掉的隊伍:面板不讓你動,但 requestStart 仍會判定成組隊局,
            // 然後因為湊不出版型而永遠開不了場。
            var r = MakeRoom();
            NormalMode(r);
            JoinMany(r, Bob, Cid, Dan);
            Assert.AreEqual(NetRoomOp.Ok, r.AssignTeams(Host, TeamLayout.V2v2));
            Assert.AreNotEqual((int)TeamTag.Free, r.State.Seats[1].Team, "前提:分完隊了");

            int[] kicked;
            Assert.AreEqual(NetRoomOp.Ok, r.SetRoomSettings(Host, ParseSettings("{\"gameMode\":0}"), out kicked));

            for (int i = 0; i < r.State.Seats.Length; i++)
                if (r.State.Seats[i].IsTaken)
                    Assert.AreEqual((int)TeamTag.Free, r.State.Seats[i].Team, "座位 " + i + " 應該被清回自由");
        }

        [Test]
        public void R10d_Clearing_Teams_Unblocks_Starting()
        {
            // 上一條的後果:清乾淨之後這一局就是普通(非組隊)局,4 個人也開得起來。
            var r = MakeRoom();
            NormalMode(r);
            JoinMany(r, Bob, Cid, Dan);
            Assert.AreEqual(NetRoomOp.Ok, r.AssignTeams(Host, TeamLayout.V2v2));

            int[] kicked;
            Assert.AreEqual(NetRoomOp.Ok, r.SetRoomSettings(Host, ParseSettings("{\"gameMode\":0}"), out kicked));
            SetSongAndHave(r, Host, Bob, Cid, Dan);
            ReadyAll(r);

            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.Ok, r.RequestStart(Host, false, Resolved(TeamLayout.None), 0, out m));
            Assert.AreEqual(TeamLayout.None, m.Resolved.TeamLayout);
        }

        // ==================== R11:旁觀人數上限 ====================

        [Test]
        public void R11_Spectating_Respects_The_Looker_Limit()
        {
            var r = MakeRoom();
            JoinMany(r, Bob, Cid);

            int[] kickedSpecs;
            // 把上限降到 1
            var patch = ParseSettings("{\"lookerCount\":1}");
            Assert.AreEqual(NetRoomOp.Ok, r.SetRoomSettings(Host, patch, out kickedSpecs));

            Assert.AreEqual(NetRoomOp.Ok, r.TrySpectate(User(Bob)));
            Assert.AreEqual(NetRoomOp.LookerFull, r.TrySpectate(User(Cid)));
        }

        [Test]
        public void A_Full_Room_Still_Takes_Spectators_Until_Those_Are_Full_Too()
        {
            // 使用者問的那條梯子,一次釘死:
            //   六個座位滿 → 第七個人坐不下(Full)→ 但**可以用旁觀身分進來**
            //   → 旁觀席也滿了才是真的進不去(LookerFull)。
            // 官方的房間就是「六個舞者 + 十個旁觀」,把人擋在座位那一關等於少了十個位置。
            var r = MakeRoom();
            JoinMany(r, Bob, Cid, Dan, Eve, Fay);       // 房主 + 5 = 六個座位坐滿

            int seat;
            Assert.AreEqual(NetRoomOp.Full, r.TryJoin(User(Gus), out seat), "座位滿了就是坐不下");

            // 同一個人改用旁觀身分 → 進得來,而且**不佔座位**。
            Assert.AreEqual(NetRoomOp.Ok, r.TrySpectate(User(Gus)));
            Assert.AreEqual(6, r.State.SeatedCount, "旁觀者不佔座位");
            Assert.AreEqual(1, r.State.Spectators.Length);
            Assert.IsTrue(r.State.Contains(Gus), "他在房間裡(只是不在座位上)");

            // 把旁觀上限降到 1(等於旁觀席也滿了)→ 下一個人才真的進不去。
            int[] kicked;
            Assert.AreEqual(NetRoomOp.Ok, r.SetRoomSettings(Host, ParseSettings("{\"lookerCount\":1}"), out kicked));
            Assert.AreEqual(NetRoomOp.LookerFull, r.TrySpectate(User(8)),
                "座位滿 + 旁觀滿 = 這時候才是真的進不了這間房");
        }

        [Test]
        public void R11_Shrinking_The_Looker_Limit_Kicks_The_Newest_Spectators()
        {
            var r = MakeRoom();
            JoinMany(r, Bob, Cid, Dan);
            r.TrySpectate(User(Bob));
            r.TrySpectate(User(Cid));
            r.TrySpectate(User(Dan));
            Assert.AreEqual(3, r.State.Spectators.Length);

            int[] kicked;
            Assert.AreEqual(NetRoomOp.Ok, r.SetRoomSettings(Host, ParseSettings("{\"lookerCount\":1}"), out kicked));

            Assert.AreEqual(1, r.State.Spectators.Length);
            Assert.AreEqual(Bob, r.State.Spectators[0].UserId, "先來的人保住位置");
            Assert.AreEqual(2, kicked.Length);
            Assert.Contains(Cid, kicked);
            Assert.Contains(Dan, kicked);
        }

        [Test]
        public void Host_Can_Spectate_By_Handing_Off_The_Host_Role()
        {
            // 使用者要求:「房主還要可以旁觀(把 host 切給下一個人)」。
            var r = MakeRoom();
            JoinMany(r, Bob, Cid);

            Assert.AreEqual(NetRoomOp.Ok, r.TrySpectate(User(Host)));

            Assert.AreEqual(Bob, r.HostUserId, "host 交給剩下座位索引最小的人");
            Assert.AreEqual(-1, r.State.SeatIndexOf(Host), "房主的座位讓出來了");
            Assert.AreEqual(0, r.State.SpectatorIndexOf(Host));
            Assert.IsTrue(r.State.Seats[0].IsOpen);
        }

        [Test]
        public void Lone_Host_Can_Spectate_Leaving_The_Room_Hostless()
        {
            // 房間只剩房主一個人,它也可以旁觀 —— 房間不會關(還有它自己在),
            // 只是變成「六個座位全空、沒有房主」的合法狀態。
            var r = MakeRoom();

            Assert.AreEqual(NetRoomOp.Ok, r.TrySpectate(User(Host)));

            Assert.AreEqual(0, r.State.SeatedCount);
            Assert.IsFalse(r.HasHost, "座位全空 → 沒有房主");
            Assert.IsFalse(r.IsEmpty, "但房間還有人,不該關");
            Assert.AreEqual(0, r.State.SpectatorIndexOf(Host));
        }

        [Test]
        public void Host_Spectating_Hands_Off_To_A_Seated_Player_When_One_Exists()
        {
            var r = MakeRoom();
            JoinMany(r, Bob, Cid);

            Assert.AreEqual(NetRoomOp.Ok, r.TrySpectate(User(Host)));
            Assert.AreEqual(Bob, r.HostUserId, "有座位玩家就交給索引最小的那個");
        }

        [Test]
        public void Ready_Players_Cannot_Spectate()
        {
            // 使用者要求:「一般玩家準備後不能旁觀」。
            // 不擋的話會出現「已準備的人數」與實際能開場的人對不上。
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            Assert.AreEqual(NetRoomOp.Ok, r.SetReady(Bob, true));

            Assert.AreEqual(NetRoomOp.BadState, r.TrySpectate(User(Bob)));
            Assert.AreEqual(1, r.State.SeatIndexOf(Bob), "還在座位上");

            // 取消準備之後就可以了。
            Assert.AreEqual(NetRoomOp.Ok, r.SetReady(Bob, false));
            Assert.AreEqual(NetRoomOp.Ok, r.TrySpectate(User(Bob)));
        }

        [Test]
        public void Participants_Cannot_Spectate_Mid_Match()
        {
            // 已經在這一場裡面的人不能中途切旁觀。
            //
            // ⚠️ 這是**防禦性**的:打歌畫面沒有「旁觀」鈕,正常玩家送不出這個請求。
            // 擋的是改過的 client —— 中途變旁觀者會讓分數與 playState 對不上,結算就錯了。
            // 真正會用到旁觀切換的是後半段那個 case:留在房間、沒被納入這一場的人。
            var r = MakeRoom();
            JoinMany(r, Bob, Cid);
            SetSongAndHave(r, Host, Bob);
            r.SetReady(Bob, true);

            NetMatchInfo m;
            r.RequestStart(Host, true, Resolved(), 0, out m);

            Assert.AreEqual(NetRoomOp.BadState, r.TrySpectate(User(Bob)), "Bob 正在載入");
            Assert.AreEqual(NetRoomOp.BadState, r.TrySpectate(User(Host)), "房主也在這一場裡");

            // 但沒被納入這一場的人(缺歌的 Cid)還是可以切 —— 它閒著。
            Assert.AreEqual(NetRoomOp.Ok, r.TrySpectate(User(Cid)));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void Completed_Participants_Can_Spectate(bool targetIsHost, bool resultsState)
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            ReadyAll(r);

            NetMatchInfo match;
            Assert.AreEqual(NetRoomOp.Ok, r.RequestStart(Host, false, Resolved(), 0, out match));
            Assert.AreEqual(NetRoomOp.Ok, r.SetPlayState(Host, PlayState.Loaded, match.MatchId));
            Assert.AreEqual(NetRoomOp.Ok, r.SetPlayState(Bob, PlayState.Loaded, match.MatchId));
            Assert.IsTrue(r.Tick(100).GameplayStarted);

            int target = targetIsHost ? Host : Bob;
            int other = targetIsHost ? Bob : Host;
            Assert.AreEqual(NetRoomOp.Ok, r.SetPlayState(target, PlayState.Finished, match.MatchId));

            if (resultsState)
            {
                Assert.AreEqual(NetRoomOp.Ok, r.SetPlayState(other, PlayState.Finished, match.MatchId));
                Assert.IsTrue(r.Tick(200).ResultsReady);
                Assert.AreEqual(PlayState.Results, r.State.SeatOf(target).PlayState);
            }
            else
            {
                Assert.AreEqual(PlayState.Finished, r.State.SeatOf(target).PlayState);
            }

            Assert.AreEqual(NetRoomOp.Ok, r.TrySpectate(User(target)));
            Assert.AreEqual(-1, r.State.SeatIndexOf(target));
            Assert.GreaterOrEqual(r.State.SpectatorIndexOf(target), 0);
            CollectionAssert.Contains(r.Match.ParticipantUserIds, target,
                "切成旁觀只移出 live 座位，不可抹掉本場凍結參與者");
            if (targetIsHost)
                Assert.AreEqual(other, r.HostUserId, "完成後切旁觀的房主要把 host 交給仍在座位的人");
        }

        [Test]
        public void Spectating_Frees_The_Seat()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            Assert.AreEqual(1, r.State.SeatIndexOf(Bob));

            Assert.AreEqual(NetRoomOp.Ok, r.TrySpectate(User(Bob)));
            Assert.AreEqual(-1, r.State.SeatIndexOf(Bob), "座位要讓出來");
            Assert.AreEqual(0, r.State.SpectatorIndexOf(Bob));
            Assert.IsTrue(r.State.Seats[1].IsOpen);
        }

        [Test]
        public void Unspectating_Takes_A_Seat_Back()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            r.TrySpectate(User(Bob));

            int seat;
            Assert.AreEqual(NetRoomOp.Ok, r.TryUnspectate(User(Bob), out seat));
            Assert.AreEqual(1, seat);
            Assert.AreEqual(-1, r.State.SpectatorIndexOf(Bob));
        }

        [Test]
        public void Unspectating_Into_A_Full_Room_Fails()
        {
            var r = MakeRoom();
            JoinMany(r, Bob, Cid, Dan, Eve, Fay);   // 6 人滿
            r.TrySpectate(User(Bob));                // 空出一位
            JoinMany(r, Gus);                        // 別人搶走了

            int seat;
            Assert.AreEqual(NetRoomOp.Full, r.TryUnspectate(User(Bob), out seat));
        }

        // ==================== R12:參與者凍結 ====================

        [Test]
        public void R12_Participants_Are_Frozen_At_Start()
        {
            var r = MakeRoom();
            JoinMany(r, Bob, Cid);
            SetSongAndHave(r, Host, Bob);   // Cid 沒有歌
            r.SetReady(Bob, true);

            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.Ok, r.RequestStart(Host, true, Resolved(), 0, out m));

            Assert.AreEqual(2, m.ParticipantUserIds.Length, "只有 Host 與 Bob");
            Assert.Contains(Host, m.ParticipantUserIds);
            Assert.Contains(Bob, m.ParticipantUserIds);
            Assert.IsFalse(System.Array.IndexOf(m.ParticipantUserIds, Cid) >= 0, "缺歌的 Cid 不該被納入");
        }

        [Test]
        public void R12_Non_Participants_Stay_In_The_Room_Unchanged()
        {
            // 需求 9:「沒有歌的人留在 room,可以看到其他人大頭貼變成顯示 playing」
            var r = MakeRoom();
            JoinMany(r, Bob, Cid);
            SetSongAndHave(r, Host, Bob);
            r.SetReady(Bob, true);

            NetMatchInfo m;
            r.RequestStart(Host, true, Resolved(), 0, out m);

            var cidSeat = r.State.SeatOf(Cid);
            Assert.IsNotNull(cidSeat, "缺歌的人還在房間裡");
            Assert.AreEqual(PlayState.Idle, cidSeat.PlayState, "而且狀態不變");
            Assert.AreEqual(RoomStatus.WaitingForLoad, r.Status);
            Assert.AreEqual(PlayState.WaitingForLoad, r.State.SeatOf(Bob).PlayState);
        }

        [Test]
        public void R12_Start_Requires_A_Song()
        {
            var r = MakeRoom();
            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.NoSong, r.RequestStart(Host, false, Resolved(), 0, out m));
        }

        [Test]
        public void R12_Start_Without_Force_Requires_Everyone_Ready()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            // Bob 沒按準備

            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.BadState, r.RequestStart(Host, false, Resolved(), 0, out m));

            // 強制開始就可以(Bob 留在房間)
            Assert.AreEqual(NetRoomOp.Ok, r.RequestStart(Host, true, Resolved(), 0, out m));
            Assert.AreEqual(1, m.ParticipantUserIds.Length);
        }

        [Test]
        public void R12_Start_Fails_When_Nobody_Has_The_Song()
        {
            var r = MakeRoom();
            Assert.AreEqual(NetRoomOp.Ok, r.SetSong(Host, OfficialSong()));
            // 連房主自己都還沒確認有歌(avail = unknown)

            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.BadState, r.RequestStart(Host, true, Resolved(), 0, out m));
        }

        [Test]
        public void R12_Cannot_Start_Twice()
        {
            var r = MakeRoom();
            SetSongAndHave(r, Host);

            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.Ok, r.RequestStart(Host, false, Resolved(), 0, out m));
            Assert.AreEqual(NetRoomOp.BadState, r.RequestStart(Host, false, Resolved(), 0, out m));
        }


        [Test]
        public void R12_Sticky_Ready_Waits_Until_The_New_Song_Is_Available()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            Assert.AreEqual(NetRoomOp.Ok, r.SetReady(Bob, true));
            var next = new NetSongRef { Official = true, Gn = "sdom0001k.gn" };
            Assert.AreEqual(NetRoomOp.Ok, r.SetSong(Host, next));
            Assert.IsTrue(r.State.SeatOf(Bob).Ready);
            Assert.AreEqual(NetRoomOp.Ok, r.SetAvailability(Host, next.Gn, Availability.Have, 0f));
            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.BadState, r.RequestStart(Host, false, Resolved(), 0, out m));
            Assert.AreEqual(NetRoomOp.Ok, r.SetAvailability(Bob, next.Gn, Availability.Have, 0f));
            Assert.AreEqual(NetRoomOp.Ok, r.RequestStart(Host, false, Resolved(), 0, out m));
        }

        [Test]
        public void R12_Force_Start_Drops_A_Sticky_Ready_Player_Without_The_New_Song()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            r.SetReady(Bob, true);
            var next = new NetSongRef { Official = true, Gn = "sdom0001k.gn" };
            r.SetSong(Host, next);
            r.SetAvailability(Host, next.Gn, Availability.Have, 0f);
            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.Ok, r.RequestStart(Host, true, Resolved(), 0, out m));
            CollectionAssert.AreEqual(new[] { Host }, m.ParticipantUserIds);
            Assert.IsFalse(r.State.SeatOf(Bob).Ready);
            Assert.AreEqual(PlayState.Idle, r.State.SeatOf(Bob).PlayState);
        }

        // ==================== R13:開場 ====================

        [Test]
        public void R13_Gameplay_Starts_When_Nobody_Is_Still_Loading()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            ReadyAll(r);

            NetMatchInfo m;
            r.RequestStart(Host, false, Resolved(), 0, out m);

            // 只有一個人載完 → 還不能開場。
            r.SetPlayState(Host, PlayState.Loaded, m.MatchId);
            var t1 = r.Tick(100);
            Assert.IsFalse(t1.GameplayStarted, "還有人在 waitingForLoad");
            Assert.AreEqual(RoomStatus.WaitingForLoad, r.Status);

            // 兩個都載完 → 開場。
            r.SetPlayState(Bob, PlayState.Loaded, m.MatchId);
            var t2 = r.Tick(200);
            Assert.IsTrue(t2.GameplayStarted);
            Assert.AreEqual(RoomStatus.Playing, r.Status);
            Assert.AreEqual(PlayState.Playing, r.State.SeatOf(Host).PlayState);
            Assert.AreEqual(PlayState.Playing, r.State.SeatOf(Bob).PlayState);
        }

        [Test]
        public void R13_ReadyForGameplay_Does_Not_Block_The_Start()
        {
            // 🔴 osu 的規則:推進條件是「沒人還在 waitingForLoad」,**不是**「全員 readyForGameplay」。
            // 所以一個人只到 loaded、另一個到 readyForGameplay,照樣開場。
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            ReadyAll(r);

            NetMatchInfo m;
            r.RequestStart(Host, false, Resolved(), 0, out m);
            r.SetPlayState(Host, PlayState.Loaded, m.MatchId);
            r.SetPlayState(Bob, PlayState.ReadyForGameplay, m.MatchId);

            var t = r.Tick(100);
            Assert.IsTrue(t.GameplayStarted);
        }

        [Test]
        public void R13_Match_Aborts_When_Everyone_Drops_Out_Of_Loading()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            ReadyAll(r);

            NetMatchInfo m;
            r.RequestStart(Host, false, Resolved(), 0, out m);

            // 兩個人都在載入中就離開了。
            r.Leave(Host);
            r.Leave(Bob);

            // 房間已經因為沒人而關閉,這裡驗的是「不會卡在 waitingForLoad」。
            Assert.AreEqual(RoomStatus.Closed, r.Status);
        }

        [Test]
        public void Client_Cannot_Set_Server_Reserved_States()
        {
            // 🔴 安全邊界:改過的 client 自稱 playing 想繞過載入同步。
            var r = MakeRoom();
            SetSongAndHave(r, Host);
            NetMatchInfo m;
            r.RequestStart(Host, false, Resolved(), 0, out m);

            Assert.AreEqual(NetRoomOp.BadState, r.SetPlayState(Host, PlayState.Playing, m.MatchId));
            Assert.AreEqual(NetRoomOp.BadState, r.SetPlayState(Host, PlayState.WaitingForLoad, m.MatchId));
            Assert.AreEqual(NetRoomOp.BadState, r.SetPlayState(Host, PlayState.Results, m.MatchId));
            Assert.AreEqual(PlayState.WaitingForLoad, r.State.SeatOf(Host).PlayState, "狀態不該被改動");
        }

        [Test]
        public void Stale_MatchId_Is_Rejected()
        {
            // 上一場的遲到訊息不能影響這一場。
            var r = MakeRoom();
            SetSongAndHave(r, Host);
            NetMatchInfo m;
            r.RequestStart(Host, false, Resolved(), 0, out m);

            Assert.AreEqual(NetRoomOp.BadState, r.SetPlayState(Host, PlayState.Loaded, m.MatchId + 99));
        }

        // ==================== M6:強制開始 + 非參與者留在房間 ====================

        [Test]
        public void M6_A_Player_Missing_The_Song_Stays_In_The_Room_While_The_Others_Play()
        {
            // 需求 9 的完整情境:三個人,一個缺歌 → 房主連按兩下強制開始 →
            // 兩個人進遊戲,缺歌那個**留在房間**看另外兩人的頭貼變「遊戲中」。
            var r = MakeRoom();
            JoinMany(r, Bob, Cid);
            Assert.AreEqual(NetRoomOp.Ok, r.SetSong(Host, OfficialSong()));
            // Host 與 Bob 有歌,Cid 沒有
            Assert.AreEqual(NetRoomOp.Ok, r.SetAvailability(Host, "sdom1435k.gn", Availability.Have, 0f));
            Assert.AreEqual(NetRoomOp.Ok, r.SetAvailability(Bob, "sdom1435k.gn", Availability.Have, 0f));
            Assert.AreEqual(NetRoomOp.Ok, r.SetAvailability(Cid, "sdom1435k.gn", Availability.Missing, 0f));
            Assert.AreEqual(NetRoomOp.Ok, r.SetReady(Bob, true));

            // Cid 缺歌 → 按不了準備(R17)→ 不強制就開不了場
            Assert.AreNotEqual(NetRoomOp.Ok, r.SetReady(Cid, true), "缺歌不該按得下準備");

            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.BadState, r.RequestStart(Host, false, Resolved(), 0, out m),
                "還有人沒準備 → 不強制開不了");
            Assert.AreEqual(NetRoomOp.Ok, r.RequestStart(Host, true, Resolved(), 0, out m), "強制開始要成功");

            CollectionAssert.AreEquivalent(new[] { Host, Bob }, m.ParticipantUserIds,
                "參與者只有『有歌 且(是房主或已準備)』的人");

            // 缺歌那個人還在房間裡,而且狀態是 idle(不是被踢出去、也不是 playing)
            var cid = r.State.Seats[r.State.SeatIndexOf(Cid)];
            Assert.IsTrue(cid.IsTaken, "缺歌的人要留在房間,不能被踢掉");
            Assert.AreEqual(PlayState.Idle, cid.PlayState);

            // 另外兩人的頭貼要看得出「在場中」—— client 的 PLAYING 徽章讀的就是這個欄位
            Assert.AreEqual(PlayState.WaitingForLoad, r.State.Seats[r.State.SeatIndexOf(Host)].PlayState);
            Assert.AreEqual(PlayState.WaitingForLoad, r.State.Seats[r.State.SeatIndexOf(Bob)].PlayState);
        }

        [Test]
        public void M6_The_Room_Comes_Back_To_Open_After_The_Match_So_The_Left_Behind_Player_Can_Play_Next()
        {
            // 留在房間的人不能永久卡住:一場打完房間要回 open,他的頭貼徽章也要消失。
            var r = MakeRoom();
            JoinMany(r, Bob, Cid);
            Assert.AreEqual(NetRoomOp.Ok, r.SetSong(Host, OfficialSong()));
            r.SetAvailability(Host, "sdom1435k.gn", Availability.Have, 0f);
            r.SetAvailability(Bob, "sdom1435k.gn", Availability.Have, 0f);
            r.SetAvailability(Cid, "sdom1435k.gn", Availability.Missing, 0f);
            r.SetReady(Bob, true);

            NetMatchInfo m;
            r.RequestStart(Host, true, Resolved(), 0, out m);
            r.SetPlayState(Host, PlayState.Loaded, m.MatchId);
            r.SetPlayState(Bob, PlayState.Loaded, m.MatchId);
            r.Tick(100);
            Assert.AreEqual(RoomStatus.Playing, r.Status);

            r.SetPlayState(Host, PlayState.Finished, m.MatchId);
            r.SetPlayState(Bob, PlayState.Finished, m.MatchId);
            var tick = r.Tick(200);
            Assert.IsTrue(tick.ResultsReady, "兩個人都打完了 → 結算");

            r.ClearResults();
            Assert.AreEqual(RoomStatus.Open, r.Status, "房間要回到 open,不然留在房間的人永遠開不了下一場");
            for (int i = 0; i < r.State.Seats.Length; i++)
            {
                var s = r.State.Seats[i];
                if (!s.IsTaken) continue;
                Assert.AreEqual(PlayState.Idle, s.PlayState, "所有人的遊玩狀態要回 idle(徽章才會消失)");
            }
        }

        // ==================== R14:結算 ====================

        [Test]
        public void R14_Results_Fire_When_Everyone_Finished()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            ReadyAll(r);

            NetMatchInfo m;
            r.RequestStart(Host, false, Resolved(), 0, out m);
            r.SetPlayState(Host, PlayState.Loaded, m.MatchId);
            r.SetPlayState(Bob, PlayState.Loaded, m.MatchId);
            r.Tick(100);
            Assert.AreEqual(RoomStatus.Playing, r.Status);

            r.SetPlayState(Host, PlayState.Finished, m.MatchId);
            var t1 = r.Tick(200);
            Assert.IsFalse(t1.ResultsReady, "還有人在打");

            r.SetPlayState(Bob, PlayState.Finished, m.MatchId);
            var t2 = r.Tick(300);
            Assert.IsTrue(t2.ResultsReady);
            Assert.AreEqual(RoomStatus.Open, r.Status, "房間回到開放狀態");
            Assert.AreEqual(PlayState.Results, r.State.SeatOf(Host).PlayState);
        }

        [Test]
        public void R14_ClearResults_Returns_Everyone_To_Idle()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            ReadyAll(r);

            NetMatchInfo m;
            r.RequestStart(Host, false, Resolved(), 0, out m);
            r.SetPlayState(Host, PlayState.Loaded, m.MatchId);
            r.SetPlayState(Bob, PlayState.Loaded, m.MatchId);
            r.Tick(100);
            r.SetPlayState(Host, PlayState.Finished, m.MatchId);
            r.SetPlayState(Bob, PlayState.Finished, m.MatchId);
            r.Tick(200);

            r.ClearResults();

            Assert.AreEqual(PlayState.Idle, r.State.SeatOf(Host).PlayState);
            Assert.AreEqual(PlayState.Idle, r.State.SeatOf(Bob).PlayState);
            Assert.IsFalse(r.State.SeatOf(Host).Ready, "房主沒有準備狀態");
            Assert.IsFalse(r.State.SeatOf(Bob).Ready, "其他人要重新準備");
            Assert.IsNull(r.Match);
        }

        // ==================== R15:載入逾時 ====================

        [Test]
        public void R15_Load_Timeout_Drops_Stuck_Players_And_Starts_Without_Them()
        {
            // 🔴 這道逃生門是必要的:少了它,一個人載入卡住就會讓整房永遠停在 loading 畫面
            // (ScreenGameplay.BootRevealCo 的 ReadyGate 迴圈本身沒有逾時)。
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            ReadyAll(r);

            NetMatchInfo m;
            r.RequestStart(Host, false, Resolved(), 1000, out m);

            r.SetPlayState(Host, PlayState.Loaded, m.MatchId);
            // Bob 卡住,一直停在 waitingForLoad

            long afterTimeout = 1000 + NetLimits.LoadTimeoutMs;
            var t = r.Tick(afterTimeout);

            Assert.AreEqual(1, t.LoadTimedOutUserIds.Length);
            Assert.AreEqual(Bob, t.LoadTimedOutUserIds[0]);
            Assert.IsTrue(t.GameplayStarted, "剩下的人照樣開場");
            Assert.AreEqual(RoomStatus.Playing, r.Status);
            Assert.AreEqual(PlayState.Idle, r.State.SeatOf(Bob).PlayState, "被逐出的人回到房間 idle");
            Assert.IsFalse(r.State.SeatOf(Bob).Ready);
            Assert.AreEqual(1, m.ParticipantUserIds.Length);
            Assert.AreEqual(Host, m.ParticipantUserIds[0]);
            Assert.AreEqual(1, m.Participants.Length);
            Assert.AreEqual(Host, m.Participants[0].UserId);
        }

        [Test]
        public void R15_Load_Timeout_Force_Advances_Players_Stuck_At_Loaded()
        {
            // 程式載完了但人卡著(osu 的情境是玩家還在調 offset 面板)→ 強制推進而不是踢掉。
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            ReadyAll(r);

            NetMatchInfo m;
            r.RequestStart(Host, false, Resolved(), 0, out m);
            r.SetPlayState(Host, PlayState.Loaded, m.MatchId);
            r.SetPlayState(Bob, PlayState.Loaded, m.MatchId);

            var t = r.Tick(NetLimits.LoadTimeoutMs);
            Assert.AreEqual(0, t.LoadTimedOutUserIds.Length, "都載完了,沒人該被踢");
            Assert.IsTrue(t.GameplayStarted);
        }

        [Test]
        public void R15_Match_Aborts_If_Nobody_Finished_Loading_By_The_Deadline()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            ReadyAll(r);

            NetMatchInfo m;
            r.RequestStart(Host, false, Resolved(), 0, out m);
            // 兩個人都卡住

            var t = r.Tick(NetLimits.LoadTimeoutMs);

            Assert.AreEqual(2, t.LoadTimedOutUserIds.Length);
            Assert.IsTrue(t.MatchAborted);
            Assert.IsFalse(t.GameplayStarted);
            Assert.AreEqual(RoomStatus.Open, r.Status, "回到房間");
            Assert.IsNull(r.Match);
        }

        [Test]
        public void R15_Tick_Before_The_Deadline_Does_Nothing()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            ReadyAll(r);

            NetMatchInfo m;
            r.RequestStart(Host, false, Resolved(), 0, out m);

            var t = r.Tick(NetLimits.LoadTimeoutMs - 1);
            Assert.AreEqual(0, t.LoadTimedOutUserIds.Length);
            Assert.IsFalse(t.GameplayStarted);
            Assert.AreEqual(RoomStatus.WaitingForLoad, r.Status);
        }

        // ==================== R16:遊玩中斷線 ====================

        [Test]
        public void R16_Disconnecting_While_Playing_Does_Not_Hang_The_Match()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            ReadyAll(r);

            NetMatchInfo m;
            r.RequestStart(Host, false, Resolved(), 0, out m);
            r.SetPlayState(Host, PlayState.Loaded, m.MatchId);
            r.SetPlayState(Bob, PlayState.Loaded, m.MatchId);
            r.Tick(100);
            Assert.AreEqual(RoomStatus.Playing, r.Status);

            // Bob 打到一半斷線。
            r.Leave(Bob);
            CollectionAssert.AreEqual(new[] { Host }, m.ParticipantUserIds,
                "active 名單要移除斷線者，避免卡住整場");
            CollectionAssert.AreEqual(new[] { Host, Bob },
                System.Array.ConvertAll(m.Participants, p => p.UserId),
                "結算用凍結名單要保留斷線者與其開場資料");
            r.SetPlayState(Host, PlayState.Finished, m.MatchId);

            var t = r.Tick(200);
            Assert.IsTrue(t.ResultsReady, "斷線的人被移出本場,剩下的人打完就該結算");
            Assert.AreEqual(RoomStatus.Open, r.Status);
        }

        [Test]
        public void R16_Kicked_Participant_Is_Removed_From_Result_Metadata()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            ReadyAll(r);

            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.Ok, r.RequestStart(Host, false, Resolved(), 0, out m));

            bool roomShouldClose;
            Assert.AreEqual(NetRoomOp.Ok, r.KickUser(Host, Bob, out roomShouldClose));
            Assert.IsFalse(roomShouldClose);
            Assert.AreEqual(1, m.ParticipantUserIds.Length);
            Assert.AreEqual(Host, m.ParticipantUserIds[0]);
            Assert.AreEqual(1, m.Participants.Length);
            Assert.AreEqual(Host, m.Participants[0].UserId);
        }

        [Test]
        public void R16_Host_Disconnecting_While_Playing_Keeps_The_Match_Going()
        {
            // 分數是 client 權威的,所以房主走了這一局照樣算完。房主身分依 R5 轉移。
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            ReadyAll(r);

            NetMatchInfo m;
            r.RequestStart(Host, false, Resolved(), 0, out m);
            r.SetPlayState(Host, PlayState.Loaded, m.MatchId);
            r.SetPlayState(Bob, PlayState.Loaded, m.MatchId);
            r.Tick(100);

            r.Leave(Host);
            Assert.AreEqual(Bob, r.HostUserId, "房主轉移給 Bob");
            CollectionAssert.AreEqual(new[] { Bob }, m.ParticipantUserIds);
            CollectionAssert.AreEqual(new[] { Host, Bob },
                System.Array.ConvertAll(m.Participants, p => p.UserId));
            Assert.AreEqual(RoomStatus.Playing, r.Status, "這一局繼續");

            r.SetPlayState(Bob, PlayState.Finished, m.MatchId);
            var t = r.Tick(200);
            Assert.IsTrue(t.ResultsReady);
        }

        // ==================== R17:缺歌不能準備 ====================

        [Test]
        public void R17_Cannot_Ready_Up_Without_The_Song()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            Assert.AreEqual(NetRoomOp.Ok, r.SetSong(Host, OfficialSong()));
            // Bob 的 avail 還是 unknown

            Assert.AreEqual(NetRoomOp.BadState, r.SetReady(Bob, true));

            r.SetAvailability(Bob, "sdom1435k.gn", Availability.Missing, 0f);
            Assert.AreEqual(NetRoomOp.BadState, r.SetReady(Bob, true), "缺歌不能準備");

            r.SetAvailability(Bob, "sdom1435k.gn", Availability.Have, 0f);
            Assert.AreEqual(NetRoomOp.Ok, r.SetReady(Bob, true));
        }

        [Test]
        public void R17_Availability_Changes_Do_Not_Cancel_Ready()
        {
            // 換歌/下載狀態變動不應要求玩家重按 Ready；
            // RequestStart 仍會用 availability 擋住沒有歌曲的人。
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            r.SetReady(Bob, true);
            Assert.IsTrue(r.State.SeatOf(Bob).Ready);

            r.SetAvailability(Bob, "sdom1435k.gn", Availability.Missing, 0f);

            Assert.IsTrue(r.State.SeatOf(Bob).Ready, "availability 變化要保留準備意願");
            Assert.AreEqual(PlayState.Ready, r.State.SeatOf(Bob).PlayState);
        }

        [Test]
        public void R17_Host_Cannot_Use_SetReady()
        {
            // 官方 UI 沒給房主準備鈕 —— 房主的頭貼位置顯示的是 host 徽章。
            var r = MakeRoom();
            SetSongAndHave(r, Host);
            Assert.AreEqual(NetRoomOp.BadState, r.SetReady(Host, false));
            Assert.AreEqual(NetRoomOp.BadState, r.SetReady(Host, true));
            Assert.IsFalse(r.State.SeatOf(Host).Ready, "Ready 始終不適用於房主");
        }

        [Test]
        public void R17_Nobody_Can_Ready_Up_While_The_Room_Is_In_Game()
        {
            // 使用者:「按準備只有在不是在遊戲中才行」。
            // 房間在 waitingForLoad / playing 期間,連留在房間沒下場的人也按不了準備 ——
            // 準備是為了「下一局」,而下一局要等這一局結束才存在。
            var r = MakeRoom();
            JoinMany(r, Bob, Cid);
            SetSongAndHave(r, Host, Bob, Cid);
            r.SetReady(Bob, true);

            NetMatchInfo m;
            r.RequestStart(Host, true, Resolved(), 0, out m);   // Cid 沒準備 → 留在房間
            Assert.AreEqual(RoomStatus.WaitingForLoad, r.Status);

            Assert.AreEqual(NetRoomOp.BadState, r.SetReady(Cid, true), "載入階段不能按準備");

            r.SetPlayState(Host, PlayState.Loaded, m.MatchId);
            r.SetPlayState(Bob, PlayState.Loaded, m.MatchId);
            r.Tick(100);
            Assert.AreEqual(RoomStatus.Playing, r.Status);

            Assert.AreEqual(NetRoomOp.BadState, r.SetReady(Cid, true), "遊玩階段也不能");

            // 這一局結束、房間回到 open 之後才可以。
            r.SetPlayState(Host, PlayState.Finished, m.MatchId);
            r.SetPlayState(Bob, PlayState.Finished, m.MatchId);
            r.Tick(200);
            r.ClearResults();
            Assert.AreEqual(RoomStatus.Open, r.Status);
            Assert.AreEqual(NetRoomOp.Ok, r.SetReady(Cid, true));
        }

        [Test]
        public void R17_Availability_For_A_Different_Song_Is_Ignored()
        {
            // 擋的是「上一首歌的遲到回報」:玩家還在下載 A 歌時房主換成了 B 歌。
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);

            // 送一個不相干的 packId
            Assert.AreEqual(NetRoomOp.Ok, r.SetAvailability(Bob, "sdom9999k.gn", Availability.Missing, 0f));
            Assert.AreEqual(Availability.Have, r.State.SeatOf(Bob).Avail, "不該被舊歌的回報污染");
        }

        [Test]
        public void R17_Download_Progress_Is_Clamped_And_Only_Kept_While_Downloading()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            r.SetSong(Host, OfficialSong());

            r.SetAvailability(Bob, "sdom1435k.gn", Availability.Downloading, 0.42f);
            Assert.AreEqual(0.42f, r.State.SeatOf(Bob).AvailProgress, 1e-6f);

            r.SetAvailability(Bob, "sdom1435k.gn", Availability.Downloading, 5f);
            Assert.AreEqual(1f, r.State.SeatOf(Bob).AvailProgress, 1e-6f, "超過 1 要夾住");

            r.SetAvailability(Bob, "sdom1435k.gn", Availability.Have, 0.9f);
            Assert.AreEqual(0f, r.State.SeatOf(Bob).AvailProgress, 1e-6f, "非下載中就不該留進度");
        }

        // ==================== R18:遊戲中的房間 ====================

        [Test]
        public void R18_Cannot_Join_A_Room_In_Game_But_Can_Spectate()
        {
            var r = MakeRoom();
            SetSongAndHave(r, Host);
            NetMatchInfo m;
            r.RequestStart(Host, false, Resolved(), 0, out m);
            Assert.AreEqual(RoomStatus.WaitingForLoad, r.Status);

            int seat;
            Assert.AreEqual(NetRoomOp.InGame, r.TryJoin(User(Bob), out seat));

            // 但可以進來旁觀(D10:已開打時仍可加入房間看頭貼,只是不會進打歌畫面)。
            Assert.AreEqual(NetRoomOp.Ok, r.TrySpectate(User(Bob)));
        }

        [Test]
        public void R18_Cannot_Unspectate_While_In_Game()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            r.TrySpectate(User(Bob));
            SetSongAndHave(r, Host);
            NetMatchInfo m;
            r.RequestStart(Host, false, Resolved(), 0, out m);

            int seat;
            Assert.AreEqual(NetRoomOp.InGame, r.TryUnspectate(User(Bob), out seat));
        }

        // ==================== 旁觀者進場資格 ====================

        [Test]
        public void Only_Spectators_With_The_Song_Join_The_Match()
        {
            // 使用者要求:旁觀者缺歌不自動下載;有歌的才跟著進打歌畫面。
            var r = MakeRoom();
            JoinMany(r, Bob, Cid);
            r.TrySpectate(User(Bob));
            r.TrySpectate(User(Cid));

            SetSongAndHave(r, Host);
            r.SetAvailability(Bob, "sdom1435k.gn", Availability.Have, 0f);
            r.SetAvailability(Cid, "sdom1435k.gn", Availability.Missing, 0f);

            NetMatchInfo m;
            Assert.AreEqual(NetRoomOp.Ok, r.RequestStart(Host, false, Resolved(), 0, out m));

            Assert.AreEqual(1, m.SpectatorUserIds.Length);
            Assert.AreEqual(Bob, m.SpectatorUserIds[0]);
        }

        // ==================== 房名 ====================

        [Test]
        public void Room_Name_Is_Trimmed_And_Clipped()
        {
            var r = new NetRoom(RoomCode, User(Host), "   有空白的房名   ");
            Assert.AreEqual("有空白的房名", r.State.Name);

            var longName = new string('好', NetLimits.MaxRoomNameChars + 10);
            Assert.AreEqual(NetRoomOp.Ok, r.SetRoomName(Host, longName));
            Assert.AreEqual(NetLimits.MaxRoomNameChars, r.State.Name.Length);
        }

        [Test]
        public void Empty_Room_Name_Is_Allowed()
        {
            // 空房名 → client 顯示「房主名 + 的舞蹈室」(RoomLabels.DisplayName)。
            var r = new NetRoom(RoomCode, User(Host), null);
            Assert.AreEqual("", r.State.Name);
        }

        // ==================== 外觀(setLook)====================

        [Test]
        public void SetLook_Updates_The_Seat_And_Bumps_Rev()
        {
            // 別人是靠座位上的 Look 把你的角色建出來的。握手帶的那份是開機狀態
            // (還沒選性別、穿搭還沒解析)→ 進房前會再報一次,這條就是那條路徑。
            var r = MakeRoom();
            JoinMany(r, Bob);
            int rev = r.State.Rev;

            var look = new NetAvatarLook { Gender = 1, BodyIndex = 2, Parts = new[] { "A", "B" } };
            Assert.AreEqual(NetRoomOp.Ok, r.SetLook(Bob, look));

            var seat = r.State.SeatOf(Bob);
            Assert.AreEqual(1, seat.Look.Gender, "性別要換過來(不然別人看到的是預設女角)");
            Assert.AreEqual(2, seat.Look.BodyIndex);
            Assert.AreEqual(2, seat.Look.Parts.Length);
            Assert.Greater(r.State.Rev, rev, "外觀變了也要 rev++,否則 client 會丟掉這份快照");
        }

        [Test]
        public void SetLook_Works_For_A_Spectator_Too()
        {
            // 旁觀者在房間 3D 裡也是站在那邊的人,一樣要有正確外觀。
            var r = MakeRoom();
            JoinMany(r, Bob);
            Assert.AreEqual(NetRoomOp.Ok, r.TrySpectate(User(Bob)));

            Assert.AreEqual(NetRoomOp.Ok, r.SetLook(Bob, new NetAvatarLook { Gender = 1 }));
            var snap = r.State;
            Assert.AreEqual(1, snap.Spectators.Length);
            Assert.AreEqual(1, snap.Spectators[0].Look.Gender);
        }

        [Test]
        public void SetLook_Needs_You_To_Be_In_The_Room()
        {
            // 正常的競態:剛離房的人送出的最後一筆。回 NotInRoom(server 端會靜默忽略)。
            var r = MakeRoom();
            Assert.AreEqual(NetRoomOp.NotInRoom, r.SetLook(Bob, new NetAvatarLook()));
        }

        [Test]
        public void SetLook_Rejects_A_Null_Look()
        {
            // 壞掉/被改過的 client 可能送出沒有 look 欄位的訊息 —— 不能讓座位的 Look 變成 null,
            // 那會讓每個讀它的地方都要記得判 null(而且忘一處就 NRE)。
            var r = MakeRoom();
            Assert.AreEqual(NetRoomOp.BadState, r.SetLook(Host, null));
            Assert.IsNotNull(r.State.SeatOf(Host).Look);
        }

        [Test]
        public void SetIdentity_Renames_The_Seat_And_Bumps_Rev()
        {
            // 選性別 == 選帳號:女角與男角是兩個 profile,名字不一樣,而握手是開機時做的。
            // 沒有這條路徑的話,換成男角進房的人在別人畫面上是「男角的模型 + 女角的名字」。
            var r = MakeRoom();
            JoinMany(r, Bob);
            int rev = r.State.Rev;

            Assert.AreEqual(NetRoomOp.Ok, r.SetIdentity(Bob, "按黑青眼暴龍壽3", "熱舞家族", 11));

            var seat = r.State.SeatOf(Bob);
            Assert.AreEqual("按黑青眼暴龍壽3", seat.Name);
            Assert.AreEqual("熱舞家族", seat.Guild);
            Assert.AreEqual(11, seat.Level);
            Assert.Greater(r.State.Rev, rev, "名字變了也要 rev++,否則 client 會丟掉這份快照");
        }

        [Test]
        public void SetIdentity_Works_For_A_Spectator_Too()
        {
            // 旁觀名單顯示的也是名字。
            var r = MakeRoom();
            JoinMany(r, Bob);
            Assert.AreEqual(NetRoomOp.Ok, r.TrySpectate(User(Bob)));

            Assert.AreEqual(NetRoomOp.Ok, r.SetIdentity(Bob, "新名字", "家族", 7));
            var snap = r.State;
            Assert.AreEqual(1, snap.Spectators.Length);
            Assert.AreEqual("新名字", snap.Spectators[0].Name);
            Assert.AreEqual(7, snap.Spectators[0].Level);
        }

        [Test]
        public void SetIdentity_Needs_You_To_Be_In_The_Room()
        {
            // 正常的競態:剛離房的人送出的最後一筆。回 NotInRoom(server 端會靜默忽略)。
            var r = MakeRoom();
            Assert.AreEqual(NetRoomOp.NotInRoom, r.SetIdentity(Bob, "誰", "", 1));
        }

        [Test]
        public void SetIdentity_Keeps_The_Old_Name_When_Given_An_Empty_One()
        {
            // 空名字保留原本的 —— 座位名字被抹成空白比顯示舊名字更糟(頭上名牌與聊天行會變成無主的字)。
            // 家族與等級照更新:那兩項「清空」是有意義的(退出家族)。
            var r = MakeRoom();
            JoinMany(r, Bob);
            Assert.AreEqual(NetRoomOp.Ok, r.SetIdentity(Bob, "小明", "舊家族", 5));

            Assert.AreEqual(NetRoomOp.Ok, r.SetIdentity(Bob, "", "", 6));
            var seat = r.State.SeatOf(Bob);
            Assert.AreEqual("小明", seat.Name, "名字不該被空字串洗掉");
            Assert.AreEqual("", seat.Guild);
            Assert.AreEqual(6, seat.Level);
        }

        [Test]
        public void SetIdentity_Clamps_A_Negative_Level()
        {
            // 被改過的 client 可能送負數。等級只用來顯示,夾到 0 就好,不值得為它斷線。
            var r = MakeRoom();
            Assert.AreEqual(NetRoomOp.Ok, r.SetIdentity(Host, "房主", "", -3));
            Assert.AreEqual(0, r.State.SeatOf(Host).Level);
        }

        // ---- helper ----

        /// <summary>把 JSON 字串解成 setRoomSettings 的 patch 節點。</summary>
        private static object ParseSettings(string json)
        {
            object node;
            Assert.IsTrue(NetJson.TryParse(json, out node), "測試用的 JSON 應該合法:" + json);
            return node;
        }
    }
}
