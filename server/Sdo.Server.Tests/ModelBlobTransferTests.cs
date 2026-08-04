using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Sdo.Net;
using Sdo.Osu;
using Sdo.Server;
using Sdo.Server.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// MMD 模型走同一條傳檔管線的端到端測試 —— 真的開 socket、真的傳位元組、真的落地。
    ///
    /// 模型與歌共用內容尋址倉庫,但 <b>kind 決定 server 收檔時套哪一張白名單</b>,而且上傳資格的
    /// 判準完全不同:歌是「房間現在選的那首」,模型是「你身上穿的那一個」(＝你自己 setLook 宣告的
    /// <c>MmdPack</c>)。這兩件事是「不能把 server 當免費檔案空間用」的唯一防線,所以每一條各一個測試。
    ///
    /// 🔴 這裡的每一條都假設 client 是**改過的**。合法 client 永遠不會送出這些訊息。
    /// </summary>
    public class ModelBlobTransferTests
    {
        private Hub _hub;
        private Task _hubTask;
        private string _dataDir;
        private readonly List<Tc> _clients = new List<Tc>();

        // 一份假模型:一個 .pmx 加一張貼圖。內容小,走的路徑與真的一模一樣。
        private static readonly byte[] PmxBytes = Bytes(64 * 1024, 13);
        private static readonly byte[] TexBytes = Bytes(150 * 1024, 29);

        [SetUp]
        public void StartServer()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "sdo_model_srv_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dataDir);

            var opts = new ServerOptions { Port = 0, Bind = "127.0.0.1", DataDir = _dataDir, CodeSeed = 707, Password = "" };
            string err;
            Assert.IsTrue(opts.Validate(out err), err);

            _hub = new Hub(opts);
            _hubTask = Task.Factory.StartNew(_hub.Run, TaskCreationOptions.LongRunning);
            var sw = Stopwatch.StartNew();
            while (!_hub.IsListening && sw.ElapsedMilliseconds < 5000) Thread.Sleep(5);
            Assert.IsTrue(_hub.IsListening);
        }

        [TearDown]
        public void StopServer()
        {
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
            if (_hub != null) _hub.Stop();
            if (_hubTask != null) _hubTask.Wait(5000);
            try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true); } catch { }
        }

        // ================= 正常路徑 =================

        [Test]
        public void Wearer_Uploads_Their_Model_And_Another_Player_Downloads_It()
        {
            var wearer = Control("穿模型的");
            CreateRoom(wearer);
            var files = Manifest();
            string packId = ModelPackId.Compute(files);
            SetLookWithModel(wearer, packId, "TestMiku");

            var up = FileConn(wearer);
            var need = UploadBegin(up, packId, files, NetProto.BlobKindModel);
            Assert.AreEqual(files.Count, need.Count, "第一次上傳應該每個檔都缺");
            SendPayloads(up, files, need);
            FinishUpload(up);

            // 另一個人:先問有沒有,再下載。這就是「看到別人穿 MMD → 去把模型拉下來」那條路。
            var viewer = Control("看的人");
            var probe = FileConn(viewer);
            viewer.Send(JObj.New().Str(NetProto.FieldType, NetProto.BlobQuery).Int(NetProto.FieldRequest, 20).Str("packId", packId));
            var info = viewer.WaitFor(NetProto.BlobInfo);
            Assert.IsNotNull(info);
            Assert.IsTrue(NetJson.Bool(info, "have"), "server 應該已經有這個模型了");

            probe.Send(JObj.New().Str(NetProto.FieldType, NetProto.BlobDownloadBegin).Int(NetProto.FieldRequest, 21).Str("packId", packId));
            var man = probe.WaitFor(NetProto.BlobManifest);
            Assert.IsNotNull(man, "沒收到 manifest:" + LastError(probe));
            var got = ReadManifest(man);
            Assert.AreEqual(files.Count, got.Count);
            var bytes = probe.ReceiveAllChunks(NetProto.BlobDownloadDone, 8000);
            Assert.IsNotNull(bytes, "下載沒收完");
            Assert.AreEqual(PmxBytes.Length + TexBytes.Length, bytes.Length, "收到的位元組數對不上");
        }

        [Test]
        public void Uploading_The_Same_Model_Twice_Needs_Zero_Files()
        {
            // 每次進房間都會上傳一次自己的模型 —— 第二次開始必須是零上傳,否則每進一次房就重傳 10 MB。
            var a = Control("A");
            CreateRoom(a);
            var files = Manifest();
            string packId = ModelPackId.Compute(files);
            SetLookWithModel(a, packId, "TestMiku");

            var up1 = FileConn(a);
            SendPayloads(up1, files, UploadBegin(up1, packId, files, NetProto.BlobKindModel));
            FinishUpload(up1);

            var b = Control("B");
            CreateRoom(b);
            SetLookWithModel(b, packId, "TestMiku");
            var up2 = FileConn(b);
            var need2 = UploadBegin(up2, packId, files, NetProto.BlobKindModel);
            Assert.AreEqual(0, need2.Count, "同一份模型第二次上傳應該一個檔都不用傳");
            FinishUpload(up2);
        }

        // ================= 上傳資格 =================

        [Test]
        public void Uploading_A_Model_You_Are_Not_Wearing_Is_Refused()
        {
            // 這是模型版的「不能把 server 當免費檔案空間用」。少了它,任何連上來的人都能上傳任意
            // 一包東西 —— 而且因為模型的白名單收 .txt/.png,那是個很好用的儲存空間。
            var c = Control("蹭空間的");
            CreateRoom(c);
            var files = Manifest();
            string packId = ModelPackId.Compute(files);
            // 故意不 setLook(身上沒穿任何模型)
            var up = FileConn(c);
            up.Send(UploadBeginMsg(packId, files, NetProto.BlobKindModel));
            Assert.IsNull(up.WaitFor(NetProto.BlobUploadAccept, 500), "沒穿模型卻被允許上傳");
            StringAssert.Contains(NetProto.ErrBadState, LastError(up));
        }

        [Test]
        public void Uploading_A_Different_Model_Than_The_One_Worn_Is_Refused()
        {
            var c = Control("換包的");
            CreateRoom(c);
            var files = Manifest();
            string realPack = ModelPackId.Compute(files);
            string otherPack = ModelPackId.Compute(new List<PackFileEntry> { new PackFileEntry("other.pmx", 10, new string('f', 64)) });
            SetLookWithModel(c, otherPack, "別的");

            var up = FileConn(c);
            up.Send(UploadBeginMsg(realPack, files, NetProto.BlobKindModel));
            Assert.IsNull(up.WaitFor(NetProto.BlobUploadAccept, 500), "宣稱穿 A 卻傳 B 竟然過了");
            StringAssert.Contains(NetProto.ErrBadState, LastError(up));
        }

        // ================= 白名單 / kind =================

        [Test]
        public void A_Model_Pack_Carrying_An_Executable_Is_Refused()
        {
            var c = Control("挾帶的");
            CreateRoom(c);
            var files = Manifest();
            files.Add(new PackFileEntry("tool.exe", 1000, new string('c', 64)));
            string packId = ModelPackId.Compute(files);
            SetLookWithModel(c, packId, "壞包");

            var up = FileConn(c);
            up.Send(UploadBeginMsg(packId, files, NetProto.BlobKindModel));
            Assert.IsNull(up.WaitFor(NetProto.BlobUploadAccept, 500), "模型包挾帶執行檔竟然過了");
            StringAssert.Contains(NetProto.BlobErrBadPath, LastError(up));
        }

        [Test]
        public void A_Model_Pack_Without_A_Pmx_Is_Refused()
        {
            var c = Control("只有貼圖");
            CreateRoom(c);
            var files = new List<PackFileEntry> { new PackFileEntry("textures/t.png", TexBytes.Length, Sha(TexBytes)) };
            string packId = ModelPackId.Compute(files);
            SetLookWithModel(c, packId, "沒本體");

            var up = FileConn(c);
            up.Send(UploadBeginMsg(packId, files, NetProto.BlobKindModel));
            Assert.IsNull(up.WaitFor(NetProto.BlobUploadAccept, 500), "沒有 .pmx 的包竟然過了");
            StringAssert.Contains(NetProto.BlobErrBadPath, LastError(up));
        }

        [Test]
        public void A_Model_Pack_Sent_As_A_Song_Is_Refused_By_The_Song_Whitelist()
        {
            // 兩張白名單分開才有意義:.pmx 不在歌曲白名單裡,所以拿 kind=song 上傳模型會被逐檔擋掉。
            var c = Control("走錯路的");
            CreateRoom(c);
            var files = Manifest();
            string packId = SongPackId.Compute(files);
            SetExternalSong(c, packId);   // 連歌都選好了,擋下它的只剩白名單

            var up = FileConn(c);
            up.Send(UploadBeginMsg(packId, files, NetProto.BlobKindSong));
            Assert.IsNull(up.WaitFor(NetProto.BlobUploadAccept, 500), ".pmx 竟然過了歌曲白名單");
            StringAssert.Contains(NetProto.BlobErrBadPath, LastError(up));
        }

        [Test]
        public void The_Same_PackId_Cannot_Change_Kind()
        {
            // 少了這條,可以先用模型的白名單把內容放進倉庫,再宣稱它是一首歌(或反過來)——
            // 兩張白名單分開的意義就沒了。
            var a = Control("先傳模型");
            CreateRoom(a);
            var files = Manifest();
            string packId = ModelPackId.Compute(files);
            SetLookWithModel(a, packId, "TestMiku");
            var up = FileConn(a);
            SendPayloads(up, files, UploadBegin(up, packId, files, NetProto.BlobKindModel));
            FinishUpload(up);

            var b = Control("再宣稱它是歌");
            CreateRoom(b);
            SetExternalSong(b, packId);
            var up2 = FileConn(b);
            up2.Send(UploadBeginMsg(packId, files, NetProto.BlobKindSong));
            Assert.IsNull(up2.WaitFor(NetProto.BlobUploadAccept, 500), "同一個 packId 換 kind 竟然過了");
            StringAssert.Contains(NetProto.BlobErrKindMismatch, LastError(up2));
        }

        [Test]
        public void A_Claimed_PackId_That_Does_Not_Match_The_Content_Is_Refused()
        {
            // 模型的 packId 是「每個檔都 hash」算出來的,server 自己重算一次。對不上 = 上傳者宣稱
            // 的身分與內容不符 —— 放行的話別人會拿到一份跟他看到的 id 不一樣的模型。
            var c = Control("冒名的");
            CreateRoom(c);
            var files = Manifest();
            string lie = ModelPackId.Compute(new List<PackFileEntry> { new PackFileEntry("x.pmx", 1, new string('e', 64)) });
            SetLookWithModel(c, lie, "冒名");

            var up = FileConn(c);
            up.Send(UploadBeginMsg(lie, files, NetProto.BlobKindModel));
            Assert.IsNull(up.WaitFor(NetProto.BlobUploadAccept, 500), "packId 對不上內容竟然過了");
            StringAssert.Contains(NetProto.BlobErrHashMismatch, LastError(up));
        }

        // ================= helper =================

        /// <summary>送出 blobUploadDone 並等 server 確認。**server 不會自己判斷「送完了」** ——
        /// 漏了這一步的症狀是上傳永遠停在最後一塊。</summary>
        private static void FinishUpload(Tc file)
        {
            file.Send(JObj.New().Str(NetProto.FieldType, NetProto.BlobUploadDone).Int(NetProto.FieldRequest, 40));
            var done = file.WaitFor(NetProto.BlobUploadDone, 8000);
            Assert.IsNotNull(done, "上傳沒完成:" + LastError(file));
            Assert.IsTrue(NetJson.Bool(done, "ok"));
        }

        private static byte[] Bytes(int n, int step)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = (byte)(i * step + 3);
            return b;
        }

        private static List<PackFileEntry> Manifest() => new List<PackFileEntry>
        {
            new PackFileEntry("model.pmx", PmxBytes.Length, Sha(PmxBytes)),
            new PackFileEntry("textures/t.png", TexBytes.Length, Sha(TexBytes)),
        };

        private static byte[] PayloadFor(string rel) => rel == "model.pmx" ? PmxBytes : TexBytes;

        private static string Sha(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var h = sha.ComputeHash(data);
                var sb = new StringBuilder(64);
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static JObj UploadBeginMsg(string packId, List<PackFileEntry> files, string kind)
        {
            var arr = JArr.New();
            for (int i = 0; i < files.Count; i++)
                arr.Add(JObj.New().Str("path", files[i].RelPath).Long("len", files[i].Length).Str("sha256", files[i].Sha256));
            return JObj.New()
                .Str(NetProto.FieldType, NetProto.BlobUploadBegin)
                .Int(NetProto.FieldRequest, 10)
                .Str("packId", packId)
                .Str(NetProto.FieldBlobKind, kind)
                .Put("files", arr);
        }

        private List<int> UploadBegin(Tc file, string packId, List<PackFileEntry> files, string kind)
        {
            file.Send(UploadBeginMsg(packId, files, kind));
            var accept = file.WaitFor(NetProto.BlobUploadAccept);
            Assert.IsNotNull(accept, "沒收到 uploadAccept:" + LastError(file));

            var need = new List<int>();
            var arr = NetJson.Arr(accept, "need");
            for (int i = 0; i < arr.Count; i++) need.Add((int)Convert.ToInt64(arr[i]));
            return need;
        }

        private static void SendPayloads(Tc file, List<PackFileEntry> files, List<int> need)
        {
            for (int i = 0; i < need.Count; i++) file.SendChunks(PayloadFor(files[need[i]].RelPath));
        }

        private static List<PackFileEntry> ReadManifest(object node)
        {
            var list = new List<PackFileEntry>();
            var arr = NetJson.Arr(node, "files");
            for (int i = 0; i < arr.Count; i++)
                list.Add(new PackFileEntry(NetJson.Str(arr[i], "path"), NetJson.Long(arr[i], "len"), NetJson.Str(arr[i], "sha256")));
            return list;
        }

        private static string LastError(Tc c)
        {
            var e = c.WaitFor(NetProto.BlobError, 300);
            return e == null ? "(沒有 blobError)" : NetJson.Str(e, "code") + " " + NetJson.Str(e, "msg");
        }

        private Tc Control(string name)
        {
            var c = new Tc(_hub.ActualPort);
            _clients.Add(c);
            c.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.Hello).Int(NetProto.FieldRequest, 1)
                .Int("proto", NetProto.Version).Str("role", NetProto.RoleControl)
                .Str("playerId", "00000000").Str("name", name));
            var w = c.WaitFor(NetProto.Welcome);
            Assert.IsNotNull(w, name + " 沒收到 welcome");
            c.UserId = NetJson.Int(w, "userId");
            c.SessionKey = NetJson.Str(w, "sessionKey");
            return c;
        }

        private Tc FileConn(Tc owner)
        {
            var c = new Tc(_hub.ActualPort);
            _clients.Add(c);
            c.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.Hello).Int(NetProto.FieldRequest, 2)
                .Int("proto", NetProto.Version).Str("role", NetProto.RoleFile)
                .Str("sessionKey", owner.SessionKey));
            Assert.IsNotNull(c.WaitFor(NetProto.Welcome), "file 連線沒認親成功");
            return c;
        }

        private int CreateRoom(Tc c)
        {
            c.Send(JObj.New().Str(NetProto.FieldType, NetProto.CreateRoom).Int(NetProto.FieldRequest, 3).Str("name", "模型測試"));
            var st = c.WaitFor(NetProto.RoomState);
            Assert.IsNotNull(st, "建房失敗");
            return NetJson.Int(st, "code");
        }

        /// <summary>宣告「我身上穿的是這個模型」—— 上傳資格看的就是它。</summary>
        private static void SetLookWithModel(Tc c, string packId, string name)
        {
            c.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetLook)
                .Int(NetProto.FieldRequest, 6)
                .Put("look", JObj.New().Int("gender", 0).Int("bodyIndex", 1).Str("mmd", packId).Str("mmdName", name)));
            Assert.IsNotNull(c.WaitFor(NetProto.RoomState), "setLook 沒生效");
        }

        private static void SetExternalSong(Tc host, string packId)
        {
            host.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetSong).Int(NetProto.FieldRequest, 5)
                .Put("song", JObj.New()
                    .Bool("official", false).Str("packId", packId)
                    .Str("songKey", "audio").Str("chartRelPath", "song.osu").Str("title", "外部測試歌")));
            Assert.IsNotNull(host.WaitFor(NetProto.RoomState), "選歌沒生效");
        }

        /// <summary>會處理 chunk 的測試 client。**與 <see cref="BlobTransferTests"/> 用的是同一份** ——
        /// framing 手寫一遍就是在重新發明 NetFrame,而發明錯了的症狀是「握手沒反應」,
        /// 看起來十足像 server 的 bug。</summary>
        private sealed class Tc : IDisposable
        {
            private readonly TcpClient _tcp;
            private readonly NetworkStream _stream;
            private readonly List<KeyValuePair<string, object>> _inbox = new List<KeyValuePair<string, object>>();
            private readonly MemoryStream _chunks = new MemoryStream();

            public int UserId;
            public string SessionKey = "";

            public Tc(int port)
            {
                _tcp = new TcpClient { NoDelay = true };
                _tcp.Connect("127.0.0.1", port);
                _stream = _tcp.GetStream();
            }

            public void Send(JObj msg)
            {
                NetFrame.Write(_stream, NetLimits.FrameKindJson, msg.Utf8());
                _stream.Flush();
            }

            /// <summary>把一份位元組切成 64 KiB 送出去(與 client 端的做法一致)。</summary>
            public void SendChunks(byte[] data)
            {
                int off = 0;
                while (off < data.Length)
                {
                    int n = Math.Min(NetLimits.BlobChunkBytes, data.Length - off);
                    var slice = new byte[n];
                    Array.Copy(data, off, slice, 0, n);
                    NetFrame.Write(_stream, NetLimits.FrameKindChunk, slice);
                    off += n;
                }
                _stream.Flush();
            }

            public object WaitFor(string type, int timeoutMs = 3000)
            {
                for (int i = 0; i < _inbox.Count; i++)
                {
                    if (_inbox[i].Key != type) continue;
                    var node = _inbox[i].Value;
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

                    if (kind == NetLimits.FrameKindChunk) { _chunks.Write(payload, 0, payload.Length); continue; }

                    object node;
                    string got;
                    if (!NetJson.TryParseMessage(payload, 0, payload.Length, out node, out got)) continue;
                    if (got == type) return node;
                    _inbox.Add(new KeyValuePair<string, object>(got, node));
                }
                return null;
            }

            /// <summary>收到 <paramref name="untilType"/> 為止,回傳這段期間收到的全部 chunk 位元組。</summary>
            public byte[] ReceiveAllChunks(string untilType, int timeoutMs)
            {
                if (WaitFor(untilType, timeoutMs) == null) return null;
                return _chunks.ToArray();
            }

            public void Dispose()
            {
                try { _tcp.Close(); } catch { }
                try { _chunks.Dispose(); } catch { }
            }
        }
    }
}
