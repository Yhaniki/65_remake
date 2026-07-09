using NUnit.Framework;
using Sdo.Localization;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    public class RoomChatCommandTests
    {
        [TestCase("/1", 1)]
        [TestCase("/168", 168)]
        [TestCase("/exp 12", 12)]
        [TestCase("/e12", 12)]
        [TestCase("/emoji:9", 9)]
        [TestCase("/开始", 2)]
        [TestCase("/開始", 2)]
        [TestCase("/start", 2)]
        [TestCase("/GO", 3)]
        [TestCase("/问候", 4)]
        [TestCase("/問候", 4)]
        [TestCase("/greet", 4)]
        [TestCase("/是", 13)]
        [TestCase("/yes", 13)]
        [TestCase("/YES", 13)]     // 指令大小寫不敏感（ExpressionAliases 用 OrdinalIgnoreCase）
        [TestCase("/Yes", 13)]
        [TestCase("/否", 6)]
        [TestCase("/no", 6)]
        [TestCase("/NO", 6)]
        [TestCase("/?", 15)]
        [TestCase("/!", 21)]
        [TestCase("/歌唱", 5)]
        [TestCase("/sing", 5)]
        [TestCase("/媚眼", 23)]
        [TestCase("/wink", 23)]
        [TestCase("/嘘", 17)]
        [TestCase("/噓", 17)]
        [TestCase("/shh", 17)]
        [TestCase("/無聊", 16)]
        public void Parses_Expression_Command(string text, int expected)
        {
            Assert.IsTrue(RoomChatCommand.TryParseExpression(text, out var id, out var trailing));
            Assert.AreEqual(expected, id);
            Assert.AreEqual("", trailing);
        }

        [TestCase("/無聊 3213131", 16, "3213131")]
        [TestCase("/无聊 hello", 16, "hello")]
        [TestCase("/無聊 哈囉世界", 16, "哈囉世界")]
        [TestCase("/无聊 在干嘛？", 16, "在干嘛？")]
        [TestCase("/GO 冲冲冲", 3, "冲冲冲")]
        [TestCase("/媚眼 耶~", 23, "耶~")]
        [TestCase("/12 hi there", 12, "hi there")]
        [TestCase("/exp 12 world", 12, "world")]
        // 前面有字：掃第一個能認的 `/`；前綴併入顯示字。
        [TestCase("哈囉 /無聊", 16, "哈囉")]
        [TestCase("前綴/開始", 2, "前綴")]
        [TestCase("説話 /無聊 嗨", 16, "説話 嗨")]
        [TestCase("hi /GO 冲", 3, "hi 冲")]
        [TestCase("a/b/無聊", 16, "a/b")]
        public void Parses_Expression_Command_With_Trailing_Text(string text, int expectedId, string expectedTrail)
        {
            Assert.IsTrue(RoomChatCommand.TryParseExpression(text, out var id, out var trailing));
            Assert.AreEqual(expectedId, id);
            Assert.AreEqual(expectedTrail, trailing);
        }

        [Test]
        public void Menu_Order_Matches_Decompiled_Popup_Order()
        {
            Assert.AreEqual(2, RoomChatCommand.TotalExpressionPages);
            int[] expectedFirstRow = { 2, 3, 4, 13, 6, 15, 21, 5, 23 };
            for (int i = 0; i < expectedFirstRow.Length; i++)
                Assert.AreEqual(expectedFirstRow[i], RoomChatCommand.ExpressionAtMenuSlot(0, i));
            Assert.AreEqual(10, RoomChatCommand.ExpressionAtMenuSlot(1, 0));
            Assert.AreEqual(48, RoomChatCommand.ExpressionAtMenuSlot(1, 23));
        }

        [Test]
        public void Display_Text_Uses_Selected_Language()
        {
            Assert.AreEqual("/開始", RoomChatCommand.ExpressionDisplayText(2, Language.TraditionalChinese));
            Assert.AreEqual("/开始", RoomChatCommand.ExpressionDisplayText(2, Language.SimplifiedChinese));
            Assert.AreEqual("/start", RoomChatCommand.ExpressionDisplayText(2, Language.English));
            Assert.AreEqual("/開始", RoomChatCommand.ExpressionDisplayText(2, Language.Japanese));
            Assert.AreEqual("/歌唱", RoomChatCommand.ExpressionDisplayText(5, Language.TraditionalChinese));
            Assert.AreEqual("/媚眼", RoomChatCommand.ExpressionDisplayText(23, Language.TraditionalChinese));
            Assert.AreEqual("/噓", RoomChatCommand.ExpressionDisplayText(17, Language.TraditionalChinese));
            Assert.AreEqual("/exp 49", RoomChatCommand.ExpressionDisplayText(49, Language.TraditionalChinese));
        }

        [TestCase("呵呵", "action_0", 0, "MOTION/WREST0058.MOT", "WOMAN_0")]
        [TestCase("哈哈", "action_1", 1, "MOTION/WREST0059.MOT", "WOMAN_1")]
        [TestCase("麼麼", "action_2", 2, "MOTION/WREST0073.MOT", "WOMAN_2")]
        [TestCase("生氣", "action_3", 3, "MOTION/WREST0061.MOT", "WOMAN_3")]
        [TestCase("你好", "action_4", 4, "MOTION/WREST0062.MOT", "WOMAN_4")]
        [TestCase("88", "action_5", 5, "MOTION/WREST0063.MOT", "WOMAN_5")]
        [TestCase("難過", "action_6", 6, "MOTION/WREST0064.MOT", "WOMAN_6")]
        [TestCase("打", "action_7", 7, "MOTION/WREST0074.MOT", null)]
        [TestCase("874", "action_7", 7, "MOTION/WREST0074.MOT", null)]
        public void Parses_Room_Action_Keyword(string text, string expectedId, int actionByte, string femaleMotion, string femaleSound)
        {
            Assert.IsTrue(RoomChatCommand.TryParseRoomAction(text, out var action));
            Assert.AreEqual(expectedId, action.Id);
            Assert.AreEqual(actionByte, action.OfficialActionByte);
            Assert.AreEqual(femaleMotion, action.FemaleMotion);
            Assert.AreEqual(femaleMotion, action.MotionFor(false));
            Assert.AreEqual(femaleSound, action.FemaleSound);
            Assert.AreEqual(femaleSound, action.SoundFor(false));
        }

        // Motion category 0x18 (24) — the room-chat default set — has 8 entries, one per action id 0-7, per gender.
        // Female = WREST, male = MREST. RE'd from Motion_LoadRestTable_004a3900 (see ROOM_CHAT_ACTIONS.md).
        [TestCase("action_0", "MOTION/WREST0058.MOT", "MOTION/MREST0070.MOT")]
        [TestCase("action_1", "MOTION/WREST0059.MOT", "MOTION/MREST0071.MOT")]
        [TestCase("action_2", "MOTION/WREST0073.MOT", "MOTION/MREST0083.MOT")]
        [TestCase("action_3", "MOTION/WREST0061.MOT", "MOTION/MREST0073.MOT")]
        [TestCase("action_4", "MOTION/WREST0062.MOT", "MOTION/MREST0074.MOT")]
        [TestCase("action_5", "MOTION/WREST0063.MOT", "MOTION/MREST0075.MOT")]
        [TestCase("action_6", "MOTION/WREST0064.MOT", "MOTION/MREST0076.MOT")]
        [TestCase("action_7", "MOTION/WREST0074.MOT", "MOTION/MREST0084.MOT")]
        public void Room_Action_Maps_To_Category24_Motion(string id, string femaleMotion, string maleMotion)
        {
            Assert.IsTrue(RoomChatCommand.TryGetRoomAction(id, out var action));
            Assert.AreEqual(femaleMotion, action.FemaleMotion);
            Assert.AreEqual(maleMotion, action.MaleMotion);
            Assert.AreEqual(femaleMotion, action.MotionFor(false));
            Assert.AreEqual(maleMotion, action.MotionFor(true));
        }

        // The same action id can mean different things per gender, so the male table must play the male clip:
        // "88" → female action_5 (再見) but male action_6 (再見); "昏" → male action_5 (dizzy), no female entry.
        [Test]
        public void Male_Room_Action_Plays_Male_Motion_And_Sound()
        {
            Assert.IsTrue(RoomChatCommand.TryParseRoomAction("88", true, out var bye));
            Assert.AreEqual("action_6", bye.Id);
            Assert.AreEqual("MOTION/MREST0076.MOT", bye.MotionFor(true));
            Assert.AreEqual("MAN_6", bye.SoundFor(true));

            Assert.IsTrue(RoomChatCommand.TryParseRoomAction("昏", true, out var dizzy));
            Assert.AreEqual("action_5", dizzy.Id);
            Assert.AreEqual("MOTION/MREST0075.MOT", dizzy.MotionFor(true));
            Assert.AreEqual("MAN_5", dizzy.SoundFor(true));
            Assert.IsFalse(RoomChatCommand.TryParseRoomAction("昏", false, out _));   // 昏 is male-only
        }

        [Test]
        public void Parses_Male_Room_Action_Table_Separately()
        {
            Assert.IsTrue(RoomChatCommand.TryParseRoomAction("READY", true, out var action));
            Assert.AreEqual("action_0", action.Id);

            Assert.IsTrue(RoomChatCommand.TryParseRoomAction("88", true, out action));
            Assert.AreEqual("action_6", action.Id);

            Assert.IsFalse(RoomChatCommand.TryParseRoomAction("READY", false, out _));
        }

        [TestCase("笑")]
        [TestCase("笑一下")]
        [TestCase("/開始")]
        [TestCase("/加油")]
        [TestCase("")]
        public void Rejects_Non_Room_Action_Keyword(string text)
            => Assert.IsFalse(RoomChatCommand.TryParseRoomAction(text, out _));

        [TestCase("hello")]
        [TestCase("/0")]
        [TestCase("/169")]
        [TestCase("/unknown")]
        public void Rejects_Non_Expression_Command(string text)
            => Assert.IsFalse(RoomChatCommand.TryParseExpression(text, out _));
    }
}
