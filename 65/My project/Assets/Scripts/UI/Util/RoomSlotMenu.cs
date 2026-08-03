using System.Collections.Generic;

namespace Sdo.UI.Util
{
    /// <summary>房主對某個座位可以做的事(右鍵選單的項目)。</summary>
    public enum RoomSlotAction
    {
        /// <summary>把那個人踢出房間。</summary>
        Kick,
        /// <summary>把房主交給那個人。</summary>
        TransferHost,
        /// <summary>關閉這個位子(有人的話 server 會先把他踢掉,見 R8)。</summary>
        CloseSeat,
        /// <summary>把關閉的位子重新開放。</summary>
        OpenSeat,
    }

    /// <summary>
    /// 「這個座位的右鍵選單要有哪幾項」的純規則。
    ///
    /// 🔴 這裡是**純 UX**,不是權限。每一個操作 server 都獨立驗過一次
    /// (R7 host-only、R8 不准關自己的位子、R10a…),client 只是不要把按不動的東西畫出來。
    /// 反過來說:這裡漏擋不會變成安全問題,但把不能按的項目畫出來會讓玩家按了收到 error 卻不知道為什麼 ——
    /// 所以規則寫在一個地方、有測試,而不是散在 UI 的 if 裡。
    /// </summary>
    public static class RoomSlotMenu
    {
        /// <summary>
        /// 依座位狀態列出可用項目(順序就是選單顯示順序)。回空陣列 = 不要彈選單。
        /// </summary>
        /// <param name="isHost">我是房主嗎。非房主一律沒有選單(所有操作都是 host-only)。</param>
        /// <param name="online">在連線模式嗎。離線房只有自己一個人,這些操作沒有意義。</param>
        /// <param name="isSelf">這格是我自己嗎。自己不能踢自己、也不能關自己的位子(server 回 badSeat)。</param>
        /// <param name="taken">這格有人坐。</param>
        /// <param name="closed">這格被關閉了。</param>
        public static RoomSlotAction[] For(bool isHost, bool online, bool isSelf, bool taken, bool closed)
        {
            if (!isHost || !online || isSelf) return Empty;
            var list = new List<RoomSlotAction>(2);
            if (taken)
            {
                // 有人:踢他,或把房主交給他。**不提供「關閉位子」** ——
                // server 那條路會先踢人再關(R8),但那是雙擊鎖格的語意;選單裡兩個都放會讓
                // 「關閉位子」看起來像不會影響到人。要連人一起清掉就用雙擊。
                list.Add(RoomSlotAction.Kick);
                list.Add(RoomSlotAction.TransferHost);
            }
            else if (closed) list.Add(RoomSlotAction.OpenSeat);
            else list.Add(RoomSlotAction.CloseSeat);
            return list.ToArray();
        }

        private static readonly RoomSlotAction[] Empty = new RoomSlotAction[0];

        /// <summary>雙擊座位要把它切成「關閉」還是「開放」?(關閉的 → 開放;其餘 → 關閉)</summary>
        public static bool DoubleClickClosesSeat(bool closed) => !closed;

        /// <summary>雙擊這個座位有意義嗎?(規則同選單:房主、連線中、不是自己)</summary>
        public static bool DoubleClickAllowed(bool isHost, bool online, bool isSelf)
            => isHost && online && !isSelf;
    }
}
