using System;
using System.Collections.Generic;

namespace Sdo.UI.Services
{
    /// <summary>Abstracted time source so chat/bot timing is deterministic in tests.</summary>
    public interface IClock { double NowMs { get; } }

    public sealed class SystemClock : IClock
    {
        public double NowMs => UnityEngine.Time.realtimeSinceStartupAsDouble * 1000.0;
    }

    /// <summary>
    /// Room list + current-room operations. The mock impl is local/offline; a future FishNet impl
    /// swaps in behind the same interface without any UI change.
    /// </summary>
    public interface IRoomService
    {
        IReadOnlyList<RoomInfo> GetRooms();
        RoomInfo GetRoom(int id);
        RoomInfo CurrentRoom { get; }
        bool IsHost { get; }

        event Action RoomsChanged;
        event Action<int> RoomUpdated;

        RoomInfo CreateRoom(GameMode mode);
        JoinResult JoinRoom(int id);
        void LeaveRoom();
        void SetReady(bool ready);
        bool AllReady();
        bool CanStart();
        void SetSong(string title);
        void SetMode(GameMode mode);
    }

    public interface IPlayerService
    {
        IReadOnlyList<PlayerProfile> GetOnlinePlayers();
        event Action PlayersChanged;
    }

    public interface IChatService
    {
        IReadOnlyList<ChatMessage> History { get; }
        event Action<ChatMessage> MessageReceived;
        void Send(string text);
        void Tick();   // drive scripted bot traffic (call each frame)
    }
}
