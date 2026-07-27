namespace Sdo.Net
{
    /// <summary>
    /// 協定版本 + 所有訊息的 type 字串。
    ///
    /// 設計:**零多型、零反射。** 每個 JSON 物件都有一個 <c>"t"</c> 欄位，它就是唯一的 dispatch key，
    /// server 端一個 <c>switch (t)</c> 分派完畢。這是刻意避開 osu 踩的坑 —— 它用 SignalR 的
    /// MessagePack [Union] 做多型，代價是 SignalRWorkaroundTypes.cs 裡一張手維護的 40+ 筆
    /// (derivedType, baseType) 對照表 + 手動維護的 union tag 整數，每加一個子類就要記一筆，
    /// 忘了就是執行期爆炸。我們不要那個。
    ///
    /// request/response 配對:client 送出的 request 帶一個遞增的 <c>"rq"</c>，server 的回應原樣帶回同一個
    /// <c>rq</c>。廣播訊息沒有 <c>rq</c>。
    ///
    /// 數字精度:所有數字走 MiniJson，也就是 <c>double</c>，安全整數上限 2^53。分數 &lt; 10^7、
    /// userId/房號 &lt; 10^5、timestamp ms &lt; 10^13 —— 全部安全。有測試(NetJsonTests)守住這件事。
    /// </summary>
    public static class NetProto
    {
        /// <summary>
        /// 協定版本。**任何 wire 格式的變更都要 +1**(欄位改名、語意改變、必要欄位增減)。
        /// server 收到 <c>hello.proto</c> 不等於這個值就直接 <c>bye{proto}</c> —— 版本不合的
        /// client 讓它明確地連不上，遠比讓它半殘地跑然後在某個角落出怪事好。
        /// 純粹新增「可省略的」欄位不需要 +1(收端讀不到就用預設值)。
        /// </summary>
        public const int Version = 1;

        // ---- 通用欄位名(常數化，避免打錯字) ----

        public const string FieldType = "t";
        public const string FieldRequest = "rq";

        // ---- 連線 / 工作階段 ----

        /// <summary>C→S 握手。帶 proto/role/身分/密碼。role="file" 的第二條連線要帶 control 連線的 sessionKey。</summary>
        public const string Hello = "hello";
        /// <summary>S→C 握手成功。server 配的 userId + sessionKey + 各種上限。</summary>
        public const string Welcome = "welcome";
        /// <summary>雙向。送完就關 socket。</summary>
        public const string Bye = "bye";
        public const string Ping = "ping";
        public const string Pong = "pong";

        // ---- 房間生命週期 ----

        public const string RoomList = "roomList";
        public const string RoomListResult = "roomListResult";
        public const string CreateRoom = "createRoom";
        public const string JoinRoom = "joinRoom";
        /// <summary>S→C unicast。result 對映現有的 Sdo.UI.Services.JoinResult enum，不用改那邊。</summary>
        public const string JoinResult = "joinResult";
        public const string LeaveRoom = "leaveRoom";
        /// <summary>
        /// S→C 房內廣播。**server 是這個訊息的唯一作者，client 只讀不寫。**
        /// 刻意推「整份 snapshot」而不是 delta:6 人 × 約 1 KB 完全不是問題，而且消滅一整類
        /// 「兩邊狀態慢慢對不上」的 bug。rev 單調遞增，client 丟掉 rev &lt;= 已見過的。
        /// </summary>
        public const string RoomState = "roomState";
        public const string SetRoomName = "setRoomName";

        // ---- 座位管理(全部 host only) ----

        public const string KickUser = "kickUser";
        /// <summary>關閉/開啟座位。關閉已有人的座位 → server 先踢出那個人再標記關閉。</summary>
        public const string SetSeatClosed = "setSeatClosed";
        public const string TransferHost = "transferHost";
        /// <summary>S→C unicast 給被踢的人。client 收到就回選男女畫面。</summary>
        public const string Kicked = "kicked";
        /// <summary>S→C unicast。**每個 host-only 操作都要 server 端再驗一次** —— client 隱藏按鈕只是 UX。</summary>
        public const string Error = "error";

        // ---- 組隊 ----

        /// <summary>host only。一鍵把座位玩家平均分成 2v2 / 3v3 / 2v2v2。</summary>
        public const string AssignTeams = "assignTeams";
        /// <summary>任何座位玩家。**playState 必須是 idle** —— 按了準備就不能再換隊。</summary>
        public const string SetOwnTeam = "setOwnTeam";

        // ---- 準備 / 開場 / 同步進場 ----

        public const string SetReady = "setReady";
        /// <summary>host only。server 原樣存，並清掉全員 ready + 全員 avail 設回 unknown(同 osu 換圖行為)。</summary>
        public const string SetSong = "setSong";
        /// <summary>host only。這就是「只有房主可以選房主設置」的 server 端把關。</summary>
        public const string SetRoomSettings = "setRoomSettings";
        /// <summary>
        /// host only。force=true 就是「連按兩下開始」的強制開始。
        /// resolved 帶 host 抽好的隨機值(場景/難度/隊形)—— server 驗範圍後 echo，
        /// 那份 echo 才是所有 client 唯一該信的版本。
        /// </summary>
        public const string RequestStart = "requestStart";
        /// <summary>S→C 給參與者 + avail=="have" 的旁觀者。</summary>
        public const string MatchStarting = "matchStarting";
        /// <summary>C→S。只准 loaded / readyForGameplay / finished 三種(其餘是 server 保留狀態)。</summary>
        public const string SetPlayState = "setPlayState";
        /// <summary>
        /// S→C 給參與者。**觸發條件是「沒有任何參與者還在 waitingForLoad」** ——
        /// 不是「全員 readyForGameplay」。這是 osu 的規則，照抄。
        /// </summary>
        public const string GameplayStarted = "gameplayStarted";
        public const string GameplayAborted = "gameplayAborted";
        public const string ResultsReady = "resultsReady";

        // ---- 缺歌上報與傳檔 ----

        public const string SetAvailability = "setAvailability";
        public const string BlobQuery = "blobQuery";
        public const string BlobInfo = "blobInfo";
        public const string BlobUploadBegin = "blobUploadBegin";
        /// <summary>S→C。needFiles = server 還沒有的那些檔的 index(已有的直接跳過 → 重複開同一首歌零上傳)。</summary>
        public const string BlobUploadAccept = "blobUploadAccept";
        public const string BlobUploadDone = "blobUploadDone";
        public const string BlobProgress = "blobProgress";
        public const string BlobDownloadBegin = "blobDownloadBegin";
        public const string BlobManifest = "blobManifest";
        /// <summary>S→C 房內廣播:「這首歌可以下載了」。</summary>
        public const string BlobAvailable = "blobAvailable";
        public const string BlobError = "blobError";

        // ---- 遊玩中的分數流 ----

        /// <summary>
        /// C→S 單人的一筆。fire-and-forget，送不出去就丟掉，**絕不阻塞 gameplay**。
        /// 這條路徑刻意與房間狀態機分離(照 osu 把 multiplayer hub 與 spectator hub 分開的做法)，
        /// server 收到不進狀態機、不落地。
        /// </summary>
        public const string Frame = "frame";
        /// <summary>S→C 房內廣播(含旁觀者)。server 攢的所有人最新一筆，固定 5 Hz。</summary>
        public const string Frames = "frames";
        public const string PlayFinished = "playFinished";

        // ---- 旁觀 ----

        public const string Spectate = "spectate";
        public const string StopSpectate = "stopSpectate";

        // ---- 聊天 ----

        public const string ChatSay = "chatSay";
        public const string ChatMsg = "chatMsg";
        public const string Announce = "announce";

        // ---- 連線角色 ----

        /// <summary>主連線:房間狀態 + 聊天 + 分數流。</summary>
        public const string RoleControl = "control";
        /// <summary>
        /// 檔案連線:大檔傳輸走這條，才不會把遊戲訊息卡在後面。
        /// 同一個 port(不用多開防火牆)，靠 hello.role 區分，並用 control 連線發的 sessionKey 認親。
        /// </summary>
        public const string RoleFile = "file";

        // ---- error code ----

        public const string ErrNotHost = "notHost";
        public const string ErrNotInRoom = "notInRoom";
        public const string ErrBadSeat = "badSeat";
        public const string ErrBadState = "badState";
        /// <summary>組隊人數湊不出官方座標表有的 layout(只有 2v2 / 3v3 / 2v2v2)→ 不准開始。</summary>
        public const string ErrBadTeams = "badTeams";
        public const string ErrNoSong = "noSong";
        public const string ErrRateLimit = "rateLimit";
        public const string ErrFull = "full";
        public const string ErrProto = "proto";
        public const string ErrBadJson = "badJson";
        public const string ErrLookerFull = "lookerFull";

        // ---- blob error code ----

        public const string BlobErrNotFound = "notFound";
        public const string BlobErrTooBig = "tooBig";
        public const string BlobErrBadPath = "badPath";
        public const string BlobErrHashMismatch = "hashMismatch";
        public const string BlobErrQuota = "quota";

        // ---- kicked reason ----

        public const string KickedByHost = "host";
        public const string KickedSeatClosed = "seatClosed";
        public const string KickedRoomClosed = "roomClosed";

        // ---- gameplayAborted reason ----

        public const string AbortLoadTookTooLong = "loadTookTooLong";
        public const string AbortNoParticipants = "noParticipants";
        public const string AbortRoomClosed = "roomClosed";
        public const string AbortHostAborted = "hostAborted";

        // ---- joinResult ----

        public const string JoinOk = "ok";
        public const string JoinFull = "full";
        public const string JoinInGame = "inGame";
        public const string JoinNotFound = "notFound";
    }
}
