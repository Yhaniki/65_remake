using NUnit.Framework;
using Sdo.UI.Services;
using Sdo.UI.Util;   // ChatPalette

namespace Sdo.Tests
{
    /// <summary>房間訊息 → 遊戲畫面聊天框:進出舞台廣播被擋掉,人講的話照常帶進來。</summary>
    public class GameplayChatFeedTests
    {
        private const int RoomId = 42;

        private static ChatMessage Room(string sender, string text)
            => new ChatMessage(sender, text, 0) { Scope = ChatScope.Room, RoomId = RoomId };

        [Test]
        public void Stage_Enter_Leave_Never_Show_In_Gameplay()
        {
            // 「xxx 進入舞台遊戲 / xxx 離開舞台」是房間的事 —— 到遊戲場景就不出現(使用者指定)。
            var enter = Room("Eithwa", "") ; enter.Stage = StageEventKind.Enter;
            var leave = Room("Eithwa", "") ; leave.Stage = StageEventKind.Leave;
            Assert.IsFalse(GameplayChatFeed.ShouldShow(enter, ChatChannel.Current, RoomId));
            Assert.IsFalse(GameplayChatFeed.ShouldShow(leave, ChatChannel.Current, RoomId));
            Assert.IsFalse(GameplayChatFeed.ShouldShow(enter, ChatChannel.Family, RoomId));
        }

        [Test]
        public void Plain_Room_Chat_Comes_Through()
        {
            var m = Room("Eithwa", "哈囉");
            Assert.IsTrue(GameplayChatFeed.ShouldShow(m, ChatChannel.Current, RoomId));
        }

        [Test]
        public void Other_Rooms_And_The_Lobby_Are_Filtered_Out()
        {
            var lobby = new ChatMessage("路人", "大廳講話", 0) { Scope = ChatScope.Lobby };
            Assert.IsFalse(GameplayChatFeed.ShouldShow(lobby, ChatChannel.Current, RoomId));
            var otherRoom = Room("路人", "別房"); otherRoom.RoomId = RoomId + 1;
            Assert.IsFalse(GameplayChatFeed.ShouldShow(otherRoom, ChatChannel.Current, RoomId));
        }

        [Test]
        public void Whisper_And_Guild_Cross_Scope_Like_In_The_Room()
        {
            var w = new ChatMessage("A", "秘密", 0) { Whisper = WhisperKind.Incoming, WhisperParty = "A" };
            Assert.IsTrue(GameplayChatFeed.ShouldShow(w, ChatChannel.Current, RoomId));
            Assert.IsTrue(GameplayChatFeed.ShouldShow(w, ChatChannel.Friend, RoomId));
            Assert.IsFalse(GameplayChatFeed.ShouldShow(w, ChatChannel.Family, RoomId));

            var g = new ChatMessage("A", "家族話", 0) { Guild = true };
            Assert.IsTrue(GameplayChatFeed.ShouldShow(g, ChatChannel.Current, RoomId));
            Assert.IsTrue(GameplayChatFeed.ShouldShow(g, ChatChannel.Family, RoomId));
            Assert.IsFalse(GameplayChatFeed.ShouldShow(g, ChatChannel.Friend, RoomId));
        }

        [Test]
        public void Plain_Line_Puts_The_Name_In_Its_Own_Column()
        {
            var line = GameplayChatFeed.ToLine(Room("Eithwa", "哈囉"), null);
            Assert.AreEqual("Eithwa:", line.Name);
            Assert.AreEqual("哈囉", line.Body);
            Assert.AreEqual(ChatPalette.PlainHex, line.ColorHex);
            Assert.AreEqual("Eithwa: 哈囉", line.PlainText());
        }

        [Test]
        public void System_And_Guild_Lines_Keep_The_Room_Colours()
        {
            var sys = Room("", "系統提示"); sys.System = true;
            var sysLine = GameplayChatFeed.ToLine(sys, null);
            Assert.AreEqual(ChatPalette.SystemHex, sysLine.ColorHex);
            Assert.IsTrue(string.IsNullOrEmpty(sysLine.Name));   // 系統行沒有名字欄

            var g = new ChatMessage("A", "家族話", 0) { Guild = true };
            var gLine = GameplayChatFeed.ToLine(g, null);
            Assert.AreEqual(ChatPalette.GuildHex, gLine.ColorHex);
            StringAssert.StartsWith(RoomChatCommand.GuildTag, gLine.Name);
        }

        [Test]
        public void Expression_Without_Art_Falls_Back_To_The_Command_Text()
        {
            // 拿不到表情圖時整句不能消失 —— 退回文字指令(/GO 之類)。
            int id = RoomChatCommand.MenuExpressionIds[0];
            var m = Room("Eithwa", "");
            m.ExpressionId = id;
            var line = GameplayChatFeed.ToLine(m, null);
            Assert.IsNull(line.ExpressionFrames);
            Assert.AreEqual(RoomChatCommand.ExpressionDisplayText(id), line.Body);
        }
    }
}
