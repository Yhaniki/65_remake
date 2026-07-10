using Sdo.Localization;

namespace Sdo.UI.Services
{
    /// <summary>
    /// 密語行的顯示字組字（大廳與房間共用，確保「你對X說 / X對你說 / X不在當前頻道 / X無此id」文案一致）。
    /// 房間的「帶表情密語」另走 inline emoji 版本（RoomScreen.AddRoomChatWhisperExpressionLine）；此處是純文字版，
    /// 表情以指令文字（如 /GO）呈現，供大廳等單行渲染使用。
    /// </summary>
    public static class ChatDisplay
    {
        public static string WhisperText(ChatMessage m)
        {
            if (m == null) return "";
            string party = m.WhisperParty ?? "";
            switch (m.Whisper)
            {
                case WhisperKind.Outgoing:   return LocalizationManager.Get("room.whisper_out", party, WhisperBody(m));
                case WhisperKind.Incoming:   return LocalizationManager.Get("room.whisper_in", party, WhisperBody(m));
                case WhisperKind.OffChannel: return LocalizationManager.Get("room.whisper_offchannel", party);
                case WhisperKind.NoId:       return LocalizationManager.Get("room.whisper_noid", party);
                default:                     return "";
            }
        }

        // 密語內容：帶表情時排成 前字 emoji指令 後字（表情用指令文字呈現）；否則就是純文字。
        private static string WhisperBody(ChatMessage m)
        {
            if (m.ExpressionId <= 0) return m.Text ?? "";
            string lead = m.LeadingText != null ? m.LeadingText.Trim() : "";
            string trail = (m.Text ?? "").Trim();
            string emoji = RoomChatCommand.ExpressionDisplayText(m.ExpressionId);
            string s = emoji;
            if (lead.Length > 0) s = lead + " " + s;
            if (trail.Length > 0) s = s + " " + trail;
            return s;
        }
    }
}
