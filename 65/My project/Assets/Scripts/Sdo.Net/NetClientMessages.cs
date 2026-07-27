using System.Collections.Generic;

namespace Sdo.Net
{
    /// <summary>
    /// client 端要解析的那些「非 roomState」訊息的 DTO。
    ///
    /// server 端是用 <see cref="JObj"/> 直接組出來的(它不需要這些型別),所以這裡只有 Decode。
    /// 放在 <c>Sdo.Net</c> 而不是 client 專屬的地方,是因為 loopback 假伺服器也要組出同樣的東西。
    ///
    /// 解析一律**寬鬆**:缺欄位就用預設值,不失敗。這些是 server 產生的訊息,
    /// 對不上代表版本不合而不是惡意 —— 讓玩家看到不完整的資訊,比讓他斷線莫名其妙好。
    /// (對比 <see cref="NetSongRef.TryDecode"/> 是嚴格的:那個壞掉會讓人跑錯譜面或寫檔到奇怪的位置。)
    /// </summary>
    public sealed class NetMatchStart
    {
        public long MatchId;
        public long StartEpochMs;
        public int LoadTimeoutMs = NetLimits.LoadTimeoutMs;

        /// <summary>這一場的舞者,依座位序。</summary>
        public NetMatchParticipant[] Participants = new NetMatchParticipant[0];

        /// <summary>要顯示在畫面右側的旁觀者名單。</summary>
        public string[] SpectatorNames = new string[0];

        /// <summary>server echo 的隨機值 —— **這是唯一該信的版本**,client 不要用自己算的。</summary>
        public NetResolvedRound Resolved;

        /// <summary>這一場要玩的歌(隨機難度時是抽出來的那首)。</summary>
        public NetSongRef Song;

        public NetRoomSettings Settings;

        /// <summary>本機在這一場裡是舞者嗎?(不是 → 它是旁觀者)</summary>
        public bool IsParticipant(int userId)
        {
            for (int i = 0; i < Participants.Length; i++)
                if (Participants[i].UserId == userId) return true;
            return false;
        }

        public static NetMatchStart Decode(object node)
        {
            var m = new NetMatchStart();
            if (node == null) return m;

            m.MatchId = NetJson.Long(node, "matchId");
            m.StartEpochMs = NetJson.Long(node, "startEpochMs");
            int t = NetJson.Int(node, "loadTimeoutMs", NetLimits.LoadTimeoutMs);
            if (t > 0) m.LoadTimeoutMs = t;

            var parts = NetJson.Arr(node, "participants");
            if (parts != null && parts.Count > 0)
            {
                var list = new List<NetMatchParticipant>(parts.Count);
                for (int i = 0; i < parts.Count; i++) list.Add(NetMatchParticipant.Decode(parts[i]));
                m.Participants = list.ToArray();
            }

            var specs = NetJson.Arr(node, "spectatorNames");
            if (specs != null && specs.Count > 0)
            {
                var names = new List<string>(specs.Count);
                for (int i = 0; i < specs.Count; i++)
                {
                    var s = specs[i] as string;
                    if (!string.IsNullOrEmpty(s)) names.Add(s);
                }
                m.SpectatorNames = names.ToArray();
            }

            NetResolvedRound resolved;
            if (NetResolvedRound.TryDecode(NetJson.Sub(node, "resolved"), out resolved)) m.Resolved = resolved;

            NetSongRef song;
            if (NetSongRef.TryDecode(NetJson.Sub(node, "song"), out song)) m.Song = song;

            m.Settings = NetRoomSettings.Decode(NetJson.Sub(node, "settings"));
            return m;
        }
    }

    /// <summary>這一場的一位舞者。</summary>
    public struct NetMatchParticipant
    {
        public int UserId;
        /// <summary>房間座位索引 —— 決定隊形的站位順序(見 FormationCatalog 的 slot 語意)。</summary>
        public int Seat;
        public string Name;
        public int Level;
        public int Team;
        public NetAvatarLook Look;

        public static NetMatchParticipant Decode(object node)
        {
            var p = new NetMatchParticipant();
            p.UserId = NetJson.Int(node, "userId");
            p.Seat = NetJson.Int(node, "seat");
            p.Name = NetJson.Str(node, "name");
            p.Level = NetJson.Int(node, "level");
            p.Team = (int)NetState.ClampTeam(NetJson.Int(node, "team", (int)TeamTag.Free));
            p.Look = NetAvatarLook.Decode(NetJson.Sub(node, "look"));
            return p;
        }
    }

    /// <summary>結算的一列。</summary>
    public struct NetResultRow
    {
        public int UserId;
        public string Name;
        public long Score;
        public int Perfect, Cool, Bad, Miss, MaxCombo;
        /// <summary>這個人是遊玩中斷線的(成績可能不完整)。</summary>
        public bool Disconnected;

        public static NetResultRow Decode(object node)
        {
            var r = new NetResultRow();
            r.UserId = NetJson.Int(node, "userId");
            r.Name = NetJson.Str(node, "name");
            r.Score = NetJson.Long(node, "score");
            r.Perfect = NetJson.Int(node, "perfect");
            r.Cool = NetJson.Int(node, "cool");
            r.Bad = NetJson.Int(node, "bad");
            r.Miss = NetJson.Int(node, "miss");
            r.MaxCombo = NetJson.Int(node, "maxCombo");
            r.Disconnected = NetJson.Bool(node, "disconnected");
            return r;
        }

