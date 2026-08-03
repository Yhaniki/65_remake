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

        /// <summary>
        /// 房名 + 房號:「飄漂o的舞蹈室(40444)」。
        ///
        /// 房號放在房名後面而不是左上那排(練習場/頻道)—— 那才是玩家要唸給朋友聽的東西,
        /// 跟房名放在一起比擠在「頻道1」後面好認。
        ///
        /// <paramref name="roomCode"/> &lt;= 0 時不加括弧 —— 還沒有房號的過渡狀態,
        /// 以及**單機**(呼叫端刻意傳 0:沒有別人能加入,那串號碼對玩家沒有意義)。
        /// </summary>
        public static string DisplayNameWithCode(string customName, string hostName, int roomCode)
        {
            string name = DisplayName(customName, hostName);
            if (roomCode <= 0) return name;
            return name + "(" + roomCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        }
    }
}
