using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Sdo.Net;
using Sdo.Server;
using Sdo.Server.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// 「**每一條**連線都要過同一組門」的回歸。
    ///
    /// 這條規則被違反過兩次,而且是同一個形狀:傳檔用的第二條連線(<c>role=file</c>)漏帶東西。
    ///   ① 漏帶密碼 → client 只看到「連線中斷(Eof)」,完全指不到密碼;
    ///   ② 漏帶 token → 公網 server 的上傳一律失敗。實機驗證抓到的:server log
    ///      「連線 #3 token 認證失敗,拒絕」、client log「傳檔失敗:server 關閉連線:badToken」,
    ///      但**遊戲畫面上毫無異狀** —— 因為那次兩台剛好都有那首歌。沒有這條測試,
    ///      它會一路活到有人真的缺歌那天才炸。
    ///
    /// server 端這兩道檢查都在 <c>OnHello</c> 的最前面、不分角色。這裡釘的就是「不分角色」,
    /// 以及反方向的「帶了就要放行」(守門不能把正常的傳檔一起擋掉)。
    /// </summary>
    public class FileConnectionAuthTests
    {
        private Hub _hub;
        private Task _hubTask;
        private string _dataDir;
        private const string Tok = "0123456789abcdef0123456789abcdef";
        private const string Pw = "abab123";

        [SetUp]
        public void StartServer()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "sdo_fileauth_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dataDir);
            var tokensFile = Path.Combine(_dataDir, "tokens.txt");
            File.WriteAllText(tokensFile, Tok + " = 00000000, 測試者\n");

            var opts = new ServerOptions
            {
                Port = 0,
                Bind = "127.0.0.1",
                DataDir = _dataDir,
                CodeSeed = 4242,
                Password = Pw,
                TokensFile = tokensFile,
            };
            string err;
            Assert.IsTrue(opts.Validate(out err), err);
            _hub = new Hub(opts);
            Assert.IsNull(_hub.TlsError);
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

        /// <summary>開一條認證好的 control 連線,回它的 sessionKey。</summary>
        private string OpenControl(out Plain c)
        {
            c = new Plain(_hub.ActualPort);
            c.SendHello(NetProto.RoleControl, playerId: "00000000", name: "主連線", password: Pw, token: Tok);
            var w = c.WaitFor(NetProto.Welcome);
            Assert.IsNotNull(w, "control 連線帶了密碼與 token 應該進得來");
            return NetJson.Str(w, "sessionKey");
        }

        [Test]
        public void A_File_Connection_Without_The_Token_Is_Refused()
        {
            Plain control;
            string key = OpenControl(out control);
            using (control)
            using (var noTok = new Plain(_hub.ActualPort))
            {
                noTok.SendHello(NetProto.RoleFile, sessionKey: key, password: Pw, token: null);
                var bye = noTok.WaitFor(NetProto.Bye);
                Assert.IsNotNull(bye, "沒帶 token 的 file 連線應該收到 bye");
                Assert.AreEqual(NetProto.ErrBadToken, NetJson.Str(bye, "reason"),
                    "reason 要是具名的 code —— client 靠它翻成人看得懂的提示");
                Assert.IsNull(noTok.WaitFor(NetProto.Welcome, 300), "不該同時認親成功");
            }
        }

        [Test]
        public void A_File_Connection_Without_The_Password_Is_Refused()
        {
            Plain control;
            string key = OpenControl(out control);
            using (control)
            using (var noPw = new Plain(_hub.ActualPort))
            {
                noPw.SendHello(NetProto.RoleFile, sessionKey: key, password: null, token: Tok);
                var bye = noPw.WaitFor(NetProto.Bye);
                Assert.IsNotNull(bye, "沒帶密碼的 file 連線應該收到 bye");
                Assert.AreEqual(NetProto.ErrBadPassword, NetJson.Str(bye, "reason"));
            }
        }

        [Test]
        public void A_File_Connection_With_Both_Gets_In()
        {
            // 反方向:守門不能把正常的傳檔連線也一起擋掉(那會讓缺歌永遠補不齊)。
            Plain control;
            string key = OpenControl(out control);
            using (control)
            using (var ok = new Plain(_hub.ActualPort))
            {
                ok.SendHello(NetProto.RoleFile, sessionKey: key, password: Pw, token: Tok);
                Assert.IsNotNull(ok.WaitFor(NetProto.Welcome), "帶了密碼與 token 的 file 連線要認親成功");
            }
        }

        private sealed class Plain : IDisposable
        {
            private readonly TcpClient _tcp;
            private readonly NetworkStream _s;
            private int _rq;

            public Plain(int port)
            {
                _tcp = new TcpClient { NoDelay = true };
                _tcp.Connect("127.0.0.1", port);
                _s = _tcp.GetStream();
            }

            public void SendHello(string role, string sessionKey = null, string password = null,
                                  string token = null, string playerId = null, string name = null)
            {
                var h = JObj.New()
                    .Str(NetProto.FieldType, NetProto.Hello)
                    .Int(NetProto.FieldRequest, ++_rq)
                    .Int("proto", NetProto.Version)
                    .Str("role", role);
                if (playerId != null) h.Str("playerId", playerId);
                if (name != null) h.Str("name", name);
                if (sessionKey != null) h.Str("sessionKey", sessionKey);
                if (password != null) h.Str("password", password);
                if (token != null) h.Str("authToken", token);
                NetFrame.Write(_s, NetLimits.FrameKindJson, h.Utf8());
                _s.Flush();
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
                    try { st = NetFrame.TryRead(_s, out kind, out payload); }
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

            public void Dispose() { try { _tcp.Close(); } catch { } }
        }
    }
}
