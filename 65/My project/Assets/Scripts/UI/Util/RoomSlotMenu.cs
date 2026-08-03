using System.Collections.Generic;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 對某個座位右鍵能做的事。順序就是官方 <c>UI/ROOM/POPMENU.XML</c>(SP_PopMenu)的排法:
    /// 前面是**社交**(玩家信息 / 私聊 / 加為好友),後面才是**房主管理**(切換房主 / 踢出 / 開關位置)。
    /// </summary>
    public enum RoomSlotAction
    {
        /// <summary>看那個人的玩家資訊視窗(官方 PopBtn_Info「玩家信息」)。</summary>
        PlayerInfo,
        /// <summary>私聊(官方 PopBtn_PrivateChat「私聊」)。</summary>
        Whisper,
        /// <summary>加為好友(官方 PopBtn_AddFriend)。與 <see cref="RemoveFriend"/> 互斥。</summary>
        AddFriend,
        /// <summary>刪除好友(官方 PopBtn_DelFriend)。官方也是「已經是好友就換成這一項」的二選一。</summary>
        RemoveFriend,
        /// <summary>把房主交給那個人。</summary>
        TransferHost,
        /// <summary>把那個人踢出房間。</summary>
        Kick,
        /// <summary>關閉這個位子(有人的話 server 會先把他踢掉,見 R8)。</summary>
        CloseSeat,
        /// <summary>把關閉的位子重新開放。</summary>
        OpenSeat,
    }

    /// <summary>
    /// 「這個座位的右鍵選單要有哪幾項」的純規則。
    ///
    /// 🔴 這裡是**純 UX**,不是權限。每一個管理操作 server 都獨立驗過一次
    /// (R7 host-only、R8 不准關自己的位子、R10a…),client 只是不要把按不動的東西畫出來。
    /// 反過來說:這裡漏擋不會變成安全問題,但把不能按的項目畫出來會讓玩家按了收到 error 卻不知道為什麼 ——
    /// 所以規則寫在一個地方、有測試,而不是散在 UI 的 if 裡。
    ///
    /// 🔴 **選單分成兩組,不再是「非房主就沒有選單」。** 官方的座位右鍵選單本體是社交選單
    /// (玩家信息/私聊/加為好友),誰都能對別人用;房主的座位管理是後來疊上去的第二組。
    /// 之前整個函式第一行就 <c>if (!isHost) return Empty;</c>,等於把官方本來就有的功能一起擋掉了。
    ///
    /// 純函式:不碰 Unity、不碰 ProfileManager。「這個人是不是我的好友」由呼叫端查好傳進來
    /// (好友清單住在 profile.json,查它就得碰檔案 → 這一層就測不動了)。
    /// </summary>
    public static class RoomSlotMenu
    {
        /// <summary>
        /// 依座位狀態列出可用項目(順序就是選單顯示順序)。回空陣列 = 不要彈選單。
        /// </summary>
        /// <param name="isHost">我是房主嗎。只影響**管理**那一組;社交那組跟房主無關。</param>
        /// <param name="online">在連線模式嗎。私聊/好友/所有管理操作都要有 server;離線只剩「玩家信息」。</param>
        /// <param name="isSelf">這格是我自己嗎。沒有「私聊自己」「加自己好友」「踢自己」這種事,只留看自己的資料。</param>
        /// <param name="taken">這格有人坐。沒人就沒有社交項目(空位不是一個人)。</param>
        /// <param name="closed">這格被關閉了。決定管理組要給「開啟」還是「關閉」。</param>
        /// <param name="isFriend">那個人已經在我的好友清單上了嗎。true → 出「刪除好友」而不是「加為好友」。</param>
        public static RoomSlotAction[] For(bool isHost, bool online, bool isSelf, bool taken, bool closed, bool isFriend)
        {
            var list = new List<RoomSlotAction>(5);

            // ---- 社交組:看的是「這格上有人」,不是「我是不是房主」----
            if (taken)
            {
                // 玩家信息離線也給:離線房只有自己,那就是「看自己的資料」——
                // 這是官方唯一一個不需要對方在線上的項目,擋掉它會讓單機完全沒有辦法看自己的數據。
                list.Add(RoomSlotAction.PlayerInfo);
                if (online && !isSelf)
                {
                    list.Add(RoomSlotAction.Whisper);
                    // 官方 PopBtn_AddFriend / PopBtn_DelFriend 疊在同一個 y=54 的格子 → 一次只出現一個。
                    list.Add(isFriend ? RoomSlotAction.RemoveFriend : RoomSlotAction.AddFriend);
                }
            }

            // ---- 管理組:host-only、連線中、而且不是自己 ----
            if (isHost && online && !isSelf)
            {
                if (taken)
                {
                    // 有人:交房主或踢他。**不提供「關閉位子」** —— server 那條路會先踢人再關(R8),
                    // 但那是雙擊鎖格的語意;選單裡兩個都放會讓「關閉位置」看起來像不會影響到人。
                    list.Add(RoomSlotAction.TransferHost);
                    list.Add(RoomSlotAction.Kick);
                }
                else if (closed) list.Add(RoomSlotAction.OpenSeat);
                else list.Add(RoomSlotAction.CloseSeat);
            }

            return list.Count == 0 ? Empty : list.ToArray();
        }

        private static readonly RoomSlotAction[] Empty = new RoomSlotAction[0];

        /// <summary>雙擊座位要把它切成「關閉」還是「開放」?(關閉的 → 開放;其餘 → 關閉)</summary>
        public static bool DoubleClickClosesSeat(bool closed) => !closed;

        /// <summary>雙擊這個座位有意義嗎?(鎖格是純管理動作,守門與管理組**相同**:房主、連線中、不是自己)</summary>
        public static bool DoubleClickAllowed(bool isHost, bool online, bool isSelf)
            => isHost && online && !isSelf;
    }
}
