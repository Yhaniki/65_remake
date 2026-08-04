using System;
using UnityEngine;
using Sdo.Game;
using Sdo.Localization;
using Sdo.UI.Util;   // ChatPalette — 房間/大廳/遊戲三邊共用同一組聊天配色

namespace Sdo.UI.Services
{
    /// <summary>
    /// 房間的 <see cref="ChatMessage"/> → 遊戲畫面聊天框要畫的 <see cref="GameplayChatLine"/>。
    ///
    /// 為什麼要這一層:<c>Sdo.Game</c>(遊戲畫面)不能引用 <c>Sdo.UI</c>(asmdef 是 UI → Game 的單向依賴),
    /// 所以「哪些話該出現、要用什麼顏色、名字後面加不加冒號」全部在這邊決定好,推進去的只是排好版的字。
    /// 顏色沿用房間那組(<see cref="ChatPalette"/>),兩邊看起來才是同一個聊天。
    ///
    /// 跟房間唯一不同的一條規則(使用者指定):**「X 進入舞台遊戲 / X 離開舞台」那種進出廣播,到了遊戲場景
    /// 就不再出現** —— 人在場上跳舞的時候不需要知道誰進出房間;人講的話則照常帶進來。
    /// </summary>
    public static class GameplayChatFeed
    {
        /// <summary>這則訊息該不該出現在**遊戲畫面**的聊天框。規則比照房間的 ShouldShowChatMessage,只多擋掉進出廣播。</summary>
        public static bool ShouldShow(ChatMessage m, ChatChannel channel, int roomId)
        {
            if (m == null) return false;
            // 進出舞台廣播:遊戲場景一律不顯示(房間仍照舊)。
            if (m.Stage != StageEventKind.None) return false;

            bool all = channel == ChatChannel.Current;
            // 本機提示行 / 家族訊息跨作用域(不看房號),在對應分類＋「當前」綜合台顯示。
            if (m.Notice == ChatNotice.SelfTalk) return all || channel == ChatChannel.Friend;
            if (m.Notice == ChatNotice.NoGuild) return all || channel == ChatChannel.Family;
            if (m.Guild) return all || channel == ChatChannel.Family;
            // 密語跨場:不受作用域限制,出現在「當前」與「好友」。
            if (m.Whisper != WhisperKind.None) return all || channel == ChatChannel.Friend;
            // 其餘(一般聊天/系統)只顯示本房間,隔離別房與大廳訊息。
            if (m.Scope != ChatScope.Room || m.RoomId != roomId) return false;
            if (m.System) return true;
            return all || m.Channel == channel;
        }

        /// <summary>
        /// 排成一行。<paramref name="frames"/> = 表情 id → 小圖畫格(<c>RoomExpressionArt.SmallFrames</c>);
        /// 傳 null 時表情退回文字指令(<c>/GO</c>),讓這個函式在沒有 Unity 資產的測試環境下也跑得動。
        /// </summary>
        public static GameplayChatLine ToLine(ChatMessage m, Func<int, Sprite[]> frames)
        {
            var line = new GameplayChatLine { ColorHex = ChatPalette.PlainHex };
            if (m == null) return line;

            // 本機提示行:你說 / 你沒有家族
            if (m.Notice == ChatNotice.NoGuild)
            {
                line.ColorHex = ChatPalette.GuildHex;
                line.Body = LocalizationManager.Get("room.no_guild");
                return line;
            }
            if (m.Notice == ChatNotice.SelfTalk)
            {
                line.Body = LocalizationManager.Get("room.selftalk", m.Text ?? "");
                return line;
            }
            // 家族綠字:「<家族>名字: 內容」
            if (m.Guild)
            {
                line.ColorHex = ChatPalette.GuildHex;
                line.Name = RoomChatCommand.GuildTag + (m.Sender ?? "") + ":";
                line.Body = BodyText(m);
                return line;
            }
            // 密語青字:整句已由 ChatDisplay 排好(帶表情時前綴＋inline emoji)
            if (m.Whisper != WhisperKind.None)
            {
                line.ColorHex = ChatPalette.WhisperHex;
                if (m.ExpressionId > 0 && (m.Whisper == WhisperKind.Outgoing || m.Whisper == WhisperKind.Incoming))
                {
                    string key = m.Whisper == WhisperKind.Outgoing ? "room.whisper_out" : "room.whisper_in";
                    line.Name = LocalizationManager.Get(key, m.WhisperParty ?? "", "");
                    ApplyExpression(ref line, m, frames);
                }
                else line.Body = ChatDisplay.WhisperText(m);
                return line;
            }
            // 系統金字(整行沒有名字欄)
            if (m.System)
            {
                line.ColorHex = ChatPalette.SystemHex;
                line.Body = m.Text ?? "";
                return line;
            }

            line.Name = (m.Sender ?? "") + ":";
            if (m.ExpressionId > 0) ApplyExpression(ref line, m, frames);
            else line.Body = m.Text ?? "";
            return line;
        }

        // 表情行:前字〔emoji〕後字。拿不到圖(或沒傳 frames)就退回文字指令,不會整句消失。
        private static void ApplyExpression(ref GameplayChatLine line, ChatMessage m, Func<int, Sprite[]> frames)
        {
            var f = frames != null ? frames(m.ExpressionId) : null;
            line.Lead = (m.LeadingText ?? "").Trim();
            string trail = (m.Text ?? "").Trim();
            if (f != null && f.Length > 0)
            {
                line.ExpressionFrames = f;
                line.Body = IsExpressionEcho(m, trail) ? "" : trail;
                return;
            }
            string cmd = RoomChatCommand.ExpressionDisplayText(m.ExpressionId);
            line.Body = IsExpressionEcho(m, trail) ? cmd : (cmd + " " + trail).Trim();
        }

        // 舊訊息把指令本身當 Text(如 "/無聊")→ 那不是尾隨字,別再重複印一次。
        private static bool IsExpressionEcho(ChatMessage m, string trail)
        {
            if (trail.Length == 0) return true;
            if (RoomChatCommand.TryParseExpression(trail, out var id, out var rest)
                && id == m.ExpressionId && string.IsNullOrEmpty(rest)) return true;
            return string.Equals(trail, RoomChatCommand.ExpressionDisplayText(m.ExpressionId),
                                 StringComparison.OrdinalIgnoreCase);
        }

        private static string BodyText(ChatMessage m)
            => m.ExpressionId > 0 ? RoomChatCommand.ExpressionDisplayText(m.ExpressionId) : (m.Text ?? "");
    }
}
