using System;
using System.Collections.Generic;
using Sdo.UI.Core;

namespace Sdo.UI.Services
{
    /// <summary>
    /// Offline mock room service. Seeds a few AI-hosted rooms in various states and manages the local
    /// player's create/join/leave/ready/song operations. Business rules (full / in-game) are enforced
    /// here so a future FishNet impl can reuse the same contract.
    /// </summary>
    public sealed class MockRoomService : IRoomService
    {
        private static readonly string[] AiNames =
            { "小舞", "DanceKing", "莉莉", "風之舞", "Neo", "櫻花", "阿傑", "Momo", "星塵", "夜貓" };
        private static readonly string[] SongLabels =
            { "危險的演出", "love song", "Butterfly", "熱情沙漠", "Sugar" };

        private readonly List<RoomInfo> _rooms = new List<RoomInfo>();
        private readonly GameSession _session;
        private readonly Random _rng;

        /// <summary>
        /// 房號池 —— 離線也配 **5 位數**房號,與連線模式一致(<c>GetRoom</c> / <c>CurrentRoomId</c> 都拿它當鍵)。
        ///
        /// 只是房間畫面上**不顯示**它 —— 線上也一樣(見 <c>RoomScreen.Render</c>);房號是給大廳
        /// 「輸入房號」那個框用的,而單機根本沒有別人能加入。
        /// 這裡仍配真號碼而不是 0,是為了讓離線與連線的房間資料結構完全一樣 —— 免得多一條
        /// 「Id 可能是 0」的分支要照顧。
        /// </summary>
        private readonly Sdo.Net.Server.RoomCodePool _codes = new Sdo.Net.Server.RoomCodePool(seed: 20260727);

        /// <summary>離線的 userId 產生器。連線模式由 server 配;離線自己給,讓兩邊的對映邏輯一致。</summary>
        private int _nextUserId = 1;

        /// <summary>
        /// 房間序號 —— 左上角「自由練習場1　頻道1　N」的那個小數字。
        /// 與房號(5 位數)是兩件不同的事:序號是給人看的門牌,房號才是加入房間的鑰匙。
        /// </summary>
        private int _nextSeq = 1;

        private RoomInfo _current;

        /// <summary>配一個 5 位數房號(池子理論上不會耗盡 —— 離線最多幾間房)。</summary>
        private int NextCode()
        {
            int code;
            return _codes.TryRent(out code) ? code : 10000;
        }

        public event Action RoomsChanged;
        public event Action<int> RoomUpdated;

        public MockRoomService(GameSession session, int seed = 12345)
        {
            _session = session;
            _rng = new Random(seed);
            AddAiRoom("DanceKing", GameMode.Normal, RoomStatus.Waiting, 3);
            AddAiRoom("莉莉", GameMode.Free, RoomStatus.InGame, 4);
            AddAiRoom("星塵", GameMode.Normal, RoomStatus.Waiting, 1);
            AddAiRoom("夜貓", GameMode.Free, RoomStatus.Waiting, 6);   // full (6/6)
        }

        private RoomInfo AddAiRoom(string host, GameMode mode, RoomStatus status, int filled)
        {
            var r = new RoomInfo { Id = NextCode(), Seq = _nextSeq++, HostName = host, Mode = mode, Status = status, Capacity = 6 };
            for (int i = 0; i < r.Capacity; i++) r.Seats.Add(new SeatInfo());

            r.Seats[0].Player = new PlayerProfile("ai_" + host, host, _rng.Next(5, 60));
            r.Seats[0].UserId = _nextUserId++;
            r.Seats[0].IsHost = true;
            r.Seats[0].IsReady = true;
            r.HostUserId = r.Seats[0].UserId;

            for (int i = 1; i < filled && i < r.Capacity; i++)
            {
                var n = AiNames[_rng.Next(AiNames.Length)];
                r.Seats[i].Player = new PlayerProfile("ai" + r.Id + "_" + i, n, _rng.Next(1, 60));
                r.Seats[i].UserId = _nextUserId++;
                r.Seats[i].IsReady = true;
            }
            r.SongTitle = SongLabels[_rng.Next(SongLabels.Length)];
            _rooms.Add(r);
            return r;
        }

