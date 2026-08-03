using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Sdo.Net;

namespace Sdo.Server.Store
{
    /// <summary>
    /// 一個玩家最後一次上線時的**公開資料快照** —— 身分 + 外觀 + 名片,合成一份可以落地的東西。
    ///
    /// 它與線上的 <c>Connection</c> 是同樣的三塊資料,只是把「跟著連線走」換成「跟著名字走」。
    /// 為什麼要有這個型別而不是直接傳 <c>Connection</c>:落地層不該認得連線
    /// (那會讓 <see cref="PlayerStore"/> 沒辦法單元測試,而它是唯一會弄壞玩家歷史資料的東西)。
    /// </summary>
    public sealed class PlayerSnapshot
    {
        public string Name = "", PlayerId = "", Guild = "", GuildEmblem = "";
        public int Level;
        public NetAvatarLook Look = new NetAvatarLook();
        public NetPlayerCard Card = new NetPlayerCard();

        /// <summary>這份快照是什麼時候寫下的(Unix ms)。查詢時會回給 client,讓它知道資料有多舊。</summary>
        public long UpdatedUtcMs;
    }

    /// <summary>
    /// 玩家公開資料的落地層 —— <c>&lt;data&gt;/players.db</c>(SQLite)。
    ///
    /// **為什麼需要它**:名片(<see cref="NetPlayerCard"/>)本來只掛在連線上,人一離線就沒了 ——
    /// 點開一個剛下線的人,整頁是 0。這裡把「最後一次看到的那份」存起來,
    /// <c>cardQuery</c> 找不到線上連線時就從這裡撈(見 <c>Hub.OnPlayerCardQuery</c>)。
    ///
    /// 🔴 <b>鍵是名字</b>(小寫化後當主鍵)。理由與 client 端好友清單認名字是同一個
    /// (見 <c>Sdo.Settings.FriendList</c>):這套連線沒有帳號系統,<c>playerId</c> 是 client 自稱的,
    /// 大家都用預設 profile 時會直接撞在一起;而**名字**至少被 server 擋成「同時線上唯一」
    /// (見 <c>Hub.ControlByName</c>)。代價是改名 = 換一筆紀錄,那是可以接受的。
    ///
    /// 🔴 <b>存的仍然是自報值</b>。落地不會讓資料變得可信 —— 判定數本來就發生在 client
    /// (見 <see cref="NetPlayerCard"/> 開頭那段)。拿來顯示可以,拿來做排行榜或獎勵**不行**。
    ///
    /// 🔴 <b>任何一次失敗都不可以往上炸。</b> 這張表壞掉最糟就是「別人的資料頁少了幾個數字」,
    /// 而它跑在 Hub 的 actor loop 上 —— 一個 <c>SqliteException</c> 就會打斷那一筆訊息的處理。
    /// 所以 <see cref="Save"/> / <see cref="TryLoad"/> 一律吞例外並回報失敗,由呼叫端決定要不要記一行。
    ///
    /// 執行緒歸屬:**只有 Hub 的 actor loop 碰它**(<see cref="SqliteConnection"/> 本來就不是
    /// thread-safe)。開檔在建構子(actor loop 還沒開始),之後所有存取都在那一條執行緒上。
    /// </summary>
    public sealed class PlayerStore : IDisposable
    {
        /// <summary>schema 版本(寫進 <c>PRAGMA user_version</c>)。將來加欄位時靠它決定要不要遷移。</summary>
        public const int SchemaVersion = 1;

        private readonly SqliteConnection _db;
        private bool _disposed;

        /// <summary>這顆 db 的檔案路徑(log / 診斷用)。</summary>
        public string Path { get; }

        private PlayerStore(SqliteConnection db, string path)
        {
            _db = db;
            Path = path;
        }

        /// <summary>
        /// 開檔(不存在就建)。**失敗不丟例外** —— 回 null 並在 <paramref name="error"/> 給原因。
        ///
        /// 為什麼不讓它擋開機:玩家資料快照是加分項,連不上資料庫時 server 應該照常收人,
        /// 只是退回「離線就查不到」的舊行為(見 <c>Hub</c> 建構子)。把 server 起不來的原因
        /// 從「port 被佔用 / 憑證讀不到」擴張到「一個顯示用的快取表」是很糟的交易。
        /// </summary>
        public static PlayerStore TryOpen(string dbPath, out string error)
        {
            error = null;
            SqliteConnection db = null;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var csb = new SqliteConnectionStringBuilder { DataSource = dbPath };
                db = new SqliteConnection(csb.ToString());
                db.Open();

                // WAL:寫入不會擋住讀取,而且 server 被 kill 時不會留下半個 journal 讓下次開檔卡住。
                // synchronous=NORMAL:這張表掉最後幾筆的代價是「某人的數字舊了幾分鐘」,
                // 不值得每次 upsert 都等一次 fsync。
                Exec(db, "PRAGMA journal_mode=WAL;");
                Exec(db, "PRAGMA synchronous=NORMAL;");
                Exec(db, CreateTableSql);
                Exec(db, "PRAGMA user_version=" + SchemaVersion + ";");

                var store = new PlayerStore(db, dbPath);
                return store;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                try { if (db != null) db.Dispose(); } catch { }
                return null;
            }
        }

