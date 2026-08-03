using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Sdo.Net;
using Sdo.Net.Server;
using Sdo.Server.Files;
using Sdo.Server.Logging;
using Sdo.Server.Store;

namespace Sdo.Server.Net
{
    /// <summary>
    /// 伺服器的核心。
    ///
    /// ★ **單執行緒 actor loop**:所有房間狀態的變更都在這一個執行緒上發生。
    ///   連線的讀取各自在自己的 Task 上,收到完整 frame 後把工作 <see cref="Post"/> 進來排隊。
    ///
    /// 為什麼是這個模型而不是「每個房間一把鎖」:
    ///   • <see cref="RoomRegistry"/> / <see cref="NetRoom"/> 完全不需要同步機制 ——
    ///     它們是純邏輯,可以直接單元測試,也可以被 client 端的 loopback 假伺服器重用。
    ///   • 房間之間有跨房操作(「已在別房 → 先隱式離房」),那在細粒度鎖下是死鎖溫床。
    ///   • 這個規模(200 房 × 6 人)一個執行緒綽綽有餘 —— 每筆訊息的工作量是「改幾個欄位 + 組一份 JSON」。
    ///
    /// loop 也順便當計時器:取工作時帶 <see cref="TickIntervalMs"/> 的 timeout,
    /// 超時就跑一次 <see cref="TickAll"/>(載入逾時、frames 彙整、ping 逾時)。
    /// 這樣連定期工作也在同一個執行緒上,零 lock。
    /// </summary>
    public sealed partial class Hub
    {
        /// <summary>actor loop 沒事做時多久醒一次(ms)。</summary>
        private const int TickIntervalMs = 50;

        /// <summary>actor 佇列容量。滿了代表 server 過載 —— 那時丟掉新工作比越積越多好。</summary>
        private const int WorkCapacity = 8192;

        private readonly ServerOptions _opts;
        private readonly RoomRegistry _rooms;
        private readonly BlockingCollection<Action> _work
            = new BlockingCollection<Action>(new ConcurrentQueue<Action>(), WorkCapacity);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>所有活著的連線(含 file 連線),key = connId。只由 actor loop 碰。</summary>
        private readonly Dictionary<int, Connection> _conns = new Dictionary<int, Connection>();

        /// <summary>userId → control 連線。只由 actor loop 碰。</summary>
        private readonly Dictionary<int, Connection> _byUser = new Dictionary<int, Connection>();

        /// <summary>sessionKey → userId(給 file 連線認親)。只由 actor loop 碰。</summary>
        private readonly Dictionary<string, int> _sessions = new Dictionary<string, int>(StringComparer.Ordinal);

        private int _nextConnId = 1;
        private int _nextUserId = 1;
        private long _lastPingSweepMs;
        private long _lastFramePushMs;
        private long _lastMovePushMs;
        private TcpListener _listener;

        /// <summary>歌曲暫存的磁碟層(缺歌傳檔用)。只由 actor loop 碰。</summary>
        private readonly DiskBlobIo _blobs;

        /// <summary>定期清掉沒人用的歌曲暫存。</summary>
        private readonly BlobJanitor _janitor;

        /// <summary>
        /// 玩家公開資料的落地層(<c>&lt;data&gt;/players.db</c>)。**null = 開不起來** ——
        /// 那時整套行為退回舊的「離線就查不到」,server 照常運作(見 <see cref="PlayerStore.TryOpen"/>)。
        /// 只由 actor loop 碰。
        /// </summary>
        private readonly PlayerStore _players;

        /// <summary>players.db 開不起來的原因(banner 印一行)。</summary>
        private readonly string _playerStoreError;

        // ---- 公網化(M10)。四個都預設關閉/寬鬆 → LAN 行為不變。----
        private readonly AuthTokens _tokens = new AuthTokens();
        private readonly OriginPolicy _origin = new OriginPolicy();
        private readonly UploadQuota _quota = new UploadQuota();

        /// <summary>TLS 憑證。null = 明文(LAN 預設)。開機載入,之後唯讀 → 多執行緒共用安全。</summary>
        private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _tlsCert;

        /// <summary>
        /// TLS 握手的逾時(ms)。連上來卻不講話的人不能無限佔住一條 task ——
        /// 那是**不需要通過任何認證**就做得到的事。
        /// </summary>
        private const int TlsHandshakeTimeoutMs = 10000;