        public IReadOnlyList<RoomInfo> GetRooms() => _rooms;

        public RoomInfo GetRoom(int id)
        {
            foreach (var r in _rooms) if (r.Id == id) return r;
            return null;
        }

        public RoomInfo CurrentRoom => _current;

        public bool IsHost { get { var s = LocalSeat(); return s != null && s.IsHost; } }

        public RoomInfo CreateRoom(GameMode mode)
        {
            var r = new RoomInfo { Id = NextCode(), Seq = _nextSeq++, HostName = _session.LocalPlayerName, Mode = mode, Status = RoomStatus.Waiting, Capacity = 6 };
            for (int i = 0; i < r.Capacity; i++) r.Seats.Add(new SeatInfo());
            r.Seats[0].Player = Local();
            r.Seats[0].UserId = _nextUserId++;
            r.Seats[0].IsHost = true;
            r.Seats[0].IsReady = true;
            r.HostUserId = r.Seats[0].UserId;
            _rooms.Insert(0, r);
            _current = r;
            _session.CurrentRoomId = r.Id;
            RoomsChanged?.Invoke();
            RoomUpdated?.Invoke(r.Id);
            return r;
        }

        public JoinResult JoinRoom(int id)
        {
            var r = GetRoom(id);
            if (r == null) return JoinResult.NotFound;
            if (r.Status == RoomStatus.InGame) return JoinResult.InGame;
            if (r.IsFull) return JoinResult.Full;
            int seat = -1;
            for (int i = 0; i < r.Seats.Count; i++) if (r.Seats[i].IsEmpty) { seat = i; break; }
            if (seat < 0) return JoinResult.Full;
            r.Seats[seat].Player = Local();
            r.Seats[seat].UserId = _nextUserId++;
            r.Seats[seat].IsHost = false;
            r.Seats[seat].IsReady = false;
            _current = r;
            _session.CurrentRoomId = r.Id;
            RoomsChanged?.Invoke();
            RoomUpdated?.Invoke(r.Id);
            return JoinResult.Ok;
        }

        public void LeaveRoom()
        {
            if (_current == null) return;
            var r = _current;
            var seat = LocalSeat();
            bool wasHost = seat != null && seat.IsHost;
            // ⚠️ 離線與連線在這裡**刻意不同**:離線是「host 走 = 整房解散」(單機沒有別人要接手);
            //    連線是「自動轉移給剩下座位索引最小的人,一個人都不剩才關房」(見 NetRoom.Leave 的註解)。
            if (wasHost) { _rooms.Remove(r); _codes.Return(r.Id); }
            else if (seat != null) { seat.Player = null; seat.UserId = 0; seat.IsReady = false; seat.IsHost = false; }
            _current = null;
            _session.CurrentRoomId = -1;
            RoomsChanged?.Invoke();
            RoomUpdated?.Invoke(r.Id);
        }

        public void SetReady(bool ready)
        {
            var s = LocalSeat();
            if (s == null) return;
            s.IsReady = ready;
            RoomUpdated?.Invoke(_current.Id);
        }

        public bool AllReady()
        {
            if (_current == null) return false;
            foreach (var s in _current.Seats) if (!s.IsEmpty && !s.IsReady) return false;
            return true;
        }

        public bool CanStart()
            => IsHost && _current != null && AllReady() && !string.IsNullOrEmpty(_current.SongTitle);

        public void SetSong(string title)
        {
            if (_current == null || !IsHost) return;
            _current.SongTitle = title;
            RoomUpdated?.Invoke(_current.Id);
        }

        public void SetMode(GameMode mode)
        {
            if (_current == null || !IsHost) return;
            _current.Mode = mode;
            RoomUpdated?.Invoke(_current.Id);
        }

        private PlayerProfile Local() => new PlayerProfile(_session.LocalPlayerId, _session.LocalPlayerName, 1);

        private SeatInfo LocalSeat()
        {
            if (_current == null) return null;
            foreach (var s in _current.Seats)
                if (!s.IsEmpty && s.Player.Id == _session.LocalPlayerId) return s;
            return null;
        }
    }
}
