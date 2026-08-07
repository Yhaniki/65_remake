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
        /// 模式那格的 loc key。大廳房卡、「房間信息」框、選歌對話框底下的下拉**共用同一組 key** ——
        /// 同一件事在三個地方不該有三種講法。
        ///
        /// 🔴 三種模式都要分:ShowTime 以前被歸進 Normal,於是「ShowTime 的房間」在大廳寫「普通模式」。
        /// </summary>
        public static string ModeKey(GameMode mode)
            => mode == GameMode.ShowTime ? "songselect.mode_showtime"
             : mode == GameMode.Normal ? "songselect.mode_normal"
             : "songselect.mode_free";

        /// <summary>同上,但吃協定/<c>GameSession.GameMode</c> 的代號(0=自由 1=普通 2=ShowTime)。</summary>
        public static string ModeKey(int gameMode) => ModeKey((GameMode)GameModeRules.Clamp(gameMode));

        /// <summary>模式那格的字(已解好 loc)。</summary>
        public static string ModeName(GameMode mode) => LocalizationManager.Get(ModeKey(mode));
    }
}