        /// <summary>憑證載入失敗的原因(非 null = server 不該啟動)。</summary>
        public string TlsError { get; private set; }

        // token 檔的載入結果。建構子讀、banner 印(見建構子的註解:順序)。
        private int _tokenCount;
        private string _tokenLoadError;
        private List<string> _tokenProblems;

        /// <summary>這台 server 的憑證指紋(SHA-256 小寫 hex)。空 = 沒開 TLS。</summary>
        public string TlsFingerprint { get; private set; } = "";

        public Hub(ServerOptions opts)
        {
            _opts = opts;
            int seed = opts.CodeSeed != 0 ? opts.CodeSeed : unchecked((int)DateTime.UtcNow.Ticks);
            _rooms = new RoomRegistry(opts.MaxRooms, seed);

            _blobs = new DiskBlobIo(opts.BlobDir);
            int dropped = _blobs.ClearTemp();     // 上次被 kill 掉留下的半個上傳
            if (dropped > 0) Log("清掉 " + dropped + " 份沒收完的上傳暫存");
            _janitor = new BlobJanitor(_blobs, opts.TtlHours,
                                       (long)opts.MaxTotalBlobGb * 1024L * 1024L * 1024L, NowMs());

            // 玩家資料快照。開不起來**不擋開機** —— 它是顯示用的加分項,不是連線的前提
            // (理由見 PlayerStore.TryOpen)。失敗的原因由 banner 印出來,不然它會靜靜地沒生效。
            string dbErr;
            _players = PlayerStore.TryOpen(opts.PlayerDbPath, out dbErr);
            _playerStoreError = dbErr;

            // 公網化的三道防線。都是「設了才生效」—— 沒設就完全是 LAN 的行為。
            _origin.SetAllowList(opts.AllowFrom);
            _origin.MaxPerIp = opts.MaxPerIp;
            _quota.BytesPerHour = opts.UploadBytesPerHour;
            // ⚠️ 這裡**不印**「已啟用」那類的行 —— 建構子比 banner 早跑,印了會插在版本/監聽之前,
            // 開機訊息就變成沒有順序的一堆。狀態統一由 PrintSecurityBanner 一行列完;
            // 這裡只留「真的出事了」的:token 檔讀不到 / 內容有問題,那是設定錯誤,不能被摺疊掉。
            if (!string.IsNullOrEmpty(opts.TokensFile))
            {
                var problems = new List<string>();
                try { _tokenCount = _tokens.Load(System.IO.File.ReadAllText(opts.TokensFile), problems); }
                catch (Exception ex)
                {
                    _tokenLoadError = ex.Message;
                }
                _tokenProblems = problems;
            }

            if (opts.TlsEnabled)
            {
                string tlsErr;
                _tlsCert = TlsSetup.Load(opts, out tlsErr);
                TlsError = tlsErr;
                TlsFingerprint = TlsSetup.Fingerprint(_tlsCert);
            }
        }

        /// <summary>Unix 毫秒。所有逾時判斷的時間源。</summary>
        public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>
        /// 實際綁到的 port。0 = 還沒開始監聽。
        ///
        /// 這個屬性存在是為了讓 <c>--port 0</c> 有意義:OS 會配一個空閒 port,
        /// 整合測試就能同時跑好幾個 server 而不會互相搶 port。
        /// </summary>
        public int ActualPort { get; private set; }

        /// <summary>已經在監聽了嗎?(整合測試等這個變 true 才開始連)</summary>
        public bool IsListening => ActualPort != 0;

        /// <summary>目前開著幾間房。診斷與測試用(唯讀)。</summary>
        public int RoomCount => _rooms.RoomCount;

        /// <summary>目前有幾條連線。診斷與測試用(唯讀)。</summary>
        public int ConnectionCount => _conns.Count;

        /// <summary>把工作排進 actor loop。**任何執行緒都可以呼叫。**</summary>
        public void Post(Action work)
        {
            if (work == null || _cts.IsCancellationRequested) return;
            // 不阻塞:寧可丟掉也不要讓 reader thread 卡在這裡。
            if (!_work.TryAdd(work)) Log("actor 佇列滿了,丟掉一筆工作(server 過載)");
        }

