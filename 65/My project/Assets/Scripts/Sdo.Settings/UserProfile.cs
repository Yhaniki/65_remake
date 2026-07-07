using System;

namespace Sdo.Settings
{
    /// <summary>
    /// 一個本機使用者 = 一個舞者角色 + 一個帳號 id。存成 DATA/PROFILE/&lt;id&gt;/profile.json，id 就是零填 8 位數的
    /// 資料夾名（00000000, 00000001, …）。收藏(favorites.json)與房間預設(config.ini)都放在同一個資料夾下。
    ///
    /// 未來線上版沿用同一個 <see cref="UserProfile"/> 形狀：後端由伺服器帳號提供（登入流程決定 active），本機這層
    /// 只是離線單機的實作 —— 見 <see cref="ProfileManager"/>。序列化用 JsonUtility（欄位公開、可手改）。
    /// </summary>
    [Serializable]
    public class UserProfile
    {
        public string id = "00000000";   // 8 位數帳號 id == DATA/PROFILE 下的資料夾名
        public string name = "玩家001";  // 角色顯示名（頭上名字 / 房間名 / 結算名）
        public int gender = 0;            // 角色性別：0=女(WOMAN) 1=男(MAN)
        public int avatarId = 0;          // 角色外觀 id（保留給未來換裝；0=預設）
        public string createdAt = "";     // ISO-8601 建立時間
        public string lastPlayedAt = "";  // ISO-8601 最後遊玩時間

        public UserProfile() { }

        public UserProfile(string id, string name, int gender)
        {
            this.id = id;
            this.name = name;
            this.gender = gender;
        }

        /// <summary>夾正非法欄位（空 id/name → 回退、gender 夾 0/1）。純函式。</summary>
        public UserProfile Sanitize()
        {
            if (string.IsNullOrEmpty(id)) id = "00000000";
            if (string.IsNullOrEmpty(name)) name = "玩家001";
            gender = gender == 1 ? 1 : 0;
            if (avatarId < 0) avatarId = 0;
            return this;
        }
    }
}
