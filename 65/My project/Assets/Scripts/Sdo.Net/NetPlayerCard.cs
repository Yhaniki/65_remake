namespace Sdo.Net
{
    /// <summary>
    /// 一個玩家的**公開名片** —— 個人資料視窗看「別人」時要顯示的那些數字。
    ///
    /// 為什麼需要這個訊息:這些資料原本只存在玩家**自己那台機器**的 <c>profile.json</c> 裡
    /// (累計判定數、勝負、經驗值、知名度、四格自我介紹),所以點開別人的資料整頁都是 0,
    /// 視窗裡還印著一句「伺服器不保存玩家統計」。玩家回報:那些資料要定期送上 server,
    /// 別人點開才看得到。這就是那條路。
    ///
    /// 🔴 **這是「自己報上來的」資料,不是 server 算出來的。** server 不驗證、也驗證不了 ——
    ///    這套連線沒有帳號系統,判定數本來就發生在 client。它與 <c>setIdentity</c> / <c>setLook</c>
    ///    是同一個信任等級:拿來顯示可以,拿來做排行榜或獎勵**不行**。
    ///
    /// 🔴 **外觀不在這裡面。** server 已經有一份(<c>setLook</c> → <c>Connection.Look</c>),
    ///    查詢回應直接附上那份,不要讓同一件事有兩個來源。
    ///
    /// 🔴 生命週期:線上那份跟著**連線**走,但 server 會把最後一份**落地**
    ///    (<c>&lt;data&gt;/players.db</c>,以名字為鍵,見 <c>Sdo.Server.Store.PlayerStore</c>)——
    ///    對方離線之後查到的就是那份快照,回應標成 <c>online=false</c> 並附上寫下的時刻。
    ///    這仍然**不是**帳號系統:改名 = 另一筆紀錄,內容依舊是自報值。
    /// </summary>
    public sealed class NetPlayerCard
    {
        /// <summary>四格自我介紹的長度上限(官方 EditBox 的 limittext:城市 12、即時通 12、星座 6、年齡 2)。</summary>
        public const int MaxCity = 12, MaxIm = 12, MaxConstellation = 6, MaxAge = 2;

        // ---- 累計判定數。命中率 / Perfect率 / Cool率 / Bad率 / Miss率 全部由這四個算出來 ----
        public long Perfect, Cool, Bad, Miss;

        /// <summary>完成的歌數。</summary>
        public int Plays;

        // ---- 勝負(熱舞戰績 + 勝率)----
        public int Wins, Losses;

        /// <summary>目前等級內的經驗值百分比 0..100 —— 個人資料頁那條經驗條。
        /// 送百分比而不是原始 exp:換算曲線(<c>Sdo.Settings.PlayerLevel</c>)是 client 的知識,
        /// server 不該為了畫一條進度條而知道升級表。</summary>
        public int ExpPercent;

        /// <summary>累計知名度(名声)—— 顯示成 <c>LV 2 (15)</c> 的那個數字。</summary>
        public int Fame;

        // ---- 官方的 city_edit / QQ_edit / constellation_edit / age_edit ----
        public string City = "", Im = "", Constellation = "", Age = "";

        public JObj Encode()
            => JObj.New()
                .Long("perfect", Perfect).Long("cool", Cool).Long("bad", Bad).Long("miss", Miss)
                .Int("plays", Plays).Int("wins", Wins).Int("losses", Losses)
                .Int("expPct", ExpPercent).Int("fame", Fame)
                .Str("city", City).Str("im", Im).Str("constellation", Constellation).Str("age", Age);

        /// <summary>
        /// 解析 + 夾值。**不會失敗** —— 名片壞掉最糟就是別人的資料頁顯示怪數字,
        /// 不值得為它斷線(同 <see cref="NetAvatarLook.Decode"/> 的取捨)。
        /// </summary>
        public static NetPlayerCard Decode(object node)
        {
            var c = new NetPlayerCard();
            if (node == null) return c;
            c.Perfect = NonNeg(NetJson.Long(node, "perfect"));
            c.Cool = NonNeg(NetJson.Long(node, "cool"));
            c.Bad = NonNeg(NetJson.Long(node, "bad"));
            c.Miss = NonNeg(NetJson.Long(node, "miss"));
            c.Plays = (int)NonNeg(NetJson.Int(node, "plays"));
            c.Wins = (int)NonNeg(NetJson.Int(node, "wins"));
            c.Losses = (int)NonNeg(NetJson.Int(node, "losses"));
            c.ExpPercent = Clamp(NetJson.Int(node, "expPct"), 0, 100);
            c.Fame = (int)NonNeg(NetJson.Int(node, "fame"));
            c.City = Clip(NetJson.Str(node, "city"), MaxCity);
            c.Im = Clip(NetJson.Str(node, "im"), MaxIm);
            c.Constellation = Clip(NetJson.Str(node, "constellation"), MaxConstellation);
            c.Age = Clip(NetJson.Str(node, "age"), MaxAge);
            return c;
        }

        /// <summary>
        /// 兩份名片一樣嗎?<see cref="Sdo.Net"/> 這一側唯一的用途是 client 的**送出去重** ——
        /// 名片會跟著大廳輪詢的節拍一直想送,而它多數時候根本沒變(同 <see cref="NetAvatarLook.SameAs"/>)。
        /// </summary>
        public bool SameAs(NetPlayerCard o)
        {
            return o != null
                && Perfect == o.Perfect && Cool == o.Cool && Bad == o.Bad && Miss == o.Miss
                && Plays == o.Plays && Wins == o.Wins && Losses == o.Losses
                && ExpPercent == o.ExpPercent && Fame == o.Fame
                && Same(City, o.City) && Same(Im, o.Im)
                && Same(Constellation, o.Constellation) && Same(Age, o.Age);
        }

        private static bool Same(string a, string b)
            => string.Equals(a ?? "", b ?? "", System.StringComparison.Ordinal);

        private static long NonNeg(long v) => v < 0 ? 0 : v;

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }

    /// <summary>
    /// <c>cardResult</c> 的內容 —— 一個玩家的完整公開資料:身分 + 外觀 + 名片。
    ///
    /// 外觀來自 server 手上那份(<c>setLook</c>),不是名片的一部分 —— 見 <see cref="NetPlayerCard"/> 的說明。
    /// </summary>
    public sealed class NetPlayerCardResult
    {
        /// <summary>查得到嗎?false = 這個人 server 完全沒印象(沒這個 userId,也沒有他的快照)。
        /// 呼叫端維持原本的空白顯示。</summary>
        public bool Found;

        /// <summary>
        /// 這份資料是**現在**的還是**存下來的**?
        ///
        /// true = 對方正在線上,資料直接來自他的連線。
        /// false = 對方離線,這是 server 落地的最後一份快照(見 <c>Sdo.Server.Store.PlayerStore</c>)——
        /// 顯示得出來,但要讓玩家知道它有多舊(見 <see cref="SeenUtcMs"/>)。
        ///
        /// 🔴 <c>Found &amp;&amp; !Online</c> 是**正常**情況,不是錯誤。舊 server 不送這個欄位 →
        /// 解出來是 false,而舊 server 只會在對方線上時回 <c>found=true</c> —— 所以判斷「線上」
        /// 不可以只看這個旗標,新 client 配舊 server 時它一律是 false。
        /// </summary>
        public bool Online;

        /// <summary>快照寫下的時刻(Unix ms)。0 = 線上那條路(或舊 server)。</summary>
        public long SeenUtcMs;

        public int UserId;
        public string Name = "", PlayerId = "", Guild = "", GuildEmblem = "";
        public int Level;

        public NetAvatarLook Look = new NetAvatarLook();
        public NetPlayerCard Card = new NetPlayerCard();

        public static NetPlayerCardResult Decode(object node)
        {
            var r = new NetPlayerCardResult();
            if (node == null) return r;
            r.Found = NetJson.Bool(node, "found");
            r.Online = NetJson.Bool(node, "online");
            r.SeenUtcMs = NetJson.Long(node, "seenMs");
            r.UserId = NetJson.Int(node, "userId");
            r.Name = NetJson.Str(node, "name") ?? "";
            r.PlayerId = NetJson.Str(node, "playerId") ?? "";
            r.Guild = NetJson.Str(node, "guild") ?? "";
            r.GuildEmblem = NetJson.Str(node, "guildEmblem") ?? "";
            r.Level = NetJson.Int(node, "level");
            r.Look = NetAvatarLook.Decode(NetJson.Sub(node, "look"));
            r.Card = NetPlayerCard.Decode(NetJson.Sub(node, "card"));
            return r;
        }
    }
}
