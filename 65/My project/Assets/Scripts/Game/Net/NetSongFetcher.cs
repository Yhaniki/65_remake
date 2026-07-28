using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Sdo.Net;
using Sdo.Osu;
using UnityEngine;

namespace Sdo.Game.Net
{
    /// <summary>傳輸走到哪了。</summary>
    public enum NetTransferState
    {
        Idle = 0,
        /// <summary>正在連 file 連線 / 握手。</summary>
        Connecting,
        /// <summary>正在算檔案清單的 SHA-256(上傳前,在背景執行緒上)。</summary>
        Hashing,
        /// <summary>清單已送出,等 server 回「還缺哪些」/ 回 manifest。</summary>
        Negotiating,
        Uploading,
        Downloading,
        /// <summary>檔案都收完了,正在逐檔比對 SHA-256。</summary>
        Verifying,
        /// <summary>驗過了,等歌庫重新掃描把它接進來。</summary>
        Importing,
        Done,
        Failed,
    }

    /// <summary>
    /// 缺歌傳檔的 client 端(M5)。一個實例做一件事:上傳一首歌,或下載一首歌。
    ///
    /// ★ 走**第二條 TCP 連線**(<c>role=file</c>,用 control 連線發的 sessionKey 認親)。
    ///   理由很實際:一首歌幾十 MB 會排在房間訊息前面,整個房間在傳檔期間看起來像卡住 ——
    ///   聊天不會出現、別人準備了看不到、頭貼狀態全部延遲。
    ///
    /// ★ **全部在主執行緒上跑**(靠 <see cref="Tick"/>),只有算 hash 那一段丟到背景。
    ///   chunk 是 64 KiB,寫一塊約 0.1 ms;每幀處理幾十塊也還在預算內。
    ///   換成背景執行緒的話要處理「掃描/匯入與寫檔同時發生」,而那種 bug 的症狀是
    ///   「偶爾下載完的歌是壞的」——查不到。這裡不值得。
    ///
    /// 🔴 收到的每個檔案都要**自己重算 SHA-256** 比對 manifest。server 也驗過,但那是 server 的信任問題;
    ///   這一邊要防的是「傳輸中壞掉」與「server 上的檔案被換掉」。譜面對不上的症狀是
    ///   「音符跟音樂差半拍」,絕對不能靜默通過。
    /// </summary>
    public sealed class NetSongFetcher
    {
        /// <summary>outbound 佇列補到這個水位就停 —— 一次全塞會超過 <c>NetConnection</c> 的容量。</summary>
        private const int UploadHighWater = 32;

        /// <summary>一幀最多處理幾塊收到的 chunk(2 MiB)。避免大歌在單幀寫爆造成掉幀。</summary>
        private const int MaxChunksPerTick = 32;

        private readonly NetConnection _link = new NetConnection();
        private bool _uploading;
        private string _sessionKey = "";
        private string _packId = "";
        private int _rq = 900;

        // ---- 上傳 ----
        private string _srcFolder = "";
        private List<PackFileEntry> _manifest;
        private Task<List<PackFileEntry>> _hashTask;
        private List<int> _need;
        private int _needCursor;
        private FileStream _readStream;
        private long _sentBytes, _totalSendBytes;

        // ---- 下載 ----
        private string _destFolder = "";
        private List<PackFileEntry> _incoming;
        private int _inCursor;
        private long _inReceived;            // 目前這個檔已收到幾 bytes
        private FileStream _writeStream;
        private string _writePath = "";
        private long _recvBytes, _totalRecvBytes;

        public NetTransferState State { get; private set; } = NetTransferState.Idle;
        public string Error { get; private set; } = "";
        public string PackId => _packId;

        /// <summary>下載的目的資料夾(匯入時要重新掃描它)。上傳時是空的。</summary>
        public string DestFolder => _destFolder;

        public bool IsBusy => State != NetTransferState.Idle && State != NetTransferState.Done && State != NetTransferState.Failed;
        public bool IsUploading => _uploading;

