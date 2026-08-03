using System.Collections.Generic;

namespace Sdo.Net.Server
{
    /// <summary>
    /// 一間房間的完整狀態機。**這是整個連線功能的規則中心。**
    ///
    /// 刻意做成**零 IO、零 socket、零 UnityEngine 的純邏輯**:
    ///   • server 的 Hub 在單執行緒 actor loop 裡獨佔它 → 不需要任何 lock
    ///   • 每一條規則都能直接單元測試(<c>dotnet test</c> 秒級,不用開 Unity、不用開 socket)
    ///   • client 端的 loopback 假伺服器驅動**同一份**這個類別 → UI 開發不需要真 server,
    ///     而且假伺服器的行為與線上逐位元組相同(這是 osu 最值得抄的工程決策)
    ///
    /// 狀態機的形狀照抄 osu! 的 MultiplayerRoom:
    ///   <c>open → waitingForLoad → playing → open</c>
    /// 推進條件與逃生門見 <see cref="Tick"/>。
    ///
    /// 時間一律由外部注入(<c>nowMs</c>)—— 測試才能精準驗證逾時行為。
    /// </summary>
    public sealed class NetRoom
    {
        private readonly NetRoomSnapshot _state;
        private readonly List<NetSpectator> _spectators = new List<NetSpectator>();
        private NetMatchInfo _match;
        private long _matchDeadlineMs;

        /// <summary>
        /// 結算寬限期的到期時刻(0 = 沒有在結算)。到期時 <see cref="Tick"/> 把還停在
        /// <see cref="PlayState.Results"/> 的座位打回 idle —— 見 <see cref="NetLimits.ResultsGraceMs"/>。
        /// </summary>
        private long _resultsClearAtMs;
        private long _nextMatchId = 1;

        /// <summary>權威狀態。**只有這個類別能改它** —— 外部只讀。</summary>
        public NetRoomSnapshot State => _state;

        public int Code => _state.Code;
        public RoomStatus Status => _state.Status;
        public int HostUserId => _state.HostUserId;

        /// <summary>進行中的場次(null = 沒有)。</summary>
        public NetMatchInfo Match => _match;

        /// <summary>
        /// 一個人都不剩了嗎?(座位與旁觀都空)—— **這才是關房的條件**。
        /// 旁觀者算人:「六個座位全空但有旁觀者」是合法狀態,那間房繼續存在,
        /// 只是**沒有房主**(見 <see cref="HasHost"/>),等有人坐上座位就由他接手。
        /// </summary>
        public bool IsEmpty => _state.SeatedCount == 0 && _spectators.Count == 0;

        /// <summary>
        /// 現在有房主嗎?
        ///
        /// **座位全空時沒有房主**(<c>hostUserId == 0</c>)—— 旁觀者不能當房主。
        /// 那個狀態下所有 host-only 操作都會被拒(沒有舞者,也就不需要選歌或開始);
        /// **第一個坐上座位的人自動成為房主**(見 <see cref="SeatPlayer"/>)。
        /// </summary>
        public bool HasHost => _state.HostUserId != 0;

        /// <summary>房內總人數(座位 + 旁觀)。</summary>
        public int TotalOccupants => _state.SeatedCount + _spectators.Count;

        /// <summary>
        /// 建房。呼叫者自動成為房主並坐上座位 0。
        /// (座位 0 只是「第一個空位」的自然結果 —— 房主身分是靠 <see cref="NetRoomSnapshot.HostUserId"/>
        /// 記的,不是靠座位索引。見 R4。)
        /// </summary>
        public NetRoom(int code, NetJoinUser host, string name, int seq = 0)
        {
            _state = new NetRoomSnapshot();
            _state.Code = code;
            _state.Seq = seq;
            _state.Name = ClipRoomName(name);
            _state.HostUserId = host.UserId;
            _state.Status = RoomStatus.Open;
            _state.Rev = 1;

            SeatPlayer(0, host);
            // 房主的 Ready 一律留 false —— 見 IsClearedToStart 的說明:
            // 房主不是「準備好的玩家」,它是房主,那是另一種身分。
        }

        /// <summary>
        /// 這個座位可以被納入下一場了嗎?
        ///
        /// 🔴 **房主沒有「準備」這個狀態。** 它的頭貼顯示的是 host 徽章(官方 master.an),
        /// 而不是準備標記;它也不需要按準備 —— 它按的是「開始」。所以判定是
        /// 「**是房主** 或 **按了準備**」,而不是把房主的 Ready 硬設成 true。
        ///
        /// 為什麼不用「房主恆 Ready = true」那種做法(我一開始就是那樣寫的):那會讓
        /// <c>Ready</c> 這個欄位同時承載兩種語意,結果每個讀它的地方都要記得排除房主 ——
        /// 忘掉一處就出錯,而且症狀很難聯想。最明顯的例子是「房主永遠不能選自己的隊」:
        /// 換隊的條件是「還沒準備」,而房主恆 Ready 就永遠通不過,
        /// 於是組隊模式(要求所有參與者都選隊)永遠開不了場。
        /// </summary>
        private bool IsClearedToStart(NetSeat seat)
            => seat.IsTaken && (_state.IsHost(seat.UserId) || seat.Ready);

        // ================= 加入 / 離開 / 旁觀 =================

        /// <summary>
        /// 加入房間(R3):坐**第一個 Open 座位**(依索引序)。
        ///
        /// **房間在遊戲中一樣坐得進來**(R18,使用者要求:「別人在遊戲我還是要可以上去一般座位,
        /// 只是沒辦法直接進去他們的遊戲」)。以前這裡回 <see cref="NetRoomOp.InGame"/>,client 的
        /// <c>JoinOrSpectate</c> 於是把人退成旁觀者 —— 而旁觀席出不來也回不去(那是另一顆鈕的事),
        /// 玩家就被卡在旁觀身分直到離開房間。
        ///
        /// 坐下的人**不會被拉進正在進行的那一場**:參與者名單在開場那一刻就凍結(R12),
        /// <c>matchStarting</c> 也早就發完了 —— 他的 playState 是 idle,留在房間等下一局。
        /// </summary>
        public NetRoomOp TryJoin(NetJoinUser user, out int seatIndex)
        {
            seatIndex = -1;
            if (_state.Contains(user.UserId)) return NetRoomOp.BadState;   // 已經在房裡

            int seat = _state.FirstOpenSeat();
            if (seat < 0) return NetRoomOp.Full;

            SeatPlayer(seat, user);
            seatIndex = seat;
            Touch();
            return NetRoomOp.Ok;
        }

