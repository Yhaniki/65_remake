using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Sdo.Net;

namespace Sdo.Server.Net
{
    /// <summary>
    /// 一條 TCP 連線。
    ///
    /// **執行緒歸屬要分清楚**:
    ///   • 讀取在自己的 async Task 上,收到完整 frame 後把工作 **marshal 進 Hub 的 actor loop**
    ///     —— 所以房間狀態永遠只有一個執行緒在碰,不需要任何 lock。
    ///   • 送出丟進自己的 <b>有界</b> outbound 佇列,由專屬的 writer Task 消化 ——
    ///     所以慢的 client 不會拖累 Hub。
    ///
    /// **佇列滿了的處置刻意分兩種**(這是防「慢 client 拖垮 server」的關鍵):
    ///   • <c>frame</c>(遊玩中的分數流)滿了就**丟掉**。它是 fire-and-forget 的最新狀態快照,
    ///     漏幾筆只是別人的分數跳動不順,下一筆就補上了。
    ///   • control 訊息滿了就**斷線**。那些是狀態變更,漏一筆 client 的房間狀態就永久偏離 ——
    ///     與其讓它顯示錯的東西,不如讓它斷線重連拿一份完整快照。
    /// </summary>
    public sealed class Connection
    {
        /// <summary>outbound 佇列容量。控制訊息很小,64 筆已經是「client 卡了好幾秒」的量級。</summary>
        private const int OutboundCapacity = 64;

        private readonly TcpClient _tcp;
        /// <summary>
        /// 收發的 stream。**不是 readonly**:啟用 TLS 時 <see cref="TryStartTls"/> 會把它換成
        /// 包起來的 <see cref="System.Net.Security.SslStream"/>。換的時機在 writer/reader 兩條
        /// task 開始**之前**,所以之後所有人看到的都是同一個(加密的)那份。
        /// </summary>
        private Stream _stream;
        private readonly BlockingCollection<Outgoing> _outbound
            = new BlockingCollection<Outgoing>(new ConcurrentQueue<Outgoing>(), OutboundCapacity);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private int _closed;

        /// <summary>server 內部的連線序號(log 用;與 userId 不同)。</summary>
        public int ConnId { get; }

        /// <summary>server 配發的使用者 id。0 = 還沒 hello。</summary>
        public int UserId;

        /// <summary>"control" 或 "file"(見 <see cref="NetProto.RoleControl"/>)。</summary>
        public string Role = NetProto.RoleControl;

        /// <summary>握手帶來的身分資料。</summary>
        public string PlayerId = "";
        public string Name = "";
        public string Guild = "";
        public int Level;
        public NetAvatarLook Look = new NetAvatarLook();

        /// <summary>file 連線用它認親到同一個玩家的 control 連線。</summary>
        public string SessionKey = "";

        /// <summary>握手完成了嗎?沒完成之前只准送 <c>hello</c>。</summary>
        public bool HelloDone;

        /// <summary>最後一次收到任何東西的時刻(Unix ms)。用來判斷 ping 逾時。</summary>
        public long LastRecvMs;

        /// <summary>rate limit 的計數狀態(由 Hub 的單執行緒維護)。</summary>
        public readonly RateCounters Rate = new RateCounters();

        public bool IsClosed => Volatile.Read(ref _closed) != 0;

        public string RemoteLabel { get; }

        public Connection(int connId, TcpClient tcp)
        {
            ConnId = connId;
            _tcp = tcp;
            _tcp.NoDelay = true;   // 房間訊息都很小,Nagle 只會讓互動變鈍
            _stream = tcp.GetStream();
            try { RemoteLabel = tcp.Client.RemoteEndPoint != null ? tcp.Client.RemoteEndPoint.ToString() : "?"; }
            catch { RemoteLabel = "?"; }
        }

        /// <summary>這條連線是加密的嗎(log 用)。</summary>
        public bool IsTls { get; private set; }

