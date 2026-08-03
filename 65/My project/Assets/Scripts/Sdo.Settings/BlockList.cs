using System;
using System.Collections.Generic;

namespace Sdo.Settings
{
    /// <summary>
    /// 本機黑名單(官方的「設置阻止 / 加入黑名單」)。與 <see cref="FriendList"/> 是同一套機制的另一半:
    /// 存在**自己的** <c>profile.json</c>(<see cref="UserProfile.blocked"/>)、鍵是**顯示名字**、
    /// 這一層不自己存檔(呼叫端改完負責 <c>ProfileManager.Save()</c>)。
    ///
    /// 🔴 為什麼是「本機靜音」而不是「伺服器封鎖」:server 沒有帳號持久化,也沒有任何 per-user 的過濾規則 ——
    ///    它把每則聊天原封廣播給房裡/大廳所有人。做得到的語意就是「**我這台機器不顯示他說的話**」,
    ///    對方不會知道、也擋不住他進同一間房。這與好友「加了對方不知道」是同一個限制。
    ///
    /// 🔴 好友與黑名單**互斥**:<see cref="Add"/> 會把那個人從好友清單裡拿掉(官方按下「設置阻止」之後
    ///    好友清單上就沒有那個人了)。反過來 <c>FriendList.Add</c> 不會自動解除封鎖 —— 解除是明確的動作
    ///    (選單上的「移出黑名單」),不該當成加好友的副作用。
    /// </summary>
    public static class BlockList
    {
        /// <summary>清單上限。同 <see cref="FriendList.MaxFriends"/> 的理由:不會有人碰到,但壞資料塞不爆檔案。</summary>
        public const int MaxBlocked = 200;

        public static bool IsBlocked(UserProfile owner, string displayName)
            => IndexOf(owner, displayName) >= 0;

        /// <summary>
        /// 封鎖一個人。回 true = 真的加了;false = 沒有名字、是自己、已經在黑名單上、或清單滿了。
        /// **成功時會順手把他從好友清單移除**(見類別註解的互斥規則)。
        /// <paramref name="nowIso"/> 由呼叫端給(這一層不碰時鐘,才測得動)。
        /// </summary>
        public static bool Add(UserProfile owner, string displayName, string playerId, string nowIso)
        {
            if (owner == null) return false;
            string name = (displayName ?? "").Trim();
            if (name.Length == 0) return false;
            if (string.Equals(name, (owner.name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return false;   // 不能封鎖自己
            if (IndexOf(owner, name) >= 0) return false;

            var list = new List<FriendEntry>(owner.blocked ?? new FriendEntry[0]);
            if (list.Count >= MaxBlocked) return false;
            list.Add(new FriendEntry { name = name, id = (playerId ?? "").Trim(), addedAt = nowIso ?? "" });
            owner.blocked = list.ToArray();
            FriendList.Remove(owner, name);   // 互斥:封鎖了就不再是好友
            return true;
        }

        /// <summary>解除封鎖。回 true = 真的刪了。**不會**把他加回好友(封鎖前是不是好友沒有記錄)。</summary>
        public static bool Remove(UserProfile owner, string displayName)
        {
            int i = IndexOf(owner, displayName);
            if (i < 0) return false;
            var list = new List<FriendEntry>(owner.blocked);
            list.RemoveAt(i);
            owner.blocked = list.ToArray();
            return true;
        }

        /// <summary>目前封鎖的名字(依加入順序)。大廳「黑名單」分頁靠它濾名單。</summary>
        public static string[] Names(UserProfile owner)
        {
            if (owner == null || owner.blocked == null) return new string[0];
            var names = new string[owner.blocked.Length];
            for (int i = 0; i < owner.blocked.Length; i++)
                names[i] = owner.blocked[i] != null ? (owner.blocked[i].name ?? "") : "";
            return names;
        }

        private static int IndexOf(UserProfile owner, string displayName)
        {
            if (owner == null || owner.blocked == null) return -1;
            string name = (displayName ?? "").Trim();
            if (name.Length == 0) return -1;
            for (int i = 0; i < owner.blocked.Length; i++)
            {
                var b = owner.blocked[i];
                if (b != null && string.Equals((b.name ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }
    }
}
