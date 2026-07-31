namespace Sdo.Settings
{
    /// <summary>
    /// <c>config.ini [Profile]</c> 的欄位怎麼解析成「這個角色現在的值」。
    ///
    /// 規則只有一條:**config.ini 是 Default,角色資料夾裡有自己寫過就以角色的為準。**
    /// (<c>DATA/PROFILE/config.ini</c> 是全域一份、所有角色共用;<c>DATA/PROFILE/&lt;id&gt;/profile.json</c> 是每個角色一份。)
    ///
    /// 為什麼要有這個類別而不是各處自己判斷:這三個值有四個消費端 ——
    /// 房間裡自己頭上的等級與家族列、送上線的 identity、還有個人資料頁。
    /// 各寫一次的話遲早會出現「頭上名牌吃 user 值、送上線的等級吃 config 值」這種對不起來的狀況。
    ///
    /// 🔴 覆寫是**整組**的,不是逐欄的:只要 <see cref="UserProfile.hasProfileOverrides"/> 是 true,
    ///    三個欄位就全部讀 profile.json(空字串代表「這個角色刻意不顯示」)。逐欄 fallback 做不到
    ///    「我就是不想顯示家族」—— 空字串會被 Default 蓋回去。
    /// </summary>
    public static class ProfileFields
    {
        /// <summary>家族名稱。空 = 不顯示家族列(連徽章一起隱藏)。</summary>
        public static string FamilyName(UserProfile p)
            => Overrides(p) ? Clean(p.familyName) : Clean(RoomConfig.familyName);

        /// <summary>家族徽章檔名(DATA/EMBLEM 底下,不含副檔名)。空 = 只顯示名稱不放徽章。</summary>
        public static string FamilyEmblem(UserProfile p)
            => Overrides(p) ? Clean(p.familyEmblem) : Clean(RoomConfig.familyEmblem);

        /// <summary>等級字串。空 = 不顯示「LV:N」。</summary>
        public static string PlayerLevel(UserProfile p)
            => Overrides(p) ? Clean(p.playerLevel) : Clean(RoomConfig.playerLevel);

        /// <summary>等級的數值形式(送上線的 identity 用)。解析不出來 → 1。</summary>
        public static int PlayerLevelValue(UserProfile p)
        {
            int v;
            return int.TryParse(PlayerLevel(p), out v) && v > 0 ? v : 1;
        }

        /// <summary>「LV:11」這種顯示字串;等級留空時回空字串(呼叫端據此決定接不接在名字後面)。</summary>
        public static string LevelLabel(UserProfile p) => RoomConfig.LevelLabel(PlayerLevel(p));

        /// <summary>
        /// 把目前生效的三個值寫進這個角色,並豎起覆寫旗標 —— 之後這個角色就不再跟著 config.ini 走。
        /// 個人資料頁編輯家族/等級時呼叫(呼叫端負責 <c>ProfileManager.Save()</c>)。
        /// </summary>
        public static void SetOverrides(UserProfile p, string familyName, string familyEmblem, string playerLevel)
        {
            if (p == null) return;
            p.familyName = Clean(familyName);
            p.familyEmblem = Clean(familyEmblem);
            p.playerLevel = Clean(playerLevel);
            p.hasProfileOverrides = true;
        }

        /// <summary>把這個角色的覆寫拿掉 → 三個值改回吃 config.ini 的 Default。</summary>
        public static void ClearOverrides(UserProfile p)
        {
            if (p == null) return;
            p.familyName = "";
            p.familyEmblem = "";
            p.playerLevel = "";
            p.hasProfileOverrides = false;
        }

        private static bool Overrides(UserProfile p) => p != null && p.hasProfileOverrides;

        private static string Clean(string s) => (s ?? "").Trim();
    }
}
