namespace Sdo.Game
{
    /// <summary>
    /// 歌名／歌手的顯示字數上限——選歌清單與遊戲中 HUD 共用同一份規則。
    ///
    /// 兩邊的欄位寬度都是官方版型烘死的固定寬（選歌列 <c>NameW</c>、HUD 底部「歌曲名:」後面那格），
    /// 文字物件也刻意不自動換行／不縮字（縮了就跟官方字距對不上），所以外部歌那種很長的英文標題
    /// （例：<c>Shiroi Yuki no Princess wa (The Snow White Princess is)</c>）會直接畫出框外壓到時間／
    /// 音符數欄。這裡在「顯示」時砍尾巴，資料本身（catalog / session / 搜尋 / 排序）一律保留全名。
    /// </summary>
    public static class SongTextLimits
    {
        /// <summary>歌名上限（字）。</summary>
        public const int Title = 35;

        /// <summary>歌手上限（字）。</summary>
        public const int Artist = 20;

        public static string ClampTitle(string s) => Clamp(s, Title);
        public static string ClampArtist(string s) => Clamp(s, Artist);

        /// <summary>截到 <paramref name="maxChars"/> 個字，多的直接砍掉（不加省略號——官方也是硬切）。</summary>
        public static string Clamp(string s, int maxChars)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (maxChars <= 0) return "";
            if (s.Length <= maxChars) return s;
            int cut = maxChars;
            // 別把 surrogate pair（emoji 之類）切成半個 → 會變成一個看不懂的破字（甚至 tofu）
            if (char.IsHighSurrogate(s[cut - 1])) cut--;
            return s.Substring(0, cut);
        }
    }
}
