namespace Sdo.Net.Server
{
    /// <summary>
    /// Match-start participant metadata. It survives live seat-table changes and ordinary disconnects so results
    /// can retain the player's identity and last frame. Explicit match removals such as a kick or load timeout
    /// prune the matching entry.
    /// </summary>
    public sealed class NetMatchPlayerSnapshot
    {
        public int UserId;
        public int Seat;
        public string Name = "";
        public int Level;
        public int Team;
        public NetAvatarLook Look = new NetAvatarLook();

        public static NetMatchPlayerSnapshot Capture(NetSeat seat, int seatIndex)
        {
            var look = seat != null ? seat.Look : null;
            return new NetMatchPlayerSnapshot
            {
                UserId = seat != null ? seat.UserId : 0,
                Seat = seatIndex,
                Name = seat != null ? (seat.Name ?? "") : "",
                Level = seat != null ? seat.Level : 0,
                Team = seat != null ? seat.Team : 0,
                // 🔴 一定要走 Clone() —— 手捲複製漏掉一個欄位不會有任何編譯錯誤,值就是靜靜地不見了。
                // (實際踩過:漏了 MmdPack → 這份快照是 matchStarting 送給每個人的參賽者名單,
                //  於是進遊戲之後每個人的 MMD 模型都變回 SDO 穿搭,而房間裡明明還是好的。)
                Look = look == null ? new NetAvatarLook() : look.Clone(),
            };
        }
    }
}
