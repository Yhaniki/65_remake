using System;
using System.Collections.Generic;

namespace Sdo.Settings
{
    /// <summary>
    /// 本機好友清單的存取。清單存在**自己的** <c>profile.json</c>(<see cref="UserProfile.friends"/>)。
    ///
    /// 為什麼是本機而不是伺服器:server 目前完全沒有帳號持久化 —— 它記房間、廣播結果,然後就把資料丟掉。
    /// 好友要跨連線活下來,唯一能記住的地方就是這台機器。代價是「加了好友對方不知道」,
    /// 而這也正好是目前這套(區網/朋友之間)連線唯一做得到的語意。
    ///
    /// 🔴 **鍵是顯示名字,不是某種 id。** 這不是偷懶:在這套連線裡,名字**就是**身分 ——
    ///    私聊的 target 是名字字串、由 server 全服照名字找人(<c>NetClient.SendWhisper</c>),
    ///    而房間座位快照(<c>NetSeat</c>)根本沒帶 playerId,只帶 server 每次連線重發的 userId
    ///    (那個下次上線就變了,拿來當鍵整串好友隔天全部失效)。用名字才與「私聊找得到誰」一致。
    ///    <see cref="FriendEntry.id"/> 只是備查用的 playerId 快照,不參與比對。
    ///
    /// 這一層**不自己存檔**:呼叫端改完之後負責 <c>ProfileManager.Save()</c>
    /// (加好友常常緊接著別的 profile 變更,分開才不會為了一筆好友寫兩次檔)。
    /// </summary>
    public static class FriendList
    {
        /// <summary>清單上限。官方沒有明確數字;這裡取一個「不會有人碰到、但壞資料塞不爆檔案」的值。</summary>
        public const int MaxFriends = 200;

        public static bool IsFriend(UserProfile owner, string displayName)
            => IndexOf(owner, displayName) >= 0;

        /// <summary>
        /// 加一個好友。回 true = 真的加了;false = 沒有名字、是自己、已經是好友、或清單滿了。
        /// <paramref name="nowIso"/> 由呼叫端給(這一層不碰時鐘,才測得動)。
        /// </summary>
        public static bool Add(UserProfile owner, string displayName, string playerId, string nowIso)
        {
            if (owner == null) return false;
            string name = (displayName ?? "").Trim();
            if (name.Length == 0) return false;
            if (string.Equals(name, (owner.name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return false;   // 不能加自己
            if (IndexOf(owner, name) >= 0) return false;

            var list = new List<FriendEntry>(owner.friends ?? new FriendEntry[0]);
            if (list.Count >= MaxFriends) return false;
            list.Add(new FriendEntry { name = name, id = (playerId ?? "").Trim(), addedAt = nowIso ?? "" });
            owner.friends = list.ToArray();
            return true;
        }

        /// <summary>刪一個好友。回 true = 真的刪了。</summary>
        public static bool Remove(UserProfile owner, string displayName)
        {
            int i = IndexOf(owner, displayName);
            if (i < 0) return false;
            var list = new List<FriendEntry>(owner.friends);
            list.RemoveAt(i);
            owner.friends = list.ToArray();
            return true;
        }

        /// <summary>目前的好友名字(依加入順序)。給大廳好友頁與「誰在線上」比對用。</summary>
        public static string[] Names(UserProfile owner)
        {
            if (owner == null || owner.friends == null) return new string[0];
            var names = new string[owner.friends.Length];
            for (int i = 0; i < owner.friends.Length; i++)
                names[i] = owner.friends[i] != null ? (owner.friends[i].name ?? "") : "";
            return names;
        }

        private static int IndexOf(UserProfile owner, string displayName)
        {
            if (owner == null || owner.friends == null) return -1;
            string name = (displayName ?? "").Trim();
            if (name.Length == 0) return -1;
            for (int i = 0; i < owner.friends.Length; i++)
            {
                var f = owner.friends[i];
                if (f != null && string.Equals((f.name ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }
    }
}
