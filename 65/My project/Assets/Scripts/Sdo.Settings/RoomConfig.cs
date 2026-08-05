using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sdo.Settings
{
    /// <summary>
    /// **本機設定的落地檔** <c>config.ini</c>：開房間右側面板的可選清單與預設值（<c>[Room]</c>）與 OPTION 對話框
    /// 設定（<c>[Option]</c>）。**全域一份**，放在存檔層 <c>DATA/PROFILE/</c>（與 profile.json / favorites.json /
    /// keymaps.ini 同層）—— 設定不跟著使用者跑（換帳號不會換設定）。
    ///
    /// **角色資料不在這裡**：登入哪個角色、家族/等級的預設值拉去同層的 <c>profile.json</c>（見
    /// <see cref="ProfileDefaults"/>）；每個角色自己的設定與經驗值在 <c>DATA/PROFILE/&lt;id&gt;/profile.json</c>。
    /// 舊 config.ini 的 <c>[Profile]</c> 區開機時一次性搬過去，之後這個檔不再寫出該區。
    ///
    /// 以前散成三個檔，開機時會一次性併進來後把舊檔移除（見 <see cref="Load"/>）：
    ///   * <c>settings.json</c>（畫面/音量/遊戲頁）→ <c>[Option]</c>，本來就是同一份值存兩處。
    ///   * <c>active.txt</c>（登入哪個角色）→ 曾經是 <c>[Profile] activeId</c>，現在在 profile.json。
    ///   * 舊位置的 config.ini：per-user（<c>DATA/PROFILE/&lt;id&gt;/</c>）優先，其次執行檔同層。
    /// **鍵位不在這裡**：4 鍵鍵位與遊玩功能鍵拆去 <see cref="KeyMap"/> 的 <c>keymaps.ini</c>（舊檔的
    /// <c>opt_keys/opt_keysAux</c> 仍讀得進來供搬遷，但不再寫出）。
    ///
    /// 純文字、好手改：第一次跑會自動寫一份附註解的範本；之後讀檔覆蓋預設。解析/夾值是純函式可單元測試
    /// （<see cref="ParseInto"/> / <see cref="Sanitize"/> 不碰檔案）。
    /// </summary>
    public static class RoomConfig
    {
        // ---- 當下生效的值（欄位＝INI 的 key）----
        public static float[] speedSteps = { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 4.0f, 5.0f, 6.0f, 8.0f };
        public static float defaultSpeed = 2.5f;     // 預設速度（會對齊到 speedSteps 最近檔位）。玩家在房間選了會寫回這裡
        public static int defaultNoteType = -1;      // note 種類(hit-effect)：-1=隨機(預設)；>=0=指定第幾種。玩家在房間選了會寫回這裡
        public static int defaultTeam = 3;           // 組隊：0=A,1=B,2=C,3=自由
        public static int defaultDropDirection = 0;  // 掉落方式：0=向上,1=向下,2=傾斜
        public static int defaultGameMode = 0;       // 模式：0=自由模式,1=普通模式,2=ShowTime模式
        public static int defaultScene = -1;         // 場景：-1=隨機(預設)；0..30=指定場景 id(見 StageCatalog)。玩家在選歌選了會寫回這裡

        // note 下落速度的「基準 BPM」（＝ ManiaScroll.DefaultReferenceBpm 的落地值，Sdo.Settings 不參照 Sdo.Osu 所以這裡寫死同一個數）。
        // 螢幕像素速度 = scrollBaseBpm × 速度檔位 × 1.6 px/s（官方公式）。每首歌用同一個基準 → 同一個檔位在每首歌都一樣快；
        // 譜面自己的 BPM 變化/SV 仍會在曲內相對加減速（ManiaScroll 的 multiplier），這個值只決定「1.0× 是多快」。
        // 130 = 現行預設；調大 = 整體變快（所有歌、所有檔位等比例）。見 docs/architecture/scroll-base-bpm.md。
        public static float scrollBaseBpm = 130f;
        // 判定精度：沿用 StepMania 的「精N」（1~8，9=JUSTICE）。以精4 為基準窗（Perfect 45 / Cool 90 / Bad 135 /
        // Miss 180 ms）乘上該精度的係數；精2 = ×1.33。手改這個 key 就能整組調鬆緊，見 JudgmentWindows.FromStepManiaJudge。
        public static int judgeLevel = 2;

        // 全域判定 offset（毫秒）：加在譜面時鐘上（GameplayClock.OffsetMs）。正 = 判定時間往後（適合整體打太早的人）。
        //
        // **機器的音訊延遲不歸它管，已經自動補掉了**（ScreenGameplay：DSP 混音緩衝算得出來、驅動延遲是實測寫死的
        // DriverLatencyMs、打拍音檔的前導靜音在排程時提早）。所以這裡預設 0，留給「我就是想打早/晚一點」的個人偏好，
        // 以及「別台機器的驅動延遲跟我的不一樣」的微調。
        //
        // 要調就用編輯器的「打拍測試」(F2) 量 —— **一定要用聽節拍器那個測法**。看著 note 打是拿時鐘畫出來的東西去對
        // 時鐘（自我參照），只量得到輸入延遲，永遠量不到音訊延遲。
        public static float globalOffsetMs = 0f;

        // 判定線的視覺偏移（設計 px）：完美時機的音符會落在受擊線 + 這個位移的地方（0 = 正中受擊線）。
        // 只影響「看起來要打在哪」，不影響判定時間（那是 globalOffsetMs 的事）。同樣用打拍測試調。
        public static float judgeOffsetY = 0f;

        // 依名次調整站位（多人同場）：1=開(預設，官方行為)、0=關。
        // 開＝比賽中即時第一名會滑進隊形的領隊格（中央前排，也是導播鏡頭的錨點），被擠掉的人退回自己原本的格子；
        // 關＝所有人整場固定站在「房間座位順序」對應的格子，名次再怎麼變都不換位（鏡頭仍錨在領隊格上的那個人）。
        // 純視覺偏好、每台各自生效，不影響判定/分數/名次計算；組隊模式本來就不跨隊換位，這個開關對它沒有作用。
        // 見 FormationAssignment.SlotForDancer 與 ScreenGameplay.TickDancerSlots。
        public static bool rankBasedFormation = true;

        // 外部歌曲（osu / StepMania / Malody）載入總開關：1=載入(預設)、0=完全不碰。關掉的話開機不掃任何歌資料夾、
        // 不建 <ADDON> 那幾個資料夾，開場那張進度條載入畫面也不出現（沒有慢的掃描要等），選歌畫面的「資料夾」頁籤
        // 不再開分類瀏覽面板 —— 整個遊戲只剩官方 DATA/MUSIC 的歌。下面 AdditionalSongFolders / AddonFolder /
        // SongUiAlpha / DifficultyCalc 都只在這個開關開著時有意義。見 ExternalSongLibrary.ScanAndRegisterCo。
        public static bool loadExternalSongs = true;

        // 額外歌曲資料夾（osu / StepMania）：分號(;)分隔的絕對路徑（逗號仍相容），每個路徑都當成一個 Songs 根目錄（底下第一層=
        // 分類 group，再一層=各首歌的資料夾），語意同 StepMania 的 AdditionalSongFolders。預設的 <ADDON>/SONG 一律自動掃描，
        // 不需列在這（舊的 exe 同層 Songs/ 也仍相容）。
        public static string[] additionalSongFolders = new string[0];

        // 外掛(ADDON)根目錄覆蓋：預設空 = DATA/ADDON（即 Root/ADDON）。想把整包外掛（SONG/NOTESKIN/THEME/MODEL）放到別的
        // 資料夾（例如另一顆硬碟 D:/SdoAddon）就填一個絕對路徑；該資料夾底下就是 SONG 等子夾。見 SdoExtracted.AddonDir。
        public static string addonFolder = "";

        // 外部歌曲「分類瀏覽」浮動面板（SongGroupPanel）的整體不透明度：0=全透明、1=不透明。預設 0.6 讓底下的唱片欄若隱若現。
        // 見 SongGroupPanel.OnGUI（整個視窗連同文字/按鈕一起以此 alpha 疊繪）。
        public static float songUiAlpha = 0.6f;

        // 外部歌難度用哪套計算：minacalc=Etterna MinaCalc 的 MSD 換算等級(預設，見 ManiaMsd.ToLevel / Sdo.Osu.Mina)；
        // osu=osu!mania 星數×7 的等級(見 ManiaStarRating)。想要 osu 星數那套就把這個鍵改成 osu。
        // **選了哪一套就整體都照那套**：選歌/房間/遊戲/編輯器顯示的數字、隨機難度的範圍篩選、以及外部歌
        // 「哪張譜排進簡單/普通/困難」（SongCatalog.Entry.SortSlotsByDisplayLevel）全部同一個來源，
        // 不會有兩套數字混用。切換不需重掃歌曲（槽位是載入時重排的）。
        // 只影響「難度得自己重算」的外部譜面(osu / StepMania / Malody)：官方 DATA/MUSIC 的 .gn 和外部資料夾裡的
        // .gn 歌包都自帶檔頭難度，兩套計算器都不會動它們（見 SongCatalog.Entry.DisplayLevel）。
        public static string difficultyCalc = "minacalc";
        // 遊戲中兩組文字的整體大小比例（1.0 = 官方原尺寸）。純顯示，不影響判定/分數。
        // comboTextScale：COMBO 字樣＋連段數字（整組一起縮放，字距/行距同比例，不會散開）。
        // judgeTextScale：PERFECT / COOL / BAD / MISS 判定字樣。
        public static float comboTextScale = 1f;
        public static float judgeTextScale = 1f;

        // 同兩組文字的整體不透明度（1.0 = 全不透明，0 = 完全看不見）。預設 0.6 —— 這兩叢字就疊在音符板正上方，
        // 全不透明會擋住下落中的音符；淡一點看得到連段又不吃視線。純顯示，不影響判定/分數。
        // 判定字不會淡出（官方是顯示完直接消失），這個值就是它顯示期間的亮度。
        public static float comboTextAlpha = 0.6f;
        public static float judgeTextAlpha = 0.6f;

        // 打中時「彈跳」放到最大那一瞬間的倍率（＝峰值大小 ÷ 靜止大小）。官方是 2.0（彈到兩倍再收回），
        // 1.0 = 完全不彈跳（維持靜止大小）。收回的速度是官方寫死的（COMBO 快、判定字慢），這裡只調幅度。
        // 與 comboTextScale/judgeTextScale 相乘：整體大小 × 這一刻的彈跳倍率。純顯示，不影響判定/分數。
        public static float comboTextPop = 2f;
        public static float judgeTextPop = 2f;

        // ---- MMD 模型顯示（[Mmd] 區）：把場上每一隻角色（跳舞的、房間走路的、以及三個各自渲一張 RT 的頭貼/預覽）
        //      的身體換成一個 MMD .pmx 模型。SDO 的 SdoAvatar 仍然活著當「動作驅動器」，所以跳的還是同一套 MOT/DPS，
        //      只是畫出來的身體換人。整組設定由 Sdo.Game 的 MmdAvatarSwap 每幀比對這裡的值套用（改了立刻看得到），
        //      UI 在開場設定面板的「MMD」分頁。以前這些值只活在一個 IMGUI 除錯面板裡、關掉遊戲就沒了。----
        /// <summary>
        /// 「我不用 MMD 模型」在 <see cref="mmdModel"/> 裡長什麼樣。
        ///
        /// 以前這是另一個布林總開關（mmdEnabled）。**選了模型卻還要再開一個開關**是多餘的一步，
        /// 而且兩個值可以互相矛盾（開著但沒選 / 選了但關著）—— 現在只有一個值：<b>選了哪個模型</b>，
        /// 而「不使用」就是這份清單的第一個選項。舊設定檔的 mmdEnabled=0 會在 <see cref="Load"/> 被搬成它。
        /// </summary>
        public const string mmdModelNone = "(不使用)";

        /// <summary>目前這個值等於「不用 MMD 顯示自己」嗎。</summary>
        public static bool IsMmdNone(string model)
            => string.Equals((model ?? "").Trim(), mmdModelNone, StringComparison.OrdinalIgnoreCase);

        public static string mmdModel = mmdModelNone;   // 用哪個模型（DATA/MODEL/<資料夾名>）；(不使用)＝維持 SDO 角色，空＝掃到的第一個
        /// <summary>舊設定檔的 mmdEnabled（已淘汰）。只在 <see cref="Load"/> 做一次性搬遷用，不再寫回檔案。</summary>
        public static bool legacyMmdEnabled = false;
        public static bool hasMmdEnabledKey = false;   // 讀到的 config.ini 還帶著舊的 mmdEnabled → 搬成 mmdModel 後重寫一次
        // 別人的 MMD 模型要不要顯示（1=顯示，預設）。與「我自己用哪個模型」完全獨立 ——
        // 這是兩件事：我想不想變成 MMD、我想不想看到別人的 MMD。關掉 → 別人一律是他的 SDO 穿搭，
        // 而且完全不會去下載別人的模型（零流量、零磁碟）。
        public static bool mmdShowOthers = true;
        // 著色後端：關(預設)＝Sdo/MmdModel，MMD 固定管線的忠實移植（unlit、ramp 直接貼、鉛筆描邊）；
        // 開＝lilToon（Assets/lilToon，MIT），兩段式 cel 陰影＋邊緣光＋吃光照的描邊，也就是「原神那一類」的取向。
        // 換值要重建身體（材質是整個模型共用的），由 MmdAvatarSwap 處理。
        public static bool mmdLilToon = false;
        public static bool mmdToon = true;          // 卡通著色（toon ramp）
        public static bool mmdOutline = true;       // 描邊（pencil edge）
        public static bool mmdSphere = true;        // sphere 反光貼圖
        public static bool mmdPhysics = true;       // 頭髮/裙擺布料模擬
        public static bool mmdAim = true;           // aim 重定向（手腳姿勢；關＝改用 world-delta 對照模式）
        public static bool mmdRootMotion = true;    // 根骨位移（走路時整個人前進）
        public static bool mmdFlipV = true;         // 貼圖 V 翻轉（PMX 的 UV 是 V 向下；某些模型的貼圖要關掉才對）
        public static float mmdGravity = 1f;        // 布料重力倍率
        public static float mmdStiffness = 0.12f;   // 布料硬度（低＝被重力拉直垂下）
        public static float mmdColliderScale = 1f;  // 身體碰撞體半徑倍率
        public static float mmdScale = 1f;          // 模型大小倍率（1＝自動對齊 SDO 舞者身高）
        // 多人連線:把自己身上的模型上傳給 server,讓同房的人也看得到(1=分享,預設)。
        // 關掉 → 別人看到的是你的 SDO 穿搭(你自己畫面上仍然是 MMD)。
        // ⚠️ 網路上流通的 MMD 模型多半帶使用規約,有些明確禁止再配布 —— 這個開關就是為此存在的。
        public static bool mmdShareModel = true;

        // ---- OPTION 對話框設定的鏡像（存進同一份全域 config.ini 的 [Option] 區）。settings.json 仍是執行期讀取的
        //      工作副本；這裡是「可手改的落地檔」：開機 Load() 後把有帶 [Option] 的值套回 GameSettings（ApplyOptionTo），
        //      OPTION 按保存時再抓回來寫檔（CaptureOptionFrom + Save）。見 OptionDlgModal.Apply / SettingsBootstrap。----
        public static bool hasOption = false;   // 解析到的 config.ini 是否帶 [Option] 區（帶了就不用去撿舊 settings.json）
        public static bool hasLegacyProfileKeys = false;   // 檔案是否還帶舊的 [Profile] 鍵（activeId/家族/等級）→ Load 要把值
                                                           // 搬進 DATA/PROFILE/profile.json 並重寫一次 config.ini 把該區去掉
        public static bool hasTextScaleKeys = false;   // 同上：檔案是否帶 combo/判定文字大小鍵
        public static bool hasTextAlphaKeys = false;   // 同上：檔案是否帶 combo/判定文字透明度鍵（比大小鍵晚加，得各自記）
        public static bool hasTextPopKeys = false;     // 同上：檔案是否帶 combo/判定文字彈跳倍率鍵（又比透明度鍵晚加）
        public static bool hasScrollBaseBpmKey = false;// 同上：檔案是否帶 scrollBaseBpm（最晚加的一個，舊檔都沒有 → 補寫模板）
        public static bool hasOptUiScale = false;   // 檔案是否帶 opt_uiScale（舊檔沒有 → 從舊 settings.json 撿）
        public static bool hasSongBombsKey = false; // 檔案是否帶 opt_songBombs（沒有＝舊檔只有語意相反的 opt_disableBombs
                                                    // → Load 重寫一次模板，把舊鍵換成新鍵）
        public static float optBgm = 0.5f, optMusic = 0.5f, optSfx = 0.5f;
        // 舊檔（config.ini 還帶鍵位的年代）的 4 鍵鍵位：只讀不寫，開機時給 KeyMap 種 keymaps.ini 用，見 KeyMap.Load。
        public static string optKeys = "A,S,W,D";
        public static string optKeysAux = "LeftArrow,DownArrow,UpArrow,RightArrow";
        public static int optDispW = 800, optDispH = 600, optVsync = 1;   // 預設視窗化 800×600（與遊戲畫面「窗口」連動，同 GameSettings 預設）
        public static float optUiScale = 1f;
        public static string optDispMode = "Windowed";
        public static string optLang = "zh-TW";
        public static bool optFullscreenFill = false, optBloom = true, optNotesPanelLeft = true,
                           optEffectChar = true, optEffectScene = true, optCameraAuto = true, optCallCard = true,
                           optPlayFullSong = false, optSongSpeed = true, optCollapseShortHolds = true,
                           optSongBombs = true,   // 歌曲炸彈：預設開（照譜面原樣有雷）
                           optDanceIgnoreMiss = false;   // 掉 miss 也照跳舞：預設關（官方玩法＝斷 combo 會停舞）
        public static int optCameraFixed = 0;   // 固定視角用哪一台（0..5）；遊戲中 F2 切鏡頭會寫回
        public static float optPanelOpacity = 1.4f;

        // ---- 舊 [Profile] 區（登入哪個角色 + 家族/等級預設值）：**已經拉出去成 DATA/PROFILE/profile.json**
        //      （見 ProfileDefaults）。這幾個欄位只剩「開機時把舊 config.ini 的值讀進來給它搬」這一個用途 ——
        //      不再寫出到 config.ini，也不要拿來當顯示來源（顯示一律問 ProfileManager）。----
        public static string legacyActiveId = "";
        public static string legacyFamilyName = "";
        public static string legacyFamilyEmblem = "";
        public static string legacyPlayerLevel = "";

        // ---- [Net]：多人連線。★ serverAddress 是整個連線功能的總開關：留空＝純單機（走 MockRoomService，
        //      體驗與加連線之前完全一樣）；填了才會去連。按登入連不上會留在單機（原因只寫 log），不會卡在畫面上。----
        // 伺服器位址：IP 或主機名（例如 192.168.1.10 或 dance.example.com）。留空＝不連線（單機）。
        public static string serverAddress = "";
        // 伺服器 port。預設 27015（沒有官方值可循，挑一個常見的遊戲 port 區間）。
        public static int serverPort = 27015;
        // 進站密碼：要與 server 的 --password 一致才連得上。留空＝連到沒設密碼的 server。
        // 預設 abab123 —— server 端的預設值也是同一個，所以「兩邊都不改」就能直接連上，
        // 而且密碼機制是**啟用**的（不是空密碼放行）。要公開的 server 請兩邊都改掉。
        // ⚠️ MVP 階段這只是個門檻，不是認證：playerId 完全由 client 自稱、連線沒有加密。
        //    只在 LAN／信任的朋友之間用，不要開在公網（見 server/README.md）。
        public static string serverPassword = DefaultServerPassword;

        /// <summary>
        /// 預設進站密碼。**指向共用的 <see cref="Sdo.Net.NetLimits.DefaultServerPassword"/>** ——
        /// server 端的 <c>ServerOptions.DefaultPassword</c> 也是指同一個常數,
        /// 所以「改了一邊忘了另一邊」在結構上就不可能發生。
        /// </summary>
        public const string DefaultServerPassword = Sdo.Net.NetLimits.DefaultServerPassword;
        // 缺歌時要不要自動從伺服器下載。true＝座位玩家自動下載（旁觀者一律不自動下載）。
        /// <summary>
        /// 連線用的 token(公網 server 才需要)。空 = 不帶。
        ///
        /// 🔴 這與 <see cref="serverPassword"/> 不同:密碼是「大家共用的一道門」,token 是
        /// **「server 認得的你」** —— 啟用之後 server 用它決定你是誰,而不是信 client 自稱的 playerId。
        /// 開在公網的 server 應該要求 token(見 server/README.md)。
        /// </summary>
        public static string serverToken = "";

        /// <summary>
        /// 用 TLS 連線(server 要有 <c>--tls-cert</c>)。**開在公網一定要開。**
        /// 不開的話密碼、token、聊天內容全部是明文 —— 同一個網路上的人看得到。
        /// </summary>
        public static bool serverTls;

        /// <summary>
        /// 釘選的 server 憑證指紋(SHA-256 hex;server 開機會印出來,冒號/空白可留)。
        ///
        /// 🔴 **自簽憑證一定要填這個。** 自簽沒有 CA 背書 → 一般驗證必定失敗;填了指紋之後
        /// client 就只認「指紋一模一樣」的那張憑證,鏈結錯誤可以忽略。
        /// 留空 = 走一般的 CA 驗證(有正式憑證、用網域名連的人適用)。
        /// 兩者都不成立時 client **連不上**,不會默默放行(見 <c>NetConnection.TryHandshake</c>)。
        /// </summary>
        public static string serverCertFingerprint = "";

        public static bool netAutoDownload = true;
        // 自動下載的單首歌大小上限（MB）。超過就不下載，只顯示缺歌，避免在慢速網路上卡很久。
        public static int netMaxDownloadMb = 200;

        /// <summary>
        /// 要走連線嗎? = <see cref="serverAddress"/> 有填東西。
        ///
        /// 這是**唯一**的離線/連線判斷點（<c>AppContext.Create</c> 用它決定要建 MockRoomService
        /// 還是 OnlineRoomService）。留空時整個連線層都不會被建起來，單機體驗一字不動。
        /// </summary>
        public static bool OnlineEnabled => !string.IsNullOrWhiteSpace(serverAddress);

        public const string FileName = "config.ini";

        /// <summary>config.ini 的完整路徑：**全域一份**，放在存檔層 <c>DATA/PROFILE/</c>（＝<see cref="ProfileManager.Root"/>，
        /// 與 active.txt / settings.json 同層）。不隨 active user 改變 —— 設定不跟著使用者，只是位置在 PROFILE 資料夾。</summary>
        public static string FilePath
        {
            get
            {
                // 存檔根（含 SDO_DATA_ROOT / data_root.txt 覆寫）由 ProfileManager.Root（= SdoDataRoot.ProfileDir）決定，
                // 跟 settings.json / active.txt 同一層。理論上一定解析得到；萬一為空（極端測試情境）才退回舊的執行檔同層。
                var profileRoot = ProfileManager.Root;
                if (!string.IsNullOrEmpty(profileRoot)) return Path.Combine(profileRoot, FileName);
                return LegacyExePath;
            }
        }

        /// <summary>舊版位置：執行檔同一層（建置版＝exe 資料夾；Editor 下＝專案根「My project/」）。只用於開機時把
        /// 舊 config.ini 一次性搬進 <see cref="FilePath"/>（PROFILE），不再是實際讀寫位置。</summary>
        public static string LegacyExePath
        {
            get
            {
                // 建置版 Application.dataPath = "<exe 同層>/<Product>_Data" → 其上一層就是 exe 所在資料夾。
                // Editor 下 dataPath = ".../My project/Assets" → 上一層 = "My project"。
                string dir;
                try { dir = Directory.GetParent(Application.dataPath).FullName; }
                catch { dir = Application.dataPath; }
                return Path.Combine(dir, FileName);
            }
        }

        /// <summary>讀 config.ini（全域，放在 DATA/PROFILE/；不存在就用內建預設並寫一份範本）。開機第一個呼叫
        /// （<see cref="SettingsBootstrap"/>，要在 <see cref="KeyMap.Load"/> 與 <see cref="ProfileManager.Boot"/> 之前，
        /// 它們都要拿這裡解析出來的值）；換 active user **不需要**重讀。
        ///
        /// 同時做三個一次性搬遷（搬完把舊檔刪掉，之後每次開機都只讀 config.ini 一個檔）：舊位置的 config.ini
        /// （per-user <c>DATA/PROFILE/&lt;id&gt;/</c> 優先，其次執行檔同層）、舊 <c>settings.json</c> → <c>[Option]</c>、
        /// 舊 <c>active.txt</c> → <c>[Profile] activeId</c>。</summary>
        public static void Load()
        {
            try
            {
                bool dirty = false;   // 有搬遷/補欄位 → 收尾要重寫一次 config.ini
                bool movedLegacyIni = false;

                if (File.Exists(FilePath))
                {
                    // 新位置（DATA/PROFILE/config.ini）已就緒 → 正常讀。
                    string text = File.ReadAllText(FilePath);
                    ParseInto(text);
                    // schema 升級：舊版存的 config.ini 可能缺這版新增的 key（AdditionalSongFolders / AddonFolder /
                    // opt_collapseShortHolds / SongUiAlpha…）。缺了就在收尾（if (dirty) Save()）補寫一次 ——
                    // 讓新 key 以預設值出現在檔案裡可手改，舊 key 既有值照留。
                    if (IsMissingCurrentKey(text)) dirty = true;
                }
                else
                {
                    // 新位置還沒有 → 找舊檔一次性搬進來：舊 per-user（DATA/PROFILE/<id>/config.ini）才是玩家實際在用的那份，
                    // 優先；沒有再看舊全域（執行檔同層）。搬完寫進新位置並把舊檔刪掉，之後每次開機都只走上面那條。
                    string legacy = FindProfileConfig() ?? FindLegacyExeConfig();
                    if (legacy != null)
                    {
                        ParseInto(File.ReadAllText(legacy));
                        movedLegacyIni = true;
                        Debug.Log($"[RoomConfig] moved legacy config.ini -> {FilePath}");
                    }
                    dirty = true;   // 第一次：在 DATA/PROFILE 留一份可編輯的範本
                }
                Sanitize();

                if (MigrateLegacyMmdEnabled()) dirty = true;   // 重寫一次，之後檔案裡不再有 mmdEnabled

                // ---- 一次性併入舊的 settings.json（同一組值以前存兩份；沒有舊檔就用內建預設）----
                var legacyJson = DisplaySettingsManager.ReadLegacyJson();
                if (!hasOption)
                {
                    CaptureOptionFrom(legacyJson ?? new GameSettings());
                    dirty = true;
                }
                else if (!hasOptUiScale && legacyJson?.display != null)
                {
                    optUiScale = legacyJson.display.uiScale;   // config.ini 有 [Option] 但還沒這個欄位 → 從舊 json 撿
                    dirty = true;
                }

                // ---- 一次性撿回舊的 active.txt（登入哪個角色）：值先放進 legacy 欄位，隨後由 ProfileDefaults.Load()
                //      一起搬進 DATA/PROFILE/profile.json（見 SettingsBootstrap 的呼叫順序）。----
                if (string.IsNullOrEmpty(legacyActiveId))
                    legacyActiveId = ProfileManager.ReadLegacyActiveId() ?? "";

                // config.ini 還帶著舊的 [Profile] 區 → 重寫一次（Serialize 已經不輸出該區），值由 ProfileDefaults 接手。
                if (hasLegacyProfileKeys) dirty = true;
                // 同理：舊檔沒有 combo/判定文字大小鍵 → 補寫一次，不然使用者在檔案裡找不到可改的鍵。
                if (!hasTextScaleKeys) dirty = true;
                if (!hasTextAlphaKeys) dirty = true;   // 透明度鍵比大小鍵晚加，只有大小鍵的檔一樣要補寫
                if (!hasTextPopKeys) dirty = true;     // 彈跳倍率鍵又更晚加，同理
                // 舊檔只有語意相反的 opt_disableBombs（值已在 ParseInto 反過來搬進 optSongBombs）→ 重寫一次，
                // 把它換成 opt_songBombs，之後檔案裡不再有舊鍵。
                if (!hasSongBombsKey) dirty = true;
                if (!hasScrollBaseBpmKey) dirty = true;// note 速度基準 BPM 是最晚加的，舊檔一律補寫一次

                _loaded = true;        // 一定要在下面那個 Save() 之前 —— 補寫新 key 是合法的寫入
                _loadedPath = FilePath;
                if (dirty) Save();
                if (movedLegacyIni) DeleteLegacyConfigs();      // 舊 per-user + 執行檔同層的 config.ini（內容已寫進新位置）
                DisplaySettingsManager.DeleteLegacyJson();      // 舊 settings.json（內容已在 [Option]）
                // 舊 active.txt 由 ProfileDefaults.Load() 收尾刪除（它才是 activeId 現在的落地處）。
                // [Option] 套回 GameSettings 已移到 DisplaySettingsManager.ApplyDisplay()（SettingsBootstrap 隨後呼叫）。
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RoomConfig] load failed, using defaults: {e.Message}");
                Sanitize();
                // 讀失敗也算「試過了」:否則後面任何一次 Save() 都會被守門擋掉,玩家連改設定都存不進去。
                _loaded = true;
                _loadedPath = FilePath;
            }
        }

        /// <summary>目前 schema（<see cref="Serialize"/> 會寫出的 key 全集）裡，是否有 key 不在給定的 INI 文字內。
        /// 用來偵測「舊版存的 config.ini 缺這版新增的 key」→ <see cref="Load"/> 會補寫一次讓新 key 出現。
        /// 純函式（只讀字串；<c>Serialize()</c> 只拿來抽 key 清單，值不影響結果）。</summary>
        public static bool IsMissingCurrentKey(string fileText)
        {
            var have = new System.Collections.Generic.HashSet<string>(KeysIn(fileText), StringComparer.Ordinal);
            foreach (var k in KeysIn(Serialize())) if (!have.Contains(k)) return true;
            return false;
        }

        // 取一份 INI 文字裡所有 "key=" 的 key（略過空行/註解/區段標頭）。純函式。
        private static System.Collections.Generic.List<string> KeysIn(string ini)
        {
            var keys = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(ini)) return keys;
            foreach (var raw in ini.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq > 0) keys.Add(line.Substring(0, eq).Trim());
            }
            return keys;
        }

        /// <summary>找一份殘留的舊 per-user config.ini：優先 active user，其次 PROFILE 下第一個找到的（只看 &lt;id&gt; 子資料夾，
        /// 不會誤抓 PROFILE 根的新全域檔）。沒有則 null。</summary>
        private static string FindProfileConfig()
        {
            try
            {
                var active = ProfileManager.ActiveDir;
                if (!string.IsNullOrEmpty(active))
                {
                    var p = Path.Combine(active, FileName);
                    if (File.Exists(p)) return p;
                }
                var root = ProfileManager.Root;
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                    foreach (var dir in Directory.GetDirectories(root))   // 只列子資料夾 → PROFILE 根的 config.ini 不在其中
                    {
                        var p = Path.Combine(dir, FileName);
                        if (File.Exists(p)) return p;
                    }
            }
            catch { /* 找不到就算了，用預設 */ }
            return null;
        }

        /// <summary>找舊全域位置（執行檔同層）的 config.ini。沒有、或它其實就是新位置（極端 fallback 情形）則 null。</summary>
        private static string FindLegacyExeConfig()
        {
            try
            {
                var p = LegacyExePath;
                if (File.Exists(p) && !SamePath(p, FilePath)) return p;
            }
            catch { }
            return null;
        }

        /// <summary>移除殘留的舊位置 config.ini（每次 <see cref="Load"/> 尾端呼叫，冪等）：PROFILE/&lt;id&gt;/config.ini
        /// （per-user，已停用、不再讀取）+ 執行檔同層的舊全域檔。新位置 <see cref="FilePath"/> 有 SamePath 守門，不會被刪。</summary>
        private static void DeleteLegacyConfigs()
        {
            try
            {
                var root = ProfileManager.Root;
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        var p = Path.Combine(dir, FileName);
                        if (File.Exists(p)) { File.Delete(p); Debug.Log($"[RoomConfig] removed per-user {p}"); }
                    }
            }
            catch (Exception e) { Debug.LogWarning($"[RoomConfig] per-user cleanup failed: {e.Message}"); }

            try
            {
                var exe = LegacyExePath;
                if (File.Exists(exe) && !SamePath(exe, FilePath)) { File.Delete(exe); Debug.Log($"[RoomConfig] removed legacy {exe}"); }
            }
            catch (Exception e) { Debug.LogWarning($"[RoomConfig] legacy cleanup failed: {e.Message}"); }
        }

        /// <summary>兩個路徑是否指同一個檔（大小寫不敏感、正規化後比較）；任一失敗保守回 false。</summary>
        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>把目前的值寫回 config.ini（附中文註解）。寫在 DATA/PROFILE/ 下（開機時該資料夾已由 ProfileManager.Boot
        /// 建好，這裡再保險確保一次，供 OPTION 保存等較晚的呼叫）。</summary>
        public static void Save()
        {
            // 🔴 沒 Load 過就不准寫。這些欄位是 static 的,而 Save 是「把現在的欄位值整份寫出去」——
            // 在 Load 之前呼叫就會把**整個 config.ini 換成內建預設值**,玩家的設定全部消失。
            // 實際踩過:開發連線功能時 serverAddress 被寫回空字串 → 遊戲默默退回單機模式,
            // 而症狀(「兩台怎麼看不到彼此」)完全指不到根因。寧可不存,也不要存錯。
            if (!_loaded)
            {
                Debug.LogWarning("[RoomConfig] Save() 在 Load() 之前被呼叫 → 不寫檔"
                                 + "(否則 config.ini 會被整份換成預設值)");
                return;
            }
            var target = FilePath;
            // 🔴 只准寫回 Load() 讀進來的那個檔 —— 見 _loadedPath 的註解(踩過兩次的坑)。
            if (!string.IsNullOrEmpty(_loadedPath)
                && !string.Equals(target, _loadedPath, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning("[RoomConfig] Save() 想寫 " + target + ",但這份設定是從 " + _loadedPath
                                 + " 讀進來的 → 不寫檔(避免把別的根的值蓋到玩家的 config.ini)");
                return;
            }
            try
            {
                var path = target;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, Serialize(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogError($"[RoomConfig] save failed: {e.Message}");
            }
        }

        /// <summary><see cref="Load"/> 有跑過嗎?<see cref="Save"/> 用它當守門(見那邊的註解)。</summary>
        private static bool _loaded;

        /// <summary>測試用:讓「Load 之前不准 Save」的守門能在單元測試裡被驗證與重置(正式流程別碰)。</summary>
        public static bool LoadedForTests { get { return _loaded; } set { _loaded = value; if (!value) _loadedPath = null; } }

        /// <summary>
        /// <see cref="Load"/> 實際讀的那個檔案路徑。<see cref="Save"/> **只准寫回這裡**。
        ///
        /// 🔴 為什麼要這條不變式:這些欄位是 static 的,而 <see cref="FilePath"/> 會跟著
        /// <see cref="ProfileManager.Root"/> 跑。任何「先把 Root 指到暫存目錄讀一份、之後又把 Root 還原」的
        /// 流程(測試就是這樣做的)都會讓後面某一次 Save() 把**暫存那份的值**寫進玩家真正的 config.ini。
        /// 實際踩過兩次:serverAddress 被寫成空字串 → 遊戲默默退回單機模式,而症狀
        /// (「兩台看不到彼此」)完全指不到根因,連房號都還是 5 位數看不出差別。
        /// 「寫回讀進來的那個檔」把整個 bug class 關掉:要存到別的根,就得先在那個根 Load()。
        /// </summary>
        private static string _loadedPath;

        /// <summary>把一份 INI 文字解析進靜態欄位（純函式：不碰檔案）。未出現的 key 保留原值。</summary>
        public static void ParseInto(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (key.StartsWith("opt_")) hasOption = true;   // 檔案帶 [Option] → 不必再去撿舊 settings.json
                switch (key)
                {
                    // 舊 [Profile] 區：只讀不寫，開機時給 ProfileDefaults 搬進 DATA/PROFILE/profile.json（見 Load）。
                    case "activeId": legacyActiveId = val; hasLegacyProfileKeys = true; break;
                    case "familyName": legacyFamilyName = val; hasLegacyProfileKeys = true; break;
                    case "familyEmblem": legacyFamilyEmblem = val; hasLegacyProfileKeys = true; break;
                    case "playerLevel": legacyPlayerLevel = val; hasLegacyProfileKeys = true; break;
                    // [Net]：大小寫敏感,要與 Serialize 寫出的 key 一字不差
                    case "serverAddress": serverAddress = val; break;
                    case "serverPort": serverPort = ParseInt(val, serverPort); break;
                    case "serverPassword": serverPassword = val; break;
                    case "serverToken": serverToken = val; break;
                    case "serverTls": serverTls = ParseBool(val, serverTls); break;
                    case "serverCertFingerprint": serverCertFingerprint = val; break;
                    case "netAutoDownload": netAutoDownload = ParseBool(val, netAutoDownload); break;
                    case "netMaxDownloadMb": netMaxDownloadMb = ParseInt(val, netMaxDownloadMb); break;
                    case "speedSteps": speedSteps = ParseFloatList(val); break;
                    case "defaultSpeed": defaultSpeed = ParseFloat(val, defaultSpeed); break;
                    case "defaultNoteType": defaultNoteType = ParseInt(val, defaultNoteType); break;
                    case "defaultTeam": defaultTeam = ParseInt(val, defaultTeam); break;
                    case "defaultDropDirection": defaultDropDirection = ParseInt(val, defaultDropDirection); break;
                    case "defaultGameMode": defaultGameMode = ParseInt(val, defaultGameMode); break;
                    case "defaultScene": defaultScene = ParseInt(val, defaultScene); break;
                    case "scrollBaseBpm": scrollBaseBpm = ParseFloat(val, scrollBaseBpm); hasScrollBaseBpmKey = true; break;
                    case "judgeLevel": judgeLevel = ParseInt(val, judgeLevel); break;
                    case "globalOffsetMs": globalOffsetMs = ParseFloat(val, globalOffsetMs); break;
                    case "judgeOffsetY": judgeOffsetY = ParseFloat(val, judgeOffsetY); break;
                    case "rankBasedFormation": rankBasedFormation = ParseBool(val, rankBasedFormation); break;
                    case "LoadExternalSongs": loadExternalSongs = ParseBool(val, loadExternalSongs); break;
                    case "AdditionalSongFolders": additionalSongFolders = ParseStringList(val); break;
                    case "AddonFolder": addonFolder = NormalizeFolder(val); break;
                    case "SongUiAlpha": songUiAlpha = ParseFloat(val, songUiAlpha); break;
                    case "DifficultyCalc": difficultyCalc = val; break;
                    case "comboTextScale": comboTextScale = ParseFloat(val, comboTextScale); hasTextScaleKeys = true; break;
                    case "judgeTextScale": judgeTextScale = ParseFloat(val, judgeTextScale); hasTextScaleKeys = true; break;
                    case "comboTextAlpha": comboTextAlpha = ParseFloat(val, comboTextAlpha); hasTextAlphaKeys = true; break;
                    case "judgeTextAlpha": judgeTextAlpha = ParseFloat(val, judgeTextAlpha); hasTextAlphaKeys = true; break;
                    case "comboTextPop": comboTextPop = ParseFloat(val, comboTextPop); hasTextPopKeys = true; break;
                    case "judgeTextPop": judgeTextPop = ParseFloat(val, judgeTextPop); hasTextPopKeys = true; break;
                    // [Mmd]
                    // 淘汰的總開關：只讀進來給 Load 搬進 mmdModel（見 mmdModelNone），Serialize 已經不輸出它。
                    case "mmdEnabled": legacyMmdEnabled = ParseBool(val, legacyMmdEnabled); hasMmdEnabledKey = true; break;
                    case "mmdModel": mmdModel = val; break;
                    case "mmdShowOthers": mmdShowOthers = ParseBool(val, mmdShowOthers); break;
                    case "mmdLilToon": mmdLilToon = ParseBool(val, mmdLilToon); break;
                    case "mmdToon": mmdToon = ParseBool(val, mmdToon); break;
                    case "mmdOutline": mmdOutline = ParseBool(val, mmdOutline); break;
                    case "mmdSphere": mmdSphere = ParseBool(val, mmdSphere); break;
                    case "mmdPhysics": mmdPhysics = ParseBool(val, mmdPhysics); break;
                    case "mmdAim": mmdAim = ParseBool(val, mmdAim); break;
                    case "mmdRootMotion": mmdRootMotion = ParseBool(val, mmdRootMotion); break;
                    case "mmdFlipV": mmdFlipV = ParseBool(val, mmdFlipV); break;
                    case "mmdGravity": mmdGravity = ParseFloat(val, mmdGravity); break;
                    case "mmdStiffness": mmdStiffness = ParseFloat(val, mmdStiffness); break;
                    case "mmdColliderScale": mmdColliderScale = ParseFloat(val, mmdColliderScale); break;
                    case "mmdScale": mmdScale = ParseFloat(val, mmdScale); break;
                    case "mmdShareModel": mmdShareModel = ParseBool(val, mmdShareModel); break;
                    // ---- OPTION 對話框設定 ----
                    case "opt_bgm": optBgm = ParseFloat(val, optBgm); break;
                    case "opt_music": optMusic = ParseFloat(val, optMusic); break;
                    case "opt_sfx": optSfx = ParseFloat(val, optSfx); break;
                    // opt_keys/opt_keysAux：舊檔殘留，只讀進來給 KeyMap 種 keymaps.ini（見 KeyMap.Load），不再寫出。
                    case "opt_keys": optKeys = val; break;
                    case "opt_keysAux": optKeysAux = val; break;
                    case "opt_dispW": optDispW = ParseInt(val, optDispW); break;
                    case "opt_dispH": optDispH = ParseInt(val, optDispH); break;
                    case "opt_uiScale": optUiScale = ParseFloat(val, optUiScale); hasOptUiScale = true; break;
                    case "opt_dispMode": optDispMode = val; break;
                    case "opt_vsync": optVsync = ParseInt(val, optVsync); break;
                    case "opt_lang": optLang = val; break;
                    case "opt_fullscreenFill": optFullscreenFill = ParseBool(val, optFullscreenFill); break;
                    case "opt_bloom": optBloom = ParseBool(val, optBloom); break;
                    case "opt_notesPanelLeft": optNotesPanelLeft = ParseBool(val, optNotesPanelLeft); break;
                    case "opt_effectCharacter": optEffectChar = ParseBool(val, optEffectChar); break;
                    case "opt_effectScene": optEffectScene = ParseBool(val, optEffectScene); break;
                    case "opt_cameraAuto": optCameraAuto = ParseBool(val, optCameraAuto); break;
                    case "opt_cameraFixed": optCameraFixed = ParseInt(val, optCameraFixed); break;
                    case "opt_callCardInGame": optCallCard = ParseBool(val, optCallCard); break;
                    case "opt_playFullSong": optPlayFullSong = ParseBool(val, optPlayFullSong); break;
                    case "opt_songSpeed": optSongSpeed = ParseBool(val, optSongSpeed); break;
                    case "opt_songBombs": optSongBombs = ParseBool(val, optSongBombs); hasSongBombsKey = true; break;
                    // 舊鍵（語意相反）：opt_disableBombs=1 表示「把炸彈拿掉」→ 搬成 opt_songBombs=0。只讀不寫，
                    // Load 會因為 hasSongBombsKey=false 重寫一次模板，之後檔案裡只剩新鍵。
                    case "opt_disableBombs": optSongBombs = !ParseBool(val, !optSongBombs); break;
                    case "opt_collapseShortHolds": optCollapseShortHolds = ParseBool(val, optCollapseShortHolds); break;
                    case "opt_danceIgnoreMiss": optDanceIgnoreMiss = ParseBool(val, optDanceIgnoreMiss); break;
                    case "opt_panelOpacity": optPanelOpacity = ParseFloat(val, optPanelOpacity); break;
                }
            }
        }

        /// <summary>
        /// 一次性搬遷：舊設定檔的 <c>mmdEnabled</c> 總開關 → <see cref="mmdModel"/> 的「(不使用)」選項。
        /// 回傳 true＝檔案要重寫一次（之後就不再有 mmdEnabled 這個鍵）。純函式（只動 static 欄位）。
        ///
        /// 關著的舊檔<b>不能</b>因為升級就突然變成 MMD —— 那是最嚇人的一種「改版自己動了我的設定」。
        /// 開著的舊檔則反過來：mmdModel 若剛好是「(不使用)」（例如預設值沒被寫過），要還原成「掃到的第一個」。
        /// </summary>
        public static bool MigrateLegacyMmdEnabled()
        {
            if (!hasMmdEnabledKey) return false;
            if (!legacyMmdEnabled) mmdModel = mmdModelNone;
            else if (IsMmdNone(mmdModel)) mmdModel = "";
            return true;
        }

        /// <summary>夾正非法值（空/壞的 speedSteps 回退內建；其餘夾範圍）。純函式。</summary>
        public static void Sanitize()
        {
            if (speedSteps == null || speedSteps.Length == 0)
                speedSteps = new[] { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 4.0f, 5.0f, 6.0f, 8.0f };
            if (defaultSpeed <= 0f) defaultSpeed = 2.5f;
            if (defaultNoteType < -1) defaultNoteType = -1;
            defaultTeam = Mathf.Clamp(defaultTeam, 0, 3);
            defaultDropDirection = Mathf.Clamp(defaultDropDirection, 0, 2);
            defaultGameMode = Mathf.Clamp(defaultGameMode, 0, 2);
            if (defaultScene < -1 || defaultScene > 30) defaultScene = -1;   // 只允許 -1(隨機) 或 0..30(可選場景 id)
            if (scrollBaseBpm <= 0f) scrollBaseBpm = 130f;                   // 0/負數＝音符不動或倒著走 → 回預設
            scrollBaseBpm = Mathf.Clamp(scrollBaseBpm, 30f, 400f);           // 30 = 慢到看不出在動；400 × 8 檔 = 5120px/s 已經飛出畫面
            judgeLevel = Mathf.Clamp(judgeLevel, 1, 9);                      // 精1~精8、9=JUSTICE
            globalOffsetMs = Mathf.Clamp(globalOffsetMs, -300f, 300f);       // 再大就不是延遲、是打錯拍了
            judgeOffsetY = Mathf.Clamp(judgeOffsetY, -200f, 200f);           // 設計 px（畫面高 600）
            if (serverToken == null) serverToken = "";
            if (additionalSongFolders == null) additionalSongFolders = new string[0];
            if (addonFolder == null) addonFolder = "";
            songUiAlpha = Mathf.Clamp01(songUiAlpha);                        // 外部歌分類面板不透明度 0..1
            difficultyCalc = (difficultyCalc ?? "").Trim().ToLowerInvariant();      // 只認 minacalc / osu
            if (difficultyCalc != "minacalc" && difficultyCalc != "osu")
                difficultyCalc = "minacalc";                                        // 打錯字/空的 → 回退預設
            comboTextScale = Mathf.Clamp(comboTextScale, 0.2f, 3f);          // 再小看不見、再大蓋滿整塊面板
            judgeTextScale = Mathf.Clamp(judgeTextScale, 0.2f, 3f);
            comboTextAlpha = Mathf.Clamp01(comboTextAlpha);                  // 0=完全隱藏（合法用法：不想看到連段字）
            judgeTextAlpha = Mathf.Clamp01(judgeTextAlpha);
            comboTextPop = Mathf.Clamp(comboTextPop, 1f, 4f);                // 1=不彈跳；>4 峰值會衝出面板
            judgeTextPop = Mathf.Clamp(judgeTextPop, 1f, 4f);
            if (optUiScale <= 0f) optUiScale = 1f;
            optUiScale = Mathf.Clamp(optUiScale, 0.5f, 3f);                  // 同 DisplaySettingsManager.Sanitize 的範圍
            // [Mmd]：範圍與開場設定面板的滑桿一致（面板一開就把值夾進滑桿範圍，兩邊不同會被夾掉玩家的設定）。
            mmdModel = (mmdModel ?? "").Trim();                              // 空＝掃到的第一個模型
            mmdGravity = Mathf.Clamp(mmdGravity, 0.05f, 8f);                 // 0＝布料不落下；>8 抖到爆
            mmdStiffness = Mathf.Clamp(mmdStiffness, 0.03f, 0.9f);           // 0＝完全軟趴；1＝硬到跟骨頭一樣不動
            mmdColliderScale = Mathf.Clamp(mmdColliderScale, 0.2f, 4f);      // 太小＝裙子穿過腿；太大＝裙子被撐飛
            mmdScale = Mathf.Clamp(mmdScale, 0.3f, 3f);                      // 模型大小（1＝自動對齊舞者身高）
            // 舊 [Profile] 區的搬遷暫存值：只去頭尾空白（前後空白會讓「留空＝沒設過」的判定失真）。
            legacyActiveId = ProfileDefaults.SanitizeActiveId(legacyActiveId);
            legacyFamilyName = (legacyFamilyName ?? "").Trim();
            legacyFamilyEmblem = (legacyFamilyEmblem ?? "").Trim();
            legacyPlayerLevel = (legacyPlayerLevel ?? "").Trim();
            // [Net]：位址一定要 Trim —— 手改設定檔很容易留下尾端空白，那會讓 OnlineEnabled
            // 誤判成「有填」然後拿一個含空白的主機名去解析,錯誤訊息會很莫名。
            serverAddress = (serverAddress ?? "").Trim();
            serverPassword = (serverPassword ?? "").Trim();
            // 指紋是複製貼上來的 —— 正規化到「64 個小寫 hex」,格式不對就當沒填(那會讓
            // 連線在握手時明確失敗,而不是靜默地放行一張不對的憑證)。
            serverCertFingerprint = Sdo.Net.TlsPinning.Normalize(serverCertFingerprint);
            serverPort = Mathf.Clamp(serverPort, 1, 65535);
            netMaxDownloadMb = Mathf.Clamp(netMaxDownloadMb, 1, 2048);
        }

        /// <summary>輸出帶註解的 INI 文字（純函式）。</summary>
        public static string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("# 本機設定總表 — 放在存檔資料夾 DATA/PROFILE/，純文字可手改，改完存檔下次開遊戲生效。\n");
            sb.Append("# [Net]=多人連線  [Room]=開房間右側面板預設  [Option]=遊戲內 OPTION 對話框的設定。\n");
            sb.Append("# 鍵位不在這個檔：4 鍵鍵位與遊玩功能鍵（換鏡頭/加減速/打拍音/Auto…）在同層的 keymaps.ini。\n");
            sb.Append("# 角色資料也不在這個檔：登入哪個角色、家族/等級的預設值在同層的 profile.json（每個角色自己的\n");
            sb.Append("# 設定與經驗值則在 DATA/PROFILE/<8位數id>/profile.json，個人資料頁改的家族/等級就寫在那裡）。\n");

            sb.Append("[Net]\n");
            sb.Append("# 多人連線。★ serverAddress 是總開關：留空＝純單機（與加連線之前完全一樣）。\n");
            sb.Append("# 填了才會去連；按登入連不上會留在單機（原因寫在 log），不會卡住。\n");
            sb.Append("# 伺服器位址：IP 或主機名（例如 192.168.1.10 或 dance.example.com）。\n");
            sb.Append("serverAddress=").Append(serverAddress ?? "").Append('\n');
            sb.Append("# 伺服器 port（1~65535）。\n");
            sb.Append("serverPort=").Append(serverPort).Append('\n');
            sb.Append("# 進站密碼：要與 server 的 --password 一致才連得上。留空＝連到沒設密碼的 server。\n");
            sb.Append("# 預設 ").Append(DefaultServerPassword).Append(" —— server 端預設值也是同一個，兩邊都不改就能直接連上。\n");
            sb.Append("# ⚠️ MVP 階段這只是門檻不是認證（身分由 client 自稱、連線沒加密）——\n");
            sb.Append("#    只在 LAN／信任的朋友之間用，不要開在公網。\n");
            sb.Append("serverPassword=").Append(serverPassword ?? "").Append('\n');
            sb.Append("# 公網伺服器的 token(空=不帶)。與密碼不同:密碼是大家共用的一道門,\n");
            sb.Append("# token 是「伺服器認得的你」—— 啟用後身分由伺服器依 token 決定,不再信本機自稱的角色 id。\n");
            sb.Append("serverToken=").Append(serverToken ?? "").Append('\n');
            sb.Append("# 用 TLS 加密連線（1=開 0=關）。伺服器要有 --tls-cert 才開得起來。\n");
            sb.Append("# ★ 開在公網一定要開:不開的話密碼、token、聊天內容全部是明文。\n");
            sb.Append("serverTls=").Append(B(serverTls)).Append('\n');
            sb.Append("# 釘選的伺服器憑證指紋（SHA-256；伺服器開機會印出來，冒號/空白可留）。\n");
            sb.Append("# ★ 自簽憑證一定要填 —— 自簽沒有 CA 背書，一般驗證必定失敗。填了之後只認這張憑證。\n");
            sb.Append("#   留空＝走一般 CA 驗證（有正式憑證、用網域名連的人適用）。兩者都不成立時連不上。\n");
            sb.Append("serverCertFingerprint=").Append(serverCertFingerprint ?? "").Append('\n');
            sb.Append("# 缺歌時自動從伺服器下載（1=開 0=關）。旁觀者一律不自動下載。\n");
            sb.Append("netAutoDownload=").Append(B(netAutoDownload)).Append('\n');
            sb.Append("# 自動下載的單首歌上限（MB）。超過只顯示缺歌，避免在慢速網路上卡很久。\n");
            sb.Append("netMaxDownloadMb=").Append(netMaxDownloadMb).Append('\n');

            sb.Append('\n').Append("[Room]\n");
            sb.Append("# 速度可選清單（逗號分隔，要加/減檔位直接改）\n");
            sb.Append("speedSteps=").Append(FloatListToString(speedSteps)).Append('\n');
            sb.Append("# 預設速度（會對齊到上面最接近的檔位）。玩家在房間選了會寫回這裡\n");
            sb.Append("defaultSpeed=").Append(defaultSpeed.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("# 預設 note 種類(hit-effect)：-1=隨機，>=0=指定第幾種\n");
            sb.Append("defaultNoteType=").Append(defaultNoteType).Append('\n');
            sb.Append("# 預設組隊：0=A 1=B 2=C 3=自由\n");
            sb.Append("defaultTeam=").Append(defaultTeam).Append('\n');
            sb.Append("# 預設掉落方式：0=向上 1=向下 2=傾斜\n");
            sb.Append("defaultDropDirection=").Append(defaultDropDirection).Append('\n');
            sb.Append("# 預設模式：0=自由模式 1=普通模式 2=ShowTime模式\n");
            sb.Append("defaultGameMode=").Append(defaultGameMode).Append('\n');
            sb.Append("# 預設場景：-1=隨機，0..30=指定場景 id（步行街=0 … 卡通公路=30）。玩家在選歌選了會寫回這裡\n");
            sb.Append("defaultScene=").Append(defaultScene).Append('\n');
            sb.Append("# note 下落速度的基準 BPM（範圍 30~400，預設 130）：畫面速度 = 這個值 × 速度檔位 × 1.6 px/s。\n");
            sb.Append("# 每首歌共用同一個基準（同一檔位在每首歌一樣快）；譜面自己的 BPM 變化/SV 仍會在曲內相對加減速。\n");
            sb.Append("# 調大＝所有歌、所有檔位一起變快（例：130→160 全部快 23%），嫌整體太快就往下調。\n");
            sb.Append("scrollBaseBpm=").Append(scrollBaseBpm.ToString("0.##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("# 判定精度（StepMania 的「精N」）：1~8，9=JUSTICE。數字越大越嚴格。\n");
            sb.Append("# 以精4 為基準窗（Perfect ±45 / Cool ±90 / Bad ±135 / Miss ±180 ms）乘該精度係數：\n");
            sb.Append("#   精1=1.50 精2=1.33 精3=1.16 精4=1.00 精5=0.84 精6=0.66 精7=0.50 精8=0.33 JUSTICE=0.20\n");
            sb.Append("#   例：精2 → Perfect ±59.9 / Cool ±119.7 / Bad ±179.6 / Miss ±239.4 ms\n");
            sb.Append("judgeLevel=").Append(judgeLevel).Append('\n');
            sb.Append("# 全域判定 offset（毫秒）：正 = 判定時間往後（整體打太早就調正的）。預設 0。\n");
            sb.Append("# 機器的音訊延遲**已經自動補掉了**（DSP 緩衝、驅動延遲、打拍音的前導靜音）→ 這裡只留給個人偏好/跨機微調。\n");
            sb.Append("# 要調就用譜面編輯器的「打拍測試」(F2)，**聽節拍器打**（看著 note 打量不到音訊延遲），它會給建議值。\n");
            sb.Append("globalOffsetMs=").Append(globalOffsetMs.ToString("0.##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("# 判定線視覺偏移（設計 px，畫面高 600）：完美時機的音符會落在受擊線 + 這個位移處。0 = 正中受擊線。\n");
            sb.Append("judgeOffsetY=").Append(judgeOffsetY.ToString("0.##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("# 依名次調整站位（多人同場）：1=開(預設，官方行為) 0=關。\n");
            sb.Append("# 開＝比賽中即時第一名會滑到隊形的中央前排（導播鏡頭錨定的那一格），被擠掉的人退回原位。\n");
            sb.Append("# 關＝整場固定站在房間座位順序的位置，名次再怎麼變都不換位。純視覺，不影響判定/分數/名次。\n");
            sb.Append("rankBasedFormation=").Append(B(rankBasedFormation)).Append('\n');
            sb.Append("# 外部歌曲（osu/StepMania/Malody）載入總開關：1=載入(預設) 0=完全不碰。\n");
            sb.Append("# 關掉後：開機不掃歌資料夾、不建 ADDON 那幾個資料夾、開場的載入進度畫面不出現，\n");
            sb.Append("# 選歌畫面的「資料夾」頁籤也不再開分類瀏覽面板 —— 只剩官方歌。下面四個設定都只在開著時有意義。\n");
            sb.Append("LoadExternalSongs=").Append(B(loadExternalSongs)).Append('\n');
            sb.Append("# 額外歌曲資料夾（osu/StepMania），仿 StepMania：分號分隔多個絕對路徑，例如 D:/test;E:/songs。\n");
            sb.Append("# 每個路徑都當成一個 Songs 根：底下第一層=分類(group)，再下一層=各首歌資料夾。\n");
            sb.Append("# 預設的 <ADDON>/SONG 一律自動掃描（舊的 exe 同層 Songs/ 仍相容），不需列在這。\n");
            sb.Append("AdditionalSongFolders=").Append(StringListToString(additionalSongFolders)).Append('\n');
            sb.Append("# 外掛(ADDON)根目錄：預設空=DATA/ADDON。想把整包外掛（SONG/NOTESKIN/THEME/MODEL）放別處就填絕對路徑，\n");
            sb.Append("# 例如 AddonFolder=D:/SdoAddon（該資料夾底下就是 SONG 等子夾）。\n");
            sb.Append("AddonFolder=").Append(addonFolder ?? "").Append('\n');
            sb.Append("# 選歌畫面「分類瀏覽」浮動面板（外部歌資料夾清單）的不透明度：0=全透明、1=不透明。預設 0.6。\n");
            sb.Append("SongUiAlpha=").Append(songUiAlpha.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("# 外部歌難度用哪套算：minacalc=Etterna MinaCalc 的 MSD 換算等級(預設)，osu=osu!mania 星數等級。\n");
            sb.Append("# 選了哪套就整體都照那套：顯示的數字、隨機難度的範圍、哪張譜排進簡單/普通/困難，全部一致。\n");
            sb.Append("# 只影響 osu/StepMania/Malody 這類要自己算難度的外部譜；.gn（官方 DATA/MUSIC 或外部歌包）一律保留原難度。\n");
            sb.Append("DifficultyCalc=").Append(difficultyCalc ?? "minacalc").Append('\n');
            sb.Append("# 遊戲中文字的整體大小比例（1.0 = 官方原尺寸，範圍 0.2~3.0）。純顯示，不影響判定與分數。\n");
            sb.Append("#   comboTextScale = COMBO 字樣＋連段數字（整組等比例縮放，字距不會散開）\n");
            sb.Append("#   judgeTextScale = PERFECT / COOL / BAD / MISS 判定字樣\n");
            sb.Append("comboTextScale=").Append(comboTextScale.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("judgeTextScale=").Append(judgeTextScale.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("# 同兩組文字的不透明度（1.0=全不透明 0=完全看不見，範圍 0.0~1.0）。預設 0.6：字就疊在音符板上，\n");
            sb.Append("# 淡一點才不會擋住下落中的音符。判定字不會淡出(顯示完直接消失)，這裡就是它的亮度。\n");
            sb.Append("comboTextAlpha=").Append(comboTextAlpha.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("judgeTextAlpha=").Append(judgeTextAlpha.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("# 打中時「彈跳」放到最大那一瞬間的倍率＝峰值大小 ÷ 靜止大小（官方 2.0＝彈到兩倍再收回，\n");
            sb.Append("# 1.0＝完全不彈跳，範圍 1.0~4.0）。收回速度是官方寫死的，這裡只調幅度。\n");
            sb.Append("comboTextPop=").Append(comboTextPop.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("judgeTextPop=").Append(judgeTextPop.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');

            // MMD 模型顯示。設定入口在開場設定面板的「MMD」分頁；改了不用重開，MmdAvatarSwap 每幀比對這些值。
            sb.Append('\n').Append("[Mmd]\n");
            sb.Append("# 把場上角色的身體換成 MMD 模型（.pmx）。動作仍由 SDO 的骨架驅動 → 跳的是同一套舞。\n");
            sb.Append("# 模型放 DATA/MODEL/<名稱>/*.pmx（開發樹：assets/MODEL/）；一個資料夾＝一個模型。\n");
            sb.Append("# 用哪個模型（＝ DATA/MODEL 底下的資料夾名）。").Append(mmdModelNone).Append("＝維持 SDO 原角色（預設）；留空＝掃到的第一個。\n");
            sb.Append("# 沒有另外的總開關：選了模型就是要用它。（舊版的 mmdEnabled 已在讀檔時搬進這個值。）\n");
            sb.Append("mmdModel=").Append(mmdModel ?? "").Append('\n');
            sb.Append("# 別人的 MMD 模型要不要顯示（1=顯示，預設）。與上面那個「我自己用哪個模型」互相獨立 ——\n");
            sb.Append("# 你可以自己維持 SDO 角色卻看得到別人的 MMD，也可以反過來。關掉＝別人一律是他的 SDO 穿搭，\n");
            sb.Append("# 而且完全不會去下載別人的模型（零流量、零磁碟）。\n");
            sb.Append("mmdShowOthers=").Append(B(mmdShowOthers)).Append('\n');
            sb.Append("# 著色（1=開 0=關）：卡通著色 / 描邊 / sphere 反光。\n");
            sb.Append("mmdLilToon=").Append(B(mmdLilToon)).Append('\n');
            sb.Append("mmdToon=").Append(B(mmdToon)).Append('\n');
            sb.Append("mmdOutline=").Append(B(mmdOutline)).Append('\n');
            sb.Append("mmdSphere=").Append(B(mmdSphere)).Append('\n');
            sb.Append("# 頭髮/裙擺的布料模擬總開關（1=開）。關掉最省效能：布料求解是建一隻 MMD 角色最貴的一段。\n");
            sb.Append("mmdPhysics=").Append(B(mmdPhysics)).Append('\n');
            sb.Append("# 布料手感：重力倍率 0.05~8、硬度 0.03~0.9（低＝被重力拉直垂下）、身體碰撞體半徑倍率 0.2~4\n");
            sb.Append("#（半徑太小裙子會穿過腿，太大會被撐飛）。模型資料夾裡若有 physics.ini，那份先套，這三個再乘上去。\n");
            sb.Append("mmdGravity=").Append(mmdGravity.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("mmdStiffness=").Append(mmdStiffness.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("mmdColliderScale=").Append(mmdColliderScale.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("mmdScale=").Append(mmdScale.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("# 動作重定向（1=開，預設）：aim＝用「骨頭指向」對齊手腳（關＝改用 world-delta 對照模式，姿勢會歪，\n");
            sb.Append("# 只在比對哪邊對時才關）；rootMotion＝根骨的位移（關＝人原地跳，不前進）。\n");
            sb.Append("mmdAim=").Append(B(mmdAim)).Append('\n');
            sb.Append("mmdRootMotion=").Append(B(mmdRootMotion)).Append('\n');
            sb.Append("# 貼圖 V 翻轉（1=開，預設）：PMX 的 UV 是 V 向下，Unity 要翻。某些模型的貼圖（領帶之類）要關掉才對。\n");
            sb.Append("mmdFlipV=").Append(B(mmdFlipV)).Append('\n');
            sb.Append("# 多人連線：把自己身上的模型上傳給 server，讓同房的人也看得到（1=分享，預設）。\n");
            sb.Append("# 關掉 → 別人看到的是你的 SDO 穿搭（你自己畫面上仍然是 MMD）。\n");
            sb.Append("# ⚠️ 網路上流通的 MMD 模型多半帶使用規約，有些明確禁止再配布 —— 這個開關就是為此存在的。\n");
            sb.Append("# 反過來，別人的模型只在 mmdShowOthers=1 時才下載。\n");
            sb.Append("mmdShareModel=").Append(B(mmdShareModel)).Append('\n');

            // OPTION 對話框（畫面/音效/鍵盤/遊戲）的全域設定。改完在遊戲內 OPTION 按「保存」也會寫回這裡。
            sb.Append('\n').Append("[Option]\n");
            sb.Append("# 音量 0.0~1.0（背景音樂 / 遊戲音樂 / 遊戲音效）\n");
            sb.Append("opt_bgm=").Append(optBgm.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("opt_music=").Append(optMusic.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("opt_sfx=").Append(optSfx.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("# 鍵位已搬到同層的 keymaps.ini（4 鍵打擊鍵位 + 遊玩中的功能鍵），這裡不再有 opt_keys。\n");
            sb.Append("# 視窗大小 / 顯示模式（Windowed|Fullscreen|Borderless）/ 垂直同步（0|1）/ UI 縮放 / 語言\n");
            sb.Append("opt_dispW=").Append(optDispW).Append('\n');
            sb.Append("opt_dispH=").Append(optDispH).Append('\n');
            sb.Append("opt_dispMode=").Append(optDispMode ?? "Windowed").Append('\n');
            sb.Append("opt_vsync=").Append(optVsync).Append('\n');
            sb.Append("opt_uiScale=").Append(optUiScale.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("opt_lang=").Append(optLang ?? "zh-TW").Append('\n');
            sb.Append("# 遊戲頁（1=開 0=關）：全屏填滿 / 泛光 / notes面板靠左 / 人物特效 / 場景特效 / 自動導播 / 呼叫卡 / 完奏模式 / 歌曲變速\n");
            sb.Append("opt_fullscreenFill=").Append(B(optFullscreenFill)).Append('\n');
            sb.Append("opt_bloom=").Append(B(optBloom)).Append('\n');
            sb.Append("opt_notesPanelLeft=").Append(B(optNotesPanelLeft)).Append('\n');
            sb.Append("opt_effectCharacter=").Append(B(optEffectChar)).Append('\n');
            sb.Append("opt_effectScene=").Append(B(optEffectScene)).Append('\n');
            sb.Append("opt_cameraAuto=").Append(B(optCameraAuto)).Append('\n');
            sb.Append("# 固定視角用哪一台（0~5，＝遊戲中 F2 循環的 6 台固定鏡頭；F2 切了會寫回這裡）\n");
            sb.Append("opt_cameraFixed=").Append(optCameraFixed).Append('\n');
            sb.Append("opt_callCardInGame=").Append(B(optCallCard)).Append('\n');
            sb.Append("opt_playFullSong=").Append(B(optPlayFullSong)).Append('\n');
            sb.Append("opt_songSpeed=").Append(B(optSongSpeed)).Append('\n');
            sb.Append("# 歌曲炸彈（1=照譜面原樣有雷，預設；0=開局載譜時把炸彈整顆拿掉）。炸彈不計分也不計 miss，拿掉不影響滿分。\n");
            sb.Append("opt_songBombs=").Append(B(optSongBombs)).Append('\n');
            sb.Append("# 無理短長條收成一般 note（短於 180BPM 16 分音符 ≈83ms 的 long note → note；1=開 預設 0=關）\n");
            sb.Append("# 只對外部轉檔譜（osu/StepMania/Malody）生效；官方 k.gn 與 .gn 歌曲包是原生譜，永遠照原樣打。\n");
            sb.Append("opt_collapseShortHolds=").Append(B(optCollapseShortHolds)).Append('\n');
            sb.Append("# 掉 miss 也照跳舞（1=開：跳舞完全不受 combo/miss 影響；0=關 預設＝官方玩法，斷 combo 且 combo≤30 會停舞）。\n");
            sb.Append("# 開著時連血量都不管：完奏模式血用完照樣跳到曲末（關著時血用完就回待機站著）。\n");
            sb.Append("opt_danceIgnoreMiss=").Append(B(optDanceIgnoreMiss)).Append('\n');
            sb.Append("# 面板透明度 0.0~1.6\n");
            sb.Append("opt_panelOpacity=").Append(optPanelOpacity.ToString("0.0##", CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        private static string B(bool v) => v ? "1" : "0";
        private static bool ParseBool(string s, bool fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            s = s.Trim().ToLowerInvariant();
            if (s == "1" || s == "true" || s == "yes" || s == "on") return true;
            if (s == "0" || s == "false" || s == "no" || s == "off") return false;
            return fallback;
        }

        /// <summary>把目前 <see cref="GameSettings"/>（settings.json 工作副本）的 OPTION 值抓進 RoomConfig 鏡像欄位，
        /// 供 <see cref="Save"/> 寫進 config.ini。OptionDlgModal 按「保存」時呼叫。純函式（不碰檔案）。</summary>
        public static void CaptureOptionFrom(GameSettings s)
        {
            if (s == null) return;
            if (s.audio != null) { optBgm = s.audio.bgm; optMusic = s.audio.gameMusic; optSfx = s.audio.sfx; }
            // 鍵位落地在 keymaps.ini（KeyMap.CaptureFrom）；這裡只留記憶體鏡像，供舊 settings.json 搬遷時種 keymaps.ini。
            if (s.keys != null)
            {
                optKeys = JoinKeys(s.keys.lane4);
                optKeysAux = JoinKeys(s.keys.lane4aux);
            }
            if (s.display != null)
            {
                optDispW = s.display.width; optDispH = s.display.height;
                optDispMode = s.display.displayMode; optVsync = s.display.vsync ? 1 : 0;
                optUiScale = s.display.uiScale;
            }
            optLang = s.language;
            if (s.gameplay != null)
            {
                var g = s.gameplay;
                optFullscreenFill = g.fullscreenFill; optBloom = g.bloom; optNotesPanelLeft = g.notesPanelLeft;
                optEffectChar = g.effectCharacter; optEffectScene = g.effectScene; optCameraAuto = g.cameraAuto;
                optCameraFixed = g.cameraFixed;
                optCallCard = g.callCardInGame; optPlayFullSong = g.playFullSong; optSongSpeed = g.songSpeed;
                optCollapseShortHolds = g.collapseShortHolds;
                optDanceIgnoreMiss = g.danceIgnoreMiss;
                optSongBombs = g.songBombs;
                optPanelOpacity = g.panelOpacity;
            }
            hasOption = true;
        }

        /// <summary>把 config.ini 的 OPTION 鏡像值套回 <see cref="GameSettings"/>（每帳號覆蓋裝置層）。開機/切帳號
        /// Load() 後呼叫（見 <see cref="Load"/>）。純函式（不碰檔案）。</summary>
        public static void ApplyOptionTo(GameSettings s)
        {
            if (s == null) return;
            if (s.audio == null) s.audio = new VolumeSettings();
            s.audio.bgm = optBgm; s.audio.gameMusic = optMusic; s.audio.sfx = optSfx;
            // 鍵位不從這裡套 —— 權威在 keymaps.ini，由 KeyMap.ApplyTo 接手（見 DisplaySettingsManager.Load）。
            if (s.display == null) s.display = new DisplaySettings();
            s.display.width = optDispW; s.display.height = optDispH;
            s.display.displayMode = optDispMode; s.display.vsync = optVsync != 0;
            s.display.uiScale = optUiScale;
            if (!string.IsNullOrEmpty(optLang)) s.language = optLang;
            if (s.gameplay == null) s.gameplay = new GameplaySettings();
            var g = s.gameplay;
            g.fullscreenFill = optFullscreenFill; g.bloom = optBloom; g.notesPanelLeft = optNotesPanelLeft;
            g.effectCharacter = optEffectChar; g.effectScene = optEffectScene; g.cameraAuto = optCameraAuto;
            g.cameraFixed = optCameraFixed;
            g.callCardInGame = optCallCard; g.playFullSong = optPlayFullSong; g.songSpeed = optSongSpeed;
            g.collapseShortHolds = optCollapseShortHolds;
            g.danceIgnoreMiss = optDanceIgnoreMiss;
            g.songBombs = optSongBombs;
            g.panelOpacity = optPanelOpacity;
        }

        private static string JoinKeys(string[] a)
        {
            if (a == null) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < a.Length; i++) { if (i > 0) sb.Append(','); sb.Append(a[i] ?? ""); }
            return sb.ToString();
        }

        // ---- small parse helpers ----
        private static float[] ParseFloatList(string s)
        {
            var parts = s.Split(',');
            var list = new System.Collections.Generic.List<float>(parts.Length);
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length == 0) continue;
                if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) list.Add(f);
            }
            return list.ToArray();
        }

        private static string FloatListToString(float[] a)
        {
            if (a == null || a.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < a.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(a[i].ToString("0.0##", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static float ParseFloat(string s, float fallback)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;

        private static int ParseInt(string s, int fallback)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

        /// <summary>Split a path list into folders — StepMania-style, semicolon-separated (<c>;</c>); comma is still
        /// accepted for back-compat. Each entry is trimmed, backslashes normalised to '/', empties dropped, and leading
        /// slashes in front of a Windows drive letter stripped (a config like <c>////D:/Songs</c> resolves to
        /// <c>D:/Songs</c>). Pure/testable — mirrors <see cref="ParseFloatList"/> for the AdditionalSongFolders key.</summary>
        public static string[] ParseStringList(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            var parts = s.Split(';', ',');   // '; ' preferred (a folder name can carry a ','), ',' kept for old configs
            var list = new System.Collections.Generic.List<string>(parts.Length);
            foreach (var p in parts)
            {
                var t = NormalizeFolder(p);
                if (t.Length > 0) list.Add(t);
            }
            return list.ToArray();
        }

        /// <summary>Clean one folder entry: trim, backslashes→'/', and strip leading slashes sitting in front of a
        /// Windows drive letter (so a StepMania-style <c>////D:/Songs</c> becomes <c>D:/Songs</c>). A UNC path
        /// (<c>//server/share</c>) is left untouched — its second segment isn't a <c>X:</c> drive.</summary>
        private static string NormalizeFolder(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            var t = p.Trim().Replace('\\', '/');
            int i = 0;
            while (i < t.Length && t[i] == '/') i++;
            if (i > 0 && i + 1 < t.Length && char.IsLetter(t[i]) && t[i + 1] == ':') t = t.Substring(i);
            return t.Trim();
        }

        private static string StringListToString(string[] a)
        {
            if (a == null || a.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < a.Length; i++) { if (i > 0) sb.Append(';'); sb.Append(a[i] ?? ""); }
            return sb.ToString();
        }
    }
}