        public static NetResultRow[] DecodeAll(List<object> arr)
        {
            if (arr == null || arr.Count == 0) return new NetResultRow[0];
            var rows = new NetResultRow[arr.Count];
            for (int i = 0; i < arr.Count; i++) rows[i] = Decode(arr[i]);
            return rows;
        }
    }

    /// <summary>
    /// 遊玩中某個人的最新分數快照。
    ///
    /// 這是**唯一**需要從網路取得的遊玩資料 —— 舞蹈本身是 DPS 編舞驅動的(同一首歌大家跳一樣),
    /// 所以不需要傳輸入或動作。收端用相鄰兩筆的判定計數差值推導出「這個 8 拍區塊有沒有斷、
    /// 有沒有中」,再套 <c>Sdo.Ruleset.DanceGate</c> 的同一組規則重現遠端舞者的跳/停。
    ///
    /// ⚠️ 這個推導的前提是 **client 在每個 8 拍結算點一定送一筆**(見
    /// <see cref="NetLimits.ClientFrameIntervalMs"/> 的說明)。少了那個保證,一筆 frame 會
    /// 跨越多個 8 拍區塊,中間的斷/中資訊就丟失了。
    /// </summary>
    public struct NetFrameRow
    {
        public int UserId;
        public double TMs;
        public long Score;
        public int Combo, MaxCombo;
        public float Hp;
        public int Perfect, Cool, Bad, Miss;

        /// <summary>判定總數 —— 用來判斷「這個區塊有沒有中到 note」。</summary>
        public int TotalJudged => Perfect + Cool + Bad + Miss;

        /// <summary>斷連擊的判定數 —— 用來判斷「這個區塊有沒有斷」。</summary>
        public int BreakingJudged => Bad + Miss;

        public static NetFrameRow Decode(object node)
        {
            var f = new NetFrameRow();
            f.UserId = NetJson.Int(node, "userId");
            f.TMs = NetJson.Num(node, "tMs");
            f.Score = NetJson.Long(node, "score");
            f.Combo = NetJson.Int(node, "combo");
            f.MaxCombo = NetJson.Int(node, "maxCombo");
            f.Hp = (float)NetJson.Num(node, "hp");
            f.Perfect = NetJson.Int(node, "p");
            f.Cool = NetJson.Int(node, "c");
            f.Bad = NetJson.Int(node, "b");
            f.Miss = NetJson.Int(node, "m");
            return f;
        }

        public static NetFrameRow[] DecodeAll(List<object> arr)
        {
            if (arr == null || arr.Count == 0) return new NetFrameRow[0];
            var rows = new NetFrameRow[arr.Count];
            for (int i = 0; i < arr.Count; i++) rows[i] = Decode(arr[i]);
            return rows;
        }
    }

    /// <summary>一則聊天訊息。欄位對映 client 既有的 <c>Sdo.UI.Services.ChatMessage</c>。</summary>
    public struct NetChatMessage
    {
        public int SenderUserId;
        public string Sender;
        public string Text;
        public string Channel;
        public int ExpressionId;
        public string LeadingText;
        public int RoomId;

        public static NetChatMessage Decode(object node)
        {
            var m = new NetChatMessage();
            m.SenderUserId = NetJson.Int(node, "senderUserId");
            m.Sender = NetJson.Str(node, "sender");
            m.Text = NetJson.Str(node, "text");
            m.Channel = NetJson.Str(node, "channel", "current");
            m.ExpressionId = NetJson.Int(node, "expressionId");
            m.LeadingText = NetJson.Str(node, "leadingText");
            m.RoomId = NetJson.Int(node, "roomId");
            return m;
        }
    }

    /// <summary>房間列表的一列。</summary>
    public struct NetRoomListEntry
    {
        public int Code;
        public string Name;
        public string HostName;
        public RoomStatus Status;
        public int Count, Capacity, Spectators;
        public int Mode;
        public string SongTitle;

        public bool IsFull => Count >= Capacity;

        public static NetRoomListEntry Decode(object node)
        {
            var e = new NetRoomListEntry();
            e.Code = NetJson.Int(node, "code");
            e.Name = NetJson.Str(node, "name");
            e.HostName = NetJson.Str(node, "hostName");

            RoomStatus st;
            if (!NetState.TryParseRoomStatus(NetJson.Str(node, "status"), out st)) st = RoomStatus.Open;
            e.Status = st;

            e.Count = NetJson.Int(node, "count");
            int cap = NetJson.Int(node, "capacity", NetLimits.RoomCapacity);
            e.Capacity = cap > 0 ? cap : NetLimits.RoomCapacity;
            e.Spectators = NetJson.Int(node, "spectators");
            e.Mode = NetJson.Int(node, "mode");
            e.SongTitle = NetJson.Str(node, "songTitle");
            return e;
        }

        public static NetRoomListEntry[] DecodeAll(List<object> arr)
        {
            if (arr == null || arr.Count == 0) return new NetRoomListEntry[0];
            var rows = new NetRoomListEntry[arr.Count];
            for (int i = 0; i < arr.Count; i++) rows[i] = Decode(arr[i]);
            return rows;
        }
    }
}
