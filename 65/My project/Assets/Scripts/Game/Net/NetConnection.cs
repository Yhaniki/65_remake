using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Sdo.Net;
using UnityEngine;

namespace Sdo.Game.Net
{
    /// <summary>連線的狀態。</summary>
    public enum NetLinkState
    {
        Idle = 0,
        Connecting,
        Connected,
        Failed,
        Closed,
    }

    /// <summary>
    /// 遊戲端的 TCP 連線:framing + 收發佇列。**只負責搬位元組,不認識協定內容。**
    ///
    /// 執行緒安排(與 server 端對稱,好對照):
    ///   • 連線與讀取在背景 thread(絕不阻塞 Unity 主執行緒 —— 那會凍畫面)
    ///   • 寫入丟進佇列,由 writer thread 消化
    ///   • 主執行緒只碰兩個 <see cref="System.Collections.Concurrent"/> 佇列,靠
    ///     <see cref="Poll"/> 取訊息(由 <c>FrontendApp.Update</c> 每幀呼叫一次)
    ///
    /// 🔴 **Unity 特有的坑:editor domain reload。**
    /// 背景 thread 不會因為 assembly 重載而消失 —— 它會繼續跑、持有舊 assembly 的物件,
    /// 於是重新編譯時 editor 就卡住(要強制關掉 Unity)。<c>Thread.IsBackground = true</c>
    /// **不夠**:那只保證 thread 不阻止 process 結束,domain reload 不是 process 結束。
    /// 所以這裡維護一份靜態的活躍連線清單,並在 <c>beforeAssemblyReload</c> / 播放模式離開時
    /// 明確關掉全部。這件事一開始就要做對,否則整個開發過程都會很痛苦。
    /// </summary>
    public sealed class NetConnection
    {
        // ---- 靜態:活躍連線清單(給 editor domain reload 收尾用) ----

        private static readonly List<NetConnection> Live = new List<NetConnection>();
        private static readonly object LiveLock = new object();

        private static void Register(NetConnection c)
        {
            lock (LiveLock) { if (!Live.Contains(c)) Live.Add(c); }
        }

        private static void Unregister(NetConnection c)
        {
            lock (LiveLock) { Live.Remove(c); }
        }

        /// <summary>關掉所有還活著的連線。domain reload / 離開播放模式時呼叫。</summary>
        public static void CloseAll(string reason)
        {
            NetConnection[] all;
            lock (LiveLock) { all = Live.ToArray(); }
            for (int i = 0; i < all.Length; i++) all[i].Close(reason);
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void HookEditorLifecycle()
        {
            // 重新編譯前:一定要把 thread 收掉,否則 editor 會卡在 domain reload。
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += () => CloseAll("assemblyReload");

            // 離開播放模式:同理(否則下次進播放模式會有兩份 reader thread)。
            UnityEditor.EditorApplication.playModeStateChanged += st =>
            {
                if (st == UnityEditor.PlayModeStateChange.ExitingPlayMode) CloseAll("exitPlayMode");
            };
        }
#endif

        // ---- 實例 ----

        private readonly System.Collections.Concurrent.ConcurrentQueue<Inbound> _inbox
            = new System.Collections.Concurrent.ConcurrentQueue<Inbound>();
        private readonly System.Collections.Concurrent.BlockingCollection<Outbound> _outbox
            = new System.Collections.Concurrent.BlockingCollection<Outbound>(
                new System.Collections.Concurrent.ConcurrentQueue<Outbound>(), OutboxCapacity);

        /// <summary>送出佇列容量。滿了代表網路卡住 —— 那時候丟掉分數流、斷開控制訊息。</summary>
        private const int OutboxCapacity = 128;

        private TcpClient _tcp;
        private NetworkStream _stream;
        private Thread _reader;
        private Thread _writer;
        private int _closed;
        private volatile NetLinkState _state = NetLinkState.Idle;
        private volatile string _lastError = "";

        public NetLinkState State => _state;
        public string LastError => _lastError;
        public bool IsConnected => _state == NetLinkState.Connected;
        public bool IsClosed => Volatile.Read(ref _closed) != 0;

        public string Host { get; private set; }
        public int Port { get; private set; }

        /// <summary>已經送出去的訊息數 / 收到的訊息數(除錯面板用)。</summary>
        public int SentCount, RecvCount;

        /// <summary>
        /// 開始連線。**立刻回傳**(連線在背景進行)—— 呼叫端輪詢 <see cref="State"/>。
        /// DNS 解析與 TCP 三向交握都可能要好幾秒,絕不能擋在主執行緒上。
        /// </summary>
        public void BeginConnect(string host, int port, int timeoutMs = 5000)
        {
            if (_state == NetLinkState.Connecting || _state == NetLinkState.Connected) return;

            Host = host;
            Port = port;
            _state = NetLinkState.Connecting;
            _lastError = "";
            Register(this);

            var t = new Thread(() => ConnectWorker(host, port, timeoutMs));
            t.IsBackground = true;
            t.Name = "SdoNetConnect";
            t.Start();
        }

        private void ConnectWorker(string host, int port, int timeoutMs)
        {
            try
            {
                var tcp = new TcpClient();
                tcp.NoDelay = true;   // 房間訊息都很小,Nagle 只會讓互動變鈍

                var ar = tcp.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    try { tcp.Close(); } catch { }
                    Fail("連線逾時(" + host + ":" + port + ")");
                    return;
                }
                tcp.EndConnect(ar);

                if (IsClosed) { try { tcp.Close(); } catch { } return; }

                _tcp = tcp;
                _stream = tcp.GetStream();
                _state = NetLinkState.Connected;

                _writer = new Thread(WriteLoop) { IsBackground = true, Name = "SdoNetWrite" };
                _writer.Start();

                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "SdoNetRead" };
                _reader.Start();
            }
            catch (SocketException ex) { Fail("連不上 " + host + ":" + port + " —— " + ex.SocketErrorCode); }
            catch (Exception ex) { Fail("連線失敗:" + ex.Message); }
        }