        /// <summary>
        /// 以旁觀身分加入,或把座位換成旁觀(使用者的選擇:房內按「旁觀」鈕切換)。
        /// **旁觀者不佔座位** —— 房間可以有 6 個舞者 + 最多 10 個旁觀者。
        /// 遊戲中的房間**允許**旁觀加入(R18),但那些人不會進打歌畫面(見 D10)。
        /// </summary>
        public NetRoomOp TrySpectate(NetJoinUser user)
        {
            if (_state.SpectatorIndexOf(user.UserId) >= 0) return NetRoomOp.Ok;   // 已經在旁觀

            if (_spectators.Count >= _state.Settings.LookerCount) return NetRoomOp.LookerFull;

            int seat = _state.SeatIndexOf(user.UserId);
            if (seat >= 0)
            {
                var s = _state.Seats[seat];

                // **一般玩家準備後不能旁觀**(使用者要求)—— 要先取消準備。
                // 不擋的話會出現「已準備的人數」與實際能開場的人對不上。
                bool completed = s.PlayState == PlayState.Finished || s.PlayState == PlayState.Results;
                if (s.Ready && !completed) return NetRoomOp.BadState;

                // 已經進入遊戲流程(載入中/遊玩中/看結算)不能切。
                //
                // ⚠️ 這條是**純防禦**,不是玩家會遇到的情境:打歌畫面裡根本沒有「旁觀」這顆鈕,
                // 正在跳舞的人沒有手段送出這個請求。它擋的是改過的 client ——
                // 中途把自己變成旁觀者會讓分數與 playState 對不上,整場的結算就錯了。
                //
                // 反過來,**沒被納入這一場的人**(缺歌或沒準備、留在房間的)playState 是 idle,
                // 所以他們隨時可以切成旁觀者 —— 他們本來就沒在玩,只是從座位換到旁觀位。
                // 但他們**不會**因此進入正在進行的打歌畫面(不做中途接入,要等下一局)。
                if (s.PlayState != PlayState.Idle && !completed) return NetRoomOp.BadState;

                // **房主可以旁觀,並且把 host 交給下一個人**(使用者要求)。
                // 沒有其他座位玩家可以接手時 → **房間變成沒有房主**(hostUserId = 0),
                // 而不是讓旁觀者當房主。六個座位全空是合法狀態,那時候本來就不需要
                // 選歌或開始;下一個坐上座位的人會自動接手(見 SeatPlayer)。
                if (_state.IsHost(user.UserId))
                {
                    int heir = FindHeirSeat(seat);
                    _state.HostUserId = heir >= 0 ? _state.Seats[heir].UserId : 0;
                }

                s.Clear();
            }

            var sp = new NetSpectator
            {
                UserId = user.UserId,
                Name = user.Name,
                Guild = user.Guild,
                GuildEmblem = user.GuildEmblem,
                Level = user.Level,
                Look = user.Look,
                Avail = Availability.Unknown,
            };
            _spectators.Add(sp);
            SyncSpectators();
            Touch();
            return NetRoomOp.Ok;
        }

        /// <summary>
        /// 旁觀者搶回座位(再按一次「旁觀」鈕 = 綠色「進入」)。
        /// **房間在遊戲中一樣回得去座位**(同 <see cref="TryJoin"/>):回去的人 playState 是 idle,
        /// 不會被納入正在進行的那一場,只是先坐好等下一局。
        /// </summary>
        public NetRoomOp TryUnspectate(NetJoinUser user, out int seatIndex)
        {
            seatIndex = -1;
            int si = _state.SpectatorIndexOf(user.UserId);
            if (si < 0) return NetRoomOp.NotInRoom;

            int seat = _state.FirstOpenSeat();
            if (seat < 0) return NetRoomOp.Full;

            _spectators.RemoveAt(si);
            SeatPlayer(seat, user);
            seatIndex = seat;
            SyncSpectators();
            Touch();
            return NetRoomOp.Ok;
        }

        /// <summary>
        /// 離開房間。斷線也走這條(R6:斷線 == leaveRoom,idempotent)。
        ///
        /// 回傳 true = **這間房該關掉了**。關房條件是「**完全沒有人**」——
        /// 座位與旁觀都空。**旁觀者算人**(使用者:「只要房間有人 旁觀也算 房間就不會被關閉」),
        /// 所以「六個座位全空但有旁觀者」是合法狀態。
        ///
        /// **房主離開會自動轉移**(R5):優先給剩下 Taken 座位中索引最小的人;
        /// 座位都空了就給第一個旁觀者(旁觀著的房主仍能選歌/踢人/改設定,
        /// 只是要等有人坐下才開得了場)。這是與離線 <c>MockRoomService.LeaveRoom</c>
        /// (host 走 = 整房解散)的**刻意分歧** —— 需求 12 要有「切換房主」。
        /// </summary>
        public bool Leave(int userId)
        {
            bool wasHost = _state.IsHost(userId);

            int seat = _state.SeatIndexOf(userId);
            if (seat >= 0) _state.Seats[seat].Clear();

            int si = _state.SpectatorIndexOf(userId);
            if (si >= 0) { _spectators.RemoveAt(si); SyncSpectators(); }

            if (seat < 0 && si < 0) return false;   // 本來就不在這房 → 什麼都沒變

            // 逐出 active 集合，讓剩下的人能完成本場；但開場時凍結的結算資料要保留，
            // 才能依 R16 把斷線者的最後一筆 frame 列進 resultsReady。
            RemoveActiveParticipant(userId);

            if (IsEmpty)
            {
                _state.Status = RoomStatus.Closed;
                Touch();
                return true;   // 一個人都不剩了 → 關房
            }

            if (wasHost) PromoteNewHost();

            Touch();
            return false;
        }

        // ================= 座位管理(全部 host only) =================

        /// <summary>踢人(R7)。不能踢房主自己。</summary>
        public NetRoomOp KickUser(int actorId, int targetId, out bool roomShouldClose)
        {
            roomShouldClose = false;
            if (!_state.IsHost(actorId)) return NetRoomOp.NotHost;
            if (targetId == actorId) return NetRoomOp.BadSeat;      // 要離開請用 leaveRoom
            if (!_state.Contains(targetId)) return NetRoomOp.NotInRoom;

            roomShouldClose = Leave(targetId);
            // Leave 為 R16 斷線路徑保留 frozen results；明確被房主踢除則不應留在結算。
            RemoveFromMatch(targetId);
            return NetRoomOp.Ok;
        }

        /// <summary>
        /// 關閉/開啟座位(R8)。**這就是需求 12 的「雙擊頭貼鎖格子」**。
        ///
        /// 關閉一個**已經有人**的座位 → 先把那個人踢出去(<paramref name="kickedUserId"/>),
        /// 再標記為 Closed。使用者的原話:「如果原本那格有玩家就會把那個玩家踢出去」。
        ///
        /// 不能關掉房主自己的座位。
        /// </summary>
        public NetRoomOp SetSeatClosed(int actorId, int seat, bool closed, out int kickedUserId)
        {
            kickedUserId = 0;
            if (!_state.IsHost(actorId)) return NetRoomOp.NotHost;
            if (seat < 0 || seat >= _state.Seats.Length) return NetRoomOp.BadSeat;

            var s = _state.Seats[seat];

            // 房主自己的座位不能關 —— 關了就沒人能管這間房了。
            if (s.IsTaken && s.UserId == _state.HostUserId) return NetRoomOp.BadSeat;

            if (closed)
            {
                if (s.IsTaken)
                {
                    kickedUserId = s.UserId;
                    RemoveFromMatch(kickedUserId);
                    s.Clear();
                }
                s.State = SeatState.Closed;
            }
            else
            {
                if (s.IsTaken) return NetRoomOp.BadSeat;   // 有人坐著的座位無所謂「開啟」
                s.State = SeatState.Open;
            }

            Touch();
            return NetRoomOp.Ok;
        }

