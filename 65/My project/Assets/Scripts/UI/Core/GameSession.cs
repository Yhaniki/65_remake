namespace Sdo.UI.Core
{
    public enum Difficulty { Easy = 0, Normal = 1, Hard = 2 }

    /// <summary>Front-end session state carried across screens (local, offline v1).</summary>
    public sealed class GameSession
    {
        public string LocalPlayerId = "me";
        public string LocalPlayerName = "玩家001";

        public int CurrentRoomId = -1;

        // pending song/stage/noteskin selection
        public string SongGn;       // e.g. "sdom1435k.gn"
        public int SongFileId;
        public string SongTitle;
        public string SongArtist;
        public Difficulty Difficulty = Difficulty.Normal;

        public string StageFolder = "SCN0009";
        public int StageId = 9;

        public string NoteSkin = "NOTEIMAGE_5";

        public bool HasSong => !string.IsNullOrEmpty(SongGn);
    }
}