        private void Fail(string message)
        {
            _lastError = message;
            _state = NetLinkState.Failed;
            Close("connectFailed");
        }

        // ---- 讀 ----

        private void ReadLoop()
        {
            string reason = "eof";
            try
            {
                while (!IsClosed)
                {
                    byte kind;
                    byte[] payload;
                    var st = NetFrame.TryRead(_stream, out kind, out payload);
                    if (st != FrameStatus.Ok) { reason = st.ToString(); break; }

                    _inbox.Enqueue(new Inbound(kind, payload));
                    Interlocked.Increment(ref RecvCount);
                }
            }
            catch (IOException) { reason = "ioError"; }
            catch (ObjectDisposedException) { reason = "closed"; }
            catch (Exception ex) { reason = "readError:" + ex.GetType().Name; }
            finally
            {
                if (_state == NetLinkState.Connected) _lastError = "連線中斷(" + reason + ")";
                Close(reason);
            }
        }

        // ---- 寫 ----

        private void WriteLoop()
        {
            try
            {
                foreach (var item in _outbox.GetConsumingEnumerable())
                {
                    if (IsClosed) break;
                    NetFrame.Write(_stream, item.Kind, item.Payload);
                    Interlocked.Increment(ref SentCount);
                }
            }
            catch (Exception) { Close("writeError"); }
        }

        /// <summary>送一個控制訊息。佇列滿了 → 斷線(狀態訊息漏掉會讓房間狀態永久偏離)。</summary>
        public void Send(JObj msg)
        {
            if (msg == null || IsClosed) return;
            if (!_outbox.TryAdd(new Outbound(NetLimits.FrameKindJson, msg.Utf8())))
                Close("outboxFull");
        }

        /// <summary>
        /// 送一個遊玩中的分數流訊息。**佇列滿了就丟掉,絕不阻塞、絕不斷線** ——
        /// 它是最新狀態的快照,漏幾筆只是別人的分數跳動不順,下一筆就補上。
        /// 這條路徑會在 gameplay 的 Update 裡呼叫,不能有任何卡住的可能。
        /// </summary>
        public void SendLossy(JObj msg)
        {
            if (msg == null || IsClosed) return;
            _outbox.TryAdd(new Outbound(NetLimits.FrameKindJson, msg.Utf8()));
        }

        // ---- 主執行緒 pump ----

        /// <summary>
        /// 取出一筆收到的訊息。由主執行緒呼叫(<c>FrontendApp.Update</c>)。
        /// 回 false = 這一幀沒有更多訊息了。
        /// </summary>
        public bool Poll(out byte kind, out byte[] payload)
        {
            Inbound item;
            if (_inbox.TryDequeue(out item)) { kind = item.Kind; payload = item.Payload; return true; }
            kind = 0;
            payload = null;
            return false;
        }

        /// <summary>還有幾筆沒處理(除錯面板用)。</summary>
        public int PendingInbound => _inbox.Count;

        // ---- 關閉 ----

        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;

            if (_state != NetLinkState.Failed) _state = NetLinkState.Closed;

            try { _outbox.CompleteAdding(); } catch { }
            try { if (_stream != null) _stream.Close(); } catch { }
            try { if (_tcp != null) _tcp.Close(); } catch { }

            // 等 thread 收掉,但**帶 timeout** —— 卡住的 socket 讀取可能不會馬上醒,
            // 而 domain reload 路徑上不能無限等(那就變成另一種卡死)。
            JoinBriefly(_reader);
            JoinBriefly(_writer);

            Unregister(this);
        }

        private static void JoinBriefly(Thread t)
        {
            if (t == null) return;
            try { if (t.IsAlive) t.Join(500); } catch { }
        }

        private struct Inbound
        {
            public readonly byte Kind;
            public readonly byte[] Payload;
            public Inbound(byte kind, byte[] payload) { Kind = kind; Payload = payload; }
        }

        private struct Outbound
        {
            public readonly byte Kind;
            public readonly byte[] Payload;
            public Outbound(byte kind, byte[] payload) { Kind = kind; Payload = payload; }
        }
    }
}
