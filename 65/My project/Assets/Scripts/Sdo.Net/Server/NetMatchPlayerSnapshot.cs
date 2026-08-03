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
                Look = look == null
                    ? new NetAvatarLook()
                    : new NetAvatarLook
                    {
                        Gender = look.Gender,
                        BodyIndex = look.BodyIndex,
                        Parts = look.Parts != null ? (string[])look.Parts.Clone() : null,
                    },
            };
        }
    }
}
