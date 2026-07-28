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
    /// 缺歌傳檔的端到端測試 —— **真的開 socket、真的傳位元組、真的落地成檔案**。
    ///
    /// 為什麼一定要做到這個程度:傳檔這條路上有一整排「單元測試抓不到」的接線
    /// (framing 的 kind=1、file 連線的認親、chunk 的順序與長度、流量控制的水位、
    /// 收完之後 pack json 的內容),而它們錯掉的症狀全都長得一樣:
    /// 「對方的下載卡在 87% 不動」。那種東西只能真的跑一次。
    ///
    /// 另一半是**安全性**:server 對上傳者的每一項宣稱都要自己重算。這裡每一條防線各一個測試,
    /// 因為它們正是「有人改了 client」時唯一還站著的東西。
    /// </summary>
    public class BlobTransferTests
    {
        private Hub _hub;
        private Task _hubTask;
        private string _dataDir;
        private readonly List<Tc> _clients = new List<Tc>();

        // 一首假的「外部歌」:一張譜 + 一個音檔。內容小,但走的路徑與真的一模一樣。
        private const string ChartText = "[General]\nAudioFilename: audio.mp3\n[HitObjects]\n64,192,1000,1,0\n";
        private static readonly byte[] AudioBytes = MakeAudio(200 * 1024);   // 跨得過好幾塊 chunk

        [SetUp]
        public void StartServer()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "sdo_blob_srv_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dataDir);

            var opts = new ServerOptions
            {
                Port = 0,
                Bind = "127.0.0.1",
                DataDir = _dataDir,
                CodeSeed = 909,
                Password = "",
            };
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
            for (int i = 0; i < _clients.Count; i++) _clients[i].Dispose();
            _clients.Clear();
            if (_hub != null) _hub.Stop();
            if (_hubTask != null) { try { _hubTask.Wait(3000); } catch { } }
            try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true); } catch { }
        }

        // ================= 測試 =================

        [Test]
        public void A_Song_Uploaded_By_The_Host_Can_Be_Downloaded_Byte_For_Byte()
        {
            var host = Control("房主");
            int code = CreateRoom(host);
            var peer = Control("缺歌的人");
            Join(peer, code);

            var files = Manifest();
            string packId = SongPackId.Compute(files);
            SetExternalSong(host, packId);

            // ---- 上傳 ----
            var hostFile = FileConn(host);
            var need = UploadBegin(hostFile, packId, files);
            CollectionAssert.AreEqual(new[] { 0, 1 }, need, "server 一個檔都沒有 → 兩個都要傳");
            SendPayloads(hostFile, files, need);

            hostFile.Send(JObj.New().Str(NetProto.FieldType, NetProto.BlobUploadDone).Int(NetProto.FieldRequest, 20));
            var done = hostFile.WaitFor(NetProto.BlobUploadDone);
            Assert.IsNotNull(done, "上傳沒完成:" + LastError(hostFile));
            Assert.IsTrue(NetJson.Bool(done, "ok"));

            // 房內每個人都要收到「這首歌可以下載了」
            Assert.IsNotNull(peer.WaitFor(NetProto.BlobAvailable), "缺歌的人沒被通知");

            // ---- 下載 ----
            var peerFile = FileConn(peer);
            peerFile.Send(JObj.New().Str(NetProto.FieldType, NetProto.BlobDownloadBegin)
                .Int(NetProto.FieldRequest, 30).Str("packId", packId));

            var manifest = peerFile.WaitFor(NetProto.BlobManifest);
            Assert.IsNotNull(manifest, "沒收到 manifest:" + LastError(peerFile));
            var got = ReadManifest(manifest);
            Assert.AreEqual(2, got.Count);

            var bytes = peerFile.ReceiveAllChunks(NetProto.BlobDownloadDone, 10000);
            Assert.IsNotNull(bytes, "chunk 沒收完");

            // 逐位元組比對:manifest 的順序 + 長度 → 切開還原成檔案
            long expectTotal = 0;
            for (int i = 0; i < got.Count; i++) expectTotal += got[i].Length;
            Assert.AreEqual(expectTotal, bytes.Length, "收到的總位元組數要與 manifest 相符");

            int off = 0;
            for (int i = 0; i < got.Count; i++)
            {
                var slice = new byte[got[i].Length];
                Array.Copy(bytes, off, slice, 0, slice.Length);
                off += slice.Length;
                Assert.AreEqual(got[i].Sha256, Sha(slice), got[i].RelPath + " 的內容 hash 對不上");
            }
        }

        [Test]
        public void Uploading_The_Same_Song_Twice_Needs_Zero_Files()
        {
            // 這是選內容尋址的**主要理由**:同一首歌第二次有人開,一個位元組都不用傳。
            var host = Control("房主");
            int code = CreateRoom(host);
            var files = Manifest();
            string packId = SongPackId.Compute(files);
            SetExternalSong(host, packId);

            var f1 = FileConn(host);
            var need = UploadBegin(f1, packId, files);
            SendPayloads(f1, files, need);
            f1.Send(JObj.New().Str(NetProto.FieldType, NetProto.BlobUploadDone).Int(NetProto.FieldRequest, 21));
            Assert.IsNotNull(f1.WaitFor(NetProto.BlobUploadDone));

            // 第二次(換一條連線,模擬另一個房主帶著同一首歌進來)
            var f2 = FileConn(host);
            var need2 = UploadBegin(f2, packId, files);
            CollectionAssert.IsEmpty(need2, "server 已經有全部檔案 → 一個都不用傳");

            // need 是空的時候 server 應該直接完成,不用再送 blobUploadDone
            var done = f2.WaitFor(NetProto.BlobUploadDone);
            Assert.IsNotNull(done, "全部去重命中時應該直接完成");
            Assert.IsTrue(NetJson.Bool(done, "ok"));
            _ = code;
        }

        [Test]
        public void Re_Uploading_An_Existing_PackId_Cannot_Replace_Its_Contents()
        {
            // 🔴 packId 的強度不是均勻的:譜面算完整 SHA-256,但音檔只進了(檔名, 長度) ——
            // 那是刻意的取捨(開機掃描不可能讀完幾 GB 音檔)。所以理論上可以送一份
            // 「譜一樣、音檔長度一樣但內容不同」的包,packId 重算相符,然後把既有那份換掉,
            // 之後所有下載的人拿到的都是被替換的音檔。防線 = 已存在的 pack 紀錄**不覆寫**。
            var host = Control("房主");
            int code = CreateRoom(host);
            var files = Manifest();
            string packId = SongPackId.Compute(files);
            SetExternalSong(host, packId);

            // 第一次:正常上傳
            var f1 = FileConn(host);
            SendPayloads(f1, files, UploadBegin(f1, packId, files));
            f1.Send(JObj.New().Str(NetProto.FieldType, NetProto.BlobUploadDone).Int(NetProto.FieldRequest, 23));
            Assert.IsNotNull(f1.WaitFor(NetProto.BlobUploadDone));

            // 第二次:同一個 packId,但音檔換成「一樣長、內容不同」的位元組
            var tampered = new byte[AudioBytes.Length];
            for (int i = 0; i < tampered.Length; i++) tampered[i] = 0x7E;
            var lying = new List<PackFileEntry>
            {
                files[0],                                                        // 譜面不動(packId 才算得相符)
                new PackFileEntry("audio.mp3", tampered.Length, Sha(tampered)),
            };
            Assert.AreEqual(packId, SongPackId.Compute(lying), "前提:換掉音檔內容但長度一樣,packId 仍然相符");

            var f2 = FileConn(host);
            var need2 = UploadBegin(f2, packId, lying);
            for (int i = 0; i < need2.Count; i++) f2.SendChunks(tampered);        // 只有音檔會被要
            f2.Send(JObj.New().Str(NetProto.FieldType, NetProto.BlobUploadDone).Int(NetProto.FieldRequest, 24));
            Assert.IsNotNull(f2.WaitFor(NetProto.BlobUploadDone), "第二次上傳本身不必失敗(內容尋址,存了也無害)");

            // 關鍵:現在下載回來的必須還是**原本**那份音檔
            var peer = Control("下載的人");
            Join(peer, code);
            var pf = FileConn(peer);
            pf.Send(JObj.New().Str(NetProto.FieldType, NetProto.BlobDownloadBegin)
                .Int(NetProto.FieldRequest, 32).Str("packId", packId));
            var manifest = pf.WaitFor(NetProto.BlobManifest);
            Assert.IsNotNull(manifest, "沒收到 manifest:" + LastError(pf));

            var got = ReadManifest(manifest);
            string wantAudioSha = Sha(AudioBytes);
            bool sawOriginal = false;
            for (int i = 0; i < got.Count; i++)
                if (got[i].RelPath == "audio.mp3") sawOriginal = got[i].Sha256 == wantAudioSha;
            Assert.IsTrue(sawOriginal, "既有的包被換掉了 —— 下載到的音檔不是原本那份");
        }

        [Test]
        public void A_Zero_Length_File_In_The_Pack_Does_Not_Stall_The_Upload()
        {
            // 🔴 上傳端照長度切 chunk → 空檔案**一塊都不會送**。server 如果傻等它的 chunk,
            // 之後的 blobUploadDone 會看到還有檔沒收完而整批退掉 —— 症狀是「某些歌永遠上傳失敗」,
            // 而清單看起來完全正常。歌曲資料夾裡放一個空的 .ini 就會踩到。
            var host = Control("房主");
            CreateRoom(host);

            var files = new List<PackFileEntry>(Manifest());
            files.Insert(0, new PackFileEntry("empty.ini", 0, Sha(new byte[0])));   // 排在最前面,卡在第一個
            string packId = SongPackId.Compute(files);
            SetExternalSong(host, packId);

            var f = FileConn(host);
            var need = UploadBegin(f, packId, files);
            CollectionAssert.Contains(need, 0, "空檔案 server 也沒有 → 要列進 need");

            // 只送有內容的那些(與真的 client 一樣:空檔案不送 chunk)
            for (int i = 0; i < need.Count; i++)
            {
                var entry = files[need[i]];
                if (entry.Length == 0) continue;
                f.SendChunks(PayloadFor(entry.RelPath));
            }

            f.Send(JObj.New().Str(NetProto.FieldType, NetProto.BlobUploadDone).Int(NetProto.FieldRequest, 22));
            var done = f.WaitFor(NetProto.BlobUploadDone);
            Assert.IsNotNull(done, "空檔案不該讓上傳卡住:" + LastError(f));
            Assert.IsTrue(NetJson.Bool(done, "ok"));
        }

        [Test]
        public void Downloading_A_Pack_With_An_Empty_File_Sends_No_Chunk_For_It()
        {
            // 這條釘住 client 端 SkipEmptyIncoming 依賴的 wire 契約:空檔案**會**出現在 manifest 裡
            // (收端要建出那個檔),但**不會**有任何 chunk 對應它。收端如果等它的 chunk 才推進 cursor,
            // 後面每個檔的內容都會寫錯位置 → 最後「檔案沒收完」。
            var host = Control("房主");
            int code = CreateRoom(host);

            var files = new List<PackFileEntry>(Manifest());
            files.Insert(1, new PackFileEntry("empty.ini", 0, Sha(new byte[0])));
            string packId = SongPackId.Compute(files);
            SetExternalSong(host, packId);

            var hf = FileConn(host);
            var need = UploadBegin(hf, packId, files);
            for (int i = 0; i < need.Count; i++)
            {
                var e = files[need[i]];
                if (e.Length == 0) continue;
                hf.SendChunks(PayloadFor(e.RelPath));
            }
            hf.Send(JObj.New().Str(NetProto.FieldType, NetProto.BlobUploadDone).Int(NetProto.FieldRequest, 25));
            Assert.IsNotNull(hf.WaitFor(NetProto.BlobUploadDone), "上傳沒完成:" + LastError(hf));

            var peer = Control("下載的人");
            Join(peer, code);
            var pf = FileConn(peer);
            pf.Send(JObj.New().Str(NetProto.FieldType, NetProto.BlobDownloadBegin)
                .Int(NetProto.FieldRequest, 33).Str("packId", packId));
            var manifest = pf.WaitFor(NetProto.BlobManifest);
            Assert.IsNotNull(manifest, "沒收到 manifest:" + LastError(pf));

            var got = ReadManifest(manifest);
            bool sawEmpty = false;
            long expect = 0;
            for (int i = 0; i < got.Count; i++)
            {
                expect += got[i].Length;
                if (got[i].RelPath == "empty.ini") { sawEmpty = true; Assert.AreEqual(0, got[i].Length); }
            }
            Assert.IsTrue(sawEmpty, "空檔案也要列在 manifest 裡 —— 收端得知道要建它");

            var bytes = pf.ReceiveAllChunks(NetProto.BlobDownloadDone, 10000);
            Assert.IsNotNull(bytes, "chunk 沒收完");
            Assert.AreEqual(expect, bytes.Length, "位元組總數要等於 manifest 的長度總和(空檔案貢獻 0)");
        }

        [Test]
        public void Blob_Query_Tells_The_Client_Whether_The_Server_Has_It()
        {
            var host = Control("房主");
            CreateRoom(host);
            var files = Manifest();
            string packId = SongPackId.Compute(files);

            host.Send(JObj.New().Str(NetProto.FieldType, NetProto.BlobQuery)
                .Int(NetProto.FieldRequest, 40).Str("packId", packId));
            var info = host.WaitFor(NetProto.BlobInfo);
            Assert.IsNotNull(info);
            Assert.IsFalse(NetJson.Bool(info, "have"), "還沒上傳 → 沒有");
        }

        // ---- 以下每一條都是「有人改了 client」時唯一還站著的東西 ----

        [Test]
        public void A_Manifest_Whose_PackId_Does_Not_Match_Its_Contents_Is_Refused()
        {
            // 🔴 最重要的一條。少了它,上傳者可以宣稱「這包是別人那首熱門歌」然後把譜換掉,
            //    之後所有下載的人拿到的都是被替換的內容 —— 而且他們的 packId 檢查會通過
            //    (因為他們信的是 server 給的清單)。
            var host = Control("房主");
            CreateRoom(host);
            var files = Manifest();
            string realId = SongPackId.Compute(files);
            SetExternalSong(host, realId);

            var f = FileConn(host);
            // 宣稱是這個 packId,但清單裡把譜的長度改掉 → 重算不會相符
            var lying = new List<PackFileEntry>(files);
            lying[0] = new PackFileEntry(files[0].RelPath, files[0].Length + 1, files[0].Sha256);

            f.Send(UploadBeginMsg(realId, lying));
            var err = f.WaitFor(NetProto.BlobError);
            Assert.IsNotNull(err, "packId 重算不符應該被拒");
            Assert.AreEqual(NetProto.BlobErrHashMismatch, NetJson.Str(err, "code"));
        }

        [Test]
        public void A_File_Whose_Bytes_Do_Not_Match_Its_Hash_Is_Refused()
        {
            var host = Control("房主");
            CreateRoom(host);
            var files = Manifest();
            string packId = SongPackId.Compute(files);
            SetExternalSong(host, packId);

            var f = FileConn(host);
            var need = UploadBegin(f, packId, files);
            Assert.Greater(need.Count, 0);

            // 長度對、內容不對 —— 只有 server 自己重算 SHA-256 才抓得到。
            var wrong = new byte[files[need[0]].Length];
            for (int i = 0; i < wrong.Length; i++) wrong[i] = 0x5A;
            f.SendChunks(wrong);

            var err = f.WaitFor(NetProto.BlobError);
            Assert.IsNotNull(err, "內容與 hash 不符應該被拒");
            Assert.AreEqual(NetProto.BlobErrHashMismatch, NetJson.Str(err, "code"));
        }

        [Test]
        public void A_Path_Outside_The_Song_Folder_Is_Refused()
        {
            var host = Control("房主");
            CreateRoom(host);
            var evil = new List<PackFileEntry>
            {
                new PackFileEntry("../../../系統檔.osu", 10, Sha(Encoding.UTF8.GetBytes("x"))),
            };
            // 先讓房間選這個 packId,才能穿過「只能傳房間選的那首歌」那道外層閘門 ——
            // 不然擋下它的會是 badState,這條測試就沒有真的驗到路徑檢查。
            SetExternalSong(host, SongPackId.Compute(evil));

            var f = FileConn(host);
            f.Send(UploadBeginMsg(SongPackId.Compute(evil), evil));

            var err = f.WaitFor(NetProto.BlobError);
            Assert.IsNotNull(err, "路徑穿越應該被拒");
            Assert.AreEqual(NetProto.BlobErrBadPath, NetJson.Str(err, "code"));
        }

        [Test]
        public void A_Video_In_The_Manifest_Is_Refused()
        {
            // 需求 8:影片自動過濾。client 端會先濾掉,但**這裡是真正的防線** ——
            // 濾掉的東西才是傳輸量最大的部分。
            var host = Control("房主");
            CreateRoom(host);
            var withVideo = new List<PackFileEntry>(Manifest())
            {
                new PackFileEntry("bg.mp4", 1024, Sha(Encoding.UTF8.GetBytes("video"))),
            };
            // 同上:先穿過外層的「這是房間選的歌嗎」,才驗得到過濾清單本身。
            SetExternalSong(host, SongPackId.Compute(withVideo));

            var f = FileConn(host);
            f.Send(UploadBeginMsg(SongPackId.Compute(withVideo), withVideo));

            var err = f.WaitFor(NetProto.BlobError);
            Assert.IsNotNull(err, "影片應該被拒");
            Assert.AreEqual(NetProto.BlobErrBadPath, NetJson.Str(err, "code"));
        }

        [Test]
        public void Uploading_A_Song_The_Room_Did_Not_Select_Is_Refused()
        {
            // 沒有這一條,任何連上來的人都能把 server 當免費檔案空間用。
            var host = Control("房主");
            CreateRoom(host);
            var files = Manifest();
            // 故意**不**選這首歌
            var f = FileConn(host);
            f.Send(UploadBeginMsg(SongPackId.Compute(files), files));

            var err = f.WaitFor(NetProto.BlobError);
            Assert.IsNotNull(err, "沒選這首歌就上傳應該被拒");
            Assert.AreEqual(NetProto.ErrBadState, NetJson.Str(err, "code"));
        }

        [Test]
        public void Sending_More_Bytes_Than_Declared_Is_Refused()
        {
            var host = Control("房主");
            CreateRoom(host);
            var files = Manifest();
            string packId = SongPackId.Compute(files);
            SetExternalSong(host, packId);

            var f = FileConn(host);
            var need = UploadBegin(f, packId, files);
            Assert.Greater(need.Count, 0);

            // 第一個檔是譜(很小)。直接送一整塊 64 KiB 過去 → 超過宣稱長度。
            f.SendChunks(new byte[NetLimits.BlobChunkBytes]);

            var err = f.WaitFor(NetProto.BlobError);
            Assert.IsNotNull(err, "超過宣稱長度應該被拒");
            Assert.AreEqual(NetProto.BlobErrTooBig, NetJson.Str(err, "code"));
        }

        [Test]
        public void Downloading_A_Song_The_Server_Does_Not_Have_Says_So()
        {
            var host = Control("房主");
            CreateRoom(host);
            var f = FileConn(host);
            f.Send(JObj.New().Str(NetProto.FieldType, NetProto.BlobDownloadBegin)
                .Int(NetProto.FieldRequest, 31)
                .Str("packId", SongPackId.Prefix + "99999999999999999999999999999999"));

            var err = f.WaitFor(NetProto.BlobError);
            Assert.IsNotNull(err);
            Assert.AreEqual(NetProto.BlobErrNotFound, NetJson.Str(err, "code"));
        }

        [Test]
        public void A_Transfer_On_The_Control_Connection_Is_Refused()
        {
            // 傳檔要走第二條連線 —— 幾十 MB 的 chunk 排在房間訊息前面的話,
            // 整個房間在下載期間會像卡住一樣。
            var host = Control("房主");
            CreateRoom(host);
            var files = Manifest();
            SetExternalSong(host, SongPackId.Compute(files));

            host.Send(UploadBeginMsg(SongPackId.Compute(files), files));
            var err = host.WaitFor(NetProto.BlobError);
            Assert.IsNotNull(err, "control 連線不該能上傳");
            Assert.AreEqual(NetProto.ErrProto, NetJson.Str(err, "code"));
        }

        // ================= helper =================

        private static byte[] MakeAudio(int n)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = (byte)(i * 31 + 7);   // 決定性、不可壓縮性不重要
            return b;
        }

        private static List<PackFileEntry> Manifest()
        {
            var chart = Encoding.UTF8.GetBytes(ChartText);
            return new List<PackFileEntry>
            {
                new PackFileEntry("song.osu", chart.Length, Sha(chart)),
                new PackFileEntry("audio.mp3", AudioBytes.Length, Sha(AudioBytes)),
            };
        }

        private static byte[] PayloadFor(string rel)
            => rel == "song.osu" ? Encoding.UTF8.GetBytes(ChartText) : AudioBytes;

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

        private static JObj UploadBeginMsg(string packId, List<PackFileEntry> files)
        {
            var arr = JArr.New();
            for (int i = 0; i < files.Count; i++)
                arr.Add(JObj.New().Str("path", files[i].RelPath).Long("len", files[i].Length).Str("sha256", files[i].Sha256));
            return JObj.New()
                .Str(NetProto.FieldType, NetProto.BlobUploadBegin)
                .Int(NetProto.FieldRequest, 10)
                .Str("packId", packId)
                .Put("files", arr);
        }

        private List<int> UploadBegin(Tc file, string packId, List<PackFileEntry> files)
        {
            file.Send(UploadBeginMsg(packId, files));
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
                list.Add(new PackFileEntry(NetJson.Str(arr[i], "path"),
                                           NetJson.Long(arr[i], "len"),
                                           NetJson.Str(arr[i], "sha256")));
            return list;
        }

        private static string LastError(Tc c)
        {
            var e = c.WaitFor(NetProto.BlobError, 200);
            return e == null ? "(沒有 blobError)" : NetJson.Str(e, "code") + " " + NetJson.Str(e, "msg");
        }

        private Tc Control(string name)
        {
            var c = new Tc(_hub.ActualPort);
            _clients.Add(c);
            c.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.Hello)
                .Int(NetProto.FieldRequest, 1)
                .Int("proto", NetProto.Version)
                .Str("role", NetProto.RoleControl)
                .Str("playerId", "00000000")
                .Str("name", name));
            var w = c.WaitFor(NetProto.Welcome);
            Assert.IsNotNull(w, name + " 沒收到 welcome");
            c.UserId = NetJson.Int(w, "userId");
            c.SessionKey = NetJson.Str(w, "sessionKey");
            return c;
        }

        /// <summary>用 control 連線發的 sessionKey 開第二條 file 連線(認親)。</summary>
        private Tc FileConn(Tc owner)
        {
            var c = new Tc(_hub.ActualPort);
            _clients.Add(c);
            c.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.Hello)
                .Int(NetProto.FieldRequest, 2)
                .Int("proto", NetProto.Version)
                .Str("role", NetProto.RoleFile)
                .Str("sessionKey", owner.SessionKey));
            Assert.IsNotNull(c.WaitFor(NetProto.Welcome), "file 連線沒認親成功");
            return c;
        }

        private int CreateRoom(Tc c)
        {
            c.Send(JObj.New().Str(NetProto.FieldType, NetProto.CreateRoom).Int(NetProto.FieldRequest, 3).Str("name", "傳檔測試"));
            var st = c.WaitFor(NetProto.RoomState);
            Assert.IsNotNull(st, "建房失敗");
            return NetJson.Int(st, "code");
        }

        private void Join(Tc c, int code)
        {
            c.Send(JObj.New().Str(NetProto.FieldType, NetProto.JoinRoom).Int(NetProto.FieldRequest, 4).Int("code", code));
            Assert.IsNotNull(c.WaitFor(NetProto.RoomState), "加入房間失敗");
        }

        private static void SetExternalSong(Tc host, string packId)
        {
            host.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.SetSong)
                .Int(NetProto.FieldRequest, 5)
                .Put("song", JObj.New()
                    .Bool("official", false)
                    .Str("packId", packId)
                    .Str("songKey", "audio")
                    .Str("chartRelPath", "song.osu")
                    .Str("title", "外部測試歌")));
            Assert.IsNotNull(host.WaitFor(NetProto.RoomState), "選歌沒生效");
        }

        /// <summary>會處理 chunk 的測試 client(<see cref="ServerIntegrationTests"/> 的那個只認 JSON)。</summary>
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