        /// <summary>0..1。談判階段回 0,完成回 1。</summary>
        public float Progress
        {
            get
            {
                switch (State)
                {
                    case NetTransferState.Uploading:
                        return _totalSendBytes <= 0 ? 1f : Mathf.Clamp01((float)((double)_sentBytes / _totalSendBytes));
                    case NetTransferState.Downloading:
                        return _totalRecvBytes <= 0 ? 0f : Mathf.Clamp01((float)((double)_recvBytes / _totalRecvBytes));
                    case NetTransferState.Verifying:
                    case NetTransferState.Importing:
                        return 1f;
                    case NetTransferState.Done:
                        return 1f;
                    default:
                        return 0f;
                }
            }
        }

        // ================= 啟動 =================

        /// <summary>房主:把 <paramref name="folder"/> 這個歌曲資料夾傳上去。</summary>
        public void BeginUpload(string host, int port, string sessionKey, string packId, string folder)
        {
            Reset();
            _uploading = true;
            _sessionKey = sessionKey ?? "";
            _packId = packId ?? "";
            _srcFolder = folder ?? "";
            State = NetTransferState.Connecting;

            // 清單的 hash 先在背景算(一首歌的音檔幾十 MB,主執行緒算會明顯卡一下)。
            // SongPackScan 是純 System.IO,沒有任何 Unity API → 可以安全地在 worker thread 上跑。
            var src = _srcFolder;
            _hashTask = Task.Run(() => SongPackScan.Enumerate(src, hashEverything: true));

            _link.BeginConnect(host, port);
        }

        /// <summary>缺歌的人:把 <paramref name="packId"/> 下載到 <paramref name="destFolder"/>。</summary>
        public void BeginDownload(string host, int port, string sessionKey, string packId, string destFolder)
        {
            Reset();
            _uploading = false;
            _sessionKey = sessionKey ?? "";
            _packId = packId ?? "";
            _destFolder = destFolder ?? "";
            State = NetTransferState.Connecting;
            _link.BeginConnect(host, port);
        }

        public void Cancel(string why)
        {
            if (!IsBusy) return;
            Fail(string.IsNullOrEmpty(why) ? "取消" : why);
        }

        private void Reset()
        {
            _link.Close("restart");
            CloseStreams();
            State = NetTransferState.Idle;
            Error = "";
            _manifest = null; _hashTask = null; _need = null; _needCursor = 0;
            _incoming = null; _inCursor = 0; _inReceived = 0;
            _sentBytes = _totalSendBytes = _recvBytes = _totalRecvBytes = 0;
            _writePath = "";
        }

        // ================= 每幀 =================

        public void Tick()
        {
            if (!IsBusy) return;

            if (_link.State == NetLinkState.Failed || _link.IsClosed)
            {
                Fail("連線中斷:" + _link.LastError);
                return;
            }

            if (State == NetTransferState.Connecting)
            {
                if (!_link.IsConnected) return;
                _link.Send(JObj.New()
                    .Str(NetProto.FieldType, NetProto.Hello)
                    .Int(NetProto.FieldRequest, ++_rq)
                    .Int("proto", NetProto.Version)
                    .Str("role", NetProto.RoleFile)
                    .Str("sessionKey", _sessionKey));
                State = _uploading ? NetTransferState.Hashing : NetTransferState.Negotiating;
                if (!_uploading) RequestDownload();
                return;
            }

            PumpInbox();
            if (!IsBusy) return;

            if (State == NetTransferState.Hashing) TickHashing();
            else if (State == NetTransferState.Uploading) TickUpload();
        }

