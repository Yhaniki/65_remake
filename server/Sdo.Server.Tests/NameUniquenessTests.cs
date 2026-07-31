using System;
using System.Collections.Generic;
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
    /// 「同一個名字同時只能有一個人在線」。**真的開 socket** 走完整握手。
    ///
    /// 為什麼這條規則重要:名字是這款遊戲裡唯一認人的東西 —— 密語照名字找人
    /// (<c>Hub.ControlByName</c>)、房間裡的名字牌、大廳的線上名單都是它。
    /// 兩個「小明」同時在線的話,密語會進到其中一個而寄的人不知道是哪一個,
    /// 收的人也不知道為什麼有一半的話不見了。所以在門口就不讓它成立。
    ///
    /// 擋的是**後上線的那個**(先上線的完全不受影響)。反過來做的話,
    /// 被冒名等於送對方一把把你踢下線的鑰匙。
    /// </summary>
    public class NameUniquenessTests
    {
        private Hub _hub;
        private Task _hubTask;
        private string _dataDir;
        private readonly List<NameClient> _clients = new List<NameClient>();

        [SetUp]
        public void StartServer()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "sdo_name_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dataDir);

            var opts = new ServerOptions
            {
                Port = 0,
                Bind = "127.0.0.1",
                DataDir = _dataDir,
                CodeSeed = 4242,
                Password = "",
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
            for (int i = 0; i < _clients.Count; i++) _clients[i].Dispose();
            _clients.Clear();

            if (_hub != null) _hub.Stop();
            if (_hubTask != null) { try { _hubTask.Wait(3000); } catch { } }
            try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true); } catch { }
            _hub = null;
            _hubTask = null;
        }

        // ---- 握手 ----

        [Test]
        public void The_Second_Login_With_The_Same_Name_Is_Rejected()
        {
            var a = Login("小明");
            Assert.Greater(a.UserId, 0);

            var b = Hello("小明");
            var bye = b.WaitFor(NetProto.Bye);
            Assert.IsNotNull(bye, "同名的後來者應該收到 bye");
            Assert.AreEqual(NetProto.ErrNameTaken, NetJson.Str(bye, "reason"),
                "reason 要是具名的 code —— client 靠它決定彈「名字已被使用」而不是「連不上伺服器」");
            Assert.IsNull(b.WaitFor(NetProto.Welcome, 300), "被擋的人不該同時拿到 welcome");
        }

        [Test]
        public void The_Name_Check_Ignores_Case()
        {
            // 密語找人是不分大小寫的(ControlByName),所以擋的規則也必須不分 ——
            // 只要有一邊分、另一邊不分,「Alice」與「alice」就會同時在線而密語只進得去一個。
            Login("Alice");

            var b = Hello("ALICE");
            var bye = b.WaitFor(NetProto.Bye);
            Assert.IsNotNull(bye, "只有大小寫不同也算同名");
            Assert.AreEqual(NetProto.ErrNameTaken, NetJson.Str(bye, "reason"));
        }

        [Test]
        public void The_Name_Check_Ignores_Surrounding_Spaces()
        {
            // SanitizeName 會把頭尾空白去掉 —— 檢查要用清理**之後**的名字,
            // 否則打一個空白就繞過去了(而別人看到的名字牌完全一樣)。
            Login("小明");

            var b = Hello("  小明  ");
            var bye = b.WaitFor(NetProto.Bye);
            Assert.IsNotNull(bye, "頭尾空白清掉之後同名 → 一樣要擋");
            Assert.AreEqual(NetProto.ErrNameTaken, NetJson.Str(bye, "reason"));
        }

        [Test]
        public void Different_Names_Both_Get_In()
        {
            // 這條是防止規則寫過頭:擋的是同名,不是「第二個人」。
            var a = Login("小明");
            var b = Login("小華");
            Assert.AreNotEqual(a.UserId, b.UserId);
        }

        [Test]
        public void The_One_Already_Online_Is_Not_Disturbed()
        {
            // 「擋後來的」的另一半:先在線的那個要完全沒事 —— 連線還在、還能繼續操作。
            // 反過來實作(踢掉舊的)的話,被冒名就等於送對方一把把你踢下線的鑰匙。
            var a = Login("小明");

            var b = Hello("小明");
            Assert.IsNotNull(b.WaitFor(NetProto.Bye));

            a.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.CreateRoom)
                .Int(NetProto.FieldRequest, 50)
                .Str("name", "我的舞蹈室"));
            var res = a.WaitFor(NetProto.JoinResult);
            Assert.IsNotNull(res, "先上線的那個應該完全不受影響");
            Assert.AreEqual(NetProto.JoinOk, NetJson.Str(res, "result"));
        }

        [Test]
        public void The_Name_Is_Free_Again_Once_The_Owner_Goes_Offline()
        {
            // 名字是**佔用**不是註冊 —— 人走了就要放回去,否則重開遊戲的人再也用不回自己的名字。
            var a = Login("小明");
            a.Dispose();

            // server 端的移除是在 reader 讀到 EOF 之後才排進 actor loop 的,所以要等一下下。
            NameClient b = null;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000)
            {
                b = Hello("小明");
                if (b.WaitFor(NetProto.Welcome, 300) != null) return;   // 拿回名字了
                Thread.Sleep(50);
            }
            Assert.Fail("原本的人離線之後,那個名字應該可以再被用");
        }

        // ---- 握手之後的改名 ----

        [Test]
        public void SetIdentity_Cannot_Take_A_Name_That_Is_Online()
        {
            // 少了這一段,握手的檢查等於白做:用別的名字進來、進來後再改成對方的名字,
            // 結果一樣是兩個同名的人同時在線。
            var a = Login("小明");
            int code = CreateRoom(a, "舞蹈室");

            var b = Login("小華");
            Join(b, code);
            WaitForState(a, s => s.SeatedCount == 2, "小華加入");

            // 等級一起改:名字被擋下時這筆更新的**其他欄位仍然要生效**,
            // 而且它讓我們確定這則訊息真的被處理過了(不是還沒送到)。
            b.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetIdentity)
                .Str("name", "小明")
                .Int("level", 42));

            var st = WaitForState(a, s => s.Seats[1].Level == 42, "小華的等級更新");
            Assert.AreEqual("小華", st.Seats[1].Name, "撞名的改名要被擋下,保留原本的名字");
            Assert.AreEqual("小明", st.Seats[0].Name, "被冒名的那個人不受影響");
        }

        [Test]
        public void SetIdentity_Can_Still_Rename_To_A_Free_Name()
        {
            // 另一半:沒撞到人的改名照常運作(換性別 == 換帳號,名字本來就會變)。
            var a = Login("小明");
            int code = CreateRoom(a, "舞蹈室");

            var b = Login("小華");
            Join(b, code);
            WaitForState(a, s => s.SeatedCount == 2, "小華加入");

            b.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetIdentity)
                .Str("name", "小華的男角")
                .Int("level", 5));

            var st = WaitForState(a, s => s.Seats[1].Name == "小華的男角", "小華改名");
            Assert.AreEqual(5, st.Seats[1].Level);
        }

        [Test]
        public void SetIdentity_With_Your_Own_Name_Is_Not_Blocked()
        {
            // 🔴 最容易寫錯的一條:檢查時找到的是**自己**。換性別的流程每次都會重送一次
            // 同樣的名字(PublishIdentity),把自己當成「已被佔用」的話,那些人的名字會
            // 永遠停在握手那份,而症狀與這個功能沒有任何關係 —— 極難聯想。
            var a = Login("小明");
            int code = CreateRoom(a, "舞蹈室");

            var b = Login("小華");
            Join(b, code);
            WaitForState(a, s => s.SeatedCount == 2, "小華加入");

            b.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetIdentity)
                .Str("name", "小華")        // 同一個名字再報一次
                .Str("guild", "熱舞家族")
                .Int("level", 9));

            var st = WaitForState(a, s => s.Seats[1].Level == 9, "小華重報身分");
            Assert.AreEqual("小華", st.Seats[1].Name, "重報自己的名字不該被當成撞名");
            Assert.AreEqual("熱舞家族", st.Seats[1].Guild);
        }

        // ---- helper ----

        /// <summary>連上去並送 hello(不等回應 —— 被擋的情況要看 bye)。</summary>
        private NameClient Hello(string name)
        {
            var c = new NameClient(_hub.ActualPort);
            _clients.Add(c);
            c.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.Hello)
                .Int(NetProto.FieldRequest, 1)
                .Int("proto", NetProto.Version)
                .Str("role", NetProto.RoleControl)
                .Str("playerId", "00000000")
                .Str("name", name)
                .Int("level", 7));
            return c;
        }

        /// <summary>連上去並完成握手(拿到 userId)。</summary>
        private NameClient Login(string name)
        {
            var c = Hello(name);
            var welcome = c.WaitFor(NetProto.Welcome);
            Assert.IsNotNull(welcome, name + " 沒收到 welcome");
            c.UserId = NetJson.Int(welcome, "userId");
            Assert.Greater(c.UserId, 0);
            return c;
        }

        private static int CreateRoom(NameClient c, string name)
        {
            c.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.CreateRoom)
                .Int(NetProto.FieldRequest, 1000)
                .Str("name", name));
            var res = c.WaitFor(NetProto.JoinResult);
            Assert.IsNotNull(res, "建房沒有回應");
            Assert.AreEqual(NetProto.JoinOk, NetJson.Str(res, "result"));
            int code = NetJson.Int(res, "code");
            c.WaitFor(NetProto.RoomState);   // 吃掉建房後的那份
            return code;
        }

        private static void Join(NameClient c, int code)
        {
            c.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.JoinRoom)
                .Int(NetProto.FieldRequest, 1001)
                .Int("code", code));
            var res = c.WaitFor(NetProto.JoinResult);
            Assert.IsNotNull(res, "加入沒有回應");
            Assert.AreEqual(NetProto.JoinOk, NetJson.Str(res, "result"));
        }

        private static NetRoomSnapshot WaitForState(NameClient c, Func<NetRoomSnapshot, bool> until,
                                                    string what, int timeoutMs = 3000)
        {
            var sw = Stopwatch.StartNew();
            NetRoomSnapshot last = null;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                int remaining = (int)Math.Max(1, timeoutMs - sw.ElapsedMilliseconds);
                var node = c.WaitFor(NetProto.RoomState, remaining);
                if (node == null) break;
                last = NetRoomSnapshot.Decode(node);
                if (until(last)) return last;
            }
            Assert.Fail("等不到「" + what + "」的 roomState" +
                        (last != null ? "(最後看到 rev=" + last.Rev + " 座位數=" + last.SeatedCount + ")" : "(完全沒收到)"));
            return null;
        }

        /// <summary>測試用的極簡 client。收到的訊息先進 inbox(協定是非同步的,順序不保證)。</summary>
        private sealed class NameClient : IDisposable
        {
            private readonly TcpClient _tcp;
            private readonly NetworkStream _stream;
            private readonly List<Envelope> _inbox = new List<Envelope>();

            public int UserId;

            public NameClient(int port)
            {
                _tcp = new TcpClient();
                _tcp.NoDelay = true;
                _tcp.Connect("127.0.0.1", port);
                _stream = _tcp.GetStream();
            }

            public void Send(JObj msg)
            {
                NetFrame.Write(_stream, NetLimits.FrameKindJson, msg.Utf8());
                _stream.Flush();
            }

            public object WaitFor(string type, int timeoutMs = 3000)
            {
                for (int i = 0; i < _inbox.Count; i++)
                {
                    if (_inbox[i].Type != type) continue;
                    var node = _inbox[i].Node;
                    _inbox.RemoveAt(i);
                    return node;
                }

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    _tcp.ReceiveTimeout = (int)Math.Max(1, timeoutMs - sw.ElapsedMilliseconds);

                    byte kind;
                    byte[] payload;
                    FrameStatus st;
                    try { st = NetFrame.TryRead(_stream, out kind, out payload); }
                    catch (IOException) { return null; }
                    catch (ObjectDisposedException) { return null; }

                    if (st != FrameStatus.Ok) return null;
                    if (kind != NetLimits.FrameKindJson) continue;

                    object node;
                    string got;
                    if (!NetJson.TryParseMessage(payload, 0, payload.Length, out node, out got)) continue;

                    if (got == type) return node;
                    _inbox.Add(new Envelope(got, node));
                }
                return null;
            }

            public void Dispose() { try { _tcp.Close(); } catch { } }

            private struct Envelope
            {
                public readonly string Type;
                public readonly object Node;
                public Envelope(string type, object node) { Type = type; Node = node; }
            }
        }
    }
}