        /// <summary>
        /// 轉移房主(需求 12 的「切換房主」)。
        /// **只換 <see cref="NetRoomSnapshot.HostUserId"/>,不搬座位** —— 所以 client 端的房主徽章
        /// 必須跟著 hostUserId 畫,不能跟著座位索引。
        /// </summary>
        public NetRoomOp TransferHost(int actorId, int targetId)
        {
            if (!_state.IsHost(actorId)) return NetRoomOp.NotHost;
            if (targetId == actorId) return NetRoomOp.BadState;

            int seat = _state.SeatIndexOf(targetId);
            if (seat < 0) return NetRoomOp.NotInRoom;   // 只能給座位玩家(旁觀者不能當房主)

            var newHostSeat = _state.Seats[seat];
            newHostSeat.Ready = false;
            if (newHostSeat.PlayState == PlayState.Ready) newHostSeat.PlayState = PlayState.Idle;
            _state.HostUserId = targetId;
            Touch();
            return NetRoomOp.Ok;
        }

        // ================= 房間設定(全部 host only) =================

        public NetRoomOp SetRoomName(int actorId, string name)
        {
            if (!_state.IsHost(actorId)) return NetRoomOp.NotHost;
            _state.Name = ClipRoomName(name);
            Touch();
            return NetRoomOp.Ok;
        }

        /// <summary>
        /// 選歌(R9)。**只有房主** —— 這就是使用者要求的「只有房主可以選房主設置選歌曲」的
        /// server 端把關。
        ///
        /// 換歌會**保留所有人的準備意願,並把所有人的 avail 設回 unknown**。
        /// availability 重新確認為 have 前不能開始；下載完成後原本已準備的人不必再按一次。
        /// </summary>
        public NetRoomOp SetSong(int actorId, NetSongRef song)
        {
            if (!_state.IsHost(actorId)) return NetRoomOp.NotHost;
            if (_state.Status != RoomStatus.Open) return NetRoomOp.BadState;

            // 🔴 **同一張譜重送一次不算換歌** —— 直接返回,不要把大家的 avail 打回 unknown。
            // 房主的 client 每次進房間畫面都會發布一次(含遊戲結束回房),那不是換歌;
            // 但重設 avail 會讓所有人重新回報,缺歌的人於是「莫名其妙又開始傳歌一次」。
            // 實機就是這樣:沒換歌,結算回房卻突然傳一次歌。
            // 用 SameChartAs 而不是自己比 —— 它只比識別欄位(標題會因簡繁/metadata 而不同)。
            if (song != null && song.SameChartAs(_state.Song)) return NetRoomOp.Ok;

            _state.Song = song;

            for (int i = 0; i < _state.Seats.Length; i++)
            {
                var s = _state.Seats[i];
                if (!s.IsTaken) continue;
                s.Avail = Availability.Unknown;
                s.AvailProgress = 0f;
            }
            for (int i = 0; i < _spectators.Count; i++)
            {
                _spectators[i].Avail = Availability.Unknown;
            }

            Touch();
            return NetRoomOp.Ok;
        }

        /// <summary>
        /// 改房間設定(host only)。<paramref name="patchNode"/> 可以只帶部分欄位。
        ///
        /// R11:把 <c>lookerCount</c> 縮小到低於現有旁觀人數時,**踢掉最晚加入的那幾個**旁觀者
        /// (從清單尾端移除 —— 先來的人保住位置比較合理)。
        /// </summary>
        public NetRoomOp SetRoomSettings(int actorId, object patchNode, out int[] kickedSpectators)
        {
            kickedSpectators = NetRoomTick.EmptyIds;
            if (!_state.IsHost(actorId)) return NetRoomOp.NotHost;

            _state.Settings.ApplyPatch(patchNode);

            // 模式離開「普通」→ 這間房不再有隊伍,把所有人清回自由。
            // 🔴 不清的話會留下一組看不見也改不掉的隊伍:面板不讓你動(只有普通模式能改),
            //    但 requestStart 那條仍會看到有人不是自由 → 判定成組隊局,然後因為湊不出版型而永遠開不了場。
            if (!TeamLayoutRules.TeamsAllowedIn(_state.Settings.GameMode)) ClearTeams();

            if (_spectators.Count > _state.Settings.LookerCount)
            {
                var kicked = new List<int>();
                while (_spectators.Count > _state.Settings.LookerCount)
                {
                    int last = _spectators.Count - 1;
                    kicked.Add(_spectators[last].UserId);
                    _spectators.RemoveAt(last);
                }
                kickedSpectators = kicked.ToArray();
                SyncSpectators();
            }

            Touch();
            return NetRoomOp.Ok;
        }

        /// <summary>
        /// 房主一鍵分隊(R10b)。人數必須剛好符合版型 —— 4 人才能 2v2、6 人才能 3v3 或 2v2v2。
        /// 依座位順序輪流發牌(見 <see cref="TeamLayoutRules.AssignTeams"/>)。
        /// </summary>
        public NetRoomOp AssignTeams(int actorId, TeamLayout layout)
        {
            if (!_state.IsHost(actorId)) return NetRoomOp.NotHost;
            if (_state.Status != RoomStatus.Open) return NetRoomOp.BadState;
            // 只有普通模式能組隊(見 TeamLayoutRules.TeamsAllowedIn)。client 也擋了一次,但那只是 UX ——
            // 這一條才是真的:改過的 client 不能靠送 assignTeams 在自由模式下把大家分隊。
            if (!TeamLayoutRules.TeamsAllowedIn(_state.Settings.GameMode)) return NetRoomOp.BadTeams;

            int seated = _state.SeatedCount;
            if (!TeamLayoutRules.CanAssign(layout, seated)) return NetRoomOp.BadTeams;

            var teams = TeamLayoutRules.AssignTeams(layout, seated);
            if (teams == null) return NetRoomOp.BadTeams;

            int k = 0;
            for (int i = 0; i < _state.Seats.Length; i++)
            {
                if (!_state.Seats[i].IsTaken) continue;
                _state.Seats[i].Team = teams[k++];
            }

            Touch();
            return NetRoomOp.Ok;
        }

        /// <summary>把所有座位清回「自由」(模式離開普通時)。已經是自由就什麼都不做。</summary>
        private void ClearTeams()
        {
            for (int i = 0; i < _state.Seats.Length; i++)
                if (_state.Seats[i].IsTaken) _state.Seats[i].Team = (int)TeamTag.Free;
        }

        // ================= 個人操作 =================