        /// <summary>
        /// 把這條連線升級成 TLS(server 端握手)。**必須在 writer/reader 開始之前呼叫。**
        ///
        /// 握手期間套 socket 逾時:對方連上來卻不講話的話,不設逾時會讓一條 task 永遠掛著
        /// (「開一堆連線只為了佔住 server 的 task」是不需要任何認證就做得到的事)。
        /// 握手完成後解掉 —— 之後的閒置由協定層的 ping 逾時管,不是 socket 層。
        /// </summary>
        public bool TryStartTls(X509Certificate2 cert, int handshakeTimeoutMs, out string error)
        {
            error = null;
            if (cert == null) { error = "沒有憑證"; return false; }
            SslStream ssl = null;
            try
            {
                _tcp.ReceiveTimeout = handshakeTimeoutMs;
                _tcp.SendTimeout = handshakeTimeoutMs;
                ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
                // 只開 TLS 1.2 / 1.3。舊版本(TLS 1.0/1.1、SSLv3)是已知有問題的,而這邊兩端的
                // client 都是自己寫的 —— 沒有任何要照顧舊 client 的理由。
                ssl.AuthenticateAsServer(cert, clientCertificateRequired: false,
                    enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
                    checkCertificateRevocation: false);
                _tcp.ReceiveTimeout = 0;
                _tcp.SendTimeout = 0;
                _stream = ssl;
                IsTls = true;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                try { if (ssl != null) ssl.Dispose(); } catch { }
                return false;
            }
        }

        // ---- 送出 ----

        /// <summary>送一個 control 訊息。佇列滿了 → 斷線(見類別註解)。</summary>
        public void Send(JObj msg)
        {
            if (msg == null) return;
            Enqueue(new Outgoing(NetLimits.FrameKindJson, msg.Utf8(), critical: true));
        }

        /// <summary>
        /// 送一個遊玩中的分數流訊息。佇列滿了 → **丟掉**(不斷線)。
        /// </summary>
        public void SendLossy(JObj msg)
        {
            if (msg == null) return;
            Enqueue(new Outgoing(NetLimits.FrameKindJson, msg.Utf8(), critical: false));
        }

        /// <summary>
        /// 送一份已經編好的 UTF-8 payload。廣播用 —— 一份 roomState 要發給房裡 6 個人時,
        /// 編碼一次就好(<see cref="Send"/> 每次呼叫都會重新 <c>Utf8()</c>)。
        /// 呼叫端**不可以**在送出後改動那個陣列。
        /// </summary>
        public void SendPreEncoded(byte[] utf8, bool critical = true)
        {
            if (utf8 == null) return;
            Enqueue(new Outgoing(NetLimits.FrameKindJson, utf8, critical));
        }

        /// <summary>
        /// 還有幾筆等著送出去。**下載的流量控制靠它** —— 一首 30 MB 的歌是 500 塊 chunk,
        /// 一次全塞進來會超過 <see cref="OutboundCapacity"/>,而 chunk 是 critical(滿了就斷線)。
        /// 所以 Hub 每輪 tick 只補到一個水位,見 <c>PumpDownloads</c>。
        /// </summary>
        public int OutboundCount => _outbound.Count;

        /// <summary>送檔案 chunk(走 file 連線)。</summary>
        public void SendChunk(byte[] data, int offset, int count)
        {
            var copy = new byte[count];
            Array.Copy(data, offset, copy, 0, count);
            Enqueue(new Outgoing(NetLimits.FrameKindChunk, copy, critical: true));
        }

        private void Enqueue(Outgoing item)
        {
            if (IsClosed) return;
            // TryAdd 不阻塞 —— Hub 的 actor loop 絕不能被一個慢 client 卡住。
            if (_outbound.TryAdd(item)) return;

            if (item.Critical) Close("outboundFull");
            // 非關鍵訊息:靜默丟棄。
        }

        /// <summary>送一個 bye 然後關連線。</summary>
        public void Kill(string reason)
        {
            try
            {
                var bye = JObj.New().Str(NetProto.FieldType, NetProto.Bye).Str("reason", reason ?? "");
                // 直接寫,不進佇列 —— 佇列可能已經滿了(那正是我們要斷線的原因)。
                WriteFrameDirect(NetLimits.FrameKindJson, bye.Utf8());
            }
            catch { }
            Close(reason);
        }

        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { _cts.Cancel(); } catch { }
            try { _outbound.CompleteAdding(); } catch { }
            try { if (_stream != null) _stream.Dispose(); } catch { }   // TLS 時這才是真正要收的那層
            try { _tcp.Close(); } catch { }
        }

        /// <summary>
        /// 送出用的鎖。**不要 <c>lock (_stream)</c>** —— TLS 會把 <c>_stream</c> 換成另一個物件,
        /// 鎖在會變的欄位上是「今天剛好對、改一行就靜默失效」的那種寫法(而且失效的症狀是
        /// 兩條 thread 同時寫進同一個 SslStream = 加密封包交錯 = 對方直接斷線)。
        /// </summary>
        private readonly object _writeLock = new object();

        private void WriteFrameDirect(byte kind, byte[] payload)
        {
            lock (_writeLock) { NetFrame.Write(_stream, kind, payload); }
        }

        // ---- 讀 / 寫迴圈 ----

