using Sdo.Game;
using Sdo.Net;
using Sdo.UI.Catalog;

namespace Sdo.UI.Core
{
    /// <summary>
    /// 把「本機選好的那首歌」翻成連線協定的 <see cref="NetSongRef"/>,交給 server 發給全房。
    ///
    /// 🔴 為什麼一定要有這一步:server 用「這間房選了哪首歌」當很多規則的前提 ——
    /// 沒歌就不能按準備(R17)、不能開始(R12),而換歌會清掉所有人的準備與「有沒有這首歌」(R9)。
    /// 只把歌存在本機 session 的話,兩台看得到歌名(那是本機畫的),但 server 眼中這間房**沒有歌**
    /// → 沒有人按得下準備、房主按開始只會收到「請先選擇歌曲」,而畫面上明明有歌。
    /// (實機兩開就是這樣卡住的:OnlineRoomService.SetSong(string) 只印一行警告,而沒有人呼叫
    ///  Ctx.Net.SetSong(NetSongRef)。)
    ///
    /// 外部歌(osu/SM)要靠 packId 才能跨電腦比對,那是 M5(缺歌傳檔)的事;這裡先做官方歌,
    /// 外部歌照樣填得出顯示欄位,只是 packId 還空著。
    /// </summary>
    public static class NetSongPublisher
    {
        // ---- 「我有沒有這首歌」的回報 ------------------------------------------------------------------------
        // 🔴 這一步少了的話**整個連線對戰都動不了**,而且症狀完全指不到原因:
        //    server 把每個人的 availability 預設成 Unknown,而
        //      • 按準備要求 avail == have(R17)→ 回 badState
        //      • 參與者集合 = 「(房主 或 已準備) 且 avail == have」(R12)→ 房主按開始也回 badState
        //    畫面上一切正常(有歌、有人、有開始鈕),按下去卻只有一個沒有文字的 badState。
        //    實機兩開就是卡在這裡查了很久。
        //
        //    完整的缺歌流程(沒有就自動下載)是 M5;這裡做的是**最小但必要**的那一半:
        //    有就說 have、沒有就說 missing。
        private static string _lastKey;
        private static Availability _lastState = Availability.Unknown;

        /// <summary>每次房間快照呼叫一次。狀態沒變就不重送。</summary>
        public static void ReportAvailability(AppContext ctx)
        {
            if (ctx == null || ctx.Net == null || ctx.Rooms == null) return;
            if (!ctx.Net.IsConnected || !ctx.Net.InRoom) return;
            var snap = ctx.Net.Room;
            var song = snap != null ? snap.Song : null;
            if (song == null || !song.HasSong) { _lastKey = null; _lastState = Availability.Unknown; return; }

            // server 對官方歌是拿 gn 當 key 比對的(NetRoom.MatchesCurrentSong),外部歌才用 packId。
            string key = song.Official ? song.Gn : song.PackId;
            if (string.IsNullOrEmpty(key)) return;

            var state = HaveSong(ctx, song) ? Availability.Have : Availability.Missing;
            if (_lastKey == key && _lastState == state) return;   // 沒變就不重送
            _lastKey = key; _lastState = state;
            UnityEngine.Debug.Log("[net] 回報可用性:" + state + " (" + key + ")");
            ctx.Net.SetAvailability(key, state);
        }

        private static bool HaveSong(AppContext ctx, NetSongRef song)
        {
            if (song.Official) return SongCatalog.Get(song.Gn) != null;
            // 外部歌:跨電腦的身分要靠 packId,而那是 M5 才算得出來的。在那之前只認「這首就是我自己選的那首」
            // (房主一定成立),別人一律 missing —— 寧可說沒有,也不要謊報 have 然後在開場時載不到譜。
            var s = ctx.Session;
            return s != null && s.IsExternalSong && !string.IsNullOrEmpty(song.SongKey)
                   && string.Equals(s.ExternalSongKey, song.SongKey, System.StringComparison.Ordinal);
        }

        /// <summary>把 session 現在選的歌轉成 wire 格式。沒選歌 → null。</summary>
        public static NetSongRef FromSession(GameSession s)
        {
            if (s == null || !s.HasSong) return null;
            var song = new NetSongRef
            {
                Official = !s.IsExternalSong,
                Gn = s.SongGn ?? "",
                FileId = s.SongFileId,
                ChartIndex = (int)s.Difficulty,
                Difficulty = (int)s.Difficulty,
                Title = s.SongTitle ?? "",
                Artist = s.SongArtist ?? "",
            };
            if (s.IsExternalSong)
            {
                // 外部歌:先帶得出「是哪一份譜」的資訊;跨電腦的身分(packId)是 M5 才算得出來。
                song.SongKey = s.ExternalSongKey ?? "";
                song.ChartRelPath = s.ExternalChartPath ?? "";
                song.ChartIndex = s.ExternalChartIndex;
                song.Level = s.ExternalLevel;
            }
            return song;
        }

        /// <summary>房主把選好的歌發給 server。非房主/離線 → 什麼都不做(server 也會擋,R7)。</summary>
        public static void Publish(AppContext ctx)
        {
            if (ctx == null || ctx.Net == null) return;
            if (!ctx.Net.IsConnected || !ctx.Net.InRoom || !ctx.Net.IsHost) return;
            var song = FromSession(ctx.Session);
            if (song == null) return;
            UnityEngine.Debug.Log("[net] 發布歌曲給 server:" + song.Title + " (gn=" + song.Gn + ")");
            ctx.Net.SetSong(song);
        }

        /// <summary>
        /// 「server 那邊還沒有歌就補發一次」—— 由房間快照的回呼每次呼叫。
        ///
        /// 🔴 為什麼不能只在進房那一刻發一次:進房時房間可能還沒建好(createRoom 要等 server 回 roomState),
        /// 那一刻 <c>InRoom</c> 還是 false → 發布被靜默跳過、而且永遠不會再試。
        /// 它也順便處理另外兩種情形:房主中途被轉給我、以及 server 把歌清掉之後。
        /// </summary>
        public static void PublishIfRoomHasNone(AppContext ctx)
        {
            if (ctx == null || ctx.Net == null || ctx.Rooms == null) return;
            if (!ctx.Net.IsConnected || !ctx.Net.InRoom || !ctx.Net.IsHost) return;
            var room = ctx.Rooms.CurrentRoom;
            if (room == null || !string.IsNullOrEmpty(room.SongTitle)) return;   // 已經有歌 → 不再送(不會迴圈)
            Publish(ctx);
        }
    }
}
