using System;

namespace Sdo.Net
{
    /// <summary>
    /// 「這兩個人是同一個家族嗎」—— 家族的**身分規則**,client 與 server 共用的唯一一份。
    ///
    /// 為什麼需要一份共用的:同族這件事有三個地方要判斷,而且必須完全一致 ——
    ///   ① server 的家族頻道(<c>Hub.OnChatSay</c>:只把 <c>channel=="family"</c> 的話轉給同族),
    ///   ② 大廳玩家名單的「家族」分頁(<c>LobbyUserPanel</c>),
    ///   ③ (未來)任何「同族才做的事」。
    /// 三邊各寫一次的話,會出現「名單上看得到他、但他收不到我的家族發言」這種只有對照兩邊程式碼
    /// 才查得出來的落差。
    ///
    /// 🔴 **家族 = 名字 + 徽章兩者都相同**(使用者要求:「家族一樣、符號也一樣才算同族」)。
    ///    這套連線沒有帳號系統、也沒有家族註冊表 —— 家族名是 client 自己在 profile.json 填的字串
    ///    (見 <c>Sdo.Settings.ProfileFields.FamilyName</c>),所以光靠名字誰都能冒充。
    ///    多一道徽章至少讓「同名不同族」不會互相聽到對方的家族頻道。
    ///
    /// 🔴 **沒有家族的人永遠不算同族**,免得全服「都沒家族」的人變成一個大家族。
    /// </summary>
    public static class GuildIdentity
    {
        /// <summary>有家族嗎(名字是家族的必要條件 —— 只有徽章沒有名字不算)。</summary>
        public static bool Has(string guildName)
            => !string.IsNullOrEmpty(NormalizeName(guildName));

        /// <summary>家族名的比較形式:去頭尾空白。大小寫在比較時才忽略(顯示要保留原樣)。</summary>
        public static string NormalizeName(string guildName)
            => guildName == null ? "" : guildName.Trim();

        /// <summary>
        /// 徽章的**比較鍵**(不是檔名!)。同一個徽章在 config / profile 裡有好幾種寫法 ——
        /// <c>SMALL43</c> / <c>small43</c> / 光一個數字 <c>43</c> / 帶副檔名 <c>SMALL43.PNG</c> ——
        /// 全部收斂成 <c>SMALL43</c>,兩個人手打的寫法不同不該害他們變成不同家族。
        ///
        /// 規則與 <c>Sdo.UI.Util.EmblemArt.FileName</c>(那邊算的是**要載入哪個檔**)刻意平行,
        /// 但這裡不補副檔名:它只拿來比對,不拿來開檔。
        /// </summary>
        public static string NormalizeEmblem(string emblem)
        {
            string s = emblem == null ? "" : emblem.Trim();
            if (s.Length == 0) return "";

            if (s.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || s.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 4);

            bool allDigits = s.Length > 0;
            for (int i = 0; i < s.Length; i++)
                if (s[i] < '0' || s[i] > '9') { allDigits = false; break; }
            if (allDigits) s = "SMALL" + s;

            return s.ToUpperInvariant();
        }

        /// <summary>
        /// 兩個人同族嗎 —— **名字相同(忽略大小寫)且徽章相同**。
        ///
        /// 兩邊徽章都留空 → 只要名字一樣就算同族(舊 client 不送 <c>guildEmblem</c>,
        /// 那時的家族頻道不能因為升級就整個斷掉;而「一邊有徽章、一邊沒有」就是不同族 ——
        /// 那是真的設得不一樣)。
        /// </summary>
        public static bool Same(string nameA, string emblemA, string nameB, string emblemB)
        {
            string a = NormalizeName(nameA);
            if (a.Length == 0) return false;
            string b = NormalizeName(nameB);
            if (b.Length == 0) return false;
            if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return false;
            return string.Equals(NormalizeEmblem(emblemA), NormalizeEmblem(emblemB), StringComparison.Ordinal);
        }
    }
}
