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

        [Test]
        public void Guild_Line_Draws_The_Expression_Not_The_Raw_Command()
        {
            // 使用者回報「在家族頻道打 emoji 沒出來」—— 家族行以前直接把 ExpressionId 印成 "/翻",
            // 現在要跟一般聊天走同一條:有圖畫圖、拿不到圖才退回指令文字,而 <家族> 前綴與綠字照舊。
            int id = RoomChatCommand.MenuExpressionIds[1];
            var m = new ChatMessage("A", "", 0, expressionId: id) { Guild = true, LeadingText = "看這個" };
            var frames = new UnityEngine.Sprite[1];

            var withArt = GameplayChatFeed.ToLine(m, _ => frames);
            Assert.AreEqual(ChatPalette.GuildHex, withArt.ColorHex);
            StringAssert.StartsWith(RoomChatCommand.GuildTag, withArt.Name);
            Assert.AreSame(frames, withArt.ExpressionFrames);
            Assert.AreEqual("看這個", withArt.Lead);
            StringAssert.DoesNotContain("/", withArt.Body ?? "");

            var noArt = GameplayChatFeed.ToLine(m, null);   // 沒有素材的環境:整句不能消失
            Assert.IsNull(noArt.ExpressionFrames);
            Assert.AreEqual(RoomChatCommand.ExpressionDisplayText(id), noArt.Body);
        }

        [Test]
        public void Only_Other_Peoples_Names_Are_Clickable_For_Whisper()
        {
            // 點名字 → 密語(同房間左下角聊天列):自己說的話、系統行、密語行的名字都不可點。
            Assert.AreEqual("Eithwa", GameplayChatFeed.ToLine(Room("Eithwa", "哈囉"), null).WhisperTarget);

            var mine = Room("我", "哈囉"); mine.Local = true;
            Assert.IsNull(GameplayChatFeed.ToLine(mine, null).WhisperTarget);

            var sys = Room("", "系統提示"); sys.System = true;
            Assert.IsNull(GameplayChatFeed.ToLine(sys, null).WhisperTarget);

            var w = Room("A", "悄悄話"); w.Whisper = WhisperKind.Incoming; w.WhisperParty = "A";
            Assert.IsNull(GameplayChatFeed.ToLine(w, null).WhisperTarget);

            // 家族行的名字也可以點(那是別人說的話)
            var g = new ChatMessage("A", "家族話", 0) { Guild = true };
            Assert.AreEqual("A", GameplayChatFeed.ToLine(g, null).WhisperTarget);
        }

        [Test]
        public void Clicking_A_Name_Puts_The_Target_In_Front_Of_What_Was_Typed()
        {
            // 房間的 InsertWhisperTarget 規則:保留已打的內容,舊的 [名字] 前綴換掉而不是疊上去。
            Assert.AreEqual("[A] ", Sdo.Game.ChatDraft.WithWhisperTarget("", "A"));
            Assert.AreEqual("[A] 哈囉", Sdo.Game.ChatDraft.WithWhisperTarget("哈囉", "A"));
            Assert.AreEqual("[B] 哈囉", Sdo.Game.ChatDraft.WithWhisperTarget("[A] 哈囉", "B"));
            Assert.AreEqual("[B] ", Sdo.Game.ChatDraft.WithWhisperTarget("[A] ", "B"));
            // 名字兩邊的空白吃掉;空名字不動輸入框
            Assert.AreEqual("[A] x", Sdo.Game.ChatDraft.WithWhisperTarget("x", "  A  "));
            Assert.AreEqual("x", Sdo.Game.ChatDraft.WithWhisperTarget("x", "   "));
        }

        [Test]
        public void Picking_An_Expression_Appends_The_Command_To_What_Was_Typed()
        {
            // 房間與遊戲畫面共用同一套:前面有字補一個空白隔開,結尾留一個空白讓人接著打。
            Assert.AreEqual("/GO ", Sdo.Game.ChatDraft.WithExpression("", "/GO"));
            Assert.AreEqual("哈囉 /GO ", Sdo.Game.ChatDraft.WithExpression("哈囉", "/GO"));
            Assert.AreEqual("哈囉 /GO ", Sdo.Game.ChatDraft.WithExpression("哈囉 ", "/GO"));   // 已有空白不再補
            Assert.AreEqual("[A] /GO ", Sdo.Game.ChatDraft.WithExpression("[A] ", "/GO"));    // 密語前綴照樣接得上
            Assert.AreEqual("哈囉", Sdo.Game.ChatDraft.WithExpression("哈囉", ""));             // 沒有指令 → 不動
            // characterLimit 會截斷（房間的輸入框有上限）
            Assert.AreEqual("哈囉 /G", Sdo.Game.ChatDraft.WithExpression("哈囉", "/GO", 5));
        }
    }
}
