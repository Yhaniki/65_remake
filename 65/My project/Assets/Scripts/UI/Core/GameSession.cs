using Sdo.Settings;
using Sdo.UI.Catalog;
using Sdo.Shop;

namespace Sdo.UI.Core
{
    public enum Difficulty { Easy = 0, Normal = 1, Hard = 2 }

    /// <summary>Front-end session state carried across screens (local, offline v1).</summary>
    public sealed class GameSession
    {
        public string LocalPlayerId = "me";
        public string LocalPlayerName = "玩家001";
        public int Gender = 0;   // 本機角色性別：0=女(WOMAN) 1=男(MAN)。由 active profile 帶入（見 AppContext.CreateMock）。

        // 本機所屬家族名。沒有真正的家族系統：值來自 config.ini [Profile] familyName（或這個角色自己覆寫的那份，
        // 見 Sdo.Settings.ProfileFields），家族頻道即可正常運作（綠字 <家族>…）；
        // 清空（""）＝沒有家族 → 家族頻道送出顯示「你沒有家族」。房間可按 F3 在「有/沒有」之間切換（除錯用）。
        //
        // DemoGuildName 只是「設定裡什麼都沒填」時的示範值 —— 以前這裡是寫死的，於是玩家在 config.ini 設了家族名，
        // 頭上名牌換了、送上線的身分卻還是「熱舞家族」，兩邊對不起來。
        public const string DemoGuildName = "熱舞家族";
        public string GuildName = DemoGuildName;
        public bool HasGuild => !string.IsNullOrWhiteSpace(GuildName);

        public int CurrentRoomId = -1;

        // 房間左上角的所在位置標示 (DDRROOM servername / channelnum)。離線單機固定 1/1，顯示「自由練習場1 頻道1」。
        public int ServerNumber = 1;   // 自由練習場 N（伺服器/練習場編號）
        public int Channel = 1;        // 頻道 N

        // pending song/stage/noteskin selection
        public string SongGn;       // e.g. "sdom1435k.gn"
        public int SongFileId;
        public string SongTitle;
        public string SongArtist;
        public Difficulty Difficulty = Difficulty.Easy;

        /// <summary>
        /// 自由模式下**本機自己**挑的難度槽(0/1/2)。房主選歌選的是「哪一首」,難度則是每個人各挑各的
        /// (官方 DDRROOM 的 FMGameLevel:非房主那格會從「房主設置」換成「難度設置 ◄ EASY ►」)——
        /// 所以同一首歌可以每個人打不同難度的譜。
        ///
        /// 跟速度 / note 皮 / 掉落方向同一類:**個人偏好,不同步給別人**(見 NetRoomSettings 的註)。
        /// 只在自由模式且不是房主時才生效;規則見 <see cref="Sdo.UI.Services.FreeModeDifficulty"/>。
        /// </summary>
        public int FreeDifficulty = 0;

        // ---- external song (user Songs/ folder: osu / StepMania). Set at SongSelectScreen.OnConfirm from a scanned
        //      SongCatalog.Entry, resolved to the chosen difficulty's chart; consumed by FrontendApp.StartGameplay. ----
        public bool IsExternalSong;
        public string ExternalChartPath = "";   // absolute .osu / .sm / .gn path for the selected difficulty
        public int ExternalChartIndex;          // .sm #NOTES block index; .gn 的難度(0/1/2)；osu: 0
        public int ExternalChartFormat;         // 1=osu, 2=sm, 3=gn 歌曲包 (Sdo.Osu.SongFormat)
        public long ExternalChartSeed;          // .gn 的 LCG 解密金鑰（0 = 未知→退回共用 seed 池）
        public string ExternalDpsPath = "";     // .gn 歌曲包自帶的官方編舞；"" → 開局自己生一份
        public string ExternalAudioPath = "";   // absolute audio (ogg/mp3/wav); "" → silent
        public int ExternalLevel;                // chosen difficulty's LV (osu!mania 星數×7) → shown in gameplay too
        // 這首歌的身分（資料夾 + 資料夾內是哪一首）：外部歌沒有官方 .dps，開局時 ExternalDps 用它當種子生一份舞蹈、
        // 寫進歌曲資料夾並記在該資料夾的 sdoinfo.dat（同一首歌永遠生出同一支舞，且只生一次）。
        public string ExternalFolderPath = "";  // 歌曲資料夾（CD 圖／sdoinfo.dat／生成的 .dps 都放這）
        public string ExternalSongKey = "";     // 資料夾內的識別（"" = 該資料夾只有一首）
        // 🔴 生成舞蹈的 seed 是這個,不是資料夾名:缺歌傳檔會把歌放進 connect/<歌名 - 作者 [packId 前8碼]>/,
        // 兩邊的資料夾名不同 → 用資料夾名當 seed 會讓同一首歌在兩台生出完全不同的舞(見 Sdo.Game.ExternalDps)。
        public string ExternalPackId = "";      // 這首歌所在資料夾的跨電腦身分（"" = 算不出 → 退回資料夾名）
        // ---- 生成編舞要的「整首歌」資料：舞是一首歌一支，不能因為換難度就變另一支（見 Sdo.Osu.DanceInputs）----
        // 這首**歌**的 BPM（選歌顯示的那個）；<= 0 = 不知道 → 退回選到那張譜自己算出來的。
        public double ExternalSongBpm;
        // 這首歌**每個難度**的譜（空格子是 ""）：舞蹈長度＝所有難度的最早第一顆音符 → 最晚最後一顆。
        public string[] ExternalSongChartPaths = new string[0];
        public int[] ExternalSongChartIndices = new int[0];

