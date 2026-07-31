using System;
using System.Collections.Generic;
using System.IO;
using Sdo.Net;
using Sdo.Osu;
using Sdo.Server.Files;

namespace Sdo.Server.Net
{
    /// <summary>
    /// 缺歌傳檔的 server 端(M5)。走 <c>file</c> 角色的第二條連線 ——
    /// 一首歌幾十 MB 的 chunk 不能把房間訊息卡在後面。
    ///
    /// 🔴 <b>完全不信任上傳者。</b>每一步都自己重算一次:
    ///   • 每個相對路徑過 <see cref="SafeRelPath.IsSafe"/> 與 <see cref="SongPackFilter"/>(擋路徑穿越、影片、執行檔);
    ///   • 每個檔案收完自己重算 SHA-256(<see cref="DiskBlobIo.CommitBlob"/>);
    ///   • **整份清單重算 packId**,對不上就整批不收 —— 否則上傳者可以宣稱「這包是別人那首歌」
    ///     然後把內容換掉,之後所有下載的人拿到的都是被替換的譜。
    /// </summary>
    public sealed partial class Hub
    {
        /// <summary>一份正在收的上傳。key = connId(file 連線)。</summary>
        private sealed class UploadSession
        {
            public int UserId;
            public int RoomCode;
            public string PackId;
            public List<PackFileEntry> Files;    // 完整清單(已驗過)
            public List<int> Need;               // 還缺的檔案 index,依序收
            public int Cursor;                   // Need 裡的位置
            public long CurReceived;             // 目前這個檔已收到幾 bytes
            public FileStream CurStream;
            public string CurTmpPath;
            public string TmpDir;
            public long TotalNeedBytes, DoneBytes;
            public long LastProgressMs;
        }

        /// <summary>一份正在送的下載。key = connId(file 連線)。</summary>
        private sealed class DownloadSession
        {
            public int UserId;
            public int RoomCode;
            public string PackId;
            public List<PackFileEntry> Files;
            public int Cursor;                   // 目前送第幾個檔
            public FileStream CurStream;
            public long TotalBytes, SentBytes;
        }

        private readonly Dictionary<int, UploadSession> _uploads = new Dictionary<int, UploadSession>();
        private readonly Dictionary<int, DownloadSession> _downloads = new Dictionary<int, DownloadSession>();

        /// <summary>
        /// 一輪 tick 最多把 outbound 佇列補到這個水位。
        /// chunk 是 critical(佇列滿 = 斷線),所以絕不能一次全塞進去;
        /// 24 塊 × 64 KiB = 1.5 MB in-flight,50 ms 一輪 → 上限約 30 MB/s,對 LAN 與網際網路都夠。
        /// </summary>
        private const int DownloadHighWater = 24;

        // ================= blobQuery:server 有沒有這首歌 =================

