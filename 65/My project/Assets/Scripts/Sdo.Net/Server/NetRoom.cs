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
        public NetRoom(int code, NetJoinUser host, string name)
        {
            _state = new NetRoomSnapshot();
            _state.Code = code;
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
        /// 遊戲中的房間不能加入(R18)—— 但 <see cref="TrySpectate"/> 可以。
        /// </summary>
        public NetRoomOp TryJoin(NetJoinUser user, out int seatIndex)
        {
            seatIndex = -1;
            if (_state.Contains(user.UserId)) return NetRoomOp.BadState;   // 已經在房裡
            if (_state.Status != RoomStatus.Open) return NetRoomOp.InGame;

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
                if (s.Ready) return NetRoomOp.BadState;

                // 已經進入遊戲流程(載入中/遊玩中/看結算)不能切。
                //
                // ⚠️ 這條是**純防禦**,不是玩家會遇到的情境:打歌畫面裡根本沒有「旁觀」這顆鈕,
                // 正在跳舞的人沒有手段送出這個請求。它擋的是改過的 client ——
                // 中途把自己變成旁觀者會讓分數與 playState 對不上,整場的結算就錯了。
                //
                // 反過來,**沒被納入這一場的人**(缺歌或沒準備、留在房間的)playState 是 idle,
                // 所以他們隨時可以切成旁觀者 —— 他們本來就沒在玩,只是從座位換到旁觀位。
                // 但他們**不會**因此進入正在進行的打歌畫面(不做中途接入,要等下一局)。
                if (s.PlayState != PlayState.Idle) return NetRoomOp.BadState;

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
                Level = user.Level,
                Look = user.Look,
                Avail = Availability.Unknown,
            };
            _spectators.Add(sp);
            SyncSpectators();
            Touch();
            return NetRoomOp.Ok;
        }

        /// <summary>旁觀者搶回座位(再按一次「旁觀」鈕)。</summary>
        public NetRoomOp TryUnspectate(NetJoinUser user, out int seatIndex)
        {
            seatIndex = -1;
            int si = _state.SpectatorIndexOf(user.UserId);
            if (si < 0) return NetRoomOp.NotInRoom;
            if (_state.Status != RoomStatus.Open) return NetRoomOp.InGame;

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

            // 把離開的人從進行中的場次移除(R15:遊玩中斷線 → 逐出本場)。
            RemoveFromMatch(userId);

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

            _state.HostUserId = targetId;
            // 新房主的 Ready 不動 —— 它現在是房主,Ready 對它已經不適用(見 IsClearedToStart)。
            // 舊房主變回一般玩家,它的 Ready 本來就是 false,所以它需要自己按準備。
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
        /// 換歌會**清掉所有人的準備狀態,並把所有人的 avail 設回 unknown**。
        /// 這是照 osu 的行為(換圖後每個人都要重新確認手上有沒有那張圖)——
        /// 少了這步,原本準備好的人會帶著「上一首歌的 have」狀態被拉進新的一局。
        /// </summary>
        public NetRoomOp SetSong(int actorId, NetSongRef song)
        {
            if (!_state.IsHost(actorId)) return NetRoomOp.NotHost;
            if (_state.Status != RoomStatus.Open) return NetRoomOp.BadState;

            _state.Song = song;

            for (int i = 0; i < _state.Seats.Length; i++)
            {
                var s = _state.Seats[i];
                if (!s.IsTaken) continue;
                s.Avail = Availability.Unknown;
                s.AvailProgress = 0f;
                s.Ready = false;   // 房主的 Ready 本來就是 false,不用特例
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
        /// 這是照 osu 的做法(它甚至更進一步:圖不見了會自動把 Ready 打回 Idle,見
        /// <see cref="SetAvailability"/>)。
        ///
        /// **房主沒有準備這個狀態** —— 它的頭貼顯示 host 徽章,而它按的是「開始」。
        /// 所以房主送這個訊息是不合法的(見 <see cref="IsClearedToStart"/>)。
        /// </summary>
        public NetRoomOp SetReady(int userId, bool ready)
        {
            var s = _state.SeatOf(userId);
            if (s == null) return NetRoomOp.NotInRoom;
            if (_state.IsHost(userId)) return NetRoomOp.BadState;   // 房主沒有準備這個狀態
            if (_state.Status != RoomStatus.Open) return NetRoomOp.BadState;

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
        /// R17 的另一半:**ready 中的人 avail 掉出 have → 自動取消 ready**
        /// (對映 osu 的 <c>NotDownloaded &amp;&amp; Ready → ChangeState(Idle)</c>)。
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

                // 準備好的人突然沒歌了 → 自動取消準備,否則會被拉進一局他打不了的歌。
                // (房主的 Ready 恆 false,所以這段自然不會動到它;房主缺歌的處置是
                //  RequestStart 時不把它算進參與者。)
                if (seat.Ready && avail != Availability.Have)
                {
                    seat.Ready = false;
                    if (seat.PlayState == PlayState.Ready) seat.PlayState = PlayState.Idle;
                }
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
                    if (_state.Status != RoomStatus.Playing) return NetRoomOp.Ok;
                    s.PlayState = PlayState.Finished;
                    break;

                default:
                    return NetRoomOp.BadState;
            }

            Touch();
            return NetRoomOp.Ok;
        }

        // ================= 開場 =================

        /// <summary>
        /// 房主按開始(R12 + R10c)。
        ///
        /// <paramref name="force"/> = 使用者要求的「連按兩下開始強制開始」:
        /// 不要求全員準備,沒準備/缺歌的人**留在房間**(狀態不變),只會看到其他人的頭貼變「遊戲中」。
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
            if (_state.Song == null) return NetRoomOp.NoSong;
            if (resolved == null) return NetRoomOp.BadState;

            // 非強制開始 → 所有**其他**座位玩家都要準備好。
            // 房主自己不算(它沒有準備狀態 —— 它按的就是開始那顆鈕)。
            if (!force)
            {
                for (int i = 0; i < _state.Seats.Length; i++)
                {
                    var s = _state.Seats[i];
                    if (s.IsTaken && !IsClearedToStart(s)) return NetRoomOp.BadState;
                }
            }

            // 參與者:可開場(房主 or 已準備)**而且**手上有歌。
            var participants = new List<int>();
            var participantSeats = new List<NetSeat>();
            for (int i = 0; i < _state.Seats.Length; i++)
            {
                var s = _state.Seats[i];
                if (!IsClearedToStart(s)) continue;
                if (s.Avail != Availability.Have) continue;
                participants.Add(s.UserId);
                participantSeats.Add(s);
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

            // ---- 凍結參與者,進入載入階段 ----
            _match = new NetMatchInfo
            {
                MatchId = _nextMatchId++,
                StartEpochMs = nowMs,
                LoadTimeoutMs = NetLimits.LoadTimeoutMs,
                ParticipantUserIds = participants.ToArray(),
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
                if (!AnyParticipantIn(PlayState.Playing))
                {
                    // 全部打完(或斷線被移除)→ 結算。
                    for (int i = 0; i < _state.Seats.Length; i++)
                    {
                        var s = _state.Seats[i];
                        if (s.IsTaken && s.PlayState == PlayState.Finished) s.PlayState = PlayState.Results;
                    }
                    _state.Status = RoomStatus.Open;
                    tick.ResultsReady = true;
                    tick.Changed = true;
                    Touch();
                }
                return tick;
            }

            return tick;
        }

        /// <summary>
        /// 結算看完了 —— 把所有人打回 idle,準備下一局。Hub 在廣播 resultsReady 之後呼叫。
        /// (分開成兩步是為了讓 client 有機會在 <c>results</c> 狀態下顯示結算畫面。)
        /// </summary>
        public void ClearResults()
        {
            for (int i = 0; i < _state.Seats.Length; i++)
            {
                var s = _state.Seats[i];
                if (!s.IsTaken) continue;
                if (s.PlayState == PlayState.Results || s.PlayState == PlayState.Finished)
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
            // 不動它的 Ready —— 它現在是房主,Ready 對它已經不適用(見 IsClearedToStart)。
            // 若它原本已按準備,那個 true 也無害:IsClearedToStart 對房主一律放行。
            _state.HostUserId = heir >= 0 ? _state.Seats[heir].UserId : 0;
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

        private void RemoveFromMatch(int userId)
        {
            if (_match == null) return;
            var ids = _match.ParticipantUserIds;
            var keep = new List<int>(ids.Length);
            for (int i = 0; i < ids.Length; i++) if (ids[i] != userId) keep.Add(ids[i]);
            _match.ParticipantUserIds = keep.ToArray();
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