        /// <summary>啟動監聽 + actor loop。會一直跑到 <see cref="Stop"/>。</summary>
        public void Run()
        {
            IPAddress addr;
            if (!IPAddress.TryParse(_opts.Bind, out addr)) addr = IPAddress.Any;

            _listener = new TcpListener(addr, _opts.Port);
            _listener.Start();
            ActualPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

            // 版本擺在第一行 —— 排查任何「更新完還是壞的」都要先確認這顆 binary 真的是新的。
            // 與 client 視窗標題同格式(dance v1.5.0-dev-d41da ↔ sdo-server v1.5.0-dev-d41da),見 BuildInfo。
            Log(BuildInfo.Banner + " (protocol v" + NetProto.Version + ")  監聽 " + addr + ":" + ActualPort);
            Log("data=" + _opts.DataDir + "  房間上限 " + _opts.MaxRooms
                + "  連線上限 " + _opts.MaxConnections
                + "  歌曲暫存 " + _opts.MaxTotalBlobGb + "GB/" + _opts.TtlHours + "h");
            // log 檔在哪要印出來 —— 出事時第一件事就是「去哪撈 log」,而路徑是可以被 --log-dir 換掉的。
            Log("log " + ServerLog.Describe());
            // 玩家快照:開起來了就報現在存了幾個人(那是「這台真的有在記」最直接的證據);
            // 開不起來一定要吵 —— 它失敗的症狀只是「離線查不到資料」,與功能沒做完長得一模一樣。
            if (_players != null) Log("玩家快照 " + _opts.PlayerDbPath + "(已存 " + _players.Count + " 人)");
            else Log("⚠️  玩家快照開不起來(" + (_playerStoreError ?? "?") + ")→ 離線的人查不到資料");
            PrintSecurityBanner();

            var accept = Task.Factory.StartNew(AcceptLoop, TaskCreationOptions.LongRunning);

            ActorLoop();

            try { _listener.Stop(); } catch { }
            try { accept.Wait(1000); } catch { }

            // 🔴 db handle 要在**這裡**收,不是在 Stop() 裡:Stop 是別條執行緒呼叫的,
            //    那時 actor loop 可能還在處理最後一筆訊息(而那筆可能正在寫快照)。
            //    ActorLoop() 回來了才代表沒有人會再碰它。不收的話 Windows 上檔案會一直被鎖著
            //    —— 整合測試一輪開好幾個 server,每個都留一顆 handle。
            try { if (_players != null) _players.Dispose(); } catch { }
        }

        /// <summary>
        /// 開機時把「這台現在受哪些保護」講清楚。
        ///
        /// 為什麼要印:M10 的四道防線都是「設了才生效」,而沒生效的時候**什麼異狀都沒有** ——
        /// 少打一個參數就是裸奔,而且要等出事才知道。所以每次開機都明確說一遍現在是哪一種模式。
        /// </summary>
        private void PrintSecurityBanner()
        {
            // 四道防線一行列完(✓/✗ 一眼掃過去),而不是每一項各印一段 ——
            // 開機訊息愈長,真正要看的那一項愈容易被跳過。
            Log("安全:加密 " + Mark(_tlsCert != null)
                + "  密碼 " + Mark(!string.IsNullOrEmpty(_opts.Password))
                + "  token " + Mark(_tokens.Enabled) + (_tokens.Enabled ? "(" + _tokenCount + ")" : "")
                + "  來源限制 " + Mark(_origin.HasAllowList)
                + "  上傳配額 " + (_quota.Enabled
                    ? (_opts.UploadBytesPerHour / (1024 * 1024)) + "MB/人/時" : "✗"));

            // 自簽憑證要把指紋填進 client,所以指紋要印;但說明壓成同一行。
            if (_tlsCert != null)
                Log("TLS 指紋 " + TlsFingerprint
                    + "  → client config.ini:serverTls=1 / serverCertFingerprint=<這串>");

            // 設定錯誤(讀不到 token 檔、檔案內容有問題)一定要吵 —— 它會讓認證靜靜地沒生效。
            if (_tokenLoadError != null)
                Log("⚠️  讀不到 token 檔 " + _opts.TokensFile + ":" + _tokenLoadError + " → token 認證未啟用");
            if (_tokenProblems != null)
                foreach (var pr in _tokenProblems) Log("⚠️  token 檔:" + pr);

            // 裸奔警告只在真的裸奔時講一次,而且講清楚缺什麼。
            if (_tokens.Enabled && _tlsCert != null) return;
            string missing = _tlsCert == null ? "加密(--tls-cert)" : "";
            if (!_tokens.Enabled) missing += (missing.Length > 0 ? "與" : "") + "帳號認證(--tokens)";
            Log("⚠️  缺少" + missing + " —— 只適合 LAN/信任的朋友,不要直接開在公網。");
        }