        private const string CreateTableSql =
            "CREATE TABLE IF NOT EXISTS players (" +
            "  name_key      TEXT PRIMARY KEY," +
            "  name          TEXT NOT NULL," +
            "  player_id     TEXT NOT NULL DEFAULT ''," +
            "  guild         TEXT NOT NULL DEFAULT ''," +
            "  guild_emblem  TEXT NOT NULL DEFAULT ''," +
            "  level         INTEGER NOT NULL DEFAULT 0," +
            "  look_json     TEXT NOT NULL DEFAULT ''," +
            "  perfect       INTEGER NOT NULL DEFAULT 0," +
            "  cool          INTEGER NOT NULL DEFAULT 0," +
            "  bad           INTEGER NOT NULL DEFAULT 0," +
            "  miss          INTEGER NOT NULL DEFAULT 0," +
            "  plays         INTEGER NOT NULL DEFAULT 0," +
            "  wins          INTEGER NOT NULL DEFAULT 0," +
            "  losses        INTEGER NOT NULL DEFAULT 0," +
            "  exp_pct       INTEGER NOT NULL DEFAULT 0," +
            "  fame          INTEGER NOT NULL DEFAULT 0," +
            "  city          TEXT NOT NULL DEFAULT ''," +
            "  im            TEXT NOT NULL DEFAULT ''," +
            "  constellation TEXT NOT NULL DEFAULT ''," +
            "  age           TEXT NOT NULL DEFAULT ''," +
            "  updated_ms    INTEGER NOT NULL DEFAULT 0" +
            ");";