        // 隨機難度選擇：確認時就抽好實際歌曲(SongGn/SongFileId/SongArtist)，但房間只顯示「隨機難度 X」標籤(SongTitle)，
        // 進遊戲才揭曉是哪首歌。重進選歌選單 → 直接回隨機 tab 的該區間。false = 一般（指定歌曲）選擇。
        public bool SongIsRandom;
        public int SongRandomRange;   // SongSelectScreen.RandRanges 索引（哪個難度區間）

        // ---- 房間**設定**的場景(房主在選歌對話框選的那個)。這三個值只有玩家自己改設定時才會動。----
        public string StageFolder = "SCN0009";
        public int StageId = 9;
        // true = 選的是「隨機場景」→ 房間第二層圖顯示 RANDOM，實際場景每一局開場才抽(見 RoundStageFolder)。
        // 預設 true：一開始還沒選歌，房間就顯示 random 場景。見 SongSelectScreen.ApplySceneToSession / RoomScreen。
        public bool StageRandom = true;

        // ---- 這一局**實際**跑的場景(隨機場景在按下「開始」那一刻抽出來的結果；線上是 server echo 的那個)。----
        //
        // 🔴 一定要跟上面的房間設定分開:以前是把抽出來的場景直接寫回 StageId/StageFolder 並把 StageRandom
        // 關掉,於是進遊戲那一瞬間房間 win2 的場景縮圖就從 RANDOM 變成抽到的那張(使用者回報的症狀),
        // 而且回房之後房間設定已經被改成那個具體場景 —— 下一局不再隨機,房主那台還會把 sceneRandom=false
        // 透過 NetRoomSettingsPublisher 推給 server,全房的縮圖跟著一起變。設定是設定,這一局的結果是結果。
        //
        // "" = 還沒解析(例如直接進 gameplay 的開發路徑)→ 退回 StageFolder(見 FrontendApp.StartGameplay)。
        public string RoundStageFolder = "";
        public int RoundStageId = -1;

        // 這一局實際用的個人隊形(0..2)。房間設定的 <see cref="Formation"/> 有第四個選項 3=隨機,
        // 一樣是開場那一刻才抽 —— 抽出來的值寫回設定的話,「隨機隊形」會在打完一局之後變成抽到的那一種
        // (房間面板的隊形下拉、線上推給 server 的房間設定都會跟著改)。-1 = 還沒解析 → gameplay 用 0。
        public int RoundFormationType = -1;

        public string NoteSkin = "NOTEIMAGE_5";

        // 商城 (shop): 衣櫃 + 錢包 + 裝備。單人離線 → 本地保存；起始給充足金額方便試玩。裝備狀態供 avatar 換裝
        // (AvatarOutfit.ResolveParts) 使用。見 [[sdo-shop-mode]] / ShopScreen。
        public readonly Wardrobe Wardrobe = new Wardrobe();

