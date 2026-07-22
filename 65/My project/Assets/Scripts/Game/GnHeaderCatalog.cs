using System;

namespace Sdo.Game
{
    /// <summary>
    /// 每個 .gn 表頭的「原文歌名」視圖：資料來自 <see cref="SongTable"/>
    /// （StreamingAssets/song_table.csv 的 titleZhCn / titleEn / title 等欄）。
    ///
    /// 歌名依語言分成 <see cref="Name"/> { zhCN, zhTW, en }，UI 要換語言不必重新解碼：
    /// zhCN 是 .gn 表頭的原字（簡中）、zhTW 是 opencc s2twp 轉出來再經人工校正的顯示名
    /// （＝ CSV 的 title 欄，選歌畫面看到的就是它）、en 只在原名本來就是拉丁字母時才有值。
    ///
    /// 表頭文字原本是 GB2312(cp936)，解碼一律在 import 時做掉 —— 理由見 <see cref="SongTable"/>。
    /// </summary>
    public static class GnHeaderCatalog
    {
        public enum Lang { ZhCN, ZhTW, En }

        [Serializable] public class Name
        {
            public string zhCN; public string zhTW; public string en;

            /// <summary>Pick a language; falls back to zhCN when the requested one is empty.</summary>
            public string For(Lang lang)
            {
                switch (lang)
                {
                    case Lang.ZhTW: return string.IsNullOrEmpty(zhTW) ? zhCN : zhTW;
                    case Lang.En:   return string.IsNullOrEmpty(en) ? zhCN : en;
                    default:        return zhCN;
                }
            }
        }

        public class Entry
        {
            public string gn; public int fileId; public string mode; public float bpm;
            public int[] levels; public int[] noteCounts; public int[] measurements; public int[] durations;
            public Name title; public Name artist; public string producer; public string origName;
        }

        /// <summary>Look up by a .gn path or filename (case-insensitive). Null if absent.</summary>
        public static Entry Get(string gnPathOrName)
        {
            var r = SongTable.Get(gnPathOrName);
            return r == null ? null : FromRow(r);
        }

        public static string Title(string gnPathOrName, Lang lang = Lang.ZhTW) => Get(gnPathOrName)?.title?.For(lang);
        public static string Artist(string gnPathOrName, Lang lang = Lang.ZhTW) => Get(gnPathOrName)?.artist?.For(lang);

        /// <summary><see cref="SongTable.Row"/> → 表頭 entry（純轉換，有測試）。</summary>
        public static Entry FromRow(SongTable.Row r)
        {
            if (r == null) return null;
            return new Entry
            {
                gn = r.gn, fileId = r.fileId, mode = r.mode,
                bpm = r.chartBpm > 0f ? r.chartBpm : r.bpm,
                levels = r.levels, noteCounts = r.noteCounts,
                measurements = r.measurements, durations = r.durations,
                title = new Name { zhCN = r.titleZhCn, zhTW = r.title, en = r.titleEn },
                artist = new Name { zhCN = r.artistZhCn, zhTW = r.artist, en = r.artistEn },
                producer = r.producer, origName = r.origName,
            };
        }
    }
}