        /// <summary>
        /// 名字 → 主鍵。**必須與 <c>Hub.ControlByName</c> 的比對規則同一套**
        /// (那邊是 <see cref="StringComparison.OrdinalIgnoreCase"/>)—— 兩邊不一致的話,
        /// 同一個人會在「線上查得到」與「離線查得到」之間漂移,而症狀只是「有時候查不到」。
        ///
        /// server 是 <c>InvariantGlobalization</c>,<c>ToLowerInvariant</c> 對非 ASCII 是恆等的,
        /// 也就是中文名維持原樣、英文名不分大小寫 —— 正好是我們要的。
        /// </summary>
        public static string KeyOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// 寫進去(有就更新)。回 false = 沒寫成(空名字,或資料庫出錯)。
        ///
        /// 🔴 <b>空名字直接跳過</b>:名字是主鍵,而「還沒 hello 完就斷線」的連線名字是空的 ——
        /// 讓那些人共用一列 <c>""</c> 的話,那一列會被每一個過客輪流覆寫成毫無意義的東西。
        /// </summary>
        public bool Save(PlayerSnapshot snap, out string error)
        {
            error = null;
            if (_disposed) { error = "store 已關閉"; return false; }
            if (snap == null) { error = "null snapshot"; return false; }

            string key = KeyOf(snap.Name);
            if (key.Length == 0) { error = "名字是空的"; return false; }

            var card = snap.Card ?? new NetPlayerCard();
            var look = snap.Look ?? new NetAvatarLook();

            try
            {
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText =
                        "INSERT INTO players (name_key, name, player_id, guild, guild_emblem, level, look_json," +
                        " perfect, cool, bad, miss, plays, wins, losses, exp_pct, fame," +
                        " city, im, constellation, age, updated_ms)" +
                        " VALUES ($key, $name, $pid, $guild, $emblem, $level, $look," +
                        " $perfect, $cool, $bad, $miss, $plays, $wins, $losses, $expPct, $fame," +
                        " $city, $im, $constellation, $age, $updated)" +
                        " ON CONFLICT(name_key) DO UPDATE SET" +
                        "  name=excluded.name, player_id=excluded.player_id, guild=excluded.guild," +
                        "  guild_emblem=excluded.guild_emblem, level=excluded.level, look_json=excluded.look_json," +
                        "  perfect=excluded.perfect, cool=excluded.cool, bad=excluded.bad, miss=excluded.miss," +
                        "  plays=excluded.plays, wins=excluded.wins, losses=excluded.losses," +
                        "  exp_pct=excluded.exp_pct, fame=excluded.fame," +
                        "  city=excluded.city, im=excluded.im, constellation=excluded.constellation," +
                        "  age=excluded.age, updated_ms=excluded.updated_ms;";

                    cmd.Parameters.AddWithValue("$key", key);
                    cmd.Parameters.AddWithValue("$name", snap.Name ?? "");
                    cmd.Parameters.AddWithValue("$pid", snap.PlayerId ?? "");
                    cmd.Parameters.AddWithValue("$guild", snap.Guild ?? "");
                    cmd.Parameters.AddWithValue("$emblem", snap.GuildEmblem ?? "");
                    cmd.Parameters.AddWithValue("$level", snap.Level);
                    cmd.Parameters.AddWithValue("$look", look.Encode().Json());
                    cmd.Parameters.AddWithValue("$perfect", card.Perfect);
                    cmd.Parameters.AddWithValue("$cool", card.Cool);
                    cmd.Parameters.AddWithValue("$bad", card.Bad);
                    cmd.Parameters.AddWithValue("$miss", card.Miss);
                    cmd.Parameters.AddWithValue("$plays", card.Plays);
                    cmd.Parameters.AddWithValue("$wins", card.Wins);
                    cmd.Parameters.AddWithValue("$losses", card.Losses);
                    cmd.Parameters.AddWithValue("$expPct", card.ExpPercent);
                    cmd.Parameters.AddWithValue("$fame", card.Fame);
                    cmd.Parameters.AddWithValue("$city", card.City ?? "");
                    cmd.Parameters.AddWithValue("$im", card.Im ?? "");
                    cmd.Parameters.AddWithValue("$constellation", card.Constellation ?? "");
                    cmd.Parameters.AddWithValue("$age", card.Age ?? "");
                    cmd.Parameters.AddWithValue("$updated", snap.UpdatedUtcMs);
                    cmd.ExecuteNonQuery();
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>照名字撈最後一份快照。找不到(或出錯)回 false。</summary>
        public bool TryLoad(string name, out PlayerSnapshot snap)
        {
            snap = null;
            if (_disposed) return false;

            string key = KeyOf(name);
            if (key.Length == 0) return false;

            try
            {
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT name, player_id, guild, guild_emblem, level, look_json," +
                        " perfect, cool, bad, miss, plays, wins, losses, exp_pct, fame," +
                        " city, im, constellation, age, updated_ms" +
                        " FROM players WHERE name_key = $key;";
                    cmd.Parameters.AddWithValue("$key", key);

                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return false;

                        var s = new PlayerSnapshot
                        {
                            Name = r.GetString(0),
                            PlayerId = r.GetString(1),
                            Guild = r.GetString(2),
                            GuildEmblem = r.GetString(3),
                            Level = r.GetInt32(4),
                            Look = DecodeLook(r.GetString(5)),
                            Card = new NetPlayerCard
                            {
                                Perfect = r.GetInt64(6),
                                Cool = r.GetInt64(7),
                                Bad = r.GetInt64(8),
                                Miss = r.GetInt64(9),
                                Plays = r.GetInt32(10),
                                Wins = r.GetInt32(11),
                                Losses = r.GetInt32(12),
                                ExpPercent = r.GetInt32(13),
                                Fame = r.GetInt32(14),
                                City = r.GetString(15),
                                Im = r.GetString(16),
                                Constellation = r.GetString(17),
                                Age = r.GetString(18),
                            },
                            UpdatedUtcMs = r.GetInt64(19),
                        };
                        snap = s;
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>現在存了幾個人(開機 banner 與診斷用)。出錯回 -1。</summary>
        public int Count
        {
            get
            {
                if (_disposed) return -1;
                try
                {
                    using (var cmd = _db.CreateCommand())
                    {
                        cmd.CommandText = "SELECT COUNT(*) FROM players;";
                        var v = cmd.ExecuteScalar();
                        return v == null ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture);
                    }
                }
                catch (Exception) { return -1; }
            }
        }

        /// <summary>
        /// 外觀是整包 JSON 存的(它是個字串陣列,拆成欄位沒有意義)。
        /// 壞掉就退回預設外觀 —— 與 <see cref="NetAvatarLook.Decode"/> 的取捨一致:
        /// 外觀資料壞掉最糟就是角色長得怪。
        /// </summary>
        private static NetAvatarLook DecodeLook(string json)
        {
            if (string.IsNullOrEmpty(json)) return new NetAvatarLook();
            object node;
            if (!NetJson.TryParse(json, out node)) return new NetAvatarLook();
            return NetAvatarLook.Decode(node);
        }

        private static void Exec(SqliteConnection db, string sql)
        {
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _db.Dispose(); } catch { }
        }
    }
}