        /// <summary>hash 算完就把清單送出去。</summary>
        private void TickHashing()
        {
            if (_hashTask == null) { Fail("沒有檔案清單"); return; }
            if (!_hashTask.IsCompleted) return;

            if (_hashTask.IsFaulted) { Fail("算檔案清單失敗"); return; }
            _manifest = _hashTask.Result;
            _hashTask = null;

            if (_manifest == null || _manifest.Count == 0) { Fail("這個歌曲資料夾沒有可以傳的檔案"); return; }

            // 自己先驗一次:重算的 packId 與房間宣稱的一致嗎?
            // 不一致代表資料夾在選歌之後被動過(或掃描快取過時)—— server 也會擋(那是它的職責),
            // 但在這裡擋掉可以省下整趟上傳,而且錯誤訊息指得到真正的原因。
            string recomputed = SongPackId.Compute(_manifest);
            if (!string.Equals(recomputed, _packId, StringComparison.Ordinal))
            {
                Fail("歌曲資料夾的內容與選歌時不一致");
                return;
            }

            var arr = JArr.New();
            for (int i = 0; i < _manifest.Count; i++)
                arr.Add(JObj.New()
                    .Str("path", _manifest[i].RelPath)
                    .Long("len", _manifest[i].Length)
                    .Str("sha256", _manifest[i].Sha256 ?? ""));

            _link.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.BlobUploadBegin)
                .Int(NetProto.FieldRequest, ++_rq)
                .Str("packId", _packId)
                .Put("files", arr));
            State = NetTransferState.Negotiating;
        }

        private void RequestDownload()
        {
            _link.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.BlobDownloadBegin)
                .Int(NetProto.FieldRequest, ++_rq)
                .Str("packId", _packId));
        }

        /// <summary>把還沒送的位元組塞進佇列,塞到水位為止(流量控制)。</summary>
        private void TickUpload()
        {
            var buf = new byte[NetLimits.BlobChunkBytes];
            while (_link.PendingOutbound < UploadHighWater)
            {
                if (_readStream == null && !OpenNextUploadFile()) break;

                int n;
                try { n = _readStream.Read(buf, 0, buf.Length); }
                catch (Exception ex) { Fail("讀不到檔案:" + ex.Message); return; }

                if (n <= 0)
                {
                    CloseStreams();
                    _needCursor++;
                    continue;
                }

                if (!_link.TrySendChunk(buf, 0, n))
                {
                    // 佇列滿了 → 這一塊沒送出去。把讀取位置退回去,下一幀再送同一塊。
                    try { _readStream.Position -= n; } catch { Fail("退不回讀取位置"); }
                    return;
                }
                _sentBytes += n;
            }

            if (_readStream == null && _need != null && _needCursor >= _need.Count)
            {
                _link.Send(JObj.New()
                    .Str(NetProto.FieldType, NetProto.BlobUploadDone)
                    .Int(NetProto.FieldRequest, ++_rq)
                    .Str("packId", _packId));
                State = NetTransferState.Negotiating;   // 等 server 回 blobUploadDone
            }
        }

        private bool OpenNextUploadFile()
        {
            CloseStreams();
            if (_need == null || _needCursor >= _need.Count) return false;

            int idx = _need[_needCursor];
            if (idx < 0 || idx >= _manifest.Count) { Fail("server 要的檔案編號超出範圍"); return false; }

            var full = Path.Combine(_srcFolder, _manifest[idx].RelPath.Replace('/', Path.DirectorySeparatorChar));
            try { _readStream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
            catch (Exception ex) { Fail("開不了 " + _manifest[idx].RelPath + ":" + ex.Message); return false; }
            return true;
        }

        // ================= 收訊息 / 收 chunk =================

        private void PumpInbox()
        {
            int chunks = 0;
            byte kind;
            byte[] payload;
            while (chunks < MaxChunksPerTick && _link.Poll(out kind, out payload))
            {
                if (kind == NetLimits.FrameKindChunk)
                {
                    chunks++;
                    WriteIncoming(payload);
                    if (!IsBusy) return;
                    continue;
                }

                object node;
                string type;
                if (!NetJson.TryParseMessage(payload, 0, payload.Length, out node, out type)) continue;
                HandleMessage(type, node);
                if (!IsBusy) return;
            }
        }

        private void HandleMessage(string type, object node)
        {
            switch (type)
            {
                case NetProto.Welcome:
                    break;      // file 連線認親成功;真正的請求在 Tick 裡發

                case NetProto.BlobUploadAccept:
                    OnUploadAccept(node);
                    break;

                case NetProto.BlobUploadDone:
                    State = NetTransferState.Done;
                    break;

                case NetProto.BlobManifest:
                    OnManifest(node);
                    break;

                case NetProto.BlobDownloadDone:
                    FinishDownload();
                    break;

                case NetProto.BlobError:
                    Fail("server 拒絕(" + NetJson.Str(node, "code") + "):" + NetJson.Str(node, "msg"));
                    break;

                case NetProto.Bye:
                    Fail("server 關閉連線:" + NetJson.Str(node, "reason"));
                    break;
            }
        }

        private void OnUploadAccept(object node)
        {
            var arr = NetJson.Arr(node, "need");
            _need = new List<int>(arr.Count);
            _totalSendBytes = 0;
            for (int i = 0; i < arr.Count; i++)
            {
                int idx = (int)ToLong(arr[i]);
                if (idx < 0 || idx >= _manifest.Count) continue;
                _need.Add(idx);
                _totalSendBytes += _manifest[idx].Length;
            }
            _needCursor = 0;
            _sentBytes = 0;

            if (_need.Count == 0)
            {
                // server 已經有全部檔案(同一首歌第二次有人玩)→ 它會直接回 blobUploadDone。
                Debug.Log("[net] server 已經有這首歌的所有檔案,不用上傳");
                State = NetTransferState.Negotiating;
                return;
            }
            Debug.Log("[net] 開始上傳 " + _need.Count + " 個檔、" + (_totalSendBytes / 1024) + " KB");
            State = NetTransferState.Uploading;
        }

        private void OnManifest(object node)
        {
            var arr = NetJson.Arr(node, "files");
            _incoming = new List<PackFileEntry>(arr.Count);
            _totalRecvBytes = 0;
            for (int i = 0; i < arr.Count; i++)
            {
                string rel = SafeRelPath.Normalize(NetJson.Str(arr[i], "path"));
                long len = NetJson.Long(arr[i], "len");
                string sha = NetJson.Str(arr[i], "sha256");

                // 🔴 server 給的路徑也要驗 —— 這條路徑會直接變成本機的檔案名稱。
                // 我們信任自己人開的 server,但「信任」不是「把寫檔位置交給對面決定」。
                if (!SafeRelPath.IsSafe(rel) || !SongPackFilter.IsTransferable(rel, len))
                {
                    Fail("server 給的檔案清單不安全:" + rel);
                    return;
                }
                _incoming.Add(new PackFileEntry(rel, len, sha));
                _totalRecvBytes += len;
            }

            if (_incoming.Count == 0) { Fail("server 給的清單是空的"); return; }

            long limitBytes = (long)Mathf.Max(1, Sdo.Settings.RoomConfig.netMaxDownloadMb) * 1024L * 1024L;
            if (_totalRecvBytes > limitBytes)
            {
                Fail("這首歌超過下載上限(" + (_totalRecvBytes / (1024 * 1024)) + " MB)");
                return;
            }

            try { Directory.CreateDirectory(_destFolder); }
            catch (Exception ex) { Fail("建不了資料夾:" + ex.Message); return; }

            _inCursor = 0;
            _inReceived = 0;
            _recvBytes = 0;
            State = NetTransferState.Downloading;
            Debug.Log("[net] 開始下載 " + _incoming.Count + " 個檔、" + (_totalRecvBytes / 1024) + " KB → " + _destFolder);
        }

        private void WriteIncoming(byte[] payload)
        {
            if (_incoming == null || _inCursor >= _incoming.Count) return;   // 多餘的 chunk:忽略
            if (_writeStream == null && !OpenNextDownloadFile()) return;

            var f = _incoming[_inCursor];
            long remain = f.Length - _inReceived;
            int n = (int)Math.Min(payload.Length, remain);

            try { _writeStream.Write(payload, 0, n); }
            catch (Exception ex) { Fail("寫不進檔案:" + ex.Message); return; }

            _inReceived += n;
            _recvBytes += n;

            if (_inReceived < f.Length) return;

            CloseStreams();
            _inCursor++;
            _inReceived = 0;
        }

        private bool OpenNextDownloadFile()
        {
            if (_inCursor >= _incoming.Count) return false;
            var rel = _incoming[_inCursor].RelPath.Replace('/', Path.DirectorySeparatorChar);
            _writePath = Path.Combine(_destFolder, rel);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_writePath));
                _writeStream = new FileStream(_writePath, FileMode.Create, FileAccess.Write, FileShare.None);
            }
            catch (Exception ex) { Fail("建不了檔案 " + rel + ":" + ex.Message); return false; }
            return true;
        }

        /// <summary>chunk 收完了 → 逐檔比對 SHA-256。</summary>
        private void FinishDownload()
        {
            CloseStreams();
            State = NetTransferState.Verifying;

            if (_incoming == null) { Fail("沒有清單"); return; }
            if (_inCursor < _incoming.Count)
            {
                Fail("檔案沒收完(" + _inCursor + "/" + _incoming.Count + ")");
                return;
            }

            for (int i = 0; i < _incoming.Count; i++)
            {
                var f = _incoming[i];
                var full = Path.Combine(_destFolder, f.RelPath.Replace('/', Path.DirectorySeparatorChar));
                string actual = SongPackId.HashFile(full);
                if (string.Equals(actual, (f.Sha256 ?? "").ToLowerInvariant(), StringComparison.Ordinal)) continue;

                Fail("下載的檔案內容不符:" + f.RelPath);
                return;
            }

            Debug.Log("[net] 下載完成並驗證通過:" + _packId);
            State = NetTransferState.Importing;      // 等呼叫端跑歌庫重新掃描
            _link.Close("done");
        }

        /// <summary>呼叫端(重新掃描完)通知匯入結束。</summary>
        public void MarkImported(bool ok, string error = "")
        {
            if (State != NetTransferState.Importing) return;
            if (ok) State = NetTransferState.Done;
            else Fail(string.IsNullOrEmpty(error) ? "歌庫接不進來" : error);
        }

        // ================= 收尾 =================

        private void Fail(string why)
        {
            Error = why ?? "";
            State = NetTransferState.Failed;
            CloseStreams();
            _link.Close("failed");
            Debug.LogWarning("[net] 傳檔失敗:" + Error);
        }

        private void CloseStreams()
        {
            if (_readStream != null) { try { _readStream.Dispose(); } catch { } _readStream = null; }
            if (_writeStream != null) { try { _writeStream.Dispose(); } catch { } _writeStream = null; }
        }

        public void Dispose()
        {
            CloseStreams();
            _link.Close("dispose");
        }

        private static long ToLong(object o)
        {
            if (o is long) return (long)o;
            if (o is double) return (long)(double)o;
            if (o is int) return (int)o;
            return 0;
        }

        // ================= 純函式:下載目的資料夾名 =================

        /// <summary>
        /// 下載來的歌要放進 <c>ADDON/SONG/connect/&lt;這個名字&gt;/</c>。
        ///
        /// 用「歌名 - 作者 [packId 前 8 碼]」而不是原本的資料夾名:
        ///   • 原本的資料夾名根本沒在協定裡傳(manifest 只有相對路徑)—— 要傳就得為了一個顯示用的
        ///     字串多加一個欄位,而歌名/作者本來就已經在 <c>NetSongRef</c> 裡了;
        ///   • **一律**加上 packId 前 8 碼 → 撞名問題直接消失(同名不同版本的歌會落在不同資料夾),
        ///     而且看到資料夾就知道它是從哪一份包下載來的。
        ///
        /// 資料夾名不影響 packId(它只看相對路徑與內容),所以這個選擇不會讓兩台算出不同的身分。
        /// </summary>
        public static string ConnectFolderName(string title, string artist, string packId)
        {
            var sb = new StringBuilder(64);
            Append(sb, title);
            if (!string.IsNullOrEmpty(artist))
            {
                if (sb.Length > 0) sb.Append(" - ");
                Append(sb, artist);
            }
            if (sb.Length == 0) sb.Append("song");
            if (sb.Length > 60) sb.Length = 60;

            // 結尾的空白/句點在 Windows 上會被靜默去掉 → 「建好的資料夾名字與我要的不一樣」。
            while (sb.Length > 0 && (sb[sb.Length - 1] == ' ' || sb[sb.Length - 1] == '.')) sb.Length--;
            if (sb.Length == 0) sb.Append("song");

            string tag = "unknown";
            if (!string.IsNullOrEmpty(packId))
            {
                string hex = packId.StartsWith(SongPackId.Prefix, StringComparison.Ordinal)
                    ? packId.Substring(SongPackId.Prefix.Length) : packId;
                if (hex.Length >= 8) tag = hex.Substring(0, 8);
            }
            return sb + " [" + tag + "]";
        }

        private static void Append(StringBuilder sb, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 0x20) continue;                                   // 控制字元
                if ("\\/:*?\"<>|".IndexOf(c) >= 0) { sb.Append('_'); continue; }
                sb.Append(c);
            }
        }
    }
}
