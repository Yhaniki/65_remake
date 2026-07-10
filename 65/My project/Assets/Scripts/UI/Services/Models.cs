using System.Collections.Generic;

namespace Sdo.UI.Services
{
    public enum GameMode { Free, Normal }
    public enum RoomStatus { Waiting, InGame }
    public enum JoinResult { Ok, Full, InGame, NotFound }
    public enum ChatChannel { Family, Friend, Current, Reply }

    public sealed class PlayerProfile
    {
        public string Id;
        public string DisplayName;
        public int Level;

        public PlayerProfile() { }
        public PlayerProfile(string id, string name, int level) { Id = id; DisplayName = name; Level = level; }
    }

    public sealed class SeatInfo
    {
        public PlayerProfile Player;   // null = empty seat
        public bool IsHost;
        public bool IsReady;
        public bool IsEmpty => Player == null;
    }

    public sealed class RoomInfo
    {
        public int Id;
        public string HostName;
        public string Name;        // 玩家自訂房名；空 → 用「房主名 + 的舞蹈室」預設 (見 RoomLabels.DisplayName)
        public GameMode Mode = GameMode.Normal;
        public RoomStatus Status = RoomStatus.Waiting;
        public int Capacity = 6;
        public List<SeatInfo> Seats = new List<SeatInfo>();
        public string SongTitle;   // currently selected song label (null = none)

        public int Count
        {
            get { int n = 0; foreach (var s in Seats) if (!s.IsEmpty) n++; return n; }
        }

        public bool IsFull => Count >= Capacity;
    }

    public sealed class ChatMessage
    {
        public string Sender;
        public string Text;
        public double TimeMs;
        public bool System;   // system/announcement line
        public int ExpressionId;
        public bool Local;
        public ChatChannel Channel = ChatChannel.Current;
        public string RoomActionId;
        public string LeadingText;   // 表情指令「前面」的字（Text 為指令後的字）；顯示時排成 LeadingText〔emoji〕Text，保留輸入時 emoji 的位置

        public ChatMessage() { }
        public ChatMessage(string sender, string text, double timeMs, bool system = false, int expressionId = 0, bool local = false,
            ChatChannel channel = ChatChannel.Current, string roomActionId = null, string leadingText = null)
        {
            Sender = sender;
            Text = text;
            TimeMs = timeMs;
            System = system;
            ExpressionId = expressionId;
            Local = local;
            Channel = channel;
            RoomActionId = roomActionId;
            LeadingText = leadingText;
        }
    }
}