        /// <summary>
        /// 自己換隊(R10a)。使用者的要求:「其它玩家如果不是在準備狀態下的話能在自己換組隊」
        /// → 按了準備就鎖定,取消準備後又能改。
        ///
        /// ⚠️ **房主是例外**:它恆 <c>Ready</c>(那是「房主沒有準備鈕」的結果,不代表它鎖定了),
        /// 所以 Ready 這道檢查只對非房主生效 —— 否則房主永遠沒辦法選自己的隊,
        /// 而組隊模式要求所有參與者都必須選隊,房間就永遠開不了場。
        /// (這是寫測試時才發現的:R10c_Uneven_Teams_Cannot_Start 一開始就是卡在這裡。)
        /// </summary>
        public NetRoomOp SetOwnTeam(int userId, int team)
        {
            var s = _state.SeatOf(userId);
            if (s == null) return NetRoomOp.NotInRoom;
            if (!NetState.IsValidTeam(team)) return NetRoomOp.BadState;
            // 只有普通模式能組隊。「改回自由」永遠放行 —— 否則模式切走之後,已經在某一隊的人
            // 就被鎖在那一隊裡出不來(而且那時候整間房都不該有隊伍了)。
            if (team != (int)TeamTag.Free && !TeamLayoutRules.TeamsAllowedIn(_state.Settings.GameMode))
                return NetRoomOp.BadTeams;

            // 已經進入遊戲流程(載入/遊玩/結算)就不能換。
            if (s.PlayState != PlayState.Idle) return NetRoomOp.BadState;
            // 按了準備就鎖定。房主的 Ready 恆為 false(它沒有準備這個狀態),所以它一直都能換隊
            // —— 這正是使用者要的:「它可以直接按開始 但是它還是要可以選隊」。
            if (s.Ready) return NetRoomOp.BadState;

            s.Team = team;
            Touch();
            return NetRoomOp.Ok;
        }

        /// <summary>
        /// 準備 / 取消準備。
        ///
        /// R17:<c>ready = true</c> 需要 <c>avail == have</c> —— **缺歌的人不能準備**。
        /// availability 之後若改變會保留 Ready 意願，但開始時仍要求當前歌曲為 have。
        ///
        /// **房主沒有準備這個狀態** —— 它的頭貼顯示 host 徽章,而它按的是「開始」。
        /// 所以房主送這個訊息是不合法的(見 <see cref="IsClearedToStart"/>)。
        /// </summary>
        /// <summary>
        /// 更新某個人的外觀(性別 / 體型 / 穿戴部件)。座位玩家與旁觀者都可能送。
        ///
        /// 沒有權限檢查 —— 外觀就是「我自己長什麼樣」,誰都能改自己的。
        /// 不在這個房裡回 <see cref="NetRoomOp.NotInRoom"/>(那不是錯誤,是正常的競態:
        /// 剛離房的人送出的最後一筆)。
        /// </summary>
        public NetRoomOp SetLook(int userId, NetAvatarLook look)
        {
            if (look == null) return NetRoomOp.BadState;

            var seat = _state.SeatOf(userId);
            if (seat != null) { seat.Look = look; Touch(); return NetRoomOp.Ok; }

            for (int i = 0; i < _spectators.Count; i++)
                if (_spectators[i].UserId == userId)
                {
                    _spectators[i].Look = look;
                    SyncSpectators();
                    Touch();
                    return NetRoomOp.Ok;
                }
            return NetRoomOp.NotInRoom;
        }

        /// <summary>
        /// 更新某個人的身分(顯示名稱 / 家族 / 等級)。座位玩家與旁觀者都可能送。
        ///
        /// 為什麼名字會在進房後還變:**選性別 == 選帳號**,而握手是開機時就做完的 ——
        /// 那時報上去的是開機那刻 active profile 的名字。沒有這條路徑的話,換成男角進房的人
        /// 在別人畫面上會是「男角的模型 + 女角的名字」(實測踩過)。
        ///
        /// 與 <see cref="SetLook"/> 同樣沒有權限檢查(名字就是「我叫什麼」),
        /// 不在房裡回 <see cref="NetRoomOp.NotInRoom"/>(正常的競態,不是錯誤)。
        ///
        /// <paramref name="name"/> 空 = 不動名字 —— 座位名字被抹成空白比顯示舊名字更糟
        /// (頭上名牌與聊天行會變成無主的字)。清理與長度截斷由呼叫端(Hub)負責。
        /// 家族(名字+徽章)座位與旁觀者都吃 —— 旁觀者一樣站在房裡、頭上一樣有名字牌。
        /// </summary>
        public NetRoomOp SetIdentity(int userId, string name, string guild, int level)
            => SetIdentity(userId, name, guild, "", level);

        public NetRoomOp SetIdentity(int userId, string name, string guild, string guildEmblem, int level)
        {
            if (level < 0) level = 0;

            var seat = _state.SeatOf(userId);
            if (seat != null)
            {
                if (!string.IsNullOrEmpty(name)) seat.Name = name;
                seat.Guild = guild ?? "";
                seat.GuildEmblem = guildEmblem ?? "";
                seat.Level = level;
                Touch();
                return NetRoomOp.Ok;
            }

            for (int i = 0; i < _spectators.Count; i++)
                if (_spectators[i].UserId == userId)
                {
                    if (!string.IsNullOrEmpty(name)) _spectators[i].Name = name;
                    _spectators[i].Guild = guild ?? "";
                    _spectators[i].GuildEmblem = guildEmblem ?? "";
                    _spectators[i].Level = level;
                    SyncSpectators();
                    Touch();
                    return NetRoomOp.Ok;
                }
            return NetRoomOp.NotInRoom;
        }

        /// <summary>
        /// 按下 / 取消準備。
        ///
        /// **房間在遊戲中,留在房間的人一樣按得了準備**(D15 放寬,使用者要求:「我還是能準備,
        /// 那些其他行為都要正常能用」)。擋的只有**這一場的參與者** —— 他們的 ready 已經被凍進
        /// <c>_match</c>,中途改會讓結算的名單與座位對不上;playState 不是 idle(載入中/遊玩中/
        /// 看結算)的座位同理。
        /// </summary>
        public NetRoomOp SetReady(int userId, bool ready)
        {
            var s = _state.SeatOf(userId);
            if (s == null) return NetRoomOp.NotInRoom;
            if (_state.IsHost(userId)) return NetRoomOp.BadState;   // 房主沒有準備這個狀態
            // 打歌期間(waitingForLoad/playing)只擋**這一場的人** —— 他們的名單已凍結,
            // 中途改 ready 會讓結算對不上;留在房間的(缺歌/沒準備/中途坐進來的)照按不誤。
            // 房間回到 open(含結算寬限期)就一律放行 —— 那時按的準備是為了下一局。
            if (_state.Status != RoomStatus.Open && IsParticipant(userId)) return NetRoomOp.BadState;

            if (ready)
            {
                if (_state.Song == null) return NetRoomOp.NoSong;
                if (s.Avail != Availability.Have) return NetRoomOp.BadState;
            }

            s.Ready = ready;
            s.PlayState = ready ? PlayState.Ready : PlayState.Idle;
            Touch();
            return NetRoomOp.Ok;
        }

