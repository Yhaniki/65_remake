namespace Sdo.UI.Core
{
    public enum Difficulty { Easy = 0, Normal = 1, Hard = 2 }

    /// <summary>Front-end session state carried across screens (local, offline v1).</summary>
    public sealed class GameSession
    {
        public string LocalPlayerId = "me";
        public string LocalPlayerName = "玩家001";

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

        public string StageFolder = "SCN0009";
        public int StageId = 9;

        public string NoteSkin = "NOTEIMAGE_5";

        // ROOMDLG room settings (single-player: stored locally; gameplay is always free/normal for now).
        public int GameMode = 0;      // 0=自由模式, 1=普通模式 (only these two enabled for now)
        public int Formation = 0;     // 0=基本, 1=扇形, 2=環線, 3=隨機
        public int LookerCount = 10;  // 旁觀人數 0..10

        public bool HasSong => !string.IsNullOrEmpty(SongGn);
    }
}
