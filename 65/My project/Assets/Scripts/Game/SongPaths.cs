using System.IO;

namespace Sdo.Game
{
    /// <summary>
    /// 一首歌的檔案位置。譜面（.gn）與音樂（.ogg）都在同一棵 MUSIC 樹底下，音檔名就是譜面名去掉難度字母：
    /// <c>sdom1197k.gn</c> → <c>sdom1197.ogg</c>（k = 一般譜、t = 另一份譜，兩者共用同一個音檔）。
    /// </summary>
    public static class SongPaths
    {
        /// <summary>譜面路徑（<c>&lt;MUSIC&gt;/sdom1197k.gn</c>）。</summary>
        public static string Gn(string songGn)
            => string.IsNullOrEmpty(songGn) ? null : Path.Combine(SdoExtracted.MusicDir, songGn);

        /// <summary>音樂路徑（<c>&lt;MUSIC&gt;/sdom1197.ogg</c>）；譜名為空時回 null。
        ///
        /// 檔名一律交給 <see cref="SongCatalog.MainOggName"/>（全專案唯一來源，開局與選歌試聽共用）。
        /// 這裡原本自己用 <c>sdom\d+</c> regex 取號，遇到撞號手動插隊的 <c>sdom1234_1k.gn</c> 會在底線處
        /// 斷掉、解成 <c>sdom1234.ogg</c> —— 開局放成「原本那首」的音樂（選歌試聽卻是對的，因為它早就走
        /// MainOggName），造成同一首歌試聽與開局不同曲。
        /// </summary>
        public static string Ogg(string songGn)
        {
            var name = SongCatalog.MainOggName(songGn);
            return name == null ? null : Path.Combine(SdoExtracted.MusicDir, name);
        }
    }
}
