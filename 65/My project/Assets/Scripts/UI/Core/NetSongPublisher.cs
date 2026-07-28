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
            if (song != null) ctx.Net.SetSong(song);
        }
    }
}