        /// <summary>
        /// 上報「我有沒有這首歌」。座位玩家與旁觀者都會送。
        ///
        /// <paramref name="packId"/> 必須對應**房間當前的歌** —— 不符就靜默忽略。
        /// 這擋掉的是「上一首歌的遲到回報」:玩家還在下載 A 歌時房主換成了 B 歌,
        /// A 歌的下載進度回報會陸續抵達,不能讓它們污染 B 歌的狀態。
        ///
        /// R17 的另一半:**availability 改變不會取消 ready**；開始前另外檢查當前歌曲必須是 have，
        /// 讓換歌或重新下載後的玩家保留準備意願，又不會被拉進沒有歌曲的一局。
        /// </summary>
        public NetRoomOp SetAvailability(int userId, string packId, Availability avail, float progress)
        {
            if (!_state.Contains(userId)) return NetRoomOp.NotInRoom;

            // 對不上當前歌就丟掉(不算錯誤 —— 是正常的競態)。
            if (!MatchesCurrentSong(packId)) return NetRoomOp.Ok;

            if (progress < 0f) progress = 0f;
            if (progress > 1f) progress = 1f;

            var seat = _state.SeatOf(userId);
            if (seat != null)
            {
                seat.Avail = avail;
                seat.AvailProgress = avail == Availability.Downloading ? progress : 0f;

                Touch();
                return NetRoomOp.Ok;
            }

            int si = _state.SpectatorIndexOf(userId);
            if (si >= 0)
            {
                _spectators[si].Avail = avail;
                SyncSpectators();
                Touch();
            }
            return NetRoomOp.Ok;
        }

        /// <summary>
        /// client 上報遊玩狀態。**只准 <c>loaded</c> / <c>readyForGameplay</c> / <c>finished</c>** ——
        /// 其餘是 server 保留狀態(見 <see cref="NetState.IsClientSettable"/> 的說明:
        /// 沒有這道檢查,改過的 client 可以自稱 playing 繞過整個載入同步)。
        ///
        /// 送的人必須是**當前場次的參與者**,而且狀態要對得上房間階段。
        /// </summary>
        public NetRoomOp SetPlayState(int userId, PlayState state, long matchId)
        {
            if (_match == null || _match.MatchId != matchId) return NetRoomOp.BadState;

            var s = _state.SeatOf(userId);
            if (s == null) return NetRoomOp.NotInRoom;
            if (!IsParticipant(userId)) return NetRoomOp.BadState;

            switch (state)
            {
                case PlayState.Loaded:
                case PlayState.ReadyForGameplay:
                    // 載入階段才有意義。已經被 server 強制推進到 playing 的人再送 loaded 就忽略
                    // (那是正常競態:逾時強制開場與 client 的 loaded 同時發生)。
                    if (_state.Status != RoomStatus.WaitingForLoad) return NetRoomOp.Ok;
                    if (s.PlayState == PlayState.Playing) return NetRoomOp.Ok;
                    s.PlayState = state;
                    break;

                case PlayState.Finished:
                    // 🔴 載入階段送 finished 一律忽略(不是退場,退場走下面的 idle)—— 這是安全邊界的一半:
                    //    Hub.OnPlayFinished 那邊也擋掉同一件事,免得有人在開跳前先報一個分數。
                    if (_state.Status != RoomStatus.Playing) return NetRoomOp.Ok;
                    s.PlayState = PlayState.Finished;
                    break;

                case PlayState.Idle:
                    // 還在等其他人載入時送 idle = 在「等待載入」畫面按了 Esc 中離 → 退出本場。
                    if (_state.Status == RoomStatus.WaitingForLoad) return AbortDuringLoad(userId, matchId);
                    // 「結算看完了,我人回房間了」。徽章的下緣時間點靠這一則 —— 沒有它,留在房間的人
                    // 只能等 ResultsGraceMs 逾時才看到你回來(而那是給斷線的人用的兜底)。
                    // 只有剛打完/中離那一場的人送得出來:其餘狀態一律擋掉,免得有人用它洗掉自己的 ready。
                    // 已經是 idle 就當成功(冪等)—— 載入階段中離的人會先被 AbortDuringLoad 清成 idle,
                    // 緊接著回房又送一次這則,不該讓他收到一個沒有意義的 badState。
                    if (s.PlayState == PlayState.Idle) return NetRoomOp.Ok;

                    // 🔴 還停在 playing 的人也要收。
                    //
                    // 「我人回房間了」是只有 client 知道的事實;server 這時還以為他在跳,唯一的原因是那則
                    // playFinished 沒生效(掉包 / 被限流 / 被暫存殘留擋掉)。以前這裡回 badState,後果是**死結**:
                    //   • 他的座位永遠是 playing → Tick 的結算條件(沒有參與者還在 playing)永遠不成立
                    //     → 房間卡在 playing,整間房再也開不了下一局;
                    //   • Ready 只在這條路徑上清,所以他頭上的「準備」也永遠退不掉,別人看他永遠是 PLAYING。
                    // 而且沒有任何逃生門救得回來:R14 的逾時只管 waitingForLoad,結算寬限期要等結算才起算。
                    //
                    // 收下之後把他移出 active 集合(**保留**結算名單 —— R16 用他最後一筆 frame 算成績,
                    // 與斷線者走同一條路),剩下的人照樣打完、照樣結算。
                    if (s.PlayState == PlayState.Playing)
                    {
                        RemoveActiveParticipant(userId);
                        s.PlayState = PlayState.Idle;
                        s.Ready = false;
                        Touch();
                        return NetRoomOp.Ok;   // 收尾(全員打完 → 結算 → 房間回 open)交給 Tick
                    }

                    if (s.PlayState != PlayState.Results && s.PlayState != PlayState.Finished)
                        return NetRoomOp.BadState;
                    s.PlayState = PlayState.Idle;
                    s.Ready = false;

                    // 🔴 這一則有兩種來源,差別在於「這一場結算了沒」:
                    //   ① 正常:曲末 → Finished → 全員打完 → Tick 結算(Status 回 Open、寬限期起算)
                    //      → 玩家關掉結算面板才送 idle。這是真的「看完成績回房間」。
                    //   ② 中離:按 Esc 的 client 會**連著**送 playFinished + setPlayState{idle}
                    //      (見 FrontendApp.AbortGameplay),所以座位剛被打成 Finished 就馬上收到這一則,
                    //      而其他人還在跳 —— 房間仍是 Playing,這一場根本還沒結算。
                    //
                    // ② 的情況下**絕對不能**收掉 _match。收掉的後果是連鎖的(實機踩到,兩個人先後中離):
                    //   • AnyParticipantIn 在 _match == null 時一律回 false → 下一次 Tick 立刻把
                    //     「還在 playing」誤判成「全員打完」→ 發出一份沒有名單的結算
                    //     (Hub.SendResultsReady 讀 room.Match.Participants 直接 NullReferenceException,
                    //      整個 tick 中斷,連 roomState 都沒廣播出去);
                    //   • 還在跳的那個人座位停在 Playing 沒有任何人清 → 他頭上永遠掛著 PLAYING,
                    //     而且他隨後送的 setPlayState{idle} 全被回 badState(場次已經不在了),救不回來。
                    //
                    // 所以收掉 _match 的條件多一個「已經結算過了」(_resultsClearAtMs > 0)。
                    // ② 的收尾照舊交給 Tick:他已經是 idle,不算在 playing 裡,剩下的人打完就結算得了。
                    if (_resultsClearAtMs > 0
                        && !AnyParticipantIn(PlayState.Results) && !AnyParticipantIn(PlayState.Finished))
                    {
                        ClearResults();
                        return NetRoomOp.Ok;
                    }
                    break;

                default:
                    return NetRoomOp.BadState;
            }

            Touch();
            return NetRoomOp.Ok;
        }

