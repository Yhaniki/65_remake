using System;
using System.Collections.Generic;
using Sdo.Game.Net;
using Sdo.Net;

namespace Sdo.UI.Services
{
    /// <summary>
    /// 連線版聊天。上網的有兩件事:**同房的公開發言**(廣播)與**密語**(server 照名字找人,跨房)。
    ///
    /// **本機專屬的那些行不上網** —— 「你說」、系統提示、進出舞台廣播都還是走底下那個離線實作
    /// (<paramref name="local"/>),它們本來就只給自己看。
    ///
    /// 🔴 **家族頻道現在會上網了**(以前也歸在上面那組,註解寫「需要伺服器端的家族資料,那是後面的階段」):
    ///    server 認得 <c>channel="family"</c>,而且**只轉發給同一個家族的人**(見 Hub 的 <c>OnChatSay</c>
    ///    與 <c>SameGuild</c>)——家族名是握手時就帶上去的,不需要另一套家族系統。
    ///    沒有家族的人送家族訊息仍走離線那份:它會回一行「你沒有家族」,那句話本來就只給自己看。
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
        /// <summary>本機這個角色的家族名(空 = 沒有家族)。家族訊息要不要上網就看它 —— 見 <see cref="SendGuild"/>。</summary>
        private readonly Func<string> _localGuild;

        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        private ChatScope _scope = ChatScope.Lobby;
        private int _scopeRoomId;

        public event Action<ChatMessage> MessageReceived;
        public IReadOnlyList<ChatMessage> History => _history;

        public OnlineChatService(NetClient net, IChatService local, Func<bool> localIsMale = null,
                                 Func<string> localGuild = null)
        {
            _net = net;
            _local = local;
            _localIsMale = localIsMale;
            _localGuild = localGuild;

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

        /// <summary>
        /// 家族頻道。**有家族就真的上網**(server 只轉發給同一個家族的人,見 Hub 的 <c>OnChatSay</c>),
        /// 沒有家族則交給底下那份離線實作 —— 它會回一行綠字「你沒有家族」,而那句話本來就只給自己看。
        ///
        /// 🔴 上網的那條**不要**在本機先畫一行:server 會把它廣播回**包含自己**的所有同族(與一般發言同一條路),
        ///    自己先畫就會看到兩份。收端靠 <c>channel=="family"</c> 標回綠字,見 <see cref="OnNetChat"/>。
        /// </summary>
        public void SendGuild(string text)
        {
            string msg = text != null ? text.Trim() : "";
            if (msg.Length == 0) return;
            string guild = _localGuild != null ? _localGuild() : null;
            if (string.IsNullOrWhiteSpace(guild)) { _local?.SendGuild(text); return; }
            _net.SendChat(msg, ChannelWire(ChatChannel.Family));
        }

        // 家族頻道的表情:與 SendGuild 同一條路(family 頻道 + 沒家族就退回本機的「你沒有家族」),
        // 只是多帶 expressionId/leadingText —— server 的 chat 封包本來就有這兩個欄位,收端照 family
        // 標成 Guild=true 且 ExpressionId>0,顯示端就畫得出 emoji。
        public void SendGuildExpression(int expressionId, string leadingText, string trailingText)
        {
            if (!RoomChatCommand.IsValidExpression(expressionId)) return;
            string guild = _localGuild != null ? _localGuild() : null;
            if (string.IsNullOrWhiteSpace(guild)) { _local?.SendGuildExpression(expressionId, leadingText, trailingText); return; }
            _net.SendChat((trailingText ?? "").Trim(), ChannelWire(ChatChannel.Family), expressionId,
                          (leadingText ?? "").Trim());
        }

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

        // 進出舞台廣播是離線那份產生的,但它 Emit 出來的行也被轉進**這一份**歷史(建構式掛的 Add),
        // 所以兩邊都要清 —— 只清一邊的話畫面讀的那份還留著。
        public void ClearStageAnnouncements()
        {
            _history.RemoveAll(m => m != null && m.Stage != StageEventKind.None);
            _local?.ClearStageAnnouncements();
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
                // roomId=0 = server 標的「這句話發生在大廳」(房間號從 1 起,見 Hub 的 OnChatSay)。
                // 以前這裡寫死 Room,大廳的發言會被標成「某間房裡說的」。
                Scope = m.RoomId > 0 ? ChatScope.Room : ChatScope.Lobby,
                RoomId = m.RoomId,
                // family 頻道的發言 = 家族訊息(綠字「<家族>名字: 內容」)。server 只轉發給同族,
                // 所以收得到就代表是自己人;顯示端靠這個旗標決定顏色與分類,與離線那份一致。
                Guild = string.Equals(m.Channel, "family", StringComparison.OrdinalIgnoreCase),
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
            if (Muted(m)) return;
            _history.Add(m);
            if (_history.Count > 200) _history.RemoveAt(0);
            var h = MessageReceived;
            if (h != null) h(m);
        }

        /// <summary>
        /// 這則要不要**整個丟掉**(發言者在本機黑名單上)。這就是「加入黑名單 / 設置阻止」在這套連線裡
        /// 做得到的全部語意 —— server 沒有 per-user 的過濾,它把每則訊息原封廣播給所有人,
        /// 能決定「不顯示」的只有收訊端(見 <see cref="Sdo.Settings.BlockList"/>)。
        ///
        /// 🔴 進 <see cref="_history"/> **之前**就擋掉,不是畫的時候才濾:歷史會被切頻道/換畫面時重播,
        ///    留在裡面遲早會從某條路徑漏出來。代價是「封鎖之後才解除」看不到中間那段話 —— 本來就該看不到。
        /// 🔴 擋的是**他說的話**,不是「關於他的一切」:<c>Local</c>(自己說的)、系統行、進出舞台廣播、
        ///    本機提示行一律放行。進出舞台那行的 <c>Sender</c> 也是玩家名 —— 把它一起吃掉的話,
        ///    被封鎖的人進了你的房間你會完全不知道。
        /// </summary>
        private static bool Muted(ChatMessage m)
        {
            if (m == null || m.Local || m.System) return false;
            if (m.Stage != StageEventKind.None || m.Notice != ChatNotice.None) return false;
            string sender = (m.Sender ?? "").Trim();
            if (sender.Length == 0) return false;
            return Sdo.Settings.BlockList.IsBlocked(Sdo.Settings.ProfileManager.Active, sender);
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
