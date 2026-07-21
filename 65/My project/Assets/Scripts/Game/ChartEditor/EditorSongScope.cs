using System;
using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>
    /// 「編輯器現在只走哪一個資料夾」的純邏輯 —— 決定 Q/E 上一首/下一首會走到哪些歌、F1 歌單列哪些歌。
    ///
    /// 動機：一整包匯進來的歌（例如 [NX] 那 199 首）常常**整包**都對不準，要一首一首調 offset。原本 Q/E 走的是
    /// 整份 <see cref="SongCatalog.Primary"/>（官方兩千多首＋所有外部歌混在一起），從包裡的第一首按下去會直接
    /// 掉進別的歌。鎖定資料夾之後，Q/E 就只在那包裡繞，調完一首按 E 就是同包的下一首。
    ///
    /// 「資料夾」＝ <see cref="SongCatalog.Entry.group"/>，也就是選歌畫面「資料夾」分頁看到的那一層：
    /// 一個 pack 的所有歌共用同一個 group，跟它們實際散在幾層子目錄無關。官方內建歌沒有 group，歸在
    /// <see cref="OfficialScope"/> 這個特別的範圍。
    ///
    /// 純函式、不碰 Unity，方便單元測試。
    /// </summary>
    public static class EditorSongScope
    {
        /// <summary>不限範圍（走整份清單）。</summary>
        public const string All = "";

        /// <summary>只走官方內建歌（沒有 group 的那些）。用一個不可能撞到資料夾名的字串當標記。</summary>
        public const string OfficialScope = "official";

        /// <summary>這首歌屬於哪個範圍：外部歌 = 它的 group（group 是空的就退回 <see cref="OfficialScope"/>，
        /// 那種歌沒有可鎖的資料夾），官方歌 = <see cref="OfficialScope"/>。</summary>
        public static string ScopeOf(SongCatalog.Entry e)
        {
            if (e == null || !e.external) return OfficialScope;
            return string.IsNullOrEmpty(e.group) ? OfficialScope : e.group;
        }

        /// <summary>清單裡有哪些資料夾可以選（外部歌的 group，去重後照字母排）。官方歌不列在這裡 ——
        /// 呼叫端自己把「全部」與「官方」兩個固定選項放在前面。</summary>
        public static List<string> Folders(IReadOnlyList<SongCatalog.Entry> songs)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            if (songs != null)
                foreach (var e in songs)
                {
                    if (e == null || !e.external || string.IsNullOrEmpty(e.group)) continue;
                    if (seen.Add(e.group)) list.Add(e.group);
                }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        /// <summary>套用範圍後的歌單（保持原順序）。<paramref name="scope"/> 是 <see cref="All"/> 就原封不動回傳；
        /// 範圍內一首都沒有（資料夾被刪了／重掃後改名）也回整份 —— 寧可讓 Q/E 還能動，也不要卡死在空清單。</summary>
        public static List<SongCatalog.Entry> InScope(IReadOnlyList<SongCatalog.Entry> songs, string scope)
        {
            var list = new List<SongCatalog.Entry>();
            if (songs == null) return list;
            bool all = string.IsNullOrEmpty(scope);
            foreach (var e in songs)
            {
                if (e == null) continue;
                if (all || string.Equals(ScopeOf(e), scope, StringComparison.OrdinalIgnoreCase)) list.Add(e);
            }
            if (list.Count == 0)
                foreach (var e in songs) if (e != null) list.Add(e);
            return list;
        }

        /// <summary>Q/E：<paramref name="gn"/> 在 <paramref name="scoped"/> 裡往前/後一首（頭尾相接）。
        /// 目前這首不在範圍內（剛換範圍）就從第一首開始。清單是空的回 null。</summary>
        public static SongCatalog.Entry Step(IReadOnlyList<SongCatalog.Entry> scoped, string gn, int dir)
        {
            if (scoped == null || scoped.Count == 0) return null;
            int cur = -1;
            for (int i = 0; i < scoped.Count; i++)
                if (scoped[i] != null && string.Equals(scoped[i].gn, gn, StringComparison.OrdinalIgnoreCase)) { cur = i; break; }
            if (cur < 0) return scoped[0];
            int n = scoped.Count;
            return scoped[((cur + dir) % n + n) % n];
        }

        /// <summary>畫面上顯示的範圍名稱。</summary>
        public static string Label(string scope)
        {
            if (string.IsNullOrEmpty(scope)) return "全部";
            return scope == OfficialScope ? "官方內建" : scope;
        }
    }
}
