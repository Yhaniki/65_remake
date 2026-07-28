using Sdo.Osu;

namespace Sdo.Net
{
    /// <summary>
    /// 房主選的那首歌在網路上的參照 —— 收端要靠它在**自己的**歌庫裡定位同一張譜。
    ///
    /// 官方歌與外部歌走兩條不同的識別路徑:
    ///
    ///   • **官方歌**(<see cref="Official"/> = true):用 <see cref="Gn"/> + <see cref="FileId"/>。
    ///     那是隨遊戲資料一起出貨的，全球唯一且穩定,大家手上一定一樣。<see cref="PackId"/> 留空。
    ///
    ///   • **外部歌**(osu / StepMania / 自製歌包):用 <see cref="PackId"/> +
    ///     <see cref="SongKey"/> + <see cref="ChartRelPath"/> + <see cref="ChartIndex"/>。
    ///     🔴 **絕對不能用本機的 gn / fileId** —— <c>ExternalSongLibrary</c> 的 gn 是
    ///     「ext_ + FNV(資料夾絕對路徑)」,fileId 是「-(掃描索引 + 1000)」,兩者換台機器就完全不同。
    ///
    /// 顯示欄位(標題/曲師/BPM/等級…)純粹給 UI 用 —— 讓缺歌的人也能在房間裡看到房主選了什麼,
    /// 以及讓 server 的房間列表能顯示歌名。它們**不參與識別**(收端一律用上面那組 key 去定位)。
    /// </summary>
    public sealed class NetSongRef
    {
        /// <summary>字串欄位的長度上限(避免有人塞一整本小說進房間列表)。</summary>
        public const int MaxTextLength = 128;

        // ---- 識別 ----

        /// <summary>官方歌 = true(用 <see cref="Gn"/>/<see cref="FileId"/>);外部歌 = false。</summary>
        public bool Official;

        /// <summary>官方歌的檔名(如 <c>sdom1435k.gn</c>)。外部歌留空。</summary>
        public string Gn = "";

        /// <summary>官方歌的 fileId(k 譜 = 10000+N)。外部歌是 0。</summary>
        public int FileId;

        /// <summary>外部歌的資料夾內容指紋(<c>sha256:…</c>)。官方歌留空。見 <see cref="SongPackId"/>。</summary>
        public string PackId = "";

        /// <summary>一個資料夾裡有多首歌時,是哪一首(來自音檔檔名 → 跨機器一致)。單首歌時是空字串。</summary>
        public string SongKey = "";

        /// <summary>譜面檔相對歌曲資料夾的路徑(已正規化:小寫、<c>/</c> 分隔)。</summary>
        public string ChartRelPath = "";

        /// <summary><c>.sm</c> 的 #NOTES block 索引 / <c>.gn</c> 的難度(0-2) / osu 是 0。</summary>
        public int ChartIndex;

        // ---- 顯示用(不參與識別) ----

        public string Title = "";
        public string Artist = "";
        public double Bpm;
        public int NoteCount;
        public int Level;
        public double DurationSec;
        /// <summary>0=Easy 1=Normal 2=Hard(對映 client 的 Sdo.UI.Core.Difficulty)。</summary>
        public int Difficulty;

        /// <summary>
        /// 「隨機難度」模式:<see cref="Title"/> 是「隨機難度 X」那個標籤,不是真正抽到的歌名。
        ///
        /// 為什麼要傳這一個 bool:抽到的歌**已經**在 <see cref="Gn"/> 裡了(開場時要用它載譜),
        /// 所以房間裡的其他人如果拿 gn 去查自己的目錄,就會顯示出那首歌的等級/BPM ——
        /// 隨機難度的意義就沒了(房間刻意不揭曉抽到哪一首)。收端看到這個旗標就不查目錄。
        /// </summary>
        public bool RandomTitle;

        /// <summary>這是「有選歌」嗎?(官方歌要有 gn,外部歌要有 packId)</summary>
        public bool HasSong
            => Official ? !string.IsNullOrEmpty(Gn) : !string.IsNullOrEmpty(PackId);

