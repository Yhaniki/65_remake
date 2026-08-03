namespace Sdo.Net
{
    /// <summary>
    /// 連線相關的所有上限與節奏常數，集中一處。client 與 server **編譯同一份這個檔**
    /// (server csproj 用 &lt;Compile Include&gt; 直接拉這份原始碼)，所以兩邊不可能漂移。
    ///
    /// 每個數字都註明來源:標「osu」的是從 osu! lazer 抄過來的(assets/osu-master，已驗證過的
    /// 實務值，不要憑感覺改);標「我們定的」的是本專案的選擇，改之前先想清楚後果。
    /// </summary>
    public static class NetLimits
    {
        // ---- 框架層 ----

        /// <summary>
        /// 單一 frame 的 payload 上限。**收端必須在 allocate 之前就檢查這個上限** ——
        /// 否則一個壞掉(或惡意)的 length 欄位就能讓對方直接 OOM。
        /// 256 KiB 對 control 訊息(最大的是 6 人的整份 roomState snapshot，約 1-2 KB)綽綽有餘，
        /// 對 binary chunk 也夠(我們一塊只送 <see cref="BlobChunkBytes"/>)。
        /// </summary>
        public const int MaxFramePayload = 256 * 1024;

        /// <summary>JSON control 訊息。</summary>
        public const byte FrameKindJson = 0;

        /// <summary>檔案傳輸的原始位元組(不 base64 —— 省 33% 而且不炸 GC)。</summary>
        public const byte FrameKindChunk = 1;

        // ---- 房間 ----

        /// <summary>座位數。官方 ROOM 版面就是 6 格頭貼(DDRROOM.XML AvatarView1..6)，PKSCORE 數字也只到 6。</summary>
        public const int RoomCapacity = 6;

        /// <summary>旁觀位。官方 EXE .data @0x00583af0 有 10 個旁觀座標(RoomLayout.SpectatorAnchors)。</summary>
        public const int MaxSpectators = 10;

        /// <summary>房號範圍(含)。5 位數是使用者要求的。</summary>
        public const int MinRoomCode = 10000;
        public const int MaxRoomCode = 99999;

        /// <summary>
        /// 可選場景 id 上限。**必須等於 Sdo.UI.Catalog.StageCatalog.MaxSelectableId** ——
        /// server 收 requestStart 時要驗 host 抽出來的 sceneId 在範圍內，但 server 編不到
        /// StageCatalog(它在 Sdo.UI，有 UnityEngine 依賴)，所以在這裡複製一份。
        /// 有一個 EditMode 測試(NetLimitsBridgeTests)斷言兩邊相等，防止漂移。
        /// </summary>
        public const int MaxSceneId = 30;

        // ---- 字數 ----

        /// <summary>玩家暱稱。超過就截斷(不是拒絕 —— 名字不該讓人連不進來)。</summary>
        public const int MaxNameChars = 16;

        /// <summary>自訂房名。</summary>
        public const int MaxRoomNameChars = 20;

        /// <summary>單則聊天。</summary>
        public const int MaxChatChars = 100;

        // ---- 開場 / 同步進場 ----

        /// <summary>
        /// 載入逾時:從 host 的 requestStart 起算。到期時還在 waitingForLoad 的人被逐出本場
        /// (gameplayAborted{loadTookTooLong})，卡在 loaded 的被強制推進 playing。
        /// 對應 osu 的 ForceGameplayStartCountdown。**這個逃生門是必要的** —— 沒有它，
        /// 一個人載入卡住就會讓整房永遠停在 loading 畫面(ScreenGameplay.BootRevealCo 的
        /// ReadyGate 迴圈本身沒有 timeout)。
        /// </summary>
        public const int LoadTimeoutMs = 30000;

        /// <summary>
        /// client 端的硬逾時，比 server 的 <see cref="LoadTimeoutMs"/> 多留 5 秒緩衝。
        /// 兩層都要有:server 的訊息也可能掉包。
        /// </summary>
        public const int ClientLoadTimeoutMs = LoadTimeoutMs + 5000;

        /// <summary>
        /// 結算的寬限期:從「全員打完」起算,到期就把還停在 <c>results</c> 的座位打回 <c>idle</c>。
        ///
        /// 為什麼需要它:client 是**曲末**就送 playFinished(不等玩家關掉 STATIS 結算面板),
        /// 所以 server 判定結算的那一刻,人其實還在看成績、還沒走回房間。留在房間的人這時
        /// 應該繼續看到他們頭貼上的 PLAYING —— 徽章要跟著「人回來了沒」,不是「歌放完了沒」。
        ///
        /// 正常情況下用不到這個逾時:玩家關掉結算面板時 client 會送 <c>setPlayState{idle}</c>,
        /// 那一格當場就清掉。這是**斷線 / 關掉遊戲 / 改過的 client** 的兜底,不然那格會永遠掛著。
        /// 比 client 的自動確認(<c>FrontendApp.NetResultAutoConfirmSec</c> = 30 秒)多留 5 秒緩衝。
        /// </summary>
        public const int ResultsGraceMs = 35000;

        // ---- 心跳 ----

        /// <summary>ping 間隔。</summary>
        public const int PingIntervalMs = 5000;

        /// <summary>多久沒收到對方任何訊息就當斷線(= 隱含的 leaveRoom)。</summary>
        public const int PingTimeoutMs = 15000;

        // ---- 進站密碼 ----

        /// <summary>
        /// 預設進站密碼。**client 的 <c>config.ini [Net] serverPassword</c> 與 server 的
        /// <c>--password</c> 都以這個為預設值**,所以兩邊都不改就能直接連上,
        /// 而且密碼機制是啟用的(不是空密碼放行)。
        ///
        /// 放在這個共用檔而不是各自寫一份字串 —— 那樣「改了一邊忘了另一邊」在結構上就不可能發生。
        /// (兩邊各自硬編的話,漂移的症狀是「誰都連不進來」,而錯誤訊息只會說密碼不對,
        ///  不會告訴你是預設值不一樣了。與其寫測試偵測,不如讓它無法發生。)
        ///
        /// 要公開的 server 請兩邊都改掉 —— 這只是個門檻,不是認證。
        /// </summary>
        public const string DefaultServerPassword = "abab123";

        // ---- 遊玩中的分數流 ----

        /// <summary>
        /// client 送 frame 的間隔。**另外在每個 8 拍結算點也一定要送一筆** ——
        /// 遠端舞者的跳/停 gate 是在 8 拍邊界判定的(見 Sdo.Ruleset.DanceGate)，
        /// 邊界上沒有資料的話 gate 會算錯。取「200ms 或 8 拍邊界，先到者」。
        /// osu 的 SpectatorClient 用的也是 200ms(TIME_BETWEEN_SENDS)。
        /// </summary>
        public const int ClientFrameIntervalMs = 200;

        /// <summary>
        /// server 彙整後推 frames 的頻率(Hz)。server 攢所有人的最新一筆，固定頻率推一次 ——
        /// N 人的下行是 N×5 訊息/秒，而不是 N² 的轉發風暴。
        /// </summary>
        public const int ServerFrameHz = 5;

        // ---- 房間裡的走動 ----

        /// <summary>
        /// 房間裡走動時,client 送位置的間隔(ms)。20 Hz —— 比分數流(200ms)密,因為位置是連續量:
        /// 太疏的話遠端角色會一格一格跳,插值也補不出中間的轉彎。停下來時送最後一筆就不再送。
        ///
        /// 🔴 為什麼從 100ms 加密到 50ms:上行與 server 的下行**是兩層各自的節奏**,兩個 10 Hz
        /// 不同步時,同一輪可能推到兩筆、下一輪一筆都沒有 —— 實際到達間隔在 0~200ms 之間跳,
        /// 遠端角色於是「滑一下、停一下」。公網的 RTT 抖動會再放大這件事。兩層都加密到 20 Hz
        /// 之後間隙變成 0~100ms,而插值的時間常數(RoomScene3D.RemoteLerpRate,約 83ms)蓋得住它。
        /// 代價很小:一個人約 60 bytes/筆,六人房下行也只有 ~7 KB/s。
        /// </summary>
        public const int ClientMoveIntervalMs = 50;

        /// <summary>server 彙整後推 moves 的頻率(Hz)。與 <see cref="ServerFrameHz"/> 同樣的理由:攢起來定頻推。</summary>
        public const int ServerMoveHz = 20;

        /// <summary>
        /// 位置訊息的速率上限(每秒)。比 <see cref="ClientMoveIntervalMs"/> 換算的 20 留一點餘裕
        /// (幀率抖動會讓實際間隔略小於 50ms),但擋得住「每幀都送」那種壞掉的 client。
        /// </summary>
        public const int RateMovePerSec = 30;

        /// <summary>
        /// 房間走動框(鏡射 <c>Sdo.Game.RoomLayout.Min/MaxX/Z</c> —— server 編不到那個 assembly)。
        /// 收到的位置一律夾進這個框:壞掉或被改過的 client 送 ±1e30 的話,
        /// 那個值會被寫進 Transform,而 NaN/Inf 會污染整棵 avatar hierarchy,
        /// 連 <c>Camera.LookAt</c> 都會壞掉 → 整個房間畫面變黑。
        /// 有一個 EditMode 測試斷言這四個數與 RoomLayout 相同(NetLimitsBridgeTests)。
        /// </summary>
        public const float RoomWalkMinX = -278f, RoomWalkMaxX = 100f;
        public const float RoomWalkMinZ = -279f, RoomWalkMaxZ = 100f;

        // ---- 缺歌 / 檔案傳輸 ----

        /// <summary>
        /// 下載/上傳進度回報的節流。osu 的 OnlinePlayBeatmapAvailabilityTracker 就是 500ms，
        /// 它的註解寫得很直白:「we don't want to flood the network with this」。
        /// **client 與 server 兩邊都要擋** —— client 自律，server 防惡意。
        /// </summary>
        public const int AvailProgressThrottleMs = 500;

        /// <summary>單首歌(過濾後)的總大小上限。預設 200 MB，可由 ServerOptions 覆寫。</summary>
        public const long DefaultMaxBlobBytes = 200L * 1024 * 1024;

        // 以下三個的真相在 Sdo.Osu.NetPackLimits —— 歌曲過濾邏輯(SongPackFilter)需要它們，
        // 而依賴方向是 Sdo.Net → Sdo.Osu，不能反過來。這裡只是轉發，讓連線層的呼叫端
        // 不用為了一個大小上限去 using Sdo.Osu。**不要在這裡改數字**,改那邊。

        /// <summary>單首歌的檔案數上限。正常的 osu/SM 資料夾遠低於此。</summary>
        public const int MaxPackFiles = Osu.NetPackLimits.MaxPackFiles;

        /// <summary>單檔上限。擋「改名成 .ogg 的影片」。</summary>
        public const long MaxSingleFileBytes = Osu.NetPackLimits.MaxSingleFileBytes;

        /// <summary>圖片單檔上限。擋 4K 背景圖(對 800×600 的遊戲毫無意義)。</summary>
        public const long MaxImageFileBytes = Osu.NetPackLimits.MaxImageFileBytes;

        /// <summary>binary chunk 大小。</summary>
        public const int BlobChunkBytes = 64 * 1024;

        /// <summary>server 上的歌曲檔案保留時數。使用者要求「最多留一天」。</summary>
        public const int DefaultBlobTtlHours = 24;

        /// <summary>每人同時下載數上限。</summary>
        public const int MaxConcurrentDownloadsPerUser = 2;

        // ---- rate limit(每連線) ----

        /// <summary>control 訊息/秒。</summary>
        public const int RateControlPerSec = 32;

        /// <summary>frame 訊息/秒。client 正常是 5/s(200ms)，給到 20 是留給 8 拍邊界那些額外的筆。</summary>
        public const int RateFramePerSec = 20;

        /// <summary>聊天:每 <see cref="RateChatWindowMs"/> 最多幾則。</summary>
        public const int RateChatPerWindow = 5;
        public const int RateChatWindowMs = 3000;

        // ---- server 容量 ----

        public const int DefaultMaxRooms = 200;
        public const int DefaultMaxConnections = 256;

        /// <summary>blob 目錄的總容量上限(GB)。超過時先刪最舊的未 pinned pack。</summary>
        public const int DefaultMaxTotalBlobGb = 20;
    }
}
