using System.Collections.Generic;

namespace Sdo.UI.Services
{
    public enum GameMode { Free, Normal }
    public enum RoomStatus { Waiting, InGame }
    public enum JoinResult { Ok, Full, InGame, NotFound }
    public enum ChatChannel { Family, Friend, Current, Reply }

    // 密語（私聊）行的種類。密語文字一律用 #1efefe（見 RoomScreen.WhisperHex），且不論當下在哪個頁籤，
    // 都同時出現在「當前」與「好友」頻道（見 RoomScreen.ShouldShowChatMessage）。
    //   Outgoing   你對 {對象} 說: {內容}
    //   Incoming   {對象} 對你說: {內容}
    //   OffChannel {對象} 不在當前頻道（帳號存在但不在同一伺服器/頻道）
    //   NoId       {對象} 無此id（查無此帳號）
    public enum WhisperKind { None, Outgoing, Incoming, OffChannel, NoId }

    // 玩家進出舞台遊戲的廣播行（顏色 #72c1fe，見 RoomScreen.StageHex）。
    public enum StageEventKind { None, Enter, Leave }

    // 訊息作用域：大廳 vs 某個房間。一般聊天/進出廣播依此隔離（房間看不到別房/大廳的訊息，大廳看不到房間訊息）。
    // 例外：密語（Whisper != None）跨作用域，大廳與所有房間都看得到（見 RoomScreen/LobbyScreen 的顯示過濾）。
    public enum ChatScope { Lobby, Room }

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
        public WhisperKind Whisper = WhisperKind.None;   // 密語行：Outgoing/Incoming 用 WhisperParty+Text 排字；OffChannel/NoId 只用 WhisperParty
        public string WhisperParty;                      // 密語對象（對方顯示名）
        public StageEventKind Stage = StageEventKind.None;   // 進出舞台廣播：Sender = 玩家名
        public ChatScope Scope = ChatScope.Lobby;        // 訊息作用域（大廳/房間）；密語不受此限（跨場顯示）
        public int RoomId;                               // Scope==Room 時所屬房號（隔離不同房間）

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
