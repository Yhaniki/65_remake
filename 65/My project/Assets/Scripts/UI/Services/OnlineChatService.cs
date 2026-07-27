using System;
using System.Collections.Generic;
using Sdo.Game.Net;
using Sdo.Net;

namespace Sdo.UI.Services
{
    /// <summary>
    /// 連線版聊天:房間裡打的字經由 server 廣播給同房所有人。
    ///
    /// **本機專屬的那些行不上網** —— 密語、家族頻道、「你說」、系統提示、進出舞台廣播都還是走
    /// 底下那個離線實作(<paramref name="local"/>),因為它們本來就只給自己看,或者(密語/家族)
    /// 需要伺服器端的名冊與家族資料,那是後面的階段。這個類別只接管一件事:**同房的公開發言**。
    ///
    /// 🔴 送出時不在本機先畫一行。server 會把訊息廣播回**包含自己**的所有人,所以本機只要等它回來 ——
    /// 這樣「自己看到的」與「別人看到的」是同一份資料,不會出現「本機顯示了但其實沒送出去」那種鬼故事。
    /// 代價是自己的字會晚一個 round-trip 才出現(區網下看不出來)。
    /// </summary>
    public sealed class OnlineChatService : IChatService
    {
        private readonly NetClient _net;
        private readonly IChatService _local;
        private readonly Func<bool> _localIsMale;

        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        private ChatScope _scope = ChatScope.Lobby;
        private int _scopeRoomId;

        public event Action<ChatMessage> MessageReceived;
        public IReadOnlyList<ChatMessage> History => _history;

        public OnlineChatService(NetClient net, IChatService local, Func<bool> localIsMale = null)
        {
            _net = net;
            _local = local;
            _localIsMale = localIsMale;

            // 底層(離線實作)產生的本機專屬行也要進到同一份歷史 —— 畫面只讀 Ctx.Chat.History,
            // 兩份歷史會讓密語/家族/系統訊息整個不見。
            if (_local != null) _local.MessageReceived += Add;
            if (_net != null) _net.ChatReceived += OnNetChat;
        }

        // ---- 送出 ----

        public void Send(string text, ChatChannel channel = ChatChannel.Current)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // 指令的判斷與離線版**完全一樣**(同一組 RoomChatCommand),否則同一句話在單機與連線
            // 會有不同行為 —— 那是玩家最難理解的一種不一致。
            string target, body;
            if (RoomChatCommand.TryParseWhisper(text, out target, out body))
            {
                SendWhisper(target, body, channel);   // 密語:MVP 仍是本機
                return;
            }

            int expressionId; string leading, trailing;
            if (RoomChatCommand.TryParseExpression(text, out expressionId, out leading, out trailing))
            {
                SendExpression(expressionId, channel, leading, trailing);
                return;
            }

            _net.SendChat(text.Trim(), ChannelWire(channel));
        }

        public void SendExpression(int expressionId, ChatChannel channel = ChatChannel.Current)
            => SendExpression(expressionId, channel, null, null);

        public void SendExpression(int expressionId, ChatChannel channel, string trailingText)
            => SendExpression(expressionId, channel, null, trailingText);

        public void SendExpression(int expressionId, ChatChannel channel, string leadingText, string trailingText)
        {
            if (!RoomChatCommand.IsValidExpression(expressionId)) return;
            _net.SendChat((trailingText ?? "").Trim(), ChannelWire(channel), expressionId,
                          (leadingText ?? "").Trim());
        }

        // ---- 本機專屬:整批轉給離線實作 ----

        public void SendWhisper(string target, string body, ChatChannel channel = ChatChannel.Current)
            => _local?.SendWhisper(target, body, channel);

        public void SendGuild(string text) => _local?.SendGuild(text);
        public void SendSelfTalk(string text) => _local?.SendSelfTalk(text);
        public void SendSystem(string text) => _local?.SendSystem(text);
        public void AnnounceStageEnter(string name) => _local?.AnnounceStageEnter(name);
        public void AnnounceStageLeave(string name) => _local?.AnnounceStageLeave(name);

        public void SetScope(ChatScope scope, int roomId = 0)
        {
            _scope = scope;
            _scopeRoomId = roomId;
            _local?.SetScope(scope, roomId);   // 底層產生的行也要蓋同一個作用域
        }

        public void Clear()
        {
            _history.Clear();
            _local?.Clear();
        }

        public void Tick() => _local?.Tick();

        // ---- 收到 ----

        private void OnNetChat(NetChatMessage m)
        {
            // 發言者的性別查一次就好 —— 解 RoomActionId 與顯示端取 clip/語音都要用同一個值,
            // 否則會出現「用女生的 id 解出來、卻播男生的動作」那種不自洽。
            bool senderMale = SenderIsMale(m.SenderUserId);

            var msg = new ChatMessage
            {
                SenderUserId = m.SenderUserId,   // 頭上泡/動作要掛到哪一個 3D 角色身上,靠這個找人
                SenderMale = senderMale,
                Sender = m.Sender ?? "",
                Text = m.Text ?? "",
                TimeMs = NowMs(),
                ExpressionId = m.ExpressionId,
                LeadingText = m.LeadingText ?? "",
                Channel = ChannelOf(m.Channel),
                // 自己的發言:標成 local,顯示端才會當成「我說的」(顏色/頭上泡的判斷都看它)。
                Local = _net != null && m.SenderUserId == _net.UserId,
                Scope = ChatScope.Room,
                RoomId = m.RoomId,
            };

            // 房間動作(關鍵字 → 舞蹈動作)在**收端**重新判斷,不由發送端決定:
            // 動作表是分性別的,而「誰說的」在收到時才知道 —— 用發言者自己的性別查表,
            // 否則同一句話在別人畫面上會做出另一個動作。
            if (msg.ExpressionId == 0 && !string.IsNullOrEmpty(msg.Text))
            {
                RoomChatAction action;
                if (RoomChatCommand.TryParseRoomAction(msg.Text, senderMale, out action) && action != null)
                    msg.RoomActionId = action.Id;
            }

            Add(msg);
        }

        /// <summary>發言者是男的嗎?從房間快照的座位查(查不到 → 退回本機性別)。</summary>
        private bool SenderIsMale(int userId)
        {
            var room = _net != null ? _net.Room : null;
            if (room != null && userId != 0)
            {
                for (int i = 0; i < room.Seats.Length; i++)
                {
                    var s = room.Seats[i];
                    if (s.IsTaken && s.UserId == userId) return s.Look.Gender == 1;
                }
                foreach (var sp in room.Spectators)
                    if (sp.UserId == userId) return sp.Look.Gender == 1;
            }
            return _localIsMale != null && _localIsMale();
        }

        private void Add(ChatMessage m)
        {
            _history.Add(m);
            if (_history.Count > 200) _history.RemoveAt(0);
            var h = MessageReceived;
            if (h != null) h(m);
        }

        private static double NowMs() => UnityEngine.Time.realtimeSinceStartupAsDouble * 1000.0;

        // wire 上的頻道名與 UI 的 enum 對照。server 只是原封轉發,所以兩邊用同一組字串就好。
        private static string ChannelWire(ChatChannel c)
        {
            switch (c)
            {
                case ChatChannel.Family: return "family";
                case ChatChannel.Friend: return "friend";
                case ChatChannel.Reply: return "reply";
                default: return "current";
            }
        }

        private static ChatChannel ChannelOf(string s)
        {
            switch (s)
            {
                case "family": return ChatChannel.Family;
                case "friend": return ChatChannel.Friend;
                case "reply": return ChatChannel.Reply;
                default: return ChatChannel.Current;
            }
        }
    }
}
