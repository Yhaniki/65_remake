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
    /// 進站密碼。**真的開 socket** 走完整握手。
    ///
    /// 密碼在 MVP 階段只是一道門檻(不是認證 —— playerId 由 client 自稱、連線沒加密),
    /// 但那道門檻要真的擋得住:密碼不對就不該拿到 userId、不該能建房。
    /// </summary>
    public class ServerPasswordTests
    {
        private const string ServerPw = "abab123";

        private Hub _hub;
        private Task _hubTask;
        private string _dataDir;

        private void StartServer(string password)
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "sdo_pw_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dataDir);

            var opts = new ServerOptions
            {
                Port = 0,
                Bind = "127.0.0.1",
                DataDir = _dataDir,
                Password = password,
                CodeSeed = 777,
            };
            string err;
            Assert.IsTrue(opts.Validate(out err), err);

            _hub = new Hub(opts);
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
            _hub = null;
            _hubTask = null;
        }

        // ---- 有設密碼的 server ----

        [Test]
        public void Correct_Password_Gets_A_Welcome()
        {
            StartServer(ServerPw);
            using (var c = new PwClient(_hub.ActualPort))
            {
                c.SendHello(ServerPw);
                var welcome = c.WaitFor(NetProto.Welcome);
                Assert.IsNotNull(welcome, "密碼正確應該收到 welcome");
                Assert.Greater(NetJson.Int(welcome, "userId"), 0);
            }
        }

        [Test]
        public void Wrong_Password_Is_Rejected_With_A_Named_Reason()
        {
            StartServer(ServerPw);
            using (var c = new PwClient(_hub.ActualPort))
            {
                c.SendHello("wrong-one");

                var bye = c.WaitFor(NetProto.Bye);
                Assert.IsNotNull(bye, "密碼錯誤應該收到 bye");
                Assert.AreEqual(NetProto.ErrBadPassword, NetJson.Str(bye, "reason"),
                    "reason 要是具名的 code —— client 靠它翻成「密碼不符」給玩家看");

                Assert.IsNull(c.WaitFor(NetProto.Welcome, 300), "不該同時收到 welcome");
            }
        }

        [Test]
        public void Missing_Password_Is_Rejected()
        {
            // 最常見的誤用:玩家的 config.ini 把 serverPassword 留空。
            StartServer(ServerPw);
            using (var c = new PwClient(_hub.ActualPort))
            {
                c.SendHello(null);
                var bye = c.WaitFor(NetProto.Bye);
                Assert.IsNotNull(bye);
                Assert.AreEqual(NetProto.ErrBadPassword, NetJson.Str(bye, "reason"));
            }
        }

        [Test]
        public void Password_Is_Case_Sensitive()
        {
            StartServer(ServerPw);
            using (var c = new PwClient(_hub.ActualPort))
            {
                c.SendHello(ServerPw.ToUpperInvariant());
                Assert.IsNotNull(c.WaitFor(NetProto.Bye), "密碼比對要區分大小寫");
            }
        }

        [Test]
        public void Rejected_Client_Cannot_Do_Anything()
        {
            // 光是「不給 welcome」不夠 —— 連線要真的被關掉,否則它還能繼續送訊息。
            StartServer(ServerPw);
            using (var c = new PwClient(_hub.ActualPort))
            {
                c.SendHello("nope");
                Assert.IsNotNull(c.WaitFor(NetProto.Bye));

                // 連線已關 → 後續請求收不到任何回應。
                try { c.Send(JObj.New().Str(NetProto.FieldType, NetProto.CreateRoom).Int(NetProto.FieldRequest, 1).Str("name", "壞人的房")); }
                catch { /* 寫入失敗也算通過 —— socket 已經關了 */ }

                Assert.IsNull(c.WaitFor(NetProto.JoinResult, 400), "被拒的連線不該能建房");
                Assert.AreEqual(0, _hub.RoomCount, "server 上不該出現房間");
            }
        }

        // ---- 沒設密碼的 server ----

        [Test]
        public void Empty_Server_Password_Lets_Anyone_In()
        {
            // --password "" = 明確關掉密碼檢查(LAN 內自己玩)。
            StartServer("");
            using (var c = new PwClient(_hub.ActualPort))
            {
                c.SendHello(null);
                Assert.IsNotNull(c.WaitFor(NetProto.Welcome), "沒設密碼時空密碼應該放行");
            }
        }

        [Test]
        public void Empty_Server_Password_Ignores_A_Supplied_One()
        {
            // client 帶了密碼但 server 沒設 —— 不該因此被拒(那會讓「server 改成不設密碼」
            // 之後所有舊 client 都連不上)。
            StartServer("");
            using (var c = new PwClient(_hub.ActualPort))
            {
                c.SendHello("whatever");
                Assert.IsNotNull(c.WaitFor(NetProto.Welcome));
            }
        }

        // ---- 參數解析 ----

        [Test]
        public void Password_Option_Overrides_The_Default()
        {
            ServerOptions o;
            string err;
            bool help;
            Assert.IsTrue(ServerOptions.TryParse(new[] { "--password", "custom" }, out o, out err, out help), err);
            Assert.AreEqual("custom", o.Password);
        }

        [Test]
        public void Default_Password_Is_Used_When_Not_Given()
        {
            ServerOptions o;
            string err;
            bool help;
            Assert.IsTrue(ServerOptions.TryParse(new string[0], out o, out err, out help), err);
            Assert.AreEqual(ServerOptions.DefaultPassword, o.Password);
        }

        [Test]
        public void Explicit_Empty_Password_Disables_The_Check()
        {
            ServerOptions o;
            string err;
            bool help;
            Assert.IsTrue(ServerOptions.TryParse(new[] { "--password", "" }, out o, out err, out help), err);
            Assert.AreEqual("", o.Password);
        }

        [Test]
        public void Password_Is_Trimmed()
        {
            // 從 systemd unit 或 shell 傳進來很容易帶到空白。
            ServerOptions o;
            string err;
            bool help;
            Assert.IsTrue(ServerOptions.TryParse(new[] { "--password", "  pw  " }, out o, out err, out help), err);
            Assert.AreEqual("pw", o.Password);
        }

        /// <summary>握手用的極簡 client(只需要送 hello 與等一個回應)。</summary>
        private sealed class PwClient : IDisposable
        {
            private readonly TcpClient _tcp;
            private readonly NetworkStream _stream;

            public PwClient(int port)
            {
                _tcp = new TcpClient();
                _tcp.NoDelay = true;
                _tcp.Connect("127.0.0.1", port);
                _stream = _tcp.GetStream();
            }

            public void SendHello(string password)
            {
                var hello = JObj.New()
                    .Str(NetProto.FieldType, NetProto.Hello)
                    .Int(NetProto.FieldRequest, 1)
                    .Int("proto", NetProto.Version)
                    .Str("role", NetProto.RoleControl)
                    .Str("playerId", "test")
                    .Str("name", "測試者")
                    .Int("level", 1);
                if (password != null) hello.Str("password", password);
                Send(hello);
            }

            public void Send(JObj msg)
            {
                NetFrame.Write(_stream, NetLimits.FrameKindJson, msg.Utf8());
                _stream.Flush();
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
                    try { st = NetFrame.TryRead(_stream, out kind, out payload); }
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
