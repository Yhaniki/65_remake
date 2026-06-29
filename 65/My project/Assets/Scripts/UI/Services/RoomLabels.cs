using Sdo.Localization;

namespace Sdo.UI.Services
{
    /// <summary>
    /// Localized text for the room header (DDRROOM win1 labels: <c>servername</c> / <c>channelnum</c> /
    /// <c>roomname</c>). Kept out of the MonoBehaviour so the i18n branching (custom-name vs default) is
    /// unit-testable: drive it via <see cref="LocalizationManager.LoadFromTables"/> like the other loc tests.
    /// </summary>
    public static class RoomLabels
    {
        /// <summary>「自由練習場{N}」— the server / practice-hall name shown top-left.</summary>
        public static string ServerName(int serverNumber) => LocalizationManager.Get("room.server_name", serverNumber);

        /// <summary>「頻道{N}」— the channel label next to the server name.</summary>
        public static string Channel(int channel) => LocalizationManager.Get("room.channel", channel);

        /// <summary>
        /// The centred room-name plate. Uses the player's custom room name when set; otherwise falls back to
        /// the host's「{name}的舞蹈室」default (e.g. 玩家001 → 玩家001的舞蹈室).
        /// </summary>
        public static string DisplayName(string customName, string hostName)
            => string.IsNullOrWhiteSpace(customName)
                ? LocalizationManager.Get("room.default_name", hostName ?? "")
                : customName;
    }
}
