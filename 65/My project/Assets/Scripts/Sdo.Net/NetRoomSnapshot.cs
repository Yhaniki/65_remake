namespace Sdo.Net
{
    /// <summary>
    /// 房間層級的設定(**只有房主能改**)。
    ///
    /// 注意這裡**不包含**速度 / note 皮 / 掉落方向 —— 那三個在官方是**個人偏好**
    /// (而且 client 會 <c>RoomConfig.Save()</c> 寫進自己的 config.ini),所以不同步、不 gate。
    /// 這是使用者的決定:「只有選歌是房主專屬」。
    /// </summary>
    public sealed class NetRoomSettings
    {
        /// <summary>0=自由模式 1=普通模式 2=ShowTime(氣條)模式。對映 <c>GameSession.GameMode</c>。</summary>
        public int GameMode;

        /// <summary>
        /// 隊形選擇 0..3。**3 = 隨機**(不是第四種隊形)—— 開場時由 host 抽成 0..2 放進
        /// <see cref="NetResolvedRound.FormationType"/>。對映 <c>GameSession.Formation</c>。
        /// </summary>
        public int Formation;

        /// <summary>允許的旁觀人數 0..<see cref="NetLimits.MaxSpectators"/>。對映 <c>GameSession.LookerCount</c>。</summary>
        public int LookerCount = NetLimits.MaxSpectators;

        /// <summary>指定的場景 id(<see cref="SceneRandom"/> 為 false 時有效)。對映 <c>GameSession.StageId</c>。</summary>
        public int SceneId = 9;

        /// <summary>
        /// true = 每局重抽場景。房間的場景縮圖顯示 RANDOM。
        /// 🔴 線上時**不在選歌那一刻 resolve**(離線版是那樣做的)—— 要留到開場才由 host 抽,
        /// server echo 給所有人,否則各 client 會看到不同場景。
        /// </summary>
        public bool SceneRandom = true;

        public JObj Encode()
            => JObj.New()
                .Int("gameMode", GameMode)
                .Int("formation", Formation)
                .Int("lookerCount", LookerCount)
                .Int("sceneId", SceneId)
                .Bool("sceneRandom", SceneRandom);

        /// <summary>解析 + 夾值(寬鬆:設定值壞掉夾回合法範圍,不斷線)。</summary>
        public static NetRoomSettings Decode(object node)
        {
            var s = new NetRoomSettings();
            if (node == null) return s;

            s.GameMode = Clamp(NetJson.Int(node, "gameMode"), 0, 2);
            s.Formation = Clamp(NetJson.Int(node, "formation"), 0, 3);
            s.LookerCount = Clamp(NetJson.Int(node, "lookerCount", NetLimits.MaxSpectators), 0, NetLimits.MaxSpectators);
            s.SceneId = Clamp(NetJson.Int(node, "sceneId", 9), 0, NetLimits.MaxSceneId);
            s.SceneRandom = NetJson.Bool(node, "sceneRandom", true);
            return s;
        }

        /// <summary>只套用 JSON 裡**真的有帶**的欄位(<c>setRoomSettings</c> 允許送任意子集)。</summary>
        public void ApplyPatch(object node)
        {
            if (node == null) return;
            if (NetJson.Has(node, "gameMode")) GameMode = Clamp(NetJson.Int(node, "gameMode"), 0, 2);
            if (NetJson.Has(node, "formation")) Formation = Clamp(NetJson.Int(node, "formation"), 0, 3);
            if (NetJson.Has(node, "lookerCount")) LookerCount = Clamp(NetJson.Int(node, "lookerCount"), 0, NetLimits.MaxSpectators);
            if (NetJson.Has(node, "sceneId")) SceneId = Clamp(NetJson.Int(node, "sceneId"), 0, NetLimits.MaxSceneId);
            if (NetJson.Has(node, "sceneRandom")) SceneRandom = NetJson.Bool(node, "sceneRandom");
        }

        public NetRoomSettings Clone()
            => new NetRoomSettings
            {
                GameMode = GameMode, Formation = Formation, LookerCount = LookerCount,
                SceneId = SceneId, SceneRandom = SceneRandom,
            };

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }

    /// <summary>
    /// 房間的完整狀態快照。**server 是唯一作者,client 只讀。**
    ///
    /// 設計決策:每次任何變更都推**整份** snapshot,而不是 delta。
    /// 6 人 × 約 1 KB 對這個規模完全不是問題,而且消滅一整類「兩邊狀態慢慢對不上」的 bug ——
    /// delta 協定的每一個漏掉的事件都會讓 client 永久偏離,而且症狀出現的地方離原因很遠。
    ///
    /// <see cref="Rev"/> 單調遞增;client 丟掉 <c>rev &lt;= 已見過的</c>
    /// (TCP 有序其實不會亂序,但 loopback 假伺服器與測試需要這個保護)。
    /// </summary>
    public sealed class NetRoomSnapshot
    {
        /// <summary>房名的長度上限。</summary>
        public const int MaxNameLength = NetLimits.MaxRoomNameChars;

        /// <summary>修訂號,每次變更 +1。</summary>
        public int Rev;

        /// <summary>5 位數房號。</summary>
        public int Code;

        /// <summary>自訂房名。空 = client 用「房主名 + 的舞蹈室」(見 <c>RoomLabels.DisplayName</c>)。</summary>
        public string Name = "";

        /// <summary>
        /// 房主的 userId。**不是 seat 0** —— 房主轉移時只換這個值,不搬座位,
        /// 所以房主徽章要跟著這個欄位畫而不是跟著座位索引。
        /// </summary>
        public int HostUserId;

        public RoomStatus Status = RoomStatus.Open;

        public int Capacity = NetLimits.RoomCapacity;

        public NetSeat[] Seats;

        public NetSpectator[] Spectators;

        /// <summary>房主選的歌。null = 還沒選。</summary>
        public NetSongRef Song;

        public NetRoomSettings Settings = new NetRoomSettings();

        public NetRoomSnapshot()
        {
            Seats = new NetSeat[NetLimits.RoomCapacity];
            for (int i = 0; i < Seats.Length; i++) Seats[i] = new NetSeat();
            Spectators = new NetSpectator[0];
        }

        // ---- 查詢 helper(client 與 server 共用) ----

        /// <summary>有人坐著的座位數。</summary>
        public int SeatedCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Seats.Length; i++) if (Seats[i].IsTaken) n++;
                return n;
            }
        }

        /// <summary>還有沒有可坐的空位?(被關閉的不算)</summary>
        public bool HasOpenSeat => FirstOpenSeat() >= 0;

        /// <summary>第一個可坐的空位索引(依索引序 —— 加入房間就是坐這個);沒有回 -1。</summary>
        public int FirstOpenSeat()
        {
            for (int i = 0; i < Seats.Length; i++) if (Seats[i].IsOpen) return i;
            return -1;
        }

        /// <summary>這個人坐在哪個座位?不在座位上(旁觀或不在房裡)回 -1。</summary>
        public int SeatIndexOf(int userId)
        {
            if (userId == 0) return -1;
            for (int i = 0; i < Seats.Length; i++)
                if (Seats[i].IsTaken && Seats[i].UserId == userId) return i;
            return -1;
        }

        public NetSeat SeatOf(int userId)
        {
            int i = SeatIndexOf(userId);
            return i < 0 ? null : Seats[i];
        }

        /// <summary>這個人是旁觀者嗎?回索引,不是則 -1。</summary>
        public int SpectatorIndexOf(int userId)
        {
            if (userId == 0 || Spectators == null) return -1;
            for (int i = 0; i < Spectators.Length; i++)
                if (Spectators[i].UserId == userId) return i;
            return -1;
        }

        /// <summary>這個人是房主嗎?</summary>
        public bool IsHost(int userId) => userId != 0 && userId == HostUserId;

        /// <summary>這個人在這間房裡(座位或旁觀)嗎?</summary>
        public bool Contains(int userId)
            => SeatIndexOf(userId) >= 0 || SpectatorIndexOf(userId) >= 0;

        /// <summary>每隊各有幾個座位玩家(索引 0..2 = A/B/C;自由不計)。給組隊版型判定用。</summary>
        public int[] TeamCounts()
        {
            var counts = new int[TeamLayoutRules.MaxTeams];
            for (int i = 0; i < Seats.Length; i++)
            {
                if (!Seats[i].IsTaken) continue;
                int t = Seats[i].Team;
                if (t >= 0 && t < counts.Length) counts[t]++;
            }
            return counts;
        }

        /// <summary>有任何座位玩家選了隊伍(不是「自由」)嗎? = 組隊模式。</summary>
        public bool AnyTeamSelected()
        {
            for (int i = 0; i < Seats.Length; i++)
                if (Seats[i].IsTaken && Seats[i].Team != (int)TeamTag.Free) return true;
            return false;
        }

        // ---- codec ----

        /// <summary>組出完整的 <c>roomState</c> 訊息(含 <c>t</c>)。</summary>
        public JObj EncodeMessage()
        {
            var seats = JArr.New();
            for (int i = 0; i < Seats.Length; i++) seats.Add(Seats[i].Encode());

            var specs = JArr.New();
            if (Spectators != null)
                for (int i = 0; i < Spectators.Length; i++) specs.Add(Spectators[i].Encode());

            return JObj.New()
                .Str(NetProto.FieldType, NetProto.RoomState)
                .Int("rev", Rev)
                .Int("code", Code)
                .Str("name", Name)
                .Int("hostUserId", HostUserId)
                .Str("status", NetState.ToWire(Status))
                .Int("capacity", Capacity)
                .Put("seats", seats)
                .Put("spectators", specs)
                .Put("song", Song != null ? Song.Encode() : null)
                .Put("settings", Settings != null ? Settings.Encode() : null);
        }

        /// <summary>
        /// 從 <c>roomState</c> 訊息解出快照(client 端用)。
        ///
        /// 寬鬆解析:座位/設定壞掉就退成預設值,不斷線 —— 這個訊息是 server 產生的,
        /// 壞掉代表版本不合而不是惡意,而且斷線只會讓玩家更困惑。
        /// 但**歌曲參照**是嚴格的(解不出來就當「沒選歌」)—— 寧可顯示沒選歌,
        /// 也不要讓玩家去載一個路徑可疑的東西。
        /// </summary>
        public static NetRoomSnapshot Decode(object node)
        {
            var r = new NetRoomSnapshot();
            if (node == null) return r;

            r.Rev = NetJson.Int(node, "rev");
            r.Code = NetJson.Int(node, "code");
            r.Name = Clip(NetJson.Str(node, "name"));
            r.HostUserId = NetJson.Int(node, "hostUserId");

            RoomStatus st;
            if (!NetState.TryParseRoomStatus(NetJson.Str(node, "status"), out st)) st = RoomStatus.Open;
            r.Status = st;

            int cap = NetJson.Int(node, "capacity", NetLimits.RoomCapacity);
            r.Capacity = cap > 0 && cap <= NetLimits.RoomCapacity ? cap : NetLimits.RoomCapacity;

            var seats = NetJson.Arr(node, "seats");
            if (seats != null)
            {
                int n = seats.Count < r.Seats.Length ? seats.Count : r.Seats.Length;
                for (int i = 0; i < n; i++) r.Seats[i] = NetSeat.Decode(seats[i]);
            }

            var specs = NetJson.Arr(node, "spectators");
            if (specs != null && specs.Count > 0)
            {
                int n = specs.Count < NetLimits.MaxSpectators ? specs.Count : NetLimits.MaxSpectators;
                var list = new NetSpectator[n];
                for (int i = 0; i < n; i++) list[i] = NetSpectator.Decode(specs[i]);
                r.Spectators = list;
            }

            var songNode = NetJson.Sub(node, "song");
            if (songNode != null)
            {
                NetSongRef song;
                if (NetSongRef.TryDecode(songNode, out song)) r.Song = song;
                // 解不出來 → Song 留 null(顯示成「沒選歌」)。刻意不斷線。
            }

            r.Settings = NetRoomSettings.Decode(NetJson.Sub(node, "settings"));
            return r;
        }

        private static string Clip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= MaxNameLength ? s : s.Substring(0, MaxNameLength);
        }
    }
}