        private void OnBlobQuery(Connection conn, object node, int rq, long now)
        {
            string packId = NetJson.Str(node, "packId");
            bool have = SongPackId.IsWellFormed(packId) && _blobs.HasPack(packId);
            if (have) _blobs.Touch(packId, now);      // 命中就續命(TTL 看的是這個時間戳)

            conn.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.BlobInfo)
                .Int(NetProto.FieldRequest, rq)
                .Str("packId", packId ?? "")
                .Bool("have", have));
        }

        // ================= 上傳 =================

        private void OnBlobUploadBegin(Connection conn, object node, int rq, long now)
        {
            if (conn.Role != NetProto.RoleFile) { SendBlobError(conn, rq, NetProto.ErrProto, "上傳要走 file 連線"); return; }
            if (_uploads.ContainsKey(conn.ConnId)) { SendBlobError(conn, rq, NetProto.ErrProto, "這條連線已經在上傳了"); return; }

            string packId = NetJson.Str(node, "packId");
            if (!SongPackId.IsWellFormed(packId)) { SendBlobError(conn, rq, NetProto.BlobErrBadPath, "packId 格式不對"); return; }

            // 只能上傳「自己房間現在選的那首歌」。
            // 不是為了規則好看 —— 少了這條,任何連上來的人都能把 server 當免費檔案空間用。
            var room = _rooms.RoomOf(conn.UserId);
            if (room == null) { SendBlobError(conn, rq, NetProto.ErrNotInRoom, "不在房間裡"); return; }
            var song = room.State != null ? room.State.Song : null;
            if (song == null || song.Official || !string.Equals(song.PackId, packId, StringComparison.Ordinal))
            {
                SendBlobError(conn, rq, NetProto.ErrBadState, "這不是房間現在選的那首歌");
                return;
            }

            // ---- 清單驗證(逐項,壞一項就整批退) ----
            var arr = NetJson.Arr(node, "files");
            if (arr.Count == 0 || arr.Count > NetLimits.MaxPackFiles)
            {
                SendBlobError(conn, rq, NetProto.BlobErrTooBig, "檔案數不合理:" + arr.Count);
                return;
            }

            var files = new List<PackFileEntry>(arr.Count);
            long total = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < arr.Count; i++)
            {
                var f = arr[i];
                string rel = SafeRelPath.Normalize(NetJson.Str(f, "path"));
                long len = NetJson.Long(f, "len");
                string sha = (NetJson.Str(f, "sha256") ?? "").ToLowerInvariant();

                if (!SafeRelPath.IsSafe(rel)) { SendBlobError(conn, rq, NetProto.BlobErrBadPath, "路徑不安全:" + rel); return; }
                if (!seen.Add(rel)) { SendBlobError(conn, rq, NetProto.BlobErrBadPath, "同一個路徑出現兩次:" + rel); return; }
                if (len < 0 || !SongPackFilter.IsTransferable(rel, len))
                {
                    SendBlobError(conn, rq, NetProto.BlobErrBadPath, "這個檔不該傳:" + rel);
                    return;
                }
                if (!DiskBlobIo.IsSha256(sha)) { SendBlobError(conn, rq, NetProto.BlobErrHashMismatch, "缺 sha256:" + rel); return; }

                files.Add(new PackFileEntry(rel, len, sha));
                total += len;
            }

            if (total > NetLimits.DefaultMaxBlobBytes)
            {
                SendBlobError(conn, rq, NetProto.BlobErrTooBig, "整包太大:" + (total / (1024 * 1024)) + " MB");
                return;
            }

            // 🔴 重算 packId。對不上 → 上傳者宣稱的身分與內容不符,整批不收。
            string recomputed = SongPackId.Compute(files);
            if (!string.Equals(recomputed, packId, StringComparison.Ordinal))
            {
                SendBlobError(conn, rq, NetProto.BlobErrHashMismatch, "重算的 packId 與宣稱的不符");
                Log("✗ user " + conn.UserId + " 上傳被拒:packId 重算不符");
                return;
            }

            // ---- 去重:server 已經有的檔直接跳過(同一首歌第二次有人玩 = 零上傳) ----
            var need = new List<int>();
            long needBytes = 0;
            for (int i = 0; i < files.Count; i++)
            {
                if (_blobs.HasBlob(files[i].Sha256)) continue;
                need.Add(i);
                needBytes += files[i].Length;
            }

            long cap = (long)_opts.MaxTotalBlobGb * 1024L * 1024L * 1024L;
            if (needBytes > 0 && _blobs.UsedBytes() + needBytes > cap)
            {
                SendBlobError(conn, rq, NetProto.BlobErrQuota, "server 的歌曲暫存空間滿了");
                return;
            }

            // 每人每小時的上傳配額(M10;預設不限 → LAN 行為不變)。
            // 🔴 這一條擋的是既有上限擋不住的東西:**一個人反覆上傳不同的歌**。
            // 每一包都合法(單檔 32 MB / 整包 200 MB 都過),加起來卻能在幾分鐘內把磁碟吃光,
            // 而且每包在房間換歌前都被 janitor pin 住,清不掉。
            if (!_quota.Allows(conn.UserId, needBytes, now))
            {
                // 下面 SendBlobError 已經會印一行「✗ … 被拒」,這裡只補「還剩多少額度」給 verbose。
                LogVerbose("user " + conn.UserId + " 的配額還剩 "
                    + (_quota.Remaining(conn.UserId, now) / 1024) + " KB");
                SendBlobError(conn, rq, NetProto.BlobErrQuota, "超過每小時上傳配額,請稍後再試");
                return;
            }
            _quota.Add(conn.UserId, needBytes, now);   // 先記帳:同時開兩條上傳的話兩邊都要算進去

            var sess = new UploadSession
            {
                UserId = conn.UserId,
                RoomCode = room.State.Code,
                PackId = packId,
                Files = files,
                Need = need,
                TotalNeedBytes = needBytes,
                TmpDir = _blobs.NewTempDir(conn.ConnId),
                LastProgressMs = now,
            };
            _uploads[conn.ConnId] = sess;

            var needArr = JArr.New();
            for (int i = 0; i < need.Count; i++) needArr.Add(need[i]);
            conn.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.BlobUploadAccept)
                .Int(NetProto.FieldRequest, rq)
                .Str("packId", packId)
                .Long("needBytes", needBytes)
                .Put("need", needArr));

            Log("房 " + sess.RoomCode + " 收上傳:" + need.Count + "/" + files.Count + " 檔、"
                + Mb(needBytes) + "(" + sess.PackId + ")");

            if (need.Count == 0) FinishUpload(conn, sess, 0, now);   // 全部去重命中 → 直接完成
            else OpenNextUploadFile(conn, sess);
        }

        private bool OpenNextUploadFile(Connection conn, UploadSession sess)
        {
            CloseUploadStream(sess);

            // 🔴 長度 0 的檔案要在這裡就地收完。上傳端是「照長度切 chunk」送的 → 空檔案**一塊都不會送**,
            // 而我們卻在等它的 chunk → 之後的 blobUploadDone 看到 cursor 還沒走完就整批退掉,
            // 症狀是「某些歌永遠上傳失敗」而清單看起來完全正常。歌曲資料夾裡有個空的 .ini 就會踩到。
            while (sess.Cursor < sess.Need.Count && sess.Files[sess.Need[sess.Cursor]].Length == 0)
            {
                var empty = sess.Files[sess.Need[sess.Cursor]];
                var tmp = Path.Combine(sess.TmpDir, "empty.part");
                try { File.WriteAllBytes(tmp, new byte[0]); }
                catch (Exception ex)
                {
                    SendBlobError(conn, 0, NetProto.BlobErrQuota, "server 開不了暫存檔: " + ex.Message);
                    AbortUpload(conn.ConnId);
                    return false;
                }
                // 照樣走 CommitBlob:它會重算 hash,所以宣稱「這個檔是空的」但 sha 不是空檔案的 sha
                // (= 說謊的清單)一樣會被擋掉。
                if (!_blobs.CommitBlob(tmp, empty.Sha256))
                {
                    SendBlobError(conn, 0, NetProto.BlobErrHashMismatch, "空檔案的 sha256 不符:" + empty.RelPath);
                    AbortUpload(conn.ConnId);
                    return false;
                }
                sess.Cursor++;
            }
            if (sess.Cursor >= sess.Need.Count) return false;

            // 暫存檔名用「第幾個」而不是原始檔名 —— 原始檔名是 client 給的字串,
            // 就算已經過 SafeRelPath 驗證,能不讓它落到檔案系統上就不要。
            sess.CurTmpPath = Path.Combine(sess.TmpDir, sess.Cursor.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".part");
            sess.CurReceived = 0;
            try { sess.CurStream = new FileStream(sess.CurTmpPath, FileMode.Create, FileAccess.Write, FileShare.None); }
            catch (Exception ex)
            {
                SendBlobError(conn, 0, NetProto.BlobErrQuota, "server 開不了暫存檔: " + ex.Message);
                AbortUpload(conn.ConnId);
                return false;
            }
            return true;
        }

        /// <summary>收到一塊上傳的位元組(<c>kind=1</c>)。</summary>
        private void OnUploadChunk(Connection conn, byte[] payload, long now)
        {
            UploadSession sess;
            if (!_uploads.TryGetValue(conn.ConnId, out sess))
            {
                SendBlobError(conn, 0, NetProto.ErrProto, "沒有進行中的上傳");
                return;
            }
            if (sess.Cursor >= sess.Need.Count || sess.CurStream == null)
            {
                SendBlobError(conn, 0, NetProto.ErrProto, "上傳的位元組比清單多");
                AbortUpload(conn.ConnId);
                return;
            }

            var f = sess.Files[sess.Need[sess.Cursor]];
            long remain = f.Length - sess.CurReceived;
            if (payload.Length > remain)
            {
                // 送超過宣稱的長度 —— 壞掉的 client 或惡意填充。不截斷,直接拒:
                // 截斷的話 hash 一定對不上,錯誤訊息會指向錯的地方。
                SendBlobError(conn, 0, NetProto.BlobErrTooBig, "這個檔送超過宣稱的長度:" + f.RelPath);
                AbortUpload(conn.ConnId);
                return;
            }

            try { sess.CurStream.Write(payload, 0, payload.Length); }
            catch (Exception ex)
            {
                SendBlobError(conn, 0, NetProto.BlobErrQuota, "server 寫不進暫存檔: " + ex.Message);
                AbortUpload(conn.ConnId);
                return;
            }
            sess.CurReceived += payload.Length;
            sess.DoneBytes += payload.Length;

            if (sess.CurReceived >= f.Length)
            {
                // 這個檔收完 → 自己重算 hash 收進倉庫
                CloseUploadStream(sess);
                if (!_blobs.CommitBlob(sess.CurTmpPath, f.Sha256))
                {
                    SendBlobError(conn, 0, NetProto.BlobErrHashMismatch, "內容與 sha256 不符:" + f.RelPath);
                    Log("✗ user " + conn.UserId + " 上傳被拒:" + f.RelPath + " hash 不符");
                    AbortUpload(conn.ConnId);
                    return;
                }
                sess.Cursor++;
                if (sess.Cursor < sess.Need.Count) OpenNextUploadFile(conn, sess);
            }

            // 房內轉播進度(頭貼下方的跑條)
            if (now - sess.LastProgressMs >= NetLimits.AvailProgressThrottleMs)
            {
                sess.LastProgressMs = now;
                BroadcastBlobProgress(sess.RoomCode, sess.UserId, Frac(sess.DoneBytes, sess.TotalNeedBytes), true);
            }
        }

        private void OnBlobUploadDone(Connection conn, object node, int rq, long now)
        {
            UploadSession sess;
            if (!_uploads.TryGetValue(conn.ConnId, out sess))
            {
                SendBlobError(conn, rq, NetProto.ErrProto, "沒有進行中的上傳");
                return;
            }
            if (sess.Cursor < sess.Need.Count)
            {
                SendBlobError(conn, rq, NetProto.ErrProto, "還有檔案沒收完(" + sess.Cursor + "/" + sess.Need.Count + ")");
                AbortUpload(conn.ConnId);
                return;
            }
            FinishUpload(conn, sess, rq, now);
        }

        private void FinishUpload(Connection conn, UploadSession sess, int rq, long now)
        {
            // 去重跳過的那些檔案現在還在嗎?(理論上在 —— 房間選著的歌會被 janitor pin 住 ——
            // 但這是「之後所有人下載到的東西」的最後一道檢查,便宜且值得。)
            for (int i = 0; i < sess.Files.Count; i++)
            {
                if (_blobs.HasBlob(sess.Files[i].Sha256)) continue;
                SendBlobError(conn, rq, NetProto.BlobErrNotFound, "缺檔:" + sess.Files[i].RelPath);
                AbortUpload(conn.ConnId);
                return;
            }

            // 🔴 已經有這個 packId 的紀錄就**不覆寫**,只續命。
            // 理由是 packId 的強度不是均勻的:譜面算完整 SHA-256,但音檔/圖片只進了(檔名, 長度)
            // (那是刻意的取捨 —— 開機掃描不可能讀完幾 GB 的音檔)。所以理論上有人可以送一份
            // 「譜一模一樣、音檔長度一樣但內容不同」的包,packId 會重算相符,然後把既有的那份換掉 ——
            // 之後所有下載的人拿到的都是被替換的音檔。不覆寫就完全沒有這條路:
            // 內容尋址的檔案本體早就都在了,重複上傳本來就不需要改任何東西。
            if (_blobs.HasPack(sess.PackId))
            {
                _blobs.Touch(sess.PackId, now);
                _blobs.DropTempDir(sess.TmpDir);
                _uploads.Remove(conn.ConnId);
                conn.Send(JObj.New()
                    .Str(NetProto.FieldType, NetProto.BlobUploadDone)
                    .Int(NetProto.FieldRequest, rq)
                    .Str("packId", sess.PackId)
                    .Bool("ok", true));
                BroadcastBlobProgress(sess.RoomCode, sess.UserId, 1f, true);
                BroadcastToRoom(sess.RoomCode, JObj.New()
                    .Str(NetProto.FieldType, NetProto.BlobAvailable)
                    .Str("packId", sess.PackId));
                LogVerbose("房 " + sess.RoomCode + " 上傳完成(這個包 server 早就有了,只續命):" + sess.PackId);
                return;
            }

            var pack = new BlobPack { PackId = sess.PackId, LastUsedUtcMs = now };
            pack.Files.AddRange(sess.Files);
            if (!_blobs.SavePack(pack))
            {
                SendBlobError(conn, rq, NetProto.BlobErrQuota, "server 存不了歌曲清單");
                AbortUpload(conn.ConnId);
                return;
            }

            _blobs.DropTempDir(sess.TmpDir);
            _uploads.Remove(conn.ConnId);

            conn.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.BlobUploadDone)
                .Int(NetProto.FieldRequest, rq)
                .Str("packId", sess.PackId)
                .Bool("ok", true));

            BroadcastBlobProgress(sess.RoomCode, sess.UserId, 1f, true);
            BroadcastToRoom(sess.RoomCode, JObj.New()
                .Str(NetProto.FieldType, NetProto.BlobAvailable)
                .Str("packId", sess.PackId));

            Log("房 " + sess.RoomCode + " 上傳完成:" + sess.PackId + "(" + sess.Files.Count + " 檔)");
        }

        private void AbortUpload(int connId)
        {
            UploadSession sess;
            if (!_uploads.TryGetValue(connId, out sess)) return;
            _uploads.Remove(connId);
            CloseUploadStream(sess);
            _blobs.DropTempDir(sess.TmpDir);
        }

        private static void CloseUploadStream(UploadSession sess)
        {
            if (sess.CurStream == null) return;
            try { sess.CurStream.Dispose(); } catch { }
            sess.CurStream = null;
        }

        // ================= 下載 =================

        private void OnBlobDownloadBegin(Connection conn, object node, int rq, long now)
        {
            if (conn.Role != NetProto.RoleFile) { SendBlobError(conn, rq, NetProto.ErrProto, "下載要走 file 連線"); return; }
            if (_downloads.ContainsKey(conn.ConnId)) { SendBlobError(conn, rq, NetProto.ErrProto, "這條連線已經在下載了"); return; }

            string packId = NetJson.Str(node, "packId");
            if (!SongPackId.IsWellFormed(packId)) { SendBlobError(conn, rq, NetProto.BlobErrBadPath, "packId 格式不對"); return; }

            var pack = _blobs.LoadPack(packId);
            if (pack == null) { SendBlobError(conn, rq, NetProto.BlobErrNotFound, "server 沒有這首歌"); return; }

            // 每人同時下載數上限 —— 一個人開十條連線把頻寬吃光的話,同房其他人就下不到歌。
            int mine = 0;
            foreach (var kv in _downloads) if (kv.Value.UserId == conn.UserId) mine++;
            if (mine >= NetLimits.MaxConcurrentDownloadsPerUser)
            {
                SendBlobError(conn, rq, NetProto.ErrRateLimit, "同時下載太多");
                return;
            }

            _blobs.Touch(packId, now);      // 有人在下載 = 這包還在用

            long total = 0;
            var files = JArr.New();
            for (int i = 0; i < pack.Files.Count; i++)
            {
                var f = pack.Files[i];
                total += f.Length;
                files.Add(JObj.New().Str("path", f.RelPath).Long("len", f.Length).Str("sha256", f.Sha256 ?? ""));
            }

            conn.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.BlobManifest)
                .Int(NetProto.FieldRequest, rq)
                .Str("packId", packId)
                .Long("totalBytes", total)
                .Put("files", files));

            var room = _rooms.RoomOf(conn.UserId);
            _downloads[conn.ConnId] = new DownloadSession
            {
                UserId = conn.UserId,
                RoomCode = room != null && room.State != null ? room.State.Code : 0,
                PackId = packId,
                Files = pack.Files,
                TotalBytes = total,
            };
            Log("user " + conn.UserId + " 下載 " + packId + "(" + Mb(total) + ")");
        }

        /// <summary>
        /// 把下載的 chunk 補進 outbound 佇列。每輪 tick 呼叫一次。
        ///
        /// **流量控制就在這裡**:只補到 <see cref="DownloadHighWater"/>。
        /// 一次全塞的話 64 格的佇列會爆,而 chunk 是 critical → 連線直接被關掉,
        /// 症狀是「下載大歌時對方莫名斷線」。
        /// </summary>
        private void PumpDownloads()
        {
            if (_downloads.Count == 0) return;

            List<int> finished = null;
            foreach (var kv in _downloads)
            {
                Connection conn;
                if (!_conns.TryGetValue(kv.Key, out conn) || conn.IsClosed)
                {
                    (finished ?? (finished = new List<int>())).Add(kv.Key);
                    continue;
                }

                var sess = kv.Value;
                var buf = new byte[NetLimits.BlobChunkBytes];

                bool aborted = false;
                // 🔴 迴圈條件要**同時**看「連線還開著」。只看佇列水位的話,連線在迴圈中途關掉時
                // (Enqueue 對已關的連線直接丟掉 → OutboundCount 永遠停在原地)水位條件永遠成立 →
                // 這一輪 tick 會把整包幾十 MB 從磁碟讀完再一塊塊丟進垃圾桶,單執行緒的 actor loop
                // 就這樣被一個剛斷線的人卡住,全 server 的房間一起頓。
                while (!conn.IsClosed && conn.OutboundCount < DownloadHighWater)
                {
                    if (sess.CurStream == null)
                    {
                        bool missing;
                        if (!OpenNextDownloadFile(sess, out missing))
                        {
                            if (missing)
                            {
                                SendBlobError(conn, 0, NetProto.BlobErrNotFound, "server 缺了這個包的一個檔案");
                                aborted = true;
                            }
                            break;   // 沒有下一個檔了(或缺檔中止)
                        }
                    }

                    int n;
                    try { n = sess.CurStream.Read(buf, 0, buf.Length); }
                    catch { n = 0; }

                    if (n <= 0)
                    {
                        try { sess.CurStream.Dispose(); } catch { }
                        sess.CurStream = null;
                        sess.Cursor++;
                        continue;
                    }

                    conn.SendChunk(buf, 0, n);
                    sess.SentBytes += n;
                }

                if (aborted)
                {
                    (finished ?? (finished = new List<int>())).Add(kv.Key);
                }
                else if (sess.CurStream == null && sess.Cursor >= sess.Files.Count)
                {
                    conn.Send(JObj.New()
                        .Str(NetProto.FieldType, NetProto.BlobDownloadDone)
                        .Str("packId", sess.PackId)
                        .Long("totalBytes", sess.SentBytes));
                    Log("user " + sess.UserId + " 下載完成 " + sess.PackId);
                    (finished ?? (finished = new List<int>())).Add(kv.Key);
                }
            }

            if (finished == null) return;
            for (int i = 0; i < finished.Count; i++) AbortDownload(finished[i]);
        }

        /// <summary>
        /// 開下一個要送的檔。
        ///
        /// 🔴 缺檔**不能跳過**。收端是照 manifest 的順序與長度把位元組切回檔案的,少送一個檔
        /// 等於把後面每一個檔的內容都挪錯位置 —— 它會把下一個檔的開頭寫進這個檔裡。
        /// 收端的 hash 驗證最後會抓到(所以不會拿到壞譜),但錯誤訊息會指向一個**內容其實沒問題**的檔案,
        /// 而真正的原因(server 少了一個 blob)只有這裡看得到。所以就地中止並說清楚。
        /// (理論上不會發生:房間選著的歌會被 janitor pin 住。但這是「之後所有人下載到什麼」的路徑,
        ///  寧可明確失敗也不要送出一份對不起來的資料。)
        /// </summary>
        private bool OpenNextDownloadFile(DownloadSession sess, out bool missing)
        {
            missing = false;
            if (sess.Cursor >= sess.Files.Count) return false;

            var f = sess.Files[sess.Cursor];
            var path = _blobs.BlobPath(f.Sha256);
            if (path != null && File.Exists(path))
            {
                try
                {
                    sess.CurStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return true;
                }
                catch (Exception ex) { Log("下載時開不了 " + f.RelPath + ":" + ex.Message); }
            }
            Log("下載中止:server 缺了 " + f.RelPath + "(pack " + sess.PackId + ")");
            missing = true;
            return false;
        }

        private void AbortDownload(int connId)
        {
            DownloadSession sess;
            if (!_downloads.TryGetValue(connId, out sess)) return;
            _downloads.Remove(connId);
            if (sess.CurStream == null) return;
            try { sess.CurStream.Dispose(); } catch { }
            sess.CurStream = null;
        }

        // ================= 共用 =================

        /// <summary>連線消失時清掉它的傳輸狀態(開著的檔案 handle、暫存目錄)。</summary>
        private void CloseBlobSessions(int connId)
        {
            AbortUpload(connId);
            AbortDownload(connId);
        }

        /// <summary>房內轉播「某個人正在上傳/下載到幾成」—— 畫在頭貼下方的跑條。</summary>
        private void BroadcastBlobProgress(int roomCode, int userId, float frac, bool uploading)
        {
            if (roomCode == 0) return;
            BroadcastToRoom(roomCode, JObj.New()
                .Str(NetProto.FieldType, NetProto.BlobProgress)
                .Int("userId", userId)
                .Num("frac", Math.Round(frac, 3))
                .Bool("up", uploading));
        }

        /// <summary>推一份訊息給房裡所有人(座位 + 旁觀)。編碼一次,同 <c>BroadcastRoomState</c>。</summary>
        private void BroadcastToRoom(int roomCode, JObj msg)
        {
            var room = _rooms.Find(roomCode);
            if (room == null || msg == null) return;
            var bytes = msg.Utf8();
            ForEachInRoom(room, c => c.SendPreEncoded(bytes));
        }

        private void SendBlobError(Connection conn, int rq, string code, string msg)
        {
            // 與 SendError 同一個形狀:先說是**哪個請求**,再說原因(見 Hub.SendError 的說明)。
            Log("✗ user " + conn.UserId + " 的 " + (string.IsNullOrEmpty(conn.CurMsgType) ? "?" : conn.CurMsgType)
                + (rq != 0 ? "#" + rq : "") + " 傳檔被拒:" + code
                + (string.IsNullOrEmpty(msg) ? "" : " — " + msg)
                + " | " + DescribeConn(conn));
            conn.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.BlobError)
                .Int(NetProto.FieldRequest, rq)
                .Str("code", code ?? "")
                .Str("msg", msg ?? ""));
        }

        private static float Frac(long done, long total)
            => total <= 0 ? 1f : (float)Math.Min(1.0, (double)done / total);
    }
}
