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
        private int _nextId = 1;
        private RoomInfo _current;

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
            var r = new RoomInfo { Id = _nextId++, HostName = host, Mode = mode, Status = status, Capacity = 6 };
            for (int i = 0; i < r.Capacity; i++) r.Seats.Add(new SeatInfo());
            r.Seats[0].Player = new PlayerProfile("ai_" + host, host, _rng.Next(5, 60));
            r.Seats[0].IsHost = true;
            r.Seats[0].IsReady = true;
            for (int i = 1; i < filled && i < r.Capacity; i++)
            {
                var n = AiNames[_rng.Next(AiNames.Length)];
                r.Seats[i].Player = new PlayerProfile("ai" + r.Id + "_" + i, n, _rng.Next(1, 60));
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
            var r = new RoomInfo { Id = _nextId++, HostName = _session.LocalPlayerName, Mode = mode, Status = RoomStatus.Waiting, Capacity = 6 };
            for (int i = 0; i < r.Capacity; i++) r.Seats.Add(new SeatInfo());
            r.Seats[0].Player = Local();
            r.Seats[0].IsHost = true;
            r.Seats[0].IsReady = true;
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
            if (wasHost) _rooms.Remove(r);                 // host leaves -> room dissolves (MVP rule)
            else if (seat != null) { seat.Player = null; seat.IsReady = false; seat.IsHost = false; }
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
