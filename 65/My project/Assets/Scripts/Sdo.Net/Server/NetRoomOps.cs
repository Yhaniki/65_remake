namespace Sdo.Net.Server
{
    /// <summary>
    /// 房間操作的結果。對映協定的 error code(見 <see cref="ToErrorCode"/>)。
    ///
    /// **每一個 host-only 操作都必須在 server 端再驗一次。** client 端隱藏按鈕只是 UX ——
    /// 一個改過的 client 照樣能把 <c>kickUser</c> 送上來。這是照 osu 的做法:它的 UI 只是
    /// <c>ChangeSettingsButton.Alpha = IsHost ? 1 : 0</c>,真正的權限在 server 拋 NotHostException。
    /// </summary>
    public enum NetRoomOp
    {
        Ok = 0,

        /// <summary>不是房主。</summary>
        NotHost,

        /// <summary>操作者或目標不在這間房裡。</summary>
        NotInRoom,

        /// <summary>座位索引不合法,或那個座位不能這樣操作(例如關掉房主自己的座位)。</summary>
        BadSeat,

        /// <summary>房間/玩家現在的狀態不允許這個操作(例如已經開打了還要改歌)。</summary>
        BadState,

        /// <summary>組隊人數湊不出官方座標表有的版型(只有 2v2 / 3v3 / 2v2v2)。</summary>
        BadTeams,

        /// <summary>還沒選歌。</summary>
        NoSong,

        /// <summary>沒有空位。</summary>
        Full,

        /// <summary>房間正在遊戲中(不能加入,但可以旁觀)。</summary>
        InGame,

        /// <summary>旁觀人數已達房間設定的上限。</summary>
        LookerFull,
    }

    public static class NetRoomOpExt
    {
        /// <summary>轉成協定的 error code 字串。<see cref="NetRoomOp.Ok"/> 回 null。</summary>
        public static string ToErrorCode(this NetRoomOp op)
        {
            switch (op)
            {
                case NetRoomOp.Ok: return null;
                case NetRoomOp.NotHost: return NetProto.ErrNotHost;
                case NetRoomOp.NotInRoom: return NetProto.ErrNotInRoom;
                case NetRoomOp.BadSeat: return NetProto.ErrBadSeat;
                case NetRoomOp.BadState: return NetProto.ErrBadState;
                case NetRoomOp.BadTeams: return NetProto.ErrBadTeams;
                case NetRoomOp.NoSong: return NetProto.ErrNoSong;
                case NetRoomOp.Full: return NetProto.ErrFull;
                case NetRoomOp.LookerFull: return NetProto.ErrLookerFull;
                default: return NetProto.ErrBadState;
            }
        }

        /// <summary>轉成 <c>joinResult</c> 的字串(對映 client 既有的 <c>JoinResult</c> enum)。</summary>
        public static string ToJoinResult(this NetRoomOp op)
        {
            switch (op)
            {
                case NetRoomOp.Ok: return NetProto.JoinOk;
                case NetRoomOp.Full: return NetProto.JoinFull;
                case NetRoomOp.InGame: return NetProto.JoinInGame;
                default: return NetProto.JoinNotFound;
            }
        }
    }

    /// <summary>加入房間時帶的身分資料(從 <c>hello</c> 得來)。</summary>
    public struct NetJoinUser
    {
        public int UserId;
        public string Name;
        public string Guild;
        public int Level;
        public NetAvatarLook Look;

        public NetJoinUser(int userId, string name, string guild, int level, NetAvatarLook look)
        {
            UserId = userId;
            Name = name ?? "";
            Guild = guild ?? "";
            Level = level;
            Look = look ?? new NetAvatarLook();
        }
    }

    /// <summary>
    /// 一場遊戲的資訊。在 <c>open → waitingForLoad</c> 的那一刻建立,
    /// **參與者集合在此刻凍結**(之後有人準備/取消準備都不影響這一場)。
    /// </summary>
    public sealed class NetMatchInfo
    {
        /// <summary>這場的 id(房間內遞增)。用來丟掉上一場的遲到訊息。</summary>
        public long MatchId;

        /// <summary>server 認定的開場時刻(Unix ms)。目前只做記錄與診斷 —— 判定是純本地的。</summary>
        public long StartEpochMs;

        /// <summary>載入逾時(ms)。client 會用它顯示「等待其他玩家」的上限。</summary>
        public int LoadTimeoutMs = NetLimits.LoadTimeoutMs;

        /// <summary>參與者(座位玩家)的 userId,依座位索引序。</summary>
        public int[] ParticipantUserIds = new int[0];

        /// <summary>
        /// 跟著進場的旁觀者 userId —— **只有 <c>avail == have</c> 的旁觀者**。
        /// 缺歌的旁觀者留在房間看頭貼(使用者要求:旁觀者缺歌不自動下載)。
        /// </summary>
        public int[] SpectatorUserIds = new int[0];

        /// <summary>這一局拍板定案的隨機值(server 驗過的)。</summary>
        public NetResolvedRound Resolved;

        /// <summary>這一局要玩的歌(隨機難度時是抽出來的那首,否則就是房間當前的歌)。</summary>
        public NetSongRef Song;

        /// <summary>已經廣播過 <c>gameplayStarted</c> 了嗎?(防重發)</summary>
        public bool GameplayStartedSent;
    }

    /// <summary>
    /// <see cref="NetRoom.Tick"/> 的結果 —— 告訴 Hub 這一拍要發哪些訊息。
    ///
    /// 把「狀態機推進」與「發訊息」分開,是為了讓狀態機保持純函式:
    /// 它不知道 socket 存在,所以可以完整地單元測試(R1-R20 每條一個測試)。
    /// </summary>
    public struct NetRoomTick
    {
        /// <summary>狀態有變 → 要推一份新的 <c>roomState</c> 給房內所有人。</summary>
        public bool Changed;

        /// <summary>要廣播 <c>gameplayStarted</c>(給參與者)。</summary>
        public bool GameplayStarted;

        /// <summary>要廣播 <c>resultsReady</c>。</summary>
        public bool ResultsReady;

        /// <summary>這些人要收到 <c>gameplayAborted{loadTookTooLong}</c> —— 載入太久被逐出本場。</summary>
        public int[] LoadTimedOutUserIds;

        /// <summary>整場被取消(沒有任何人載入成功)→ 廣播 <c>gameplayAborted{noParticipants}</c>。</summary>
        public bool MatchAborted;

        /// <summary>這一拍發生的 matchId(0 = 沒有進行中的場次)。</summary>
        public long MatchId;

        public static NetRoomTick None => new NetRoomTick { LoadTimedOutUserIds = EmptyIds };

        internal static readonly int[] EmptyIds = new int[0];
    }
}