        // ---- 房間右側面板（DDRROOM win2）當下選的值。預設由 RoomDefaults(settings.json) 種入 SeedRoomDefaults()。----
        public float Speed = 2.5f;       // 下落速度倍率（對齊 RoomDefaults.speedSteps 的某一檔）
        public int NoteType = -1;        // note 種類(hit-effect)：-1=隨機, >=0=指定
        public int Team = 3;             // 組隊：0=A,1=B,2=C,3=自由
        public int DropDirection = 0;    // 掉落方式：0=向上,1=向下,2=傾斜

        // ROOMDLG room settings (single-player: stored locally).
        public int GameMode = 0;      // 0=自由模式, 1=普通模式, 2=ShowTime模式 (氣條/集氣 → ScreenGameplay.showtimeMode)
        public int Formation = 0;     // 0=基本, 1=扇形, 2=環線, 3=隨機
        public int LookerCount = 10;  // 旁觀人數 0..10

        public bool HasSong => !string.IsNullOrEmpty(SongGn);

        /// <summary>把房間面板的「當下值」種成 config.ini 的預設（速度/note/組隊/掉落/模式）。
        /// 在 AppContext 建立 session 時呼叫一次；玩家之後在房間裡改的值就蓋過這些預設。見 <see cref="RoomConfig"/>。</summary>
        public void SeedRoomDefaults()
        {
            Speed = NearestSpeed(RoomConfig.speedSteps, RoomConfig.defaultSpeed);
            NoteType = RoomConfig.defaultNoteType;
            Team = RoomConfig.defaultTeam;
            DropDirection = RoomConfig.defaultDropDirection;
            GameMode = RoomConfig.defaultGameMode;
            // 家族名跟著這個角色走（config.ini [Profile] 是 Default，角色自己設過就以角色的為準）。
            // 這裡種是因為 SeedRoomDefaults 每次切帳號都會重跑 —— 換角色時家族名要跟著換，
            // 而 GuildName 是家族頻道與送上線身分共同的來源。設定裡完全沒填 → 留著示範家族名，
            // 家族頻道才不會在單機下變成一個永遠說「你沒有家族」的死頁籤。
            string family = ProfileFields.FamilyName(ProfileManager.Active);
            GuildName = family.Length > 0 ? family : DemoGuildName;
            // 場景：config 沒指定（-1，或 config.ini 被刪 → 回退預設 -1）就維持隨機；指定了就套用那個場景。
            if (RoomConfig.defaultScene < 0)
            {
                StageRandom = true;
            }
            else
            {
                var st = StageCatalog.Get(RoomConfig.defaultScene);
                StageId = st.Id; StageFolder = st.Folder; StageRandom = false;
            }
            // 錢包 + 衣櫃(擁有/穿搭) 現在從 active user 的 profile.json 載入 (見 WardrobeStore.Load，於 AppContext.CreateMock
            // 呼叫)；首次(wallet 未 seeded)才發起始金額。這裡不再種錢包，避免每次開機把花掉的錢補回去。
        }

        /// <summary>
        /// 這一局要玩的是**官方歌** → 寫上 gn/fileId/顯示欄位,並把外部歌那一整組欄位關掉。
        ///
        /// 🔴 <see cref="IsExternalSong"/> 是 <c>FrontendApp.StartGameplay</c> 唯一的分岔點:它還留著 true 的話,
        /// 譜、音檔、生成的舞蹈**全部**走 External* 那組舊值 —— SongGn 明明已經是官方那首,放出來的卻是
        /// 這台上一次選過的外部歌。所以「換成官方歌」不能只寫 SongGn,一定要走這裡。
        ///
        /// (實機症狀:房主選 sdom2530k.gn,旁觀者進場放的是他自己上次玩到一半跳出的那首 osu 歌。
        ///  旁觀者/非房主的 session 是他**自己**選過的歌 —— 只有開場時 server echo 的那份會蓋過去,
        ///  而那條路徑以前只蓋 gn,沒有關旗標。)
        ///
        /// <paramref name="title"/> / <paramref name="artist"/> 傳 null = 不動(呼叫端查不到目錄時)。
        /// </summary>
        public void SetOfficialSong(string gn, int fileId, string title, string artist)
        {
            SongGn = gn;
            SongFileId = fileId;
            if (title != null) SongTitle = title;
            if (artist != null) SongArtist = artist;

            // 旗標是關鍵的那一行;其餘欄位一起清掉是為了不要留下「看起來還有效」的殘值 ——
            // 下一首外部歌只會覆寫它自己有的欄位,舊值留著遲早會被別的路徑讀到。
            IsExternalSong = false;
            ExternalChartPath = "";
            ExternalChartIndex = 0;
            ExternalChartFormat = 0;
            ExternalChartSeed = 0;
            ExternalDpsPath = "";
            ExternalAudioPath = "";
            ExternalLevel = 0;
            ExternalFolderPath = "";
            ExternalSongKey = "";
            ExternalPackId = "";
            ExternalSongBpm = 0;
            ExternalSongChartPaths = new string[0];
            ExternalSongChartIndices = new int[0];
        }

