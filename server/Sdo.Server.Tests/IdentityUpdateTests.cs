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
    /// 握手之後才變的身分(<c>setIdentity</c>)。
    ///
    /// 為什麼需要這條訊息:握手發生在**開機時**,而**選性別 == 選帳號** —— 女角與男角是兩個
    /// profile,各有自己的名字。只補送 <c>setLook</c> 的話,別人看到的是「新的男角模型」配
    /// 「舊的女角名字」,而且**永遠**不會變(座位名字是進房那一刻從連線上抄過去的)。
    ///
    /// 這裡守的是那條路徑的兩端:去重規則(client 端不白送)與 token 綁定(server 端不被冒用)。
    /// </summary>
    public class IdentityUpdateTests
    {
        // ==================== 去重規則(client 端用) ====================

        [Test]
        public void SameAs_Treats_Null_And_Empty_Strings_As_Equal()
        {
            // 每送一次 server 就 rev++ 並向全房廣播一份完整快照 —— 沒變就不能送。
            // 「沒有家族」在不同來源會是 null 或 ""(profile 沒這個欄位 vs 欄位留空),
            // 那兩者代表同一件事,不該被當成「身分變了」。
            var a = new NetPlayerIdentity { Name = "小明", PlayerId = "00000001", Guild = null, Level = 3 };
            var b = new NetPlayerIdentity { Name = "小明", PlayerId = "00000001", Guild = "", Level = 3 };
            Assert.IsTrue(a.SameAs(b));
            Assert.IsTrue(b.SameAs(a));
        }

        [Test]
        public void SameAs_Sees_Every_Field_Change()
        {
            var baseId = new NetPlayerIdentity { Name = "小明", PlayerId = "00000000", Guild = "家族", Level = 3 };

            Assert.IsFalse(baseId.SameAs(new NetPlayerIdentity { Name = "小華", PlayerId = "00000000", Guild = "家族", Level = 3 }), "換名字");
            Assert.IsFalse(baseId.SameAs(new NetPlayerIdentity { Name = "小明", PlayerId = "00000001", Guild = "家族", Level = 3 }), "換帳號");
            Assert.IsFalse(baseId.SameAs(new NetPlayerIdentity { Name = "小明", PlayerId = "00000000", Guild = "別族", Level = 3 }), "換家族");
            Assert.IsFalse(baseId.SameAs(new NetPlayerIdentity { Name = "小明", PlayerId = "00000000", Guild = "家族", Level = 4 }), "升級");
            Assert.IsFalse(baseId.SameAs(null), "沒送過就是不一樣(第一次一定要送)");
        }

        [Test]
        public void SameAs_Is_Case_Sensitive()
        {
            // 名字比對用 Ordinal:「Bob」與「bob」是兩個不同的名字,別人畫面上看得出來差別。
            var a = new NetPlayerIdentity { Name = "Bob" };
            Assert.IsFalse(a.SameAs(new NetPlayerIdentity { Name = "bob" }));
        }

        // ==================== token 綁定(server 端擋冒用) ====================

        private Hub _hub;
        private Task _hubTask;
        private string _dataDir;

        private const string BoundToken = "bound-token-0123456789";
        private const string FreeToken = "free-token-0123456789";
        private const string BoundName = "官方認證的名字";

        /// <summary>開一台啟用 token 認證的 server:一個 token 綁了名字,另一個沒綁。</summary>
        private void StartServerWithTokens()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "sdo_ident_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dataDir);

            string tokensFile = Path.Combine(_dataDir, "tokens.txt");
            File.WriteAllText(tokensFile,
                BoundToken + " = 00000001, " + BoundName + "\n" +
                FreeToken + "\n");

            var opts = new ServerOptions
            {
                Port = 0,
                Bind = "127.0.0.1",
                DataDir = _dataDir,
                Password = "",
                CodeSeed = 991,
                TokensFile = tokensFile,
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

        [Test]
        public void A_Token_Bound_Name_Cannot_Be_Changed_By_SetIdentity()
        {
            // 🔴 這條訊息不能變成 AuthTokens 的後門:hello 擋下的冒用身分,不可以改成握手後
            // 再送一次就成立。等級照樣更新 —— 用它證明那筆 setIdentity 真的被處理過了
            // (不然這個測試在「server 根本沒收到訊息」的情況下也會過)。
            StartServerWithTokens();
            using (var c = new IdentClient(_hub.ActualPort))
            {
                c.Hello(BoundToken, "我自稱是別人");
                Assert.AreEqual(BoundName, c.CreateRoomAndReadName(), "握手時就該用 token 綁的名字");

                c.Send(JObj.New()
                    .Str(NetProto.FieldType, NetProto.SetIdentity)
                    .Str("name", "冒用者")
                    .Str("playerId", "99999999")
                    .Int("level", 42));

                var snap = c.WaitForState(s => s.Seats[0].Level == 42, "level 更新到了");
                Assert.AreEqual(BoundName, snap.Seats[0].Name, "token 綁的名字改不動");
            }
        }

        [Test]
        public void An_Unbound_Token_Still_Lets_You_Rename()
        {
            // token 沒綁名字(只是「這個人可以連進來」)→ 行為維持 MVP:名字 client 說了算。
            // 這正是實際會用到的設定 —— 不然換角色進房的人永遠改不了名字。
            StartServerWithTokens();
            using (var c = new IdentClient(_hub.ActualPort))
            {
                c.Hello(FreeToken, "開機時的女角");
                Assert.AreEqual("開機時的女角", c.CreateRoomAndReadName());

                c.Send(JObj.New()
                    .Str(NetProto.FieldType, NetProto.SetIdentity)
                    .Str("name", "換過去的男角")
                    .Str("playerId", "00000001")
                    .Int("level", 11));

                var snap = c.WaitForState(s => s.Seats[0].Name == "換過去的男角", "名字換過來");
                Assert.AreEqual(11, snap.Seats[0].Level);
            }
        }

        /// <summary>握手 + 建房 + 讀快照的極簡 client(整合測試的 TestClient 是 private,不能共用)。</summary>
        private sealed class IdentClient : IDisposable
        {
            private readonly TcpClient _tcp;
            private readonly NetworkStream _stream;

            public IdentClient(int port)
            {
                _tcp = new TcpClient();
                _tcp.NoDelay = true;
                _tcp.Connect("127.0.0.1", port);
                _stream = _tcp.GetStream();
            }

            public void Hello(string token, string name)
            {
                Send(JObj.New()
                    .Str(NetProto.FieldType, NetProto.Hello)
                    .Int(NetProto.FieldRequest, 1)
                    .Int("proto", NetProto.Version)
                    .Str("role", NetProto.RoleControl)
                    .Str("authToken", token)
                    .Str("playerId", "00000000")
                    .Str("name", name)
                    .Int("level", 1));
                Assert.IsNotNull(WaitFor(NetProto.Welcome), "沒收到 welcome(token 應該是有效的)");
            }

            /// <summary>建房並回傳自己座位上的名字。</summary>
            public string CreateRoomAndReadName()
            {
                Send(JObj.New()
                    .Str(NetProto.FieldType, NetProto.CreateRoom)
                    .Int(NetProto.FieldRequest, 10)
                    .Str("name", "測試房"));
                Assert.AreEqual(NetProto.JoinOk, NetJson.Str(WaitFor(NetProto.JoinResult), "result"));
                return WaitForState(s => s.SeatedCount == 1, "建房後的快照").Seats[0].Name;
            }

            public NetRoomSnapshot WaitForState(Func<NetRoomSnapshot, bool> until, string what, int timeoutMs = 3000)
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    int remaining = (int)Math.Max(1, timeoutMs - sw.ElapsedMilliseconds);
                    var node = WaitFor(NetProto.RoomState, remaining);
                    if (node == null) break;
                    var snap = NetRoomSnapshot.Decode(node);
                    if (until(snap)) return snap;
                }
                Assert.Fail("等不到「" + what + "」的 roomState");
                return null;
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