        private static string Mark(bool on) => on ? "✓" : "✗";

        public void Stop()
        {
            _cts.Cancel();
            try { _work.CompleteAdding(); } catch { }
            try { if (_listener != null) _listener.Stop(); } catch { }
        }

        // ---- accept ----

        private void AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient tcp;
                try { tcp = _listener.AcceptTcpClient(); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception) { continue; }

                // 一條連線 = 一個 task。**TLS 握手要在這個 task 上做,不能在 accept 迴圈裡等** ——
                // 握手是好幾趟來回,擋在這裡的話一個慢(或惡意不講話)的 client 就讓所有人都連不進來。
                var accepted = tcp;
                Task.Factory.StartNew(() => ServeConnection(accepted), TaskCreationOptions.LongRunning);
            }
        }

        /// <summary>
        /// 一條連線的一生:(TLS 握手)→ 註冊 → 讀迴圈。跑在自己的 task 上。
        ///
        /// 註冊(以及來源/連線數的檢查)排進 actor loop,而關閉的通知也是從這個 task 排進去的 ——
        /// 同一個 task 依序 Post,所以「加入」一定排在「移除」前面。
        /// </summary>
        private void ServeConnection(TcpClient tcp)
        {
            var conn = new Connection(Interlocked.Increment(ref _nextConnId), tcp);
            conn.LastRecvMs = NowMs();

            if (_tlsCert != null)
            {
                string tlsErr;
                if (!conn.TryStartTls(_tlsCert, TlsHandshakeTimeoutMs, out tlsErr))
                {
                    // 握手失敗就結束 —— 這時候還沒有加密通道,送 bye 對方也解不開。
                    Post(() => Log("連線 #" + conn.ConnId + " 來自 " + conn.RemoteLabel + " TLS 握手失敗:" + tlsErr));
                    conn.Close("tlsHandshake");
                    return;
                }
            }

            // 連線數上限:在 actor loop 裡判斷(它才知道目前有幾條)。
            Post(() =>
            {
                if (_conns.Count >= _opts.MaxConnections)
                {
                    conn.Kill("serverFull");
                    return;
                }
                // 🔴 來源限制與 per-IP 上限要在**hello 之前**擋 —— 連線在握手之前就已經成立,
                // 所以「開一百條連線把 maxConnections 佔滿」不需要通過任何認證就做得到。
                string ip = OriginPolicy.IpOf(conn.RemoteLabel);
                if (!_origin.Allows(ip))
                {
                    Log("連線 #" + conn.ConnId + " 來源不在允許名單(" + ip + "),拒絕");
                    conn.Kill("notAllowed");
                    return;
                }
                if (!_origin.AllowsAnother(CountFromIp(ip)))
                {
                    Log("連線 #" + conn.ConnId + " 來自 " + ip + " 的連線數已達上限,拒絕");
                    conn.Kill("tooManyFromIp");
                    return;
                }
                _conns[conn.ConnId] = conn;
                // TCP 接上還不是「有人來了」—— 真正有意義的是 hello 完成那一行(它才有名字與版本)。
                // 這一行在正常流程上是重複的,留給 --verbose 診斷「連上來但沒講話」的情況。
                LogVerbose("連線 #" + conn.ConnId + " 來自 " + conn.RemoteLabel
                    + (conn.IsTls ? "(TLS" : "(明文") + ",共 " + _conns.Count + " 條)");
            });

            conn.StartWriter();
            // 讀迴圈就跑在這個 task 上(以前是再開一個)—— 一條連線一個 task 就夠。
            conn.RunReadLoop(OnFrameFromReader, NowMs, OnConnectionClosed);
        }

        /// <summary>這個 IP 現在有幾條連線(per-IP 上限用)。只由 actor loop 呼叫。</summary>
        private int CountFromIp(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return 0;
            int n = 0;
            foreach (var kv in _conns)
                if (string.Equals(OriginPolicy.IpOf(kv.Value.RemoteLabel), ip, StringComparison.Ordinal)) n++;
            return n;
        }

        /// <summary>
        /// 從 reader thread 呼叫 —— **這裡不碰任何共享狀態**,只把工作 marshal 進 actor loop。
        /// </summary>
        private void OnFrameFromReader(Connection conn, byte kind, byte[] payload)
        {
            Post(() => HandleFrame(conn, kind, payload));
        }

        private void OnConnectionClosed(Connection conn, string reason)
        {
            Post(() => RemoveConnection(conn, reason));
        }

        // ---- actor loop ----

        private void ActorLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                Action work;
                bool got;
                try { got = _work.TryTake(out work, TickIntervalMs, _cts.Token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }

                if (got && work != null)
                {
                    try { work(); }
                    catch (Exception ex) { Log("處理訊息時例外: " + ex); }
                }

                try { TickAll(); }
                catch (Exception ex) { Log("Tick 例外: " + ex); }
            }
        }

        /// <summary>定期工作:房間狀態機推進、frames 彙整、ping 逾時掃描。全在 actor 執行緒上。</summary>
        private void TickAll()
        {
            long now = NowMs();

            // 1) 房間狀態機(載入逾時 / 開場 / 結算)
            var ticks = _rooms.TickAll(now);
            for (int i = 0; i < ticks.Count; i++) ApplyRoomTick(ticks[i], now);

            // 2) 遊玩中的分數流:固定頻率把彙整結果推出去
            if (now - _lastFramePushMs >= 1000 / NetLimits.ServerFrameHz)
            {
                _lastFramePushMs = now;
                PushPendingFrames();
            }

            // 2b) 房間裡走動的位置流:同樣攢起來定頻推,但比分數流密(位置是連續量,太疏會一格一格跳)
            if (now - _lastMovePushMs >= 1000 / NetLimits.ServerMoveHz)
            {
                _lastMovePushMs = now;
                PushPendingMoves();
            }

            // 3) ping 逾時 = 斷線 = 離房(每秒掃一次就夠)
            if (now - _lastPingSweepMs >= 1000)
            {
                _lastPingSweepMs = now;
                SweepDeadConnections(now);
            }

            // 3b) 下載中的歌:把 chunk 補到水位(流量控制,見 PumpDownloads)。
            //     停滯逾時也在裡面就地判 —— 它每輪本來就在看每一份下載。
            PumpDownloads(now);

            // 3c) 上傳方向沒有「每輪都會跑過一遍」的地方(它是被 chunk 推著走的)→ 另外掃。
            SweepStalledUploads(now);

            // 4) 歌曲暫存清理(15 分鐘一次)
            //
            // 🔴 有上傳進行中就整輪跳過。上傳是「一個檔一個檔 commit 進 files/,最後才寫 pack json」,
            // 所以在那段時間裡已經收好的 blob **還沒有任何 pack 引用它們** → 清理程序會把它們當孤兒刪掉,
            // 然後 FinishUpload 的存在檢查失敗 → 整份上傳白做。一首 200 MB 的歌在慢線路上傳得夠久,
            // 這不是理論問題。延後一輪(15 分鐘)完全無害,所以用最笨但最明顯正確的做法。
            if (_janitor.Due(now))
            {
                if (_uploads.Count > 0)
                {
                    LogVerbose("歌曲暫存清理:有 " + _uploads.Count + " 份上傳進行中,這輪跳過");
                    _janitor.Defer(now);   // 不推遲的話 Due 會一直是 true → 每秒 20 行日誌
                }
                else
                {
                    var r = _janitor.Sweep(now, PinnedPackIds());
                    if (r.DidAnything) Log("歌曲暫存清理:" + r);
                }
            }
        }

        /// <summary>
        /// 存活房間現在選的那些歌 —— 清理時**絕對不能刪**這些包(<see cref="BlobJanitor.Sweep"/>)。
        /// 少了它,一場正在等人下載的比賽會被自己的清理程序把來源刪掉。
        /// </summary>
        private HashSet<string> PinnedPackIds()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var room in _rooms.Rooms)
            {
                var song = room.State != null ? room.State.Song : null;
                if (song != null && !song.Official && !string.IsNullOrEmpty(song.PackId)) set.Add(song.PackId);
            }
            return set;
        }

        private void SweepDeadConnections(long now)
        {
            List<Connection> dead = null;
            foreach (var kv in _conns)
            {
                var c = kv.Value;
                if (now - c.LastRecvMs <= NetLimits.PingTimeoutMs) continue;
                if (dead == null) dead = new List<Connection>();
                dead.Add(c);
            }
            if (dead == null) return;

            for (int i = 0; i < dead.Count; i++)
            {
                Log("連線 #" + dead[i].ConnId + " ping 逾時 → 視為斷線");
                dead[i].Kill("pingTimeout");
                RemoveConnection(dead[i], "pingTimeout");
            }
        }

        private void RemoveConnection(Connection conn, string reason)
        {
            if (!_conns.Remove(conn.ConnId)) return;

            // 認得這個人就用名字報離線;還沒 hello 的(掃 port、握手失敗)只是雜訊 → verbose。
            //
            // 🔴 例外:**身上還有傳輸的 file 連線一律用 Log**。它關掉就代表那份上傳/下載當場死掉,
            // 而那正是「傳完歌卻按不了準備」查起來最缺的一行 —— 以前它只在 verbose 出現,
            // 於是正式 log 上「下載開始」之後就是一片空白,分不出還在傳、傳完了、還是連線沒了。
            bool hadTransfer = _uploads.ContainsKey(conn.ConnId) || _downloads.ContainsKey(conn.ConnId);
            if (conn.UserId != 0 && conn.Role == NetProto.RoleControl)
                Log("user " + conn.UserId + "「" + conn.Name + "」離線(" + reason + ")");
            else if (hadTransfer)
                Log("傳檔連線 #" + conn.ConnId + " 關閉(" + reason + ")— user " + conn.UserId);
            else
                LogVerbose("連線 #" + conn.ConnId + " 關閉(" + reason + ")");

            // 那個人的上傳配額紀錄可以丟了(不清的話這張表會跟著 server 的執行時間一直長)。
            if (conn.UserId != 0 && conn.Role == NetProto.RoleControl) _quota.Forget(conn.UserId);

            // 🔴 離線**之前**再寫一次快照。三個 setXxx handler 各自寫過了,但最後一局打完到斷線之間
            //    還可能有一次 setCard 沒送到(client 是打完才送,而人常常是打完就關掉)——
            //    這一次補寫讓「最後看到的樣子」真的是最後的樣子。寫的是連線上那份,不是重新算的。
            PersistPlayer(conn);

            // 傳輸中斷:關掉開著的檔案 handle、清掉暫存目錄。
            // 不做的話那份半成品會一直佔著空間,而且沒有任何 pack 引用它 → 連 janitor 都掃不到
            // (它只認 files/ 底下的東西)。
            CloseBlobSessions(conn.ConnId);

            if (!string.IsNullOrEmpty(conn.SessionKey) && conn.Role == NetProto.RoleControl)
                _sessions.Remove(conn.SessionKey);

            if (conn.UserId != 0 && conn.Role == NetProto.RoleControl)
            {
                Connection existing;
                if (_byUser.TryGetValue(conn.UserId, out existing) && existing == conn)
                    _byUser.Remove(conn.UserId);

                // 斷線 == 離房(R6)。
                LeaveRoomFor(conn.UserId);
            }
        }

        // ---- 廣播 helper ----

        /// <summary>把一份 roomState 推給房裡所有人(座位 + 旁觀)。編碼一次。</summary>
        private void BroadcastRoomState(NetRoom room)
        {
            if (room == null) return;
            var bytes = room.State.EncodeMessage().Utf8();
            ForEachInRoom(room, c => c.SendPreEncoded(bytes));
        }

        /// <summary>
        /// 對**大廳裡**每一條 control 連線做一件事 —— 「大廳」的定義就是「線上、但不在任何房間裡」
        /// (server 沒有大廳這個容器,大廳是房間的補集)。大廳聊天用它廣播,見 <c>OnChatSay</c>。
        ///
        /// 🔴 發話者自己也在這個集合裡,**要留著** —— 那句話要回到他自己的聊天區才看得見,
        ///    client 不會先斬後奏地把自己說的話畫上去(密語也是同一個原則,見 <c>OnChatWhisper</c>)。
        /// </summary>
        private void ForEachInLobby(Action<Connection> act)
        {
            foreach (var kv in _byUser)
            {
                var c = kv.Value;
                if (c == null || c.IsClosed) continue;
                if (_rooms.RoomOf(c.UserId) != null) continue;   // 在房裡的人收房內那份,不重複收
                act(c);
            }
        }

        /// <summary>對房裡每一條 control 連線做一件事。</summary>
        private void ForEachInRoom(NetRoom room, Action<Connection> act)
        {
            if (room == null) return;
            var seats = room.State.Seats;
            for (int i = 0; i < seats.Length; i++)
            {
                if (!seats[i].IsTaken) continue;
                var c = ControlOf(seats[i].UserId);
                if (c != null) act(c);
            }
            var specs = room.State.Spectators;
            if (specs != null)
                for (int i = 0; i < specs.Length; i++)
                {
                    var c = ControlOf(specs[i].UserId);
                    if (c != null) act(c);
                }
        }

        private Connection ControlOf(int userId)
        {
            Connection c;
            return _byUser.TryGetValue(userId, out c) && !c.IsClosed ? c : null;
        }

        /// <summary>
        /// 照名字找 control 連線(不分大小寫)。兩個用途:
        ///   • 密語要送給誰 —— 全服都找,不限同房。
        ///   • 「這個名字現在有人在用嗎」—— 握手與改名的唯一性檢查(見 <c>OnHello</c> / <c>OnSetIdentity</c>)。
        ///
        /// 那兩處把名字擋成唯一的(**同一份比對規則:不分大小寫**),所以正常情況下這裡最多只會找到一個。
        /// 同名時取 userId 最小的那條是保險:Dictionary 的列舉順序不保證,萬一唯一性哪天被繞過,
        /// 不挑一個穩定的規則的話「密語進到誰的視窗」會隨執行而變 —— 那種 bug 沒人查得出來。
        /// userId 最小 == 先上線的那個,而先上線的正是同名時**留下來**的那一個。
        /// </summary>
        private Connection ControlByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Connection best = null;
            foreach (var kv in _byUser)
            {
                var c = kv.Value;
                if (c == null || c.IsClosed) continue;
                if (!string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || c.UserId < best.UserId) best = c;
            }
            return best;
        }

        private void SendTo(int userId, JObj msg)
        {
            var c = ControlOf(userId);
            if (c != null) c.Send(msg);
        }

        /// <summary>
        /// 回一個 error(帶上原請求的 rq 讓 client 對得上)。
        ///
        /// 🔴 一定要把拒絕**印出來**:這些規則(host-only、要有歌、已準備不能換隊…)在 client 端全都是
        /// 靜默的 —— 玩家按了沒反應,而 server 這邊什麼都沒留。實際上因為這個查了很久:
        /// 「兩台都看得到歌名、按開始沒反應」的真因是 server 眼中這間房沒有歌,而唯一能證明它的
        /// 就是這一行 log。拒絕本來就是低頻事件,印出來不會吵。
        ///
        /// 🔴 而且要印出**哪個請求**與**當下的狀態**。只印 code 的話 log 長這樣:
        ///   <c>✗ user 1 的請求被拒:badState</c> ×12
        /// —— <c>badState</c> 有十幾個來源(場次對不上、狀態機不允許、client 自稱保留狀態…),
        /// 而十二行一模一樣的字連「是同一個請求重送十二次還是十二個不同請求」都分不出來。
        /// 加上請求型別(<see cref="Connection.CurMsgType"/>)、<paramref name="note"/> 的請求內容、
        /// 以及 <see cref="DescribeConn"/> 的當下狀態,同一行就能直接讀出「誰、按了什麼、當時在哪一階段」。
        /// </summary>
        /// <param name="msg">給 client 的簡短原因(會進 wire)。</param>
        /// <param name="note">只進 log 的請求細節(送上來的參數值)。不進 wire —— 診斷用的東西沒必要外流。</param>
        private void SendError(Connection conn, int rq, string code, string msg = null, string note = null)
        {
            var o = JObj.New().Str(NetProto.FieldType, NetProto.Error).Str("code", code ?? NetProto.ErrBadState);
            if (rq != 0) o.Int(NetProto.FieldRequest, rq);
            if (!string.IsNullOrEmpty(msg)) o.Str("msg", msg);
            conn.Send(o);
            Log("✗ user " + conn.UserId + " 的 " + (string.IsNullOrEmpty(conn.CurMsgType) ? "?" : conn.CurMsgType)
                + (rq != 0 ? "#" + rq : "")
                + (string.IsNullOrEmpty(note) ? "" : "(" + note + ")")
                + " 被拒:" + (code ?? "?")
                + (string.IsNullOrEmpty(msg) ? "" : " — " + msg)
                + " | " + DescribeConn(conn));
        }

        private void SendOpError(Connection conn, int rq, NetRoomOp op, string note = null)
        {
            var code = op.ToErrorCode();
            if (code != null) SendError(conn, rq, code, null, note);
        }

        /// <summary>
        /// 拒絕時要印的「這個人當下處在什麼狀態」。
        ///
        /// 這裡挑的欄位就是**規則實際會看的那些**(見 <see cref="NetRoom"/>):房間階段、
        /// 進行中的場次與他是不是參與者、座位上的 ready / playState / 有沒有這首歌。
        /// 任何一次拒絕都是這幾個值其中之一不合 —— 一起印出來,才不用再回頭猜。
        /// </summary>
        private string DescribeConn(Connection conn)
        {
            if (!conn.HelloDone) return "還沒握手";
            if (conn.Role == NetProto.RoleFile) return "檔案連線";

            var room = _rooms.RoomOf(conn.UserId);
            if (room == null) return "不在任何房間";

            string s = "房 " + room.Code + " " + room.Status;

            var match = room.Match;
            if (match == null) s += " 沒有進行中的場次";
            else s += " 第" + match.MatchId + "場(" + (IsParticipant(match, conn.UserId) ? "參與者" : "非參與者") + ")";

            var seat = room.State.SeatOf(conn.UserId);
            if (seat != null)
            {
                s += " 座位" + room.State.SeatIndexOf(conn.UserId)
                   + (room.State.IsHost(conn.UserId) ? "(房主)" : "")
                   + " play=" + seat.PlayState
                   + " ready=" + (seat.Ready ? "是" : "否")
                   + " avail=" + seat.Avail;
            }
            else if (room.State.SpectatorIndexOf(conn.UserId) >= 0) s += " 旁觀中";
            else s += " 不在座位也不在旁觀名單";

            return s;
        }

        private static bool IsParticipant(NetMatchInfo match, int userId)
        {
            var ids = match.ParticipantUserIds;
            for (int i = 0; i < ids.Length; i++) if (ids[i] == userId) return true;
            return false;
        }

        /// <summary>踢出通知(client 收到會回選男女畫面)。</summary>
        private void SendKicked(int userId, string reason)
        {
            SendTo(userId, JObj.New().Str(NetProto.FieldType, NetProto.Kicked).Str("reason", reason));
        }

        /// <summary>
        /// server 的每一行訊息都從這裡出去 —— 時間戳、畫面、依日期分的 log 檔都由
        /// <see cref="ServerLog"/> 統一處理(誰都不要自己 Console.WriteLine,
        /// 那樣寫出去的行不會進檔案,而「檔案裡沒有」與「事情沒發生」看起來一樣)。
        /// </summary>
        internal static void Log(string line)
        {
            ServerLog.Write(line);
        }

        /// <summary>位元組數印成人看得懂的大小(log 用,不精確沒關係)。</summary>
        internal static string Mb(long bytes)
        {
            if (bytes >= 1024L * 1024L) return (bytes / (1024L * 1024L)) + " MB";
            if (bytes >= 1024L) return (bytes / 1024L) + " KB";
            return bytes + " B";
        }

        internal void LogVerbose(string line)
        {
            if (_opts.Verbose) ServerLog.Write(line);
        }
    }
}