        /// <summary>
        /// 把歌庫裡的一筆(entry + 難度槽)整組套進 session:官方歌走 <see cref="SetOfficialSong"/>,
        /// 外部歌填 External* 那一整組(譜/音檔/資料夾/packId/生成編舞要的三個槽)。
        ///
        /// 🔴 為什麼需要這個共用點:換歌**不能只寫 SongGn**。以前「隨機難度每局重抽」那條路徑
        /// (FrontendApp.StartGameplay)就只寫了 gn/fileId/artist/難度 —— 抽到外部歌時 IsExternalSong
        /// 還是 false,進場照著官方 gn 路徑去 DATA/MUSIC 找一個叫 ext_xxxxxxxx 的檔案(不存在);
        /// 反過來(上一首是外部歌、這次抽到官方歌)就會放上一首外部歌的譜與音檔。
        ///
        /// <paramref name="keepTitle"/> = 隨機難度:房間顯示的是「隨機難度 X」標籤,不能被抽到的歌名蓋掉
        /// (蓋掉就等於提前揭曉,線上還會讓 <c>NetSongPublisher</c> 把它當成換歌送出去)。
        /// </summary>
        public void SetSongFromCatalog(Sdo.Game.SongCatalog.Entry e, int slot, bool keepTitle = false)
        {
            if (e == null) return;
            slot = slot < 0 ? 0 : (slot > 2 ? 2 : slot);
            Difficulty = (Difficulty)slot;

            if (!e.external)
            {
                SetOfficialSong(e.gn, e.fileId, keepTitle ? null : (e.title ?? e.gn), e.artist);
                return;
            }

            SongGn = e.gn;
            SongFileId = e.fileId;
            if (!keepTitle) SongTitle = e.title ?? e.gn;
            SongArtist = e.artist;
            IsExternalSong = true;
            ExternalChartFormat = e.chartFormat;
            ExternalChartPath = e.ChartPath(slot);
            ExternalChartIndex = e.ChartIndex(slot);
            ExternalChartSeed = e.chartSeed;          // .gn 歌曲包:每首譜自己的金鑰
            ExternalDpsPath = e.dpsPath ?? "";        // 包裡有官方編舞就跳那支,不用生成的
            ExternalAudioPath = e.audioPath ?? "";
            ExternalLevel = e.DisplayLevel(slot);
            ExternalFolderPath = e.folderPath ?? "";
            ExternalSongKey = e.songKey ?? "";
            ExternalPackId = e.packId ?? "";          // 生成舞蹈的 seed:內容指紋(見 [[sdo-external-dance-per-song-inputs]])
            ExternalSongBpm = e.bpm;
            // 生成編舞要量「這首歌所有難度」的頭尾(不是只有選到這張)—— 三個格子照原順序帶過去,空的留 ""
            ExternalSongChartPaths = new[] { e.ChartPath(0), e.ChartPath(1), e.ChartPath(2) };
            ExternalSongChartIndices = new[] { e.ChartIndex(0), e.ChartIndex(1), e.ChartIndex(2) };
        }

        /// <summary>回傳 steps 裡最接近 want 的檔位（steps 空 → 直接回 want）。</summary>
        public static float NearestSpeed(float[] steps, float want)
        {
            if (steps == null || steps.Length == 0) return want;
            float best = steps[0];
            float bestDiff = System.Math.Abs(steps[0] - want);
            for (int i = 1; i < steps.Length; i++)
            {
                float d = System.Math.Abs(steps[i] - want);
                if (d < bestDiff) { bestDiff = d; best = steps[i]; }
            }
            return best;
        }
    }
}
