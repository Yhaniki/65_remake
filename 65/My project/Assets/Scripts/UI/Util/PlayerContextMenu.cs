using System.Collections.Generic;

namespace Sdo.UI.Util
{
    /// <summary>對「某個玩家」右鍵能做的事。用在大廳玩家名單、大廳房間信息的參與者列表。</summary>
    public enum PlayerAction
    {
        /// <summary>看那個人的玩家資訊視窗(官方 PopBtn_Info「玩家信息」)。</summary>
        PlayerInfo,
        /// <summary>私聊(官方「私聊 / 玩家私聊」)。</summary>
        Whisper,
        /// <summary>加為好友。與 <see cref="RemoveFriend"/> 互斥。</summary>
        AddFriend,
        /// <summary>刪除好友。</summary>
        RemoveFriend,
        /// <summary>加入黑名單(官方「設置阻止」)。與 <see cref="Unblock"/> 互斥。</summary>
        Block,
        /// <summary>移出黑名單。</summary>
        Unblock,
    }

    /// <summary>
    /// 「右鍵這個玩家要有哪幾項」的純規則 —— 大廳側的版本。
    ///
    /// 🔴 與房間座位的 <see cref="RoomSlotMenu"/> **刻意分家**,不是重複:座位選單多了一整組房主管理項
    /// (踢人/轉房主/開關位置),而且它的守門條件是「座位」的狀態(有沒有人、鎖了沒);這裡的對象是
    /// **名單上的一個人**,只有社交項。共用一個函式就得把兩邊的參數全塞進同一個簽章,兩邊都會變難讀。
    ///
    /// 🔴 官方選單裡還有「發送短信」與「使用迷魂藥 / 清醒藥」——這個重製版**沒有**簡訊系統,也沒有道具效果,
    ///    所以不畫出來:按了沒反應的項目比少一項更糟(見 LobbyUserPanel 那條「安靜地沒反應」的教訓)。
    ///
    /// 純函式:不碰 Unity、不碰 ProfileManager。「是不是好友 / 是不是已封鎖」由呼叫端查好傳進來
    /// (那兩份清單住在 profile.json,查它就得碰檔案 → 這一層就測不動了)。
    /// </summary>
    public static class PlayerContextMenu
    {
        /// <summary>
        /// 依對象狀態列出可用項目(順序就是選單顯示順序)。回空陣列 = 不要彈選單。
        /// </summary>
        /// <param name="online">在連線模式嗎。私聊要有 server;離線名單上不會有別人,只剩「玩家信息」。</param>
        /// <param name="isSelf">這是我自己嗎。沒有「私聊自己」「加自己好友」「封鎖自己」,只留看自己的資料。</param>
        /// <param name="isFriend">已經在我的好友清單上了嗎。true → 出「刪除好友」而不是「加為好友」。</param>
        /// <param name="isBlocked">已經在我的黑名單上了嗎。true → 出「移出黑名單」而不是「加入黑名單」。</param>
        public static PlayerAction[] For(bool online, bool isSelf, bool isFriend, bool isBlocked)
        {
            var list = new List<PlayerAction>(4);
            list.Add(PlayerAction.PlayerInfo);   // 唯一一項離線也成立(看的是本機資料)
            if (online && !isSelf)
            {
                list.Add(PlayerAction.Whisper);
                // 已封鎖的人不提供「加為好友」:那兩件事互斥(BlockList.Add 會把他從好友移除),
                // 兩項並排會讓玩家以為可以同時成立。
                if (!isBlocked) list.Add(isFriend ? PlayerAction.RemoveFriend : PlayerAction.AddFriend);
                list.Add(isBlocked ? PlayerAction.Unblock : PlayerAction.Block);
            }
            return list.ToArray();
        }
    }
}
