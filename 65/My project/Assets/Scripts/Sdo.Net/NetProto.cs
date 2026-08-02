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

        /// <summary>
        /// C→S 請求「現在誰在線上」(大廳的玩家名單:全部 / 好友 / 家族三個分頁的資料來源)。
        ///
        /// 與 <see cref="RoomList"/> 一樣是**問答式**的:server 沒有「有人上下線了」的推播,
        /// 名單由大廳自己跟著房間列表同一個節拍回頭問。
        /// </summary>
        public const string UserList = "userList";

        /// <summary>S→C。<c>users</c> 是一列 <c>{userId,name,guild,level,gender,roomSeq}</c>
        /// (<c>roomSeq</c> <b>-1</b> = 人在大廳,&gt;= 0 = 在門牌 N 那間房 —— 門牌從 000 起算,
        /// 所以 0 不能當「在大廳」的哨兵值)。</summary>
        public const string UserListResult = "userListResult";
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
        /// <summary>host only。server 原樣存，保留全員 ready 意願，並把全員 avail 設回 unknown 重新確認歌曲。</summary>
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

        /// <summary>
        /// C→S。回報自己在房間裡的位置與朝向(走動時才送)。
        ///
        /// 為什麼不塞進 <see cref="RoomState"/> 快照:那份快照的 <c>rev</c> 是「房間設定變了」的訊號 ——
        /// client 靠它決定要不要**重建遠端角色**(生一隻 avatar 要讀十幾個部件檔)。
        /// 走路一秒十次,塞進去等於一秒重建十次遠端角色,而且座位/準備狀態的廣播頻率也被一起拉高。
        /// 所以位置走自己的一條流,與分數流(<see cref="Frame"/>)完全同一個形狀。
        /// </summary>
        public const string Move = "move";

        /// <summary>S→C。server 攢好房內所有人的最新位置,固定頻率推一次(見 <see cref="Frames"/> 的理由)。</summary>
        public const string Moves = "moves";

        /// <summary>
        /// C→S。回報自己的外觀(性別 / 體型 / 穿戴部件)。
        ///
        /// 為什麼不只靠 <see cref="Hello"/> 帶:握手發生在開機時,那時玩家還沒在選角色畫面選性別、
        /// 穿搭也還沒解析(要讀 profile.json)。而別人要把你的角色建出來就是靠這份資料 ——
        /// 沒有它,房間裡每個人在別人畫面上都是預設的女角。進房前與換裝後都要重送一次。
        /// </summary>
        public const string SetLook = "setLook";

        /// <summary>
        /// C→S。回報自己的身分(顯示名稱 / playerId / 家族 / 等級)。
        ///
        /// 與 <see cref="SetLook"/> 完全同一個理由,而且是同一個坑的另一半:握手發生在開機時,
        /// 那時玩家還沒在選男女畫面選角色 —— 而**選性別 == 選帳號**(女/男是兩個 profile,
        /// 各有自己的名字)。只補送外觀的話,別人看到的是「新的男角模型」配「舊的女角名字」。
        ///
        /// 送的時機與 setLook 一模一樣:進房前(建房/加入/旁觀)與切換身分後。
        ///
        /// 🔴 **server 端要尊重 token 綁定**:token 綁了名字/playerId 的連線不准靠這條訊息改 ——
        /// 否則它就是 <c>AuthTokens</c> 的後門(hello 擋住的東西從側門走進來)。
        ///
        /// 協定版本沒有 +1:這是**新增**的可省略訊息,舊 client 不送、舊 server 回一個
        /// 「unknown message」的 error 但不斷線 —— 降級成現狀(名字不會更新),不會壞掉。
        /// </summary>
        public const string SetIdentity = "setIdentity";

        /// <summary>
        /// C→S。回報自己的**公開名片**(累計判定數 / 勝負 / 經驗值% / 知名度 / 四格自我介紹)——
        /// 就是個人資料視窗看別人時該顯示的那些數字。內容見 <see cref="NetPlayerCard"/>。
        ///
        /// 為什麼要有:那些資料原本只存在玩家自己那台機器的 profile.json,所以點開別人的資料整頁是 0。
        /// 送的時機與 <see cref="SetLook"/> / <see cref="SetIdentity"/> 同一組(進房前 + 打完一局後 +
        /// 大廳輪詢的節拍),client 端自己去重(內容沒變就不送)。
        ///
        /// 🔴 **自報值,server 不驗證** —— 判定數本來就發生在 client。拿來顯示可以,
        /// 拿來做排行榜或發獎勵不行(與 setIdentity/setLook 同一個信任等級)。
        /// </summary>
        public const string SetPlayerCard = "setCard";

        /// <summary>C→S。問「userId 這個人的公開資料」。對方不在線上 → <c>found=false</c>。</summary>
        public const string PlayerCardQuery = "cardQuery";

        /// <summary>S→C。<c>{found,userId,name,playerId,guild,level,look{...},card{...}}</c>。
        /// <c>look</c> 是 server 手上那份(setLook 來的),不是名片的一部分 —— 同一件事只該有一個來源。</summary>
        public const string PlayerCardResult = "cardResult";

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
        /// <summary>
        /// S→C。所有 chunk 都送完了。
        ///
        /// 為什麼要一個明確的結束訊號、而不是讓收端自己數位元組:數得出來,但那樣
        /// 「傳輸中斷」與「還沒傳完」在收端看起來一模一樣,只能靠 timeout 猜 ——
        /// 猜錯的方向是「等了三十秒才告訴你下載失敗」。收到這個才開始驗證檔案。
        /// </summary>
        public const string BlobDownloadDone = "blobDownloadDone";
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
        /// <summary>
        /// C-to-S-to-C reliable 50-combo milestone. A 5 Hz frame snapshot can skip
        /// straight past 50 or 100, so one-shot visual effects need a separate event.
        /// </summary>
        public const string ComboMilestone = "comboMilestone";


        // ---- 旁觀 ----

        public const string Spectate = "spectate";
        public const string StopSpectate = "stopSpectate";

        // ---- 聊天 ----

        public const string ChatSay = "chatSay";
        public const string ChatMsg = "chatMsg";
        public const string Announce = "announce";

        /// <summary>
        /// C→S 密語:`{target, text, expressionId, leading, channel}`。
        ///
        /// 跟 <see cref="ChatSay"/> 分開是因為收件人完全不同 —— 公開發言是「房裡所有人」,
        /// 密語是「全服照名字找出來的那一個人」(密語本來就跨房,對方在大廳或別間房都要收得到)。
        /// </summary>
        public const string ChatWhisper = "chatWhisper";

        /// <summary>
        /// S→C 密語結果:`{kind, party, senderUserId, text, expressionId, leadingText, channel}`。
        ///
        /// 🔴 連自己那行「你對X說」也是等 server 回這個訊息才畫(kind=<see cref="WhisperOut"/>),
        /// 不在本機先畫 —— 理由與公開發言相同:本機顯示了但其實沒送到,是最難查的那種鬼故事。
        /// 而且「對方到底存不存在」只有 server 知道,本機沒有全服名冊可查。
        /// </summary>
        public const string WhisperMsg = "whisperMsg";

        /// <summary>whisperMsg.kind:你對某人說的那一行(回給發送者)。</summary>
        public const string WhisperOut = "out";
        /// <summary>whisperMsg.kind:某人對你說的那一行(送給收件人)。</summary>
        public const string WhisperIn = "in";
        /// <summary>
        /// whisperMsg.kind:找不到這個名字(回給發送者)。
        ///
        /// server 只有「現在連著的人」這份名冊,所以無法區分「名字不存在」與「這個人存在但沒上線」——
        /// 兩者都回這個。離線版另有的「不在當前頻道」(WhisperKind.OffChannel)因此不會在連線時出現。
        /// </summary>
        public const string WhisperNoId = "noid";

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

        /// <summary>進站密碼不符(<c>bye</c> 的 reason)。client 要翻成人看得懂的提示。</summary>
        public const string ErrBadPassword = "badPassword";

        /// <summary>
        /// token 認證失敗(<c>bye</c> 的 reason)。M10 公網化:server 啟用 token 檔之後,
        /// 身分由 server 決定,client 沒帶或帶錯 token 就進不來。
        /// </summary>
        public const string ErrBadToken = "badToken";

        /// <summary>
        /// 這個名字線上已經有人在用了(<c>bye</c> 的 reason)—— **後上線的那個被擋**。
        ///
        /// 為什麼要擋:名字是這款遊戲裡唯一認人的東西 —— 密語照名字找人、房間裡的名字牌、
        /// 大廳的線上名單都是。允許兩個「小明」同時在線,密語就會進到不確定的那一個,
        /// 而看到名字牌的人也分不出誰是誰。
        /// </summary>
        public const string ErrNameTaken = "nameTaken";

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