        /// <summary>
        /// 載入階段中離 —— 把這個人從本場拿掉,座位打回 idle。
        ///
        /// 為什麼不是設成 <see cref="PlayState.Finished"/>:他一個音符都還沒打,沒有成績要進結算名單
        /// (<see cref="RemoveFromMatch"/> 連 <c>Participants</c> 一起移除,結算就不會多出一列 0 分)。
        ///
        /// 之後的收尾由既有的 <see cref="Tick"/> 接手,不需要在這裡處理:還有人在載 → 繼續等;
        /// 他是最後一個可開場的人 → <c>starters.Count == 0</c> 那條會把整場取消、房間打回 open。
        /// </summary>
        public NetRoomOp AbortDuringLoad(int userId, long matchId)
        {
            if (_match == null || _match.MatchId != matchId) return NetRoomOp.BadState;
            if (_state.Status != RoomStatus.WaitingForLoad) return NetRoomOp.BadState;

            var s = _state.SeatOf(userId);
            if (s == null) return NetRoomOp.NotInRoom;
            if (!IsParticipant(userId)) return NetRoomOp.BadState;

            s.PlayState = PlayState.Idle;
            s.Ready = false;
            RemoveFromMatch(userId);
            Touch();
            return NetRoomOp.Ok;
        }

        // ================= 開場 =================

        /// <summary>
        /// 房主按開始(R12 + R10c)。
        ///
        /// <paramref name="force"/> = 使用者要求的「連按兩下開始強制開始」:
        /// 不要求全員準備,沒準備/缺歌的人**留在房間**,只會看到其他人的頭貼變「遊戲中」。
        /// 被 force 排除的缺歌玩家會清掉 sticky Ready，避免 Ready playState 洩漏進進行中的房間狀態。
        /// 這與 osu 的行為一致(host 直接 StartMatch 時,Idle 的人整場被跳過)。
        ///
        /// **參與者集合 = <see cref="IsClearedToStart"/> 且 <c>avail == have</c> 的座位玩家**。
        /// 房主不需要按準備(它就是按開始的那個人),但**它自己也必須有歌**才算參與者。
        /// 集合在這一刻**凍結**。
        ///
        /// 🔴 R10c:組隊模式下湊不出官方座標表有的版型就**擋住不開始**(連 force 也擋)。
        /// </summary>
        public NetRoomOp RequestStart(int actorId, bool force, NetResolvedRound resolved, long nowMs,
                                      out NetMatchInfo match)
        {
            match = null;
            if (!_state.IsHost(actorId)) return NetRoomOp.NotHost;
            if (_state.Status != RoomStatus.Open) return NetRoomOp.BadState;

            // 上一局的結算還掛著(有人沒關結算面板,或已經斷線)→ 房主按下一局就等於「結算階段結束」。
            // 不先清的話那幾格會頂著 results 跨進下一局,而 _match 也還是上一場的。
            if (_resultsClearAtMs > 0) ClearResults();
            if (_state.Song == null) return NetRoomOp.NoSong;
            if (resolved == null) return NetRoomOp.BadState;

            // 非強制開始 → 所有**其他**座位玩家都要準備好。
            // 房主自己不算(它沒有準備狀態 —— 它按的就是開始那顆鈕)。
            if (!force)
            {
                for (int i = 0; i < _state.Seats.Length; i++)
                {
                    var s = _state.Seats[i];
                    if (s.IsTaken && (!IsClearedToStart(s) || s.Avail != Availability.Have))
                        return NetRoomOp.BadState;
                }
            }

            // 參與者:可開場(房主 or 已準備)**而且**手上有歌。
            var participants = new List<int>();
            var participantSeats = new List<NetSeat>();
            var participantSnapshots = new List<NetMatchPlayerSnapshot>();
            for (int i = 0; i < _state.Seats.Length; i++)
            {
                var s = _state.Seats[i];
                if (!IsClearedToStart(s)) continue;
                if (s.Avail != Availability.Have) continue;
                participants.Add(s.UserId);
                participantSeats.Add(s);
                participantSnapshots.Add(NetMatchPlayerSnapshot.Capture(s, i));
            }
            if (participants.Count == 0) return NetRoomOp.BadState;

            // ---- R10c:組隊版型驗證 ----
            var wantLayout = TeamLayout.None;
            if (AnyParticipantHasTeam(participantSeats))
            {
                // 組隊模式下,所有參與者都必須選了隊(不能有人是「自由」)。
                for (int i = 0; i < participantSeats.Count; i++)
                    if (participantSeats[i].Team == (int)TeamTag.Free) return NetRoomOp.BadTeams;

                var counts = new int[TeamLayoutRules.MaxTeams];
                for (int i = 0; i < participantSeats.Count; i++)
                {
                    int t = participantSeats[i].Team;
                    if (t >= 0 && t < counts.Length) counts[t]++;
                }

                if (!TeamLayoutRules.TryLayoutFor(counts, out wantLayout)) return NetRoomOp.BadTeams;
            }

            // server 用自己手上的參與者名單重算版型,不信 host 送來的那個值。
            if (resolved.TeamLayout != wantLayout) return NetRoomOp.BadTeams;


            // Force start may skip a sticky-ready player whose new song is still unavailable.
            // Clear that excluded intent so PlayState.Ready cannot leak through the active match.
            if (force)
            {
                for (int i = 0; i < _state.Seats.Length; i++)
                {
                    var s = _state.Seats[i];
                    if (!s.IsTaken || !s.Ready || s.Avail == Availability.Have) continue;
                    s.Ready = false;
                    if (s.PlayState == PlayState.Ready) s.PlayState = PlayState.Idle;
                }
            }
            // ---- 凍結參與者,進入載入階段 ----
            _match = new NetMatchInfo
            {
                MatchId = _nextMatchId++,
                StartEpochMs = nowMs,
                LoadTimeoutMs = NetLimits.LoadTimeoutMs,
                ParticipantUserIds = participants.ToArray(),
                Participants = participantSnapshots.ToArray(),
                SpectatorUserIds = SpectatorsWithSong(),
                Resolved = resolved,
                Song = resolved.RandomSong ?? _state.Song,
            };
            _matchDeadlineMs = nowMs + NetLimits.LoadTimeoutMs;

            for (int i = 0; i < participantSeats.Count; i++)
                participantSeats[i].PlayState = PlayState.WaitingForLoad;

            _state.Status = RoomStatus.WaitingForLoad;
            Touch();

            match = _match;
            return NetRoomOp.Ok;
        }

        // ================= 狀態機推進 =================

