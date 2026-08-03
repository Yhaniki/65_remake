using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Sdo.Net;
using Sdo.Server;
using Sdo.Server.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// 憑證指紋比對的純規則(<see cref="TlsPinning"/>)。
    ///
    /// 這一組守的是**釘選不能被繞過**。最容易寫錯的兩件事:
    ///   ① 使用者沒填指紋時「什麼都符合」→ TLS 只剩裝飾;
    ///   ② 使用者貼的是 openssl 印的 <c>AA:BB:…</c> 卻被判定成格式錯誤 → 以為功能壞了。
    /// </summary>
    public class TlsPinningTests
    {
        private const string Hex64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Test]
        public void Separators_And_Case_Are_Tolerated()
        {
            // openssl 印冒號、Windows 憑證管理員印空白,而使用者是複製貼上的。
            Assert.AreEqual(Hex64, TlsPinning.Normalize(Hex64.ToUpperInvariant()));
            string colons = string.Join(":", Split2(Hex64));
            Assert.AreEqual(Hex64, TlsPinning.Normalize(colons));
            Assert.AreEqual(Hex64, TlsPinning.Normalize(string.Join(" ", Split2(Hex64.ToUpperInvariant()))));
            Assert.AreEqual(Hex64, TlsPinning.Normalize("  " + colons + "\r\n"));
            Assert.IsTrue(TlsPinning.Matches(colons, Hex64), "冒號版與純 hex 版必須視為同一個指紋");
        }

        [Test]
        public void Not_Exactly_Sixty_Four_Hex_Chars_Is_Not_A_Fingerprint()
        {
            Assert.AreEqual("", TlsPinning.Normalize(Hex64.Substring(1)), "63 個字元不是 SHA-256");
            Assert.AreEqual("", TlsPinning.Normalize(Hex64 + "ab"), "太長也不行");
            Assert.AreEqual("", TlsPinning.Normalize(Hex64.Substring(0, 62) + "gg"), "非 hex 字元 → 整串不採用");
            Assert.AreEqual("", TlsPinning.Normalize("(貼錯東西了)"));
            Assert.IsFalse(TlsPinning.Configured(Hex64.Substring(1)));
            Assert.IsTrue(TlsPinning.Configured(Hex64));
        }

        [Test]
        public void An_Empty_Pin_Matches_Nothing()
        {
            // 🔴 這是整支檔案最重要的一條:「沒設定」絕對不能等於「什麼都符合」。
            Assert.IsFalse(TlsPinning.Matches(null, Hex64));
            Assert.IsFalse(TlsPinning.Matches("", Hex64));
            Assert.IsFalse(TlsPinning.Matches("   ", Hex64));
            Assert.IsFalse(TlsPinning.Configured(null));
            // 反方向也一樣:收到的憑證算不出指紋時不能放行。
            Assert.IsFalse(TlsPinning.Matches(Hex64, ""));
            Assert.IsFalse(TlsPinning.Matches(Hex64, null));
        }

        [Test]
        public void A_Different_Fingerprint_Does_Not_Match()
        {
            string other = Hex64.Substring(0, 63) + "e";
            Assert.IsFalse(TlsPinning.Matches(Hex64, other), "差一個字元就是另一張憑證");
            Assert.IsTrue(TlsPinning.Matches(Hex64, Hex64));
        }

        [Test]
        public void ToHex_Is_Lowercase_And_Zero_Padded()
        {
            Assert.AreEqual("000aff", TlsPinning.ToHex(new byte[] { 0x00, 0x0a, 0xff }));
            Assert.AreEqual("", TlsPinning.ToHex(null));
        }

        private static string[] Split2(string s)
        {
            var parts = new string[s.Length / 2];
            for (int i = 0; i < parts.Length; i++) parts[i] = s.Substring(i * 2, 2);
            return parts;
        }
    }

    /// <summary>
    /// **真的做一次 TLS 握手**的整合測試。
    ///
    /// 為什麼不能只測純規則:TLS 這條路徑上會失敗的是「線接錯了」——
    /// stream 沒換成 SslStream、握手在 accept 迴圈裡把 server 卡住、writer 先寫了明文…
    /// 這些單元測試一條都不會紅,但實際上誰都連不上(或更糟:連上了卻沒加密)。
    /// </summary>
    public class TlsHandshakeTests
    {
        private Hub _hub;
        private Task _hubTask;
        private string _dataDir;
        private string _pfxPath;
        private string _fingerprint;

        private const string Pw = "abab123";
        private const string PfxPass = "pfx-pass";

        [SetUp]
        public void StartTlsServer()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "sdo_tls_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dataDir);
            _pfxPath = Path.Combine(_dataDir, "test.pfx");

            using (var cert = MakeSelfSigned())
            {
                File.WriteAllBytes(_pfxPath, cert.Export(X509ContentType.Pfx, PfxPass));
                _fingerprint = Sha256Hex(cert.RawData);
            }

            var opts = new ServerOptions
            {
                Port = 0,
                Bind = "127.0.0.1",
                DataDir = _dataDir,
                CodeSeed = 4242,
                Password = Pw,
                TlsCertFile = _pfxPath,
                TlsCertPass = PfxPass,
            };
            string err;
            Assert.IsTrue(opts.Validate(out err), err);

            _hub = new Hub(opts);
            Assert.IsNull(_hub.TlsError, "憑證應該載得起來:" + _hub.TlsError);
            Assert.AreEqual(_fingerprint, _hub.TlsFingerprint,
                "server 印給玩家貼進 config.ini 的指紋,必須就是憑證本身的 SHA-256");

            _hubTask = Task.Factory.StartNew(_hub.Run, TaskCreationOptions.LongRunning);
            var sw = Stopwatch.StartNew();
            while (!_hub.IsListening && sw.ElapsedMilliseconds < 5000) Thread.Sleep(5);
            Assert.IsTrue(_hub.IsListening, "server 沒有在 5 秒內開始監聽");
        }

        [TearDown]
        public void StopServer()
        {
            if (_hub != null) _hub.Stop();
            if (_hubTask != null) { try { _hubTask.Wait(3000); } catch { } }
            try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true); } catch { }
        }

        [Test]
        public void A_Pinned_Client_Completes_The_Handshake_And_Gets_Welcome()
        {
            using (var c = TlsClient.Connect(_hub.ActualPort, _fingerprint))
            {
                Assert.IsTrue(c.IsEncrypted, "連線必須真的是加密的");
                c.SendHello(Pw);
                var welcome = c.WaitFor(NetProto.Welcome);
                Assert.IsNotNull(welcome, "指紋相符 + 密碼正確 → 應該收到 welcome");
                Assert.Greater(NetJson.Int(welcome, "userId"), 0);
            }
        }

        [Test]
        public void A_Client_Pinning_The_Wrong_Fingerprint_Is_Refused()
        {
            // 中間人的情境:憑證換了一張。client 必須**連不上**,而不是「先連上再說」。
            string wrong = _fingerprint.Substring(0, 63) + (_fingerprint[63] == 'a' ? 'b' : 'a');
            Assert.Throws<AuthenticationException>(() => TlsClient.Connect(_hub.ActualPort, wrong).Dispose());
        }

        [Test]
        public void A_Client_With_No_Pin_Is_Refused_Because_The_Cert_Is_Self_Signed()
        {
            // 沒填指紋 → 走一般 CA 驗證 → 自簽必定失敗。這正是我們要的:
            // 「沒填就放行」才是漏洞,而它的症狀是「一切正常」。
            Assert.Throws<AuthenticationException>(() => TlsClient.Connect(_hub.ActualPort, null).Dispose());
        }

        [Test]
        public void A_Plaintext_Client_Gets_Nothing_From_A_Tls_Server()
        {
            // 使用者最可能的手滑:server 開了 TLS,client 的 config.ini 忘了 serverTls=1。
            // 要「明確地連不上」,絕不能退回明文。
            using (var tcp = new TcpClient())
            {
                tcp.NoDelay = true;
                tcp.Connect("127.0.0.1", _hub.ActualPort);
                var stream = tcp.GetStream();
                var hello = JObj.New()
                    .Str(NetProto.FieldType, NetProto.Hello)
                    .Int(NetProto.FieldRequest, 1)
                    .Int("proto", NetProto.Version)
                    .Str("role", NetProto.RoleControl)
                    .Str("playerId", "plain")
                    .Str("name", "明文")
                    .Str("password", Pw);
                try
                {
                    NetFrame.Write(stream, NetLimits.FrameKindJson, hello.Utf8());
                    stream.Flush();
                }
                catch (IOException) { return; }   // server 已經把它關了 —— 也是合格的結果

                tcp.ReceiveTimeout = 2000;
                byte kind;
                byte[] payload;
                FrameStatus st;
                try { st = NetFrame.TryRead(stream, out kind, out payload); }
                catch { return; }                  // 連線被關 = 正確行為
                Assert.AreNotEqual(FrameStatus.Ok, st, "明文 client 不該從 TLS server 讀到任何有效 frame");
            }
        }

        [Test]
        public void The_Server_Keeps_Accepting_After_A_Failed_Handshake()
        {
            // 🔴 握手如果做在 accept 迴圈裡,一條壞連線就會擋住後面所有人。
            // 這條測試就是那個回歸:先弄壞一次握手,再確認正常的 client 還連得進來。
            using (var junk = new TcpClient())
            {
                junk.Connect("127.0.0.1", _hub.ActualPort);
                junk.GetStream().Write(new byte[] { 1, 2, 3, 4, 5 }, 0, 5);   // 不是 ClientHello
                junk.GetStream().Flush();
            }
            using (var c = TlsClient.Connect(_hub.ActualPort, _fingerprint))
            {
                c.SendHello(Pw);
                Assert.IsNotNull(c.WaitFor(NetProto.Welcome), "壞掉的握手不該影響後面的連線");
            }
        }

        // ---- helpers ----

        private static X509Certificate2 MakeSelfSigned()
        {
            using (var rsa = RSA.Create(2048))
            {
                var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName("localhost");
                san.AddIpAddress(IPAddress.Loopback);
                req.CertificateExtensions.Add(san.Build());
                return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            }
        }

        private static string Sha256Hex(byte[] der)
        {
            using (var sha = SHA256.Create()) return TlsPinning.ToHex(sha.ComputeHash(der));
        }

        /// <summary>測試用的 TLS client。驗證規則刻意與 <c>NetConnection.TryHandshake</c> 同一套。</summary>
        private sealed class TlsClient : IDisposable
        {
            private readonly TcpClient _tcp;
            private readonly SslStream _ssl;

            public bool IsEncrypted => _ssl.IsEncrypted;

            private TlsClient(TcpClient tcp, SslStream ssl) { _tcp = tcp; _ssl = ssl; }

            public static TlsClient Connect(int port, string pin)
            {
                var tcp = new TcpClient();
                tcp.NoDelay = true;
                tcp.Connect("127.0.0.1", port);
                bool pinned = TlsPinning.Configured(pin);
                var ssl = new SslStream(tcp.GetStream(), false, (s, cert, chain, errors) =>
                {
                    if (cert == null) return false;
                    if (pinned) return TlsPinning.Matches(pin, Sha256Hex(cert.GetRawCertData()));
                    return errors == SslPolicyErrors.None;
                });
                try
                {
                    ssl.AuthenticateAsClient("localhost", null, SslProtocols.Tls12, false);
                }
                catch
                {
                    try { ssl.Dispose(); } catch { }
                    try { tcp.Close(); } catch { }
                    throw;
                }
                return new TlsClient(tcp, ssl);
            }

            public void SendHello(string password)
            {
                var hello = JObj.New()
                    .Str(NetProto.FieldType, NetProto.Hello)
                    .Int(NetProto.FieldRequest, 1)
                    .Int("proto", NetProto.Version)
                    .Str("role", NetProto.RoleControl)
                    .Str("playerId", "tls-test")
                    .Str("name", "加密測試")
                    .Int("level", 1);
                if (password != null) hello.Str("password", password);
                NetFrame.Write(_ssl, NetLimits.FrameKindJson, hello.Utf8());
                _ssl.Flush();
            }

            public object WaitFor(string type, int timeoutMs = 3000)
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    _tcp.ReceiveTimeout = (int)Math.Max(1, timeoutMs - sw.ElapsedMilliseconds);
                    byte kind;
                    byte[] payload;
                    FrameStatus st;
                    try { st = NetFrame.TryRead(_ssl, out kind, out payload); }
                    catch { return null; }
                    if (st != FrameStatus.Ok) return null;
                    if (kind != NetLimits.FrameKindJson) continue;
                    object node;
                    string got;
                    if (!NetJson.TryParseMessage(payload, 0, payload.Length, out node, out got)) continue;
                    if (got == type) return node;
                }
                return null;
            }

            public void Dispose()
            {
                try { _ssl.Dispose(); } catch { }
                try { _tcp.Close(); } catch { }
            }
        }
    }
}
