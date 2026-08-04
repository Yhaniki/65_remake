using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Sdo.Game.Net;
using Sdo.Net;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    /// <summary>
    /// 線上密語。釘住的是實際踩到的 bug:連線時密語任何真人都回「找不到玩家」——
    /// 因為 OnlineChatService 把 SendWhisper 直接轉給離線實作,而離線那份比的是寫死的假名冊。
    /// 收件人只有 server 找得到(它才有全服在線名冊,而且密語跨房)。
    /// </summary>
    public class OnlineChatWhisperTests
    {
        private const int SenderUser = 501;

        [Test]
        public void SendWhisperDoesNotFallBackToTheOfflineRoster()
        {
            var net = new NetClient();
            var local = new RecordingChatService();
            var chat = new OnlineChatService(net, local);
            try
            {
                chat.SendWhisper("某個真人", "你在嗎");

                Assert.AreEqual(0, local.WhisperCalls,
                    "密語不能再轉給離線實作 —— 它拿假名冊比名字,線上一定比不到");
                foreach (var m in chat.History)
                    Assert.AreNotEqual(WhisperKind.NoId, m.Whisper,
                        "「找不到玩家」只能由 server 判定,本機沒有全服名冊可以下這個結論");
            }
            finally
            {
                net.Disconnect("test");
            }
        }

        [Test]
        public void EmptyTargetOrBodyIsNotSent()
        {
            var net = new NetClient();
            var local = new RecordingChatService();
            var chat = new OnlineChatService(net, local);
            try
            {
                chat.SendWhisper("   ", "有內容沒對象");
                chat.SendWhisper("有對象", "   ");   // 只選了對象還沒打內容

                Assert.AreEqual(0, chat.History.Count, "兩者都不該產生任何一行");
                Assert.AreEqual(0, local.WhisperCalls);
            }
            finally
            {
                net.Disconnect("test");
            }
        }

        [Test]
        public void IncomingWhisperBecomesAnIncomingLine()
        {
            var chat = ChatWithWhisper(new NetWhisperMessage
            {
                Kind = NetProto.WhisperIn,
                Party = "對我說話的人",
                SenderUserId = 777,
                Text = "只給你看",
                Channel = "current",
            }, out var net);
            try
            {
                var m = Last(chat);
                Assert.AreEqual(WhisperKind.Incoming, m.Whisper);
                Assert.AreEqual("對我說話的人", m.WhisperParty);
                Assert.AreEqual("對我說話的人", m.Sender, "點名字回話要靠 Sender");
                Assert.AreEqual(777, m.SenderUserId);
                Assert.AreEqual("只給你看", m.Text);
                Assert.IsFalse(m.Local, "別人說的話不能標成本機");
            }
            finally
            {
                net.Disconnect("test");
            }
        }

        [Test]
        public void OutgoingEchoFromServerIsTheLocalLine()
        {
            var chat = ChatWithWhisper(new NetWhisperMessage
            {
                Kind = NetProto.WhisperOut,
                Party = "收到的人",
                SenderUserId = SenderUser,
                Text = "我說的話",
            }, out var net);
            try
            {
                var m = Last(chat);
                Assert.AreEqual(WhisperKind.Outgoing, m.Whisper);
                Assert.AreEqual("收到的人", m.WhisperParty, "自己那行要顯示對方的名字");
                Assert.IsTrue(m.Local, "「你對X說」是本機那一行");
            }
            finally
            {
                net.Disconnect("test");
            }
        }

        [Test]
        public void NoIdFromServerKeepsWhatThePlayerTyped()
        {
            var chat = ChatWithWhisper(new NetWhisperMessage
            {
                Kind = NetProto.WhisperNoId,
                Party = "打錯的名字",
            }, out var net);
            try
            {
                var m = Last(chat);
                Assert.AreEqual(WhisperKind.NoId, m.Whisper);
                Assert.AreEqual("打錯的名字", m.WhisperParty,
                    "要照樣顯示玩家打的字,他才知道自己打錯了什麼");
                Assert.IsTrue(m.Local);
            }
            finally
            {
                net.Disconnect("test");
            }
        }

        [Test]
        public void WhisperKeepsExpressionFields()
        {
            var chat = ChatWithWhisper(new NetWhisperMessage
            {
                Kind = NetProto.WhisperIn,
                Party = "帶表情的人",
                ExpressionId = 3,
                LeadingText = "前面",
                Text = "後面",
            }, out var net);
            try
            {
                var m = Last(chat);
                Assert.AreEqual(3, m.ExpressionId);
                Assert.AreEqual("前面", m.LeadingText);
                Assert.AreEqual("後面", m.Text);
            }
            finally
            {
                net.Disconnect("test");
            }
        }

        [Test]
        public void WhisperLinesReachTheScreenThroughMessageReceived()
        {
            var net = new NetClient();
            var chat = new OnlineChatService(net, new RecordingChatService());
            try
            {
                int raised = 0;
                chat.MessageReceived += _ => raised++;
                Deliver(chat, new NetWhisperMessage { Kind = NetProto.WhisperIn, Party = "誰", Text = "嗨" });

                Assert.AreEqual(1, raised, "畫面靠這個事件加聊天行,不發就等於沒收到");
                Assert.AreEqual(1, chat.History.Count);
            }
            finally
            {
                net.Disconnect("test");
            }
        }

        // ---- helpers ----

        private static OnlineChatService ChatWithWhisper(NetWhisperMessage w, out NetClient net)
        {
            net = new NetClient();
            var chat = new OnlineChatService(net, new RecordingChatService());
            Deliver(chat, w);
            return chat;
        }

        /// <summary>
        /// 直接餵一則 server 密語進去(不開真連線)。走 private 的 OnNetWhisper ——
        /// 與 NetClientRoomLifecycleTests 同樣的反射手法。
        /// </summary>
        private static void Deliver(OnlineChatService chat, NetWhisperMessage w)
        {
            var method = typeof(OnlineChatService).GetMethod("OnNetWhisper",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, "OnNetWhisper 不見了(改名的話這些測試要跟著改)");
            method.Invoke(chat, new object[] { w });
        }

        private static ChatMessage Last(OnlineChatService chat)
        {
            Assert.AreEqual(1, chat.History.Count, "應該剛好產生一行");
            return chat.History[0];
        }

        /// <summary>只記錄有沒有被呼叫的假離線實作 —— 用來證明密語沒有再掉回本機那條路。</summary>
        private sealed class RecordingChatService : IChatService
        {
            private readonly List<ChatMessage> _history = new List<ChatMessage>();

            public int WhisperCalls;
            public int GuildExpressionCalls;   // 沒家族時 OnlineChatService 會退回本機這一支

            public IReadOnlyList<ChatMessage> History => _history;
            public event Action<ChatMessage> MessageReceived;

            public void SendWhisper(string target, string body, ChatChannel channel = ChatChannel.Current)
                => WhisperCalls++;

            public void Send(string text, ChatChannel channel = ChatChannel.Current) { }
            public void SendExpression(int expressionId, ChatChannel channel = ChatChannel.Current) { }
            public void SendExpression(int expressionId, ChatChannel channel, string trailingText) { }
            public void SendExpression(int expressionId, ChatChannel channel, string leadingText, string trailingText) { }
            public void SendGuild(string text) { }
            public void SendGuildExpression(int expressionId, string leadingText, string trailingText)
                => GuildExpressionCalls++;
            public void SendSelfTalk(string text) { }
            public void SendSystem(string text) { }
            public void AnnounceStageEnter(string name) { }
            public void AnnounceStageLeave(string name) { }
            public void SetScope(ChatScope scope, int roomId = 0) { }
            public void Clear() => _history.Clear();
            public void Tick() { }

            // 介面上的事件沒被用到時 C# 會警告;明確留一個沒人呼叫的觸發點避免那個警告。
            private void Unused() => MessageReceived?.Invoke(null);
        }
    }
}
