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

    // 本機專屬的提示行（不送給任何人、不彈頭上泡）：
    //   SelfTalk  好友頻道沒帶 [名字] 就送出 → 白字「你說: {內容}」，只有自己看得到。
    //   NoGuild   家族頻道但沒有家族 → 紅字「你沒有家族」。
    public enum ChatNotice { None, SelfTalk, NoGuild }

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

        // ---- 以下是連線才用得到的欄位。離線(MockRoomService)填中性值，
        //      所以讀它們的 UI 程式碼不需要分辨自己在哪個模式。----

        /// <summary>server 配發的使用者 id。0 = 空位或離線模式。</summary>
        public int UserId;

        /// <summary>被房主關閉的座位(需求 12 的「鎖格子」)。空位 + 關閉 → 畫 🚫 覆蓋圖。</summary>
        public bool IsClosed;

        /// <summary>隊伍 0=A 1=B 2=C 3=自由。對映 <c>GameSession.Team</c> 的編碼。</summary>
        public int Team = 3;

        /// <summary>有沒有房主選的那首歌 —— 頭貼上的「缺歌 / 下載」徽章看這個。</summary>
        public Sdo.Net.Availability Avail = Sdo.Net.Availability.Have;

        /// <summary>下載進度 0..1(只在 <c>Downloading</c> 時有意義)。</summary>
        public float AvailProgress;

        /// <summary>遊玩狀態 —— 頭貼上的「遊戲中」徽章看這個(需求 9)。</summary>
        public Sdo.Net.PlayState PlayState = Sdo.Net.PlayState.Idle;
    }

    /// <summary>房間裡的旁觀者(不佔座位)。離線模式永遠是空的。</summary>
    public sealed class SpectatorInfo
    {
        public int UserId;
        public string DisplayName;
        public int Level;
        public Sdo.Net.Availability Avail = Sdo.Net.Availability.Unknown;
    }

    public sealed class RoomInfo
    {
        /// <summary>
        /// **房號** —— 5 位數(10000..99999),玩家要唸給朋友聽、用來加入房間的那個。
        /// 連線時顯示在房名後面的括弧裡:「飄漂o的舞蹈室(40444)」。
        /// 離線也配一個(當內部的鍵),但**畫面上不顯示** —— 單機沒有別人能加入(見 RoomScreen.Render)。
        /// </summary>
        public int Id;

        /// <summary>
        /// **房間序號** —— 左上角那排「自由練習場1　頻道1　<b>N</b>」的 N。官方的顯示習慣是
        /// 一個小數字(第幾間房),所以它跟 <see cref="Id"/> 是兩件不同的事:
        /// Id 是加入房間的鑰匙(5 位數、全域唯一),Seq 只是給人看的門牌號。
        /// </summary>
        public int Seq;
        public string HostName;
        public string Name;        // 玩家自訂房名；空 → 用「房主名 + 的舞蹈室」預設 (見 RoomLabels.DisplayName)
        public GameMode Mode = GameMode.Normal;
        public RoomStatus Status = RoomStatus.Waiting;
        public int Capacity = 6;
        public List<SeatInfo> Seats = new List<SeatInfo>();
        public string SongTitle;   // currently selected song label (null = none)

        // ---- 連線才用得到的欄位 ----

        /// <summary>
        /// 房主的 userId。0 = 沒有房主(座位全空;連線模式才可能出現)。
        ///
        /// 🔴 **房主徽章要跟著這個欄位畫,不要假設「座位 0 是房主」** —— 轉移房主時
        /// 只換這個值、不搬座位。離線模式下它等於座位 0 那個人的 UserId。
        /// </summary>
        public int HostUserId;

        /// <summary>server 的修訂號(離線恆 0)。除錯用。</summary>
        public int Rev;

        /// <summary>旁觀者(離線永遠是空的)。</summary>
        public List<SpectatorInfo> Spectators = new List<SpectatorInfo>();

        public int Count
        {
            get { int n = 0; foreach (var s in Seats) if (!s.IsEmpty) n++; return n; }
        }

        public bool IsFull => Count >= Capacity;

        /// <summary>這個人是房主嗎?</summary>
        public bool IsHostUser(int userId) => userId != 0 && userId == HostUserId;

        /// <summary>這個人坐在哪一格?不在座位上回 -1。</summary>
        public int SeatIndexOfUser(int userId)
        {
            if (userId == 0) return -1;
            for (int i = 0; i < Seats.Count; i++)
                if (!Seats[i].IsEmpty && Seats[i].UserId == userId) return i;
            return -1;
        }
    }

    public sealed class ChatMessage
    {
        /// <summary>
        /// 發言者的 server userId。**0 = 離線模式 / 系統行 / 本機專屬提示行。**
        ///
        /// 為什麼不能只靠 <see cref="Sender"/>(顯示名):名字可以重複,而「這句話的頭上泡與舞蹈動作
        /// 要掛到**哪一個** 3D 角色身上」必須是唯一的。連線版的遠端頭上泡就是靠它找到人的。
        /// </summary>
        public int SenderUserId;

        /// <summary>
        /// 發言者是男的嗎?**動作 clip 與語音必須用這個**,不是本機玩家的性別。
        ///
        /// 房間動作的關鍵字表是分性別的(「再見」女→act5/WREST0063/WOMAN_5、「88」男→act6/MREST0076/MAN_6),
        /// 而 <c>OnlineChatService</c> 在收端就是用發言者的性別解出 <see cref="RoomActionId"/> 的。
        /// 顯示端若改用本機性別去取 clip/語音,就會出現「用女生的 id 解出來、卻播男生的動作與 MAN_n 語音」
        /// 這種不自洽 —— 所以把已經查過的那個值順手帶下來。
        /// </summary>
        public bool SenderMale;

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
        public ChatNotice Notice = ChatNotice.None;      // 本機提示行（你說 / 你沒有家族）：本機專屬、不彈泡、跨場、只在對應分類顯示
        public bool Guild;                               // 家族頻道訊息：綠字「<家族>名字: 內容」，跨場、只在家族分類顯示

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
