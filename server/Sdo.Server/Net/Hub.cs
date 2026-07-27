using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Sdo.Net;
using Sdo.Net.Server;

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
        private TcpListener _listener;

        public Hub(ServerOptions opts)
        {
            _opts = opts;
            int seed = opts.CodeSeed != 0 ? opts.CodeSeed : unchecked((int)DateTime.UtcNow.Ticks);
            _rooms = new RoomRegistry(opts.MaxRooms, seed);
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

            Console.WriteLine("[sdo-server] 監聽中 " + addr + ":" + ActualPort + "  (protocol v" + NetProto.Version + ")");
            Console.WriteLine("[sdo-server] " + _opts);
            Console.WriteLine("[sdo-server] ⚠️  MVP:沒有帳號認證、沒有加密 —— 請只在 LAN／信任的朋友之間使用。");

            var accept = Task.Factory.StartNew(AcceptLoop, TaskCreationOptions.LongRunning);

            ActorLoop();

            try { _listener.Stop(); } catch { }
            try { accept.Wait(1000); } catch { }
        }

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

                var conn = new Connection(Interlocked.Increment(ref _nextConnId), tcp);
                conn.LastRecvMs = NowMs();

                // 連線數上限:在 actor loop 裡判斷(它才知道目前有幾條)。
                Post(() =>
                {
                    if (_conns.Count >= _opts.MaxConnections)
                    {
                        conn.Kill("serverFull");
                        return;
                    }
                    _conns[conn.ConnId] = conn;
                    Log("連線 #" + conn.ConnId + " 來自 " + conn.RemoteLabel + "(共 " + _conns.Count + " 條)");
                });

                conn.StartWriter();
                Task.Factory.StartNew(
                    () => conn.RunReadLoop(OnFrameFromReader, NowMs, OnConnectionClosed),
                    TaskCreationOptions.LongRunning);
            }
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

            // 3) ping 逾時 = 斷線 = 離房(每秒掃一次就夠)
            if (now - _lastPingSweepMs >= 1000)
            {
                _lastPingSweepMs = now;
                SweepDeadConnections(now);
            }
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

            Log("連線 #" + conn.ConnId + " 關閉(" + reason + ")");

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

        private void SendTo(int userId, JObj msg)
        {
            var c = ControlOf(userId);
            if (c != null) c.Send(msg);
        }

        /// <summary>回一個 error(帶上原請求的 rq 讓 client 對得上)。</summary>
        private static void SendError(Connection conn, int rq, string code, string msg = null)
        {
            var o = JObj.New().Str(NetProto.FieldType, NetProto.Error).Str("code", code ?? NetProto.ErrBadState);
            if (rq != 0) o.Int(NetProto.FieldRequest, rq);
            if (!string.IsNullOrEmpty(msg)) o.Str("msg", msg);
            conn.Send(o);
        }

        private static void SendOpError(Connection conn, int rq, NetRoomOp op)
        {
            var code = op.ToErrorCode();
            if (code != null) SendError(conn, rq, code);
        }

        /// <summary>踢出通知(client 收到會回選男女畫面)。</summary>
        private void SendKicked(int userId, string reason)
        {
            SendTo(userId, JObj.New().Str(NetProto.FieldType, NetProto.Kicked).Str("reason", reason));
        }

        internal void Log(string line)
        {
            Console.WriteLine("[sdo-server] " + line);
        }

        internal void LogVerbose(string line)
        {
            if (_opts.Verbose) Console.WriteLine("[sdo-server] " + line);
        }
    }
}
