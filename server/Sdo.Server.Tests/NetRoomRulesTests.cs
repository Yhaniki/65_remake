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
        public void R4_New_Host_Becomes_Ready_Automatically()
        {
            // 房主沒有準備鈕(恆 ready),所以轉移後新房主必須自動變 ready ——
            // 否則會出現「房主自己沒準備所以不能開始」的死結。
            var r = MakeRoom();
            JoinMany(r, Bob);
            Assert.IsFalse(r.State.Seats[1].Ready);

            r.TransferHost(Host, Bob);
            Assert.IsTrue(r.State.Seats[1].Ready);
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
            Assert.IsTrue(r.State.Seats[1].Ready, "新房主自動 ready");
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
        public void R5_Room_Closes_When_The_Last_Seated_Player_Leaves()
        {
            var r = MakeRoom();
            Assert.IsTrue(r.Leave(Host), "最後一個座位玩家離開 → 關房");
            Assert.AreEqual(RoomStatus.Closed, r.Status);
        }

        [Test]
        public void R5_Room_Closes_Even_If_Spectators_Remain()
        {
            // 旁觀者不能擁有房間 —— 沒有舞者的房間沒有意義,而且沒人能開始遊戲。
            // Hub 收到 true 之後要把剩下的旁觀者踢掉並發 kicked{roomClosed}。
            var r = MakeRoom();
            JoinMany(r, Bob);
            Assert.AreEqual(NetRoomOp.Ok, r.TrySpectate(User(Bob)));
            Assert.AreEqual(1, r.State.Spectators.Length);
            Assert.AreEqual(1, r.State.SeatedCount, "只剩房主坐著");

            Assert.IsTrue(r.Leave(Host), "座位全空 → 關房,即使還有旁觀者");
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

        // ==================== R9:換歌清狀態 ====================

        [Test]
        public void R9_Changing_The_Song_Clears_Ready_And_Availability()
        {
            // 少了這步,原本準備好的人會帶著「上一首歌的 have」被拉進新的一局。
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            Assert.AreEqual(NetRoomOp.Ok, r.SetReady(Bob, true));
            Assert.IsTrue(r.State.Seats[1].Ready);

            // 換另一首歌
            Assert.AreEqual(NetRoomOp.Ok, r.SetSong(Host, new NetSongRef { Official = true, Gn = "sdom0001k.gn" }));

            Assert.IsFalse(r.State.Seats[1].Ready, "換歌要清掉準備狀態");
            Assert.AreEqual(Availability.Unknown, r.State.Seats[1].Avail);
            Assert.AreEqual(Availability.Unknown, r.State.Seats[0].Avail);
            Assert.IsTrue(r.State.Seats[0].Ready, "房主恆 ready");
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
            JoinMany(r, Bob);

            Assert.AreEqual(NetRoomOp.Ok, r.SetOwnTeam(Bob, (int)TeamTag.A));
            Assert.AreEqual((int)TeamTag.A, r.State.Seats[1].Team);
        }

        [Test]
        public void R10a_Own_Team_Cannot_Be_Changed_After_Readying_Up()
        {
            var r = MakeRoom();
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
            JoinMany(r, Bob);
            Assert.AreEqual(NetRoomOp.BadState, r.SetOwnTeam(Bob, 4));
            Assert.AreEqual(NetRoomOp.BadState, r.SetOwnTeam(Bob, -1));
        }

        [Test]
        public void R10a_Spectators_Cannot_Set_A_Team()
        {
            var r = MakeRoom();
            JoinMany(r, Bob);
            r.TrySpectate(User(Bob));
            Assert.AreEqual(NetRoomOp.NotInRoom, r.SetOwnTeam(Bob, (int)TeamTag.A));
        }

        // ==================== R10b:房主一鍵分隊 ====================

        [Test]
        public void R10b_AssignTeams_Requires_An_Exact_Player_Count()
        {
            var r = MakeRoom();
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
        public void Host_Cannot_Become_A_Spectator()
        {
            // 房主要留著管房間,否則沒人能開始遊戲。
            var r = MakeRoom();
            Assert.AreEqual(NetRoomOp.BadState, r.TrySpectate(User(Host)));
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
            Assert.IsTrue(r.State.SeatOf(Host).Ready, "房主恆 ready");
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
            r.SetPlayState(Host, PlayState.Finished, m.MatchId);

            var t = r.Tick(200);
            Assert.IsTrue(t.ResultsReady, "斷線的人被移出本場,剩下的人打完就該結算");
            Assert.AreEqual(RoomStatus.Open, r.Status);
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
        public void R17_Losing_The_Song_Auto_Cancels_Ready()
        {
            // 對映 osu 的「NotDownloaded && Ready → ChangeState(Idle)」。
            // 少了這步,人會被拉進一局他打不了的歌。
            var r = MakeRoom();
            JoinMany(r, Bob);
            SetSongAndHave(r, Host, Bob);
            r.SetReady(Bob, true);
            Assert.IsTrue(r.State.SeatOf(Bob).Ready);

            r.SetAvailability(Bob, "sdom1435k.gn", Availability.Missing, 0f);

            Assert.IsFalse(r.State.SeatOf(Bob).Ready, "歌不見了要自動取消準備");
            Assert.AreEqual(PlayState.Idle, r.State.SeatOf(Bob).PlayState);
        }

        [Test]
        public void R17_Host_Cannot_Use_SetReady()
        {
            // 房主恆 ready(官方 UI 也沒給房主準備鈕)。
            var r = MakeRoom();
            SetSongAndHave(r, Host);
            Assert.AreEqual(NetRoomOp.BadState, r.SetReady(Host, false));
            Assert.IsTrue(r.State.SeatOf(Host).Ready);
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
