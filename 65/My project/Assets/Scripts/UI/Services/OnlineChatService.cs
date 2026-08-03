using System;
using System.Collections.Generic;
using Sdo.Game.Net;
using Sdo.Net;

namespace Sdo.UI.Services
{
    /// <summary>
    /// 連線版聊天。上網的有兩件事:**同房的公開發言**(廣播)與**密語**(server 照名字找人,跨房)。
    ///
    /// **本機專屬的那些行不上網** —— 家族頻道、「你說」、系統提示、進出舞台廣播都還是走底下那個
    /// 離線實作(<paramref name="local"/>),因為它們本來就只給自己看,或者(家族)需要伺服器端的
    /// 家族資料,那是後面的階段。
    ///
    /// 🔴 送出時不在本機先畫一行。server 會把訊息廣播回**包含自己**的所有人(密語則是單獨回一份
    /// kind=out 給發送者),所以本機只要等它回來 —— 這樣「自己看到的」與「別人看到的」是同一份資料,
    /// 不會出現「本機顯示了但其實沒送出去」那種鬼故事。密語還多一層:對方到底存不存在只有 server
    /// 知道,本機沒有全服名冊可查。代價是自己的字會晚一個 round-trip 才出現(區網下看不出來)。
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
            if (_net != null)
            {
                _net.ChatReceived += OnNetChat;
                _net.WhisperReceived += OnNetWhisper;
            }
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
                SendWhisper(target, body, channel);
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

        /// <summary>
        /// 密語走 server —— 不是本機。
        ///
        /// 🔴 之前這裡直接轉給離線實作,而離線那份是拿寫死的假名冊(_onlineNames)比名字的,
        /// 所以線上密語任何真人都是「找不到玩家」。收件人只有 server 找得到:它是唯一
        /// 握有全服在線名冊的一方,而且密語跨房,連同房快照都不夠用。
        ///
        /// 送出後本機**不畫任何東西**,「你對X說」那行等 server 回 whisperMsg(kind=out)才出現,
        /// 與公開發言同一套哲學(見類別註解)——沒送到就不該看到自己說了話。
        /// </summary>
        public void SendWhisper(string target, string body, ChatChannel channel = ChatChannel.Current)
        {
            string tgt = target != null ? target.Trim() : "";
            string msg = body != null ? body.Trim() : "";
            if (tgt.Length == 0 || msg.Length == 0) return;   // 只選了對象還沒打內容 → 不送
            if (_net == null) return;

            // 內容裡夾的表情指令([名字] /GO)在送出前就解好:server 只原封轉發,
            // 兩邊拿到同一組 expressionId/leading/text,收端不必也不會再解一次。
            int exprId; string lead, trail;
            if (!RoomChatCommand.TryParseExpression(msg, out exprId, out lead, out trail))
            {
                exprId = 0;
                lead = "";
                trail = msg;
            }
            _net.SendWhisper(tgt, trail, ChannelWire(channel), exprId, lead);
        }

        // ---- 本機專屬:整批轉給離線實作 ----

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

        /// <summary>
        /// server 回來的密語:三種 kind 各對應顯示端已經會畫的一種行
        /// (「你對X說」/「X對你說」/「找不到玩家X」),這裡只做欄位對應。
        ///
        /// 文案與離線版共用 <see cref="ChatDisplay.WhisperText"/>,所以線上/單機看到的字一模一樣。
        /// 密語行不彈頭上泡、不觸發舞蹈動作(RoomScreen 依 <c>Whisper != None</c> 判斷),
        /// 因此這裡刻意不解 RoomActionId。
        /// </summary>
        private void OnNetWhisper(NetWhisperMessage m)
        {
            string party = m.Party ?? "";
            var msg = new ChatMessage
            {
                WhisperParty = party,
                Text = m.Text ?? "",
                ExpressionId = m.ExpressionId,
                LeadingText = m.LeadingText ?? "",
                Channel = ChannelOf(m.Channel),
                TimeMs = NowMs(),
                // 密語其實不受作用域過濾(跨大廳/房間都看得到),但還是蓋上當下的作用域,
                // 讓歷史裡每一則都有一致的欄位 —— 與離線實作的 Emit 同樣做法。
                Scope = _scope,
                RoomId = _scopeRoomId,
            };

            switch (m.Kind)
            {
                case NetProto.WhisperOut:
                    msg.Whisper = WhisperKind.Outgoing;
                    msg.Local = true;
                    msg.SenderUserId = m.SenderUserId;
                    break;

                case NetProto.WhisperNoId:
                    msg.Whisper = WhisperKind.NoId;
                    msg.Local = true;
                    msg.Sender = "系統";
                    break;

                default:   // WhisperIn
                    msg.Whisper = WhisperKind.Incoming;
                    msg.Sender = party;                 // 「X 對你說」的 X:點名字回話要用
                    msg.SenderUserId = m.SenderUserId;
                    msg.SenderMale = SenderIsMale(m.SenderUserId);
                    break;
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