        /// <summary>
        /// 推進狀態機。Hub 在每次操作之後 + 定期(至少每秒)呼叫一次。
        ///
        /// 對應 osu 的 <c>UpdateRoomStateIfRequired</c>,外加它也有的兩道逃生門:
        ///
        /// **R14 載入逾時**(<see cref="NetLimits.LoadTimeoutMs"/>,從 requestStart 起算):
        ///   • 還在 <c>waitingForLoad</c> 的 → 逐出本場 + <c>gameplayAborted{loadTookTooLong}</c>
        ///   • 卡在 <c>loaded</c> 的 → **強制轉 playing**
        /// 🔴 這道門是必要的:少了它,一個人載入卡住就會讓整房永遠停在 loading 畫面
        /// (<c>ScreenGameplay.BootRevealCo</c> 的 ReadyGate 迴圈本身沒有逾時)。
        ///
        /// **R13 開場**:沒有任何參與者還在 <c>waitingForLoad</c> 時 → 可開場的人一起轉
        /// <c>playing</c> + 廣播 <c>gameplayStarted</c>。注意條件是「沒人還在 waitingForLoad」,
        /// **不是**「全員 readyForGameplay」—— 這是 osu 的規則,照抄。
        ///
        /// **R14 結算**:沒有任何參與者還是 <c>playing</c> 時 → 轉 <c>results</c> + 廣播
        /// <c>resultsReady</c>,房間回 <c>open</c>。
        /// </summary>
        public NetRoomTick Tick(long nowMs)
        {
            var tick = NetRoomTick.None;
            if (_match != null) tick.MatchId = _match.MatchId;

            if (_state.Status == RoomStatus.WaitingForLoad)
            {
                // ---- 逾時處理 ----
                if (nowMs >= _matchDeadlineMs)
                {
                    var timedOut = new List<int>();
                    for (int i = 0; i < _state.Seats.Length; i++)
                    {
                        var s = _state.Seats[i];
                        if (!s.IsTaken || !IsParticipant(s.UserId)) continue;

                        if (s.PlayState == PlayState.WaitingForLoad)
                        {
                            // 載不完 → 這場不帶它。
                            timedOut.Add(s.UserId);
                            s.PlayState = PlayState.Idle;
                            s.Ready = false;
                        }
                        else if (s.PlayState == PlayState.Loaded)
                        {
                            // 程式載完了但人卡著(osu 的情境是玩家還在調 offset 面板)→ 強制推進。
                            s.PlayState = PlayState.ReadyForGameplay;
                        }
                    }
                    if (timedOut.Count > 0)
                    {
                        tick.LoadTimedOutUserIds = timedOut.ToArray();
                        RemoveManyFromMatch(tick.LoadTimedOutUserIds);
                        tick.Changed = true;
                    }
                }

                // ---- 可以開場了嗎? ----
                if (!AnyParticipantIn(PlayState.WaitingForLoad))
                {
                    var starters = ParticipantsWhoCanStart();
                    if (starters.Count == 0)
                    {
                        // 全部退出載入 → 整場取消,回到房間。
                        _state.Status = RoomStatus.Open;
                        ResetParticipantsToIdle();
                        _match = null;
                        tick.MatchAborted = true;
                        tick.Changed = true;
                    }
                    else
                    {
                        for (int i = 0; i < starters.Count; i++) starters[i].PlayState = PlayState.Playing;
                        _state.Status = RoomStatus.Playing;
                        if (_match != null) _match.GameplayStartedSent = true;
                        tick.GameplayStarted = true;
                        tick.Changed = true;
                    }
                    if (tick.Changed) Touch();
                }
                else if (tick.Changed)
                {
                    Touch();
                }

                return tick;
            }

            if (_state.Status == RoomStatus.Playing)
            {
                // 防禦:停在 playing 卻沒有場次 = 壞狀態。AnyParticipantIn 這時一律回 false,
                // 直接往下走會發出一份**沒有名單**的結算(Hub 讀 room.Match 就 NRE)。
                // 這裡把房間收乾淨回 open 就好 —— 沒有名單就沒有成績可以結算。
                if (_match == null)
                {
                    ResetParticipantsToIdle();
                    _state.Status = RoomStatus.Open;
                    tick.Changed = true;
                    Touch();
                    return tick;
                }

                if (!AnyParticipantIn(PlayState.Playing))
                {
                    // 全部打完(或斷線被移除)→ 結算。
                    for (int i = 0; i < _state.Seats.Length; i++)
                    {
                        var s = _state.Seats[i];
                        if (!s.IsTaken) continue;
                        // 🔴 還停在 playing 的座位也一起轉 —— 進到這裡代表沒有**參與者**還在 playing,
                        //    所以這種座位是狀態機漏掉的殘留。不收的話那一格永遠掛著 PLAYING
                        //    (寬限期只清 results,ClearResults 也只認 results/finished),誰都救不回來。
                        if (s.PlayState == PlayState.Finished || s.PlayState == PlayState.Playing)
                            s.PlayState = PlayState.Results;
                    }
                    _state.Status = RoomStatus.Open;
                    // 從這一刻起算寬限期。**這裡不清 results** —— 那份帶著 results 的快照要先廣播出去,
                    // 留在房間的人才會繼續看到「他們還在結算、還沒回來」(見 ResultsGraceMs)。
                    _resultsClearAtMs = nowMs + NetLimits.ResultsGraceMs;
                    tick.ResultsReady = true;
                    tick.Changed = true;
                    Touch();
                }
                return tick;
            }

            // 結算寬限期到了 —— 還停在 results 的人多半是關掉遊戲/斷線走的(正常關結算面板的人
            // 早就送過 setPlayState{idle} 把自己那格清掉了)。不兜這一手,那幾格會永遠掛著 PLAYING。
            if (_resultsClearAtMs > 0 && nowMs >= _resultsClearAtMs)
            {
                ClearResults();
                tick.Changed = true;
            }

            return tick;
        }

        /// <summary>
        /// 結算階段結束 —— 把所有人打回 idle,準備下一局。
        ///
        /// 🔴 呼叫時機是「**人回來了**」,不是「歌放完了」:
        ///   ① 每個人自己關掉結算面板時 client 送 <c>setPlayState{idle}</c> → 只清他那一格;
        ///      最後一個人清完時這裡會被叫到,收掉 <c>_match</c>。
        ///   ② 沒人回報的兜底:<see cref="Tick"/> 的寬限期到了(斷線 / 關掉遊戲)。
        ///   ③ 房主直接按下一局:<see cref="RequestStart"/> 進來時先清乾淨。
        ///
        /// 以前是由 Hub 在廣播 resultsReady 的**同一輪**就呼叫,結果是那份帶 <c>results</c> 的快照
        /// 從來沒被送出去過一次 —— 留在房間的人看到 PLAYING 在曲末就消失,而那些人還在看成績。
        /// </summary>
        public void ClearResults()
        {
            _resultsClearAtMs = 0;
            for (int i = 0; i < _state.Seats.Length; i++)
            {
                var s = _state.Seats[i];
                if (!s.IsTaken) continue;
                // 🔴 只動「剛打完那一場」的人 —— 清 ready 也一樣。以前是無條件清全部,因為那時這支
                //    是在結算的**同一瞬間**跑的,沒參加的人不可能在那個瞬間之前按下準備(打歌期間
                //    SetReady 被 Status != Open 擋著)。現在清除延後到「人回來了 / 寬限期到」,中間
                //    那幾十秒房間已經是 open —— 留在房間的人按了準備等下一局,不能被這裡洗掉。
                if (s.PlayState != PlayState.Results && s.PlayState != PlayState.Finished) continue;
                s.PlayState = PlayState.Idle;
                s.Ready = false;   // 下一局要重新準備;房主本來就沒有這個狀態
            }
            _match = null;
            Touch();
        }

