namespace Sdo.UI.Services
{
    /// <summary>
    /// 把 server 回的 <c>joinResult</c> 代碼翻成玩家看得懂的一句話的 localization key。
    ///
    /// 抽成純函式是因為**加入房間有兩個入口**:大廳點房間卡片、以及「輸入房號」框。
    /// 兩邊要說一樣的話 —— 同一個失敗原因在不同入口講不同句子,玩家會以為是兩件事。
    /// </summary>
    public static class JoinErrorText
    {
        public static string KeyFor(string joinResult)
        {
            switch (joinResult)
            {
                // 座位滿了會先自動轉旁觀(NetClient.JoinOrSpectate),所以走到這裡的 lookerFull
                // 代表**座位 6 個 + 旁觀 10 個都滿了**。對玩家來說那就是同一件事(這間房進不去),
                // 所以與座位滿共用同一句「房間已滿」—— 不必解釋是哪一種滿。
                case Sdo.Net.NetProto.ErrLookerFull:
                case Sdo.Net.NetProto.JoinFull: return "room.join_full";
                case Sdo.Net.NetProto.JoinInGame: return "room.join_in_game";
                case Sdo.Net.NetProto.JoinNotFound: return "room.join_not_found";
                default: return "room.join_failed";
            }
        }
    }
}