        /// <summary>
        /// 兩個參照指的是同一張譜嗎?**只比對識別欄位**,顯示欄位不算
        /// (同一張譜在不同人的機器上,標題可能因為簡繁轉換或 metadata 覆寫而略有差異)。
        /// </summary>
        public bool SameChartAs(NetSongRef other)
        {
            if (other == null) return false;
            if (Official != other.Official) return false;
            if (Official)
                return string.Equals(Gn, other.Gn, System.StringComparison.Ordinal);
            return string.Equals(PackId, other.PackId, System.StringComparison.Ordinal)
                && string.Equals(SongKey, other.SongKey, System.StringComparison.Ordinal)
                && string.Equals(ChartRelPath, other.ChartRelPath, System.StringComparison.Ordinal)
                && ChartIndex == other.ChartIndex;
        }

        // ---- codec ----

        public JObj Encode()
        {
            var o = JObj.New()
                .Bool("official", Official)
                .Int("chartIndex", ChartIndex)
                .Int("difficulty", Difficulty)
                .Int("noteCount", NoteCount)
                .Int("level", Level)
                .Num("bpm", Bpm)
                .Num("durationSec", DurationSec)
                .Str("title", Title)
                .Str("artist", Artist)
                .Bool("randomTitle", RandomTitle);

            if (Official)
            {
                o.Str("gn", Gn).Int("fileId", FileId);
            }
            else
            {
                o.Str("packId", PackId).Str("songKey", SongKey).Str("chartRelPath", ChartRelPath);
            }
            return o;
        }

        /// <summary>
        /// 從 JSON 節點解出來,**並驗證**。回 false = 這個參照不可信,呼叫端應該拒絕整個訊息。
        ///
        /// server 端一定要走這條(而不是直接信 client 送來的欄位):packId 的格式、
        /// 譜面路徑的安全性、字串長度都在這裡把關。
        /// </summary>
        public static bool TryDecode(object node, out NetSongRef song)
        {
            song = null;
            if (node == null) return false;

            var s = new NetSongRef();
            s.Official = NetJson.Bool(node, "official");
            s.ChartIndex = NetJson.Int(node, "chartIndex");
            s.Difficulty = NetJson.Int(node, "difficulty");
            s.NoteCount = NetJson.Int(node, "noteCount");
            s.Level = NetJson.Int(node, "level");
            s.Bpm = NetJson.Num(node, "bpm");
            s.DurationSec = NetJson.Num(node, "durationSec");
            s.Title = Clip(NetJson.Str(node, "title"));
            s.Artist = Clip(NetJson.Str(node, "artist"));
            s.RandomTitle = NetJson.Bool(node, "randomTitle");

            if (s.Official)
            {
                s.Gn = Clip(NetJson.Str(node, "gn"));
                s.FileId = NetJson.Int(node, "fileId");
                if (string.IsNullOrEmpty(s.Gn)) return false;
            }
            else
            {
                s.PackId = NetJson.Str(node, "packId");
                s.SongKey = Clip(NetJson.Str(node, "songKey"));
                s.ChartRelPath = NetJson.Str(node, "chartRelPath");

                // 🔴 格式與安全驗證。ChartRelPath 會被收端拿去組真實路徑，所以非驗不可。
                if (!SongPackId.IsWellFormed(s.PackId)) return false;
                if (string.IsNullOrEmpty(s.ChartRelPath)) return false;
                if (!SafeRelPath.IsSafe(s.ChartRelPath)) return false;
                s.ChartRelPath = SafeRelPath.Normalize(s.ChartRelPath);
            }

            // 範圍夾值:壞資料不要讓它變成負的 note 數之類的東西流進 UI。
            if (s.ChartIndex < 0) s.ChartIndex = 0;
            if (s.Difficulty < 0) s.Difficulty = 0;
            if (s.Difficulty > 2) s.Difficulty = 2;
            if (s.NoteCount < 0) s.NoteCount = 0;
            if (s.Level < 0) s.Level = 0;
            if (s.Bpm < 0) s.Bpm = 0;
            if (s.DurationSec < 0) s.DurationSec = 0;

            song = s;
            return true;
        }

        private static string Clip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= MaxTextLength ? s : s.Substring(0, MaxTextLength);
        }
    }
}
