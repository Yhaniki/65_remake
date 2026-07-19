using System.IO;
using System.Text.RegularExpressions;

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

        /// <summary>音樂路徑（<c>&lt;MUSIC&gt;/sdom1197.ogg</c>）；譜名不合慣例時回 null。</summary>
        public static string Ogg(string songGn)
        {
            string b = Regex.Match(songGn ?? "", @"sdom\d+").Value;
            return b.Length > 0 ? Path.Combine(SdoExtracted.MusicDir, b + ".ogg") : null;
        }
    }
}