        // ================= 內部 =================

        /// <summary>狀態變更 → rev 遞增。client 靠它丟掉過期的快照。</summary>
        private void Touch()
        {
            _state.Rev++;
        }

        /// <summary>
        /// 把一個人放進座位。
        ///
        /// **如果這時候沒有房主(座位本來全空),他就成為房主。**
        /// 所有「坐下」的路徑都經過這裡(建房 / 加入 / 旁觀者搶回座位),所以不會漏。
        /// </summary>
        private void SeatPlayer(int seat, NetJoinUser user)
        {
            // 座位全空 → 沒有房主 → 第一個坐下的人接手。
            if (_state.HostUserId == 0) _state.HostUserId = user.UserId;

            var s = _state.Seats[seat];
            s.State = SeatState.Taken;
            s.UserId = user.UserId;
            s.Name = user.Name;
            s.Guild = user.Guild;
            s.GuildEmblem = user.GuildEmblem;
            s.Level = user.Level;
            s.Look = user.Look;
            s.Ready = false;
            s.Team = (int)TeamTag.Free;
            s.PlayState = PlayState.Idle;
            s.Avail = Availability.Unknown;
            s.AvailProgress = 0f;
        }

        /// <summary>把 <see cref="_spectators"/> 同步進快照(快照持有陣列以便編碼)。</summary>
        private void SyncSpectators()
        {
            _state.Spectators = _spectators.ToArray();
        }

        /// <summary>
        /// 重新指派房主(R5)。給剩下 Taken 座位中**索引最小**的人。
        ///
        /// **座位全空就沒有房主**(<c>hostUserId = 0</c>)—— 旁觀者不能當房主。
        /// 那不是壞狀態:沒有舞者的房間本來就不需要選歌或開始,而
        /// <see cref="SeatPlayer"/> 會讓下一個坐上座位的人自動接手。
        /// </summary>
        private void PromoteNewHost()
        {
            int heir = FindHeirSeat(-1);
            if (heir < 0)
            {
                _state.HostUserId = 0;
                return;
            }

            var newHostSeat = _state.Seats[heir];
            newHostSeat.Ready = false;
            if (newHostSeat.PlayState == PlayState.Ready) newHostSeat.PlayState = PlayState.Idle;
            _state.HostUserId = newHostSeat.UserId;
        }

        /// <summary>
        /// 找可以接手房主的座位:Taken 且索引最小,並排除 <paramref name="exceptSeat"/>
        /// (房主要變旁觀時,要排除它自己那個還沒清空的座位)。找不到回 -1。
        /// </summary>
        private int FindHeirSeat(int exceptSeat)
        {
            for (int i = 0; i < _state.Seats.Length; i++)
            {
                if (i == exceptSeat) continue;
                if (_state.Seats[i].IsTaken) return i;
            }
            return -1;
        }

        private static string ClipRoomName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            name = name.Trim();
            return name.Length <= NetLimits.MaxRoomNameChars
                ? name
                : name.Substring(0, NetLimits.MaxRoomNameChars);
        }

        /// <summary>這個 packId 對應房間當前的歌嗎?(官方歌用 gn 比對)</summary>
        private bool MatchesCurrentSong(string packId)
        {
            if (_state.Song == null) return false;
            if (_state.Song.Official)
                return string.Equals(packId, _state.Song.Gn, System.StringComparison.Ordinal);
            return string.Equals(packId, _state.Song.PackId, System.StringComparison.Ordinal);
        }

        private bool IsParticipant(int userId)
        {
            if (_match == null) return false;
            var ids = _match.ParticipantUserIds;
            for (int i = 0; i < ids.Length; i++) if (ids[i] == userId) return true;
            return false;
        }

        private void RemoveActiveParticipant(int userId)
        {
            if (_match == null) return;
            var ids = _match.ParticipantUserIds;
            var keep = new List<int>(ids.Length);
            for (int i = 0; i < ids.Length; i++) if (ids[i] != userId) keep.Add(ids[i]);
            _match.ParticipantUserIds = keep.ToArray();
        }

        /// <summary>
        /// 完整逐出本場（載入逾時 / 被踢）：active 狀態與結算名單都移除。
        /// 一般離房或斷線只呼叫 <see cref="RemoveActiveParticipant"/>，保留 R16 結算資料。
        /// </summary>
        private void RemoveFromMatch(int userId)
        {
            RemoveActiveParticipant(userId);
            if (_match == null) return;
            var players = _match.Participants;
            var keepPlayers = new List<NetMatchPlayerSnapshot>(players.Length);
            for (int i = 0; i < players.Length; i++)
                if (players[i].UserId != userId) keepPlayers.Add(players[i]);
            _match.Participants = keepPlayers.ToArray();
        }

        private void RemoveManyFromMatch(int[] userIds)
        {
            for (int i = 0; i < userIds.Length; i++) RemoveFromMatch(userIds[i]);
        }

        private bool AnyParticipantIn(PlayState state)
        {
            if (_match == null) return false;
            var ids = _match.ParticipantUserIds;
            for (int i = 0; i < ids.Length; i++)
            {
                var s = _state.SeatOf(ids[i]);
                if (s != null && s.PlayState == state) return true;
            }
            return false;
        }

        private List<NetSeat> ParticipantsWhoCanStart()
        {
            var list = new List<NetSeat>();
            if (_match == null) return list;
            var ids = _match.ParticipantUserIds;
            for (int i = 0; i < ids.Length; i++)
            {
                var s = _state.SeatOf(ids[i]);
                if (s != null && NetState.CanStartGameplay(s.PlayState)) list.Add(s);
            }
            return list;
        }

        private void ResetParticipantsToIdle()
        {
            for (int i = 0; i < _state.Seats.Length; i++)
            {
                var s = _state.Seats[i];
                if (!s.IsTaken) continue;
                if (NetState.IsInMatch(s.PlayState)) { s.PlayState = PlayState.Idle; s.Ready = false; }
            }
        }

        private static bool AnyParticipantHasTeam(List<NetSeat> seats)
        {
            for (int i = 0; i < seats.Count; i++)
                if (seats[i].Team != (int)TeamTag.Free) return true;
            return false;
        }

        /// <summary>有歌的旁觀者 —— 只有他們會跟著進打歌畫面。</summary>
        private int[] SpectatorsWithSong()
        {
            var list = new List<int>();
            for (int i = 0; i < _spectators.Count; i++)
                if (_spectators[i].Avail == Availability.Have) list.Add(_spectators[i].UserId);
            return list.ToArray();
        }
    }
}
