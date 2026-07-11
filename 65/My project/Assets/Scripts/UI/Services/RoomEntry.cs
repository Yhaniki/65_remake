namespace Sdo.UI.Services
{
    /// <summary>
    /// Pure entry-into-own-room policy, shared by the gender-select screen and any「建自己的房」flow.
    /// Kept out of the MonoBehaviour so it's unit-testable (drive it with a MockRoomService).
    /// </summary>
    public static class RoomEntry
    {
        /// <summary>
        /// Guarantee the local player enters a room it HOSTS with its current identity.
        /// If a room is current but the local player is not its host (a stale room left over from a
        /// previous identity, or someone else's room), leave it first, then create a fresh one.
        /// This is what makes 房主標記 (IsHost) correct after switching gender/account and re-entering:
        /// without it, re-entering while a foreign room is still current would silently skip CreateRoom
        /// and leave IsHost=false (see the 女角→退出→男角 host-badge-vanishes bug).
        /// </summary>
        public static void EnsureOwnHostRoom(IRoomService rooms, GameMode mode)
        {
            if (rooms == null) return;
            if (rooms.CurrentRoom != null && !rooms.IsHost) rooms.LeaveRoom();
            if (rooms.CurrentRoom == null) rooms.CreateRoom(mode);
        }
    }
}
