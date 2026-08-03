using System;
using System.Reflection;

namespace Sdo.Server
{
    /// <summary>
    /// 「這顆 binary 是哪個 commit」。啟動時印出來,而且**與 client 視窗標題用同一顆 formatter**
    /// (<see cref="Sdo.Game.BuildTitle"/>)—— 兩邊字串長得一樣才能一眼比對:
    /// client 標題 <c>dance v1.5.0-dev-d41da</c> 對 server <c>sdo-server v1.5.0-dev-d41da</c>。
    ///
    /// 🔴 為什麼需要這個:更新 server 之後「密語沒反應、遠端不動」這類症狀,與「binary 根本沒換到」
    /// 長得一模一樣(舊 server 收到不認識的訊息只會靜靜回一個 error)。沒有版本號的話,第一件該排除的事
    /// 反而是最難確認的那件。
    ///
    /// 三個值由 csproj 的 SdoStampGitVersion 在編譯時用 git 填進 assembly metadata;
    /// 沒有 git / 不是 repo(例如從 tarball 建)時全部是空的 → <c>unknown</c>。
    /// </summary>
    public static class BuildInfo
    {
        /// <summary>例:<c>v1.5.0-dev-d41da</c>;拿不到 git 資訊時 <c>unknown</c>。</summary>
        public static string Version { get; } = Resolve(typeof(BuildInfo).Assembly);

        /// <summary>
        /// 例:<c>sdo-server v1.5.0-dev-d41da</c>;拿不到 git 資訊時 <c>sdo-server unknown</c>。
        ///
        /// ⚠️ 計算屬性,不是 <c>{ get; } = "sdo-server " + Version</c> —— 那樣寫的話 static 初始化
        /// 依**宣告順序**執行,Banner 會在 Version 還是 null 時就算完,印出來只剩「sdo-server 」。
        /// (實際踩到:banner 印出來版本是空白的。)
        /// </summary>
        public static string Banner => "sdo-server " + Version;

        internal static string Resolve(Assembly asm)
            => Compose(Meta(asm, "GitExactTag"), Meta(asm, "GitNearestTag"), Meta(asm, "GitHash5"));

        /// <summary>三個 git 輸出 → 版本字串。product 傳空的:這裡只要版本那半段。</summary>
        internal static string Compose(string exactTag, string nearestTag, string hash5)
        {
            string v = Sdo.Game.BuildTitle.Format("", Sane(exactTag), Sane(nearestTag), Sane(hash5));
            return string.IsNullOrEmpty(v) ? "unknown" : v;
        }

        /// <summary>
        /// 擋掉不是版本的東西。git 失敗時(不在 tag 上、沒有 .git)拿到的可能是 stderr
        /// (<c>fatal: no tag exactly matches …</c>)或空字串 —— tag 名與 hash **不含空白**,
        /// 用這一條就足以把那些訊息擋在版本字串外面。
        /// </summary>
        internal static string Sane(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.Length > 64) return null;
            for (int i = 0; i < s.Length; i++)
                if (char.IsWhiteSpace(s[i])) return null;
            return s;
        }

        private static string Meta(Assembly asm, string key)
        {
            if (asm == null) return null;
            foreach (var a in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
                if (string.Equals(a.Key, key, StringComparison.Ordinal)) return a.Value;
            return null;
        }
    }

    /// <summary>
    /// client 與 server 是同一個 commit 嗎?兩邊的字串各自帶著自己的產品名
    /// (<c>dance v1.5.0-dev-d41da</c> / <c>sdo-server v1.5.0-dev-d41da</c>),所以比的是**最後那一段**。
    ///
    /// 🔴 拿不到版本的一邊就不要吵:Unity Editor 裡 <c>productName</c> 只是「dance」(沒有 git 後綴),
    /// 從 tarball 建的 server 則是「unknown」—— 這兩種情況下警告每次連線都會出現,而它什麼也沒證明。
    /// 只有在兩邊都拿得出「看起來像版本」的字串、而且不相等時,才是真的值得喊的版本不一致。
    /// </summary>
    public static class BuildVersionMatch
    {
        public static bool Same(string clientBuild, string serverBuild)
        {
            string a = VersionPart(clientBuild);
            string b = VersionPart(serverBuild);
            if (!LooksLikeVersion(a) || !LooksLikeVersion(b)) return true;   // 無從比較 → 不警告
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>最後一個空白之後的那一段(產品名可能自己含空白,所以從右邊切)。</summary>
        internal static string VersionPart(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim();
            int sp = s.LastIndexOf(' ');
            return sp >= 0 ? s.Substring(sp + 1) : s;
        }

        /// <summary>`v1.2.3…` 或帶 `dev-` 的才算版本;產品名(dance)與 unknown 都不算。</summary>
        internal static bool LooksLikeVersion(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.IndexOf("dev-", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return s.Length >= 2 && (s[0] == 'v' || s[0] == 'V') && char.IsDigit(s[1]);
        }
    }
}