        /// <summary>
        /// 讀迴圈。每收到一個完整 frame 就呼叫 <paramref name="onFrame"/>
        /// (那個 callback 負責把工作 marshal 進 Hub —— 這裡不碰任何共享狀態)。
        /// </summary>
        public void RunReadLoop(Action<Connection, byte, byte[]> onFrame, Func<long> nowMs, Action<Connection, string> onClosed)
        {
            string reason = "eof";
            try
            {
                while (!IsClosed)
                {
                    byte kind;
                    byte[] payload;
                    var st = NetFrame.TryRead(_stream, out kind, out payload);
                    if (st == FrameStatus.Eof) { reason = "eof"; break; }
                    if (st == FrameStatus.TooLarge) { reason = "frameTooLarge"; break; }
                    if (st == FrameStatus.BadKind) { reason = "badKind"; break; }
                    if (st == FrameStatus.Truncated) { reason = "truncated"; break; }

                    LastRecvMs = nowMs();
                    onFrame(this, kind, payload);
                }
            }
            catch (IOException) { reason = "ioError"; }
            catch (ObjectDisposedException) { reason = "closed"; }
            catch (Exception ex) { reason = "readError:" + ex.GetType().Name; }
            finally
            {
                Close(reason);
                onClosed(this, reason);
            }
        }

        /// <summary>寫迴圈。消化 outbound 佇列直到連線關閉。</summary>
        public void RunWriteLoop()
        {
            try
            {
                foreach (var item in _outbound.GetConsumingEnumerable(_cts.Token))
                {
                    WriteFrameDirect(item.Kind, item.Payload);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { Close("writeError"); }
        }

        public Task StartWriter()
            => Task.Factory.StartNew(RunWriteLoop, TaskCreationOptions.LongRunning);

        private struct Outgoing
        {
            public readonly byte Kind;
            public readonly byte[] Payload;
            /// <summary>true = 漏掉會讓 client 狀態永久偏離 → 佇列滿就斷線。</summary>
            public readonly bool Critical;

            public Outgoing(byte kind, byte[] payload, bool critical)
            {
                Kind = kind;
                Payload = payload;
                Critical = critical;
            }
        }
    }

    /// <summary>
    /// 每連線的 rate limit 計數。**只由 Hub 的單執行緒碰**,所以不需要同步。
    ///
    /// 用「固定視窗計數」而不是 token bucket:協定的訊息量很小,而我們要防的是
    /// 「壞掉或惡意的 client 把 server 打爆」,不是精細的流量整形。簡單的東西比較不會自己出錯。
    /// </summary>
    public sealed class RateCounters
    {
        private long _controlWindowStart;
        private int _controlCount;

        private long _frameWindowStart;
        private int _frameCount;

        private long _chatWindowStart;
        private int _chatCount;

        private long _moveWindowStart;
        private int _moveCount;

        private long _availLastMs;

        /// <summary>連續超限的次數 —— 偶爾爆一下丟訊息就好,一直爆就斷線。</summary>
        public int Strikes;

        public bool AllowControl(long nowMs)
            => Allow(nowMs, ref _controlWindowStart, ref _controlCount, 1000, NetLimits.RateControlPerSec);

        public bool AllowFrame(long nowMs)
            => Allow(nowMs, ref _frameWindowStart, ref _frameCount, 1000, NetLimits.RateFramePerSec);

        public bool AllowChat(long nowMs)
            => Allow(nowMs, ref _chatWindowStart, ref _chatCount, NetLimits.RateChatWindowMs, NetLimits.RateChatPerWindow);

        /// <summary>房間裡走動的位置回報(10 Hz + 餘裕)。超限就丟這一筆 —— 位置是狀態快照,丟掉下一筆就補上了。</summary>
        public bool AllowMove(long nowMs)
            => Allow(nowMs, ref _moveWindowStart, ref _moveCount, 1000, NetLimits.RateMovePerSec);

        /// <summary>
        /// 下載/上傳進度回報的節流(每 500ms 一筆)。
        /// client 自己也會節流,但 server 要獨立擋 —— 不能假設對方的 client 沒被改過。
        /// </summary>
        public bool AllowAvailProgress(long nowMs)
        {
            if (nowMs - _availLastMs < NetLimits.AvailProgressThrottleMs) return false;
            _availLastMs = nowMs;
            return true;
        }

        private static bool Allow(long nowMs, ref long windowStart, ref int count, int windowMs, int limit)
        {
            if (nowMs - windowStart >= windowMs) { windowStart = nowMs; count = 0; }
            if (count >= limit) return false;
            count++;
            return true;
        }
    }
}
