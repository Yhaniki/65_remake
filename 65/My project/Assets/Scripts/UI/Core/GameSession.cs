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

        // 隨機難度選擇：確認時就抽好實際歌曲(SongGn/SongFileId/SongArtist)，但房間只顯示「隨機難度 X」標籤(SongTitle)，
        // 進遊戲才揭曉是哪首歌。重進選歌選單 → 直接回隨機 tab 的該區間。false = 一般（指定歌曲）選擇。
        public bool SongIsRandom;
        public int SongRandomRange;   // SongSelectScreen.RandRanges 索引（哪個難度區間）

        public string StageFolder = "SCN0009";
        public int StageId = 9;
        // true = 選歌時選的是「隨機場景」→ 房間第二層圖顯示 RANDOM（雖然 gameplay 仍用上面解析出的具體場景）。
        // 預設 true：一開始還沒選歌，房間就顯示 random 場景。見 SongSelectScreen.OnConfirm / RoomScreen。
        public bool StageRandom = true;

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
