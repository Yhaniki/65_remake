using Sdo.Net;

namespace Sdo.UI.Services
{
    /// <summary>
    /// 頭上聊天泡的使用權。規則:**旁觀的人不能用氣泡,只能用左下打字框**。
    ///
    /// 為什麼要明文擋:旁觀者在房間 3D 裡是有角色的(旁觀席 slot 6..15,見 RoomScreen.SyncRemoteRoomAvatars),
    /// 泡掛得上他的肩膀 —— 少了這道門,他跟座位上的人一樣會彈泡。
    ///
    /// 兩邊都要擋,而且判準一致(都以 server 快照為準):
    ///   • 自己旁觀 → 打字不進頭上泡,改在左下輸入框回顯(見 RoomScreen.BeginRoomBubbleTyping)。
    ///   • 別人旁觀 → 他的話只進左下訊息欄(見 RoomScreen.OnRoomChatMessage)。
    /// </summary>
    public static class RoomBubblePolicy
    {
        /// <summary>這個 userId 在這份房間快照裡是不是旁觀者。userId 0(離線 / 還沒拿到 server id)一律不是。</summary>
        public static bool IsSpectator(NetRoomSnapshot snap, int userId)
            => snap != null && userId != 0 && snap.SpectatorIndexOf(userId) >= 0;

        /// <summary>
        /// 這則訊息的發話者是不是旁觀者。
        ///
        /// 🔴 本機說的話(<c>m.Local</c>)一定要看**本機自己的旁觀狀態**:本機訊息的 SenderUserId 是 0
        /// (見 RoomChatCommand.TryResolveBubbleOwner 的第 2 道門),只查快照永遠會判成「不是旁觀者」。
        /// </summary>
        public static bool SpeakerIsSpectator(ChatMessage m, bool localSpectating, NetRoomSnapshot snap)
        {
            if (m == null) return false;
            return m.Local ? localSpectating : IsSpectator(snap, m.SenderUserId);
        }

        /// <summary>本機現在能不能用頭上泡打字(旁觀 → 不行,改用左下打字框)。</summary>
        public static bool CanTypeInBubble(bool localSpectating) => !localSpectating;

        /// <summary>
        /// 這位發話者的話能不能在房間 3D 上表現出來 —— **頭上泡與關鍵字動作(含語音)都算**。
        /// 旁觀者兩樣都沒有:他的話只以文字進左下訊息欄,角色站在旁觀席不動。
        /// </summary>
        public static bool CanEmoteInRoom(bool speakerIsSpectator) => !speakerIsSpectator;
    }
}
