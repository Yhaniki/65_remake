namespace Sdo.UI.Services
{
    /// <summary>
    /// 房間裡「其他人」的假身分（id / 名字 / 等級）。離線 EXE 的房間只有房主一個人，其他人是連線時伺服器
    /// 放進來的 —— 但「右鍵選人看戰績」要有人可以點，所以離線版給每個 slot 一個決定性的假身分。
    ///
    /// 決定性：slot → 固定的 id → 固定的名字/等級/戰績（<see cref="MockPlayerStats"/>），所以同一個人每次點開都一樣。
    /// 接上線之後整個 class 丟掉，改用伺服器下發的房間名單。
    /// </summary>
    public static class RoomOccupants
    {
        // 15 個名字：slot 1-5 = 舞者，slot 6-15 = 旁觀者（RoomLayout 的 slot 編號）。
        private static readonly string[] Names =
        {
            "炫炎輪火", "Polaris晴天坊", "小醜麵具", "奶茶布丁", "醉小蛇",
            "夜貓", "星塵", "櫻花雨", "DanceKing", "阿傑",
            "莉莉安", "風之舞", "Momo醬", "藍色蝴蝶", "無敵小胖",
        };

        /// <summary>slot 的固定 id（1 起算；slot 0 是本機玩家，不走這裡）。</summary>
        public static string IdFor(int slot) => "room" + slot.ToString("00");

        /// <summary>slot 的固定名字。超出名單就繞回去並加編號，避免重名。</summary>
        public static string NameFor(int slot)
        {
            if (slot < 1) return "";
            int i = (slot - 1) % Names.Length;
            int lap = (slot - 1) / Names.Length;
            return lap == 0 ? Names[i] : Names[i] + (lap + 1);
        }
    }
}
