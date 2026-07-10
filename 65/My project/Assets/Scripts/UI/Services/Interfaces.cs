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
        void Send(string text, ChatChannel channel = ChatChannel.Current);
        void SendExpression(int expressionId, ChatChannel channel = ChatChannel.Current);
        void SendExpression(int expressionId, ChatChannel channel, string trailingText);
        // leadingText / trailingText = 指令前/後的字，保留 emoji 在輸入中的位置（見 RoomChatCommand.TryParseExpression）。
        void SendExpression(int expressionId, ChatChannel channel, string leadingText, string trailingText);
        // 密語（私聊）：查對象是否在同伺服器/頻道 → 送「你對X說」+對方回「X對你說」；不在頻道→「X不在當前頻道」；查無帳號→「X無此id」。
        void SendWhisper(string target, string body, ChatChannel channel = ChatChannel.Current);
        // 玩家進出舞台遊戲的廣播（顏色 72c1fe）：「X 進入舞台遊戲」/「X 離開舞台」。
        void AnnounceStageEnter(string name);
        void AnnounceStageLeave(string name);
        // 設定目前作用域：之後送出的訊息會標記成大廳或該房間（密語除外，永遠跨場）。畫面在 OnShow 設定。
        void SetScope(ChatScope scope, int roomId = 0);
        void Tick();   // drive scripted bot traffic (call each frame)
    }
}
