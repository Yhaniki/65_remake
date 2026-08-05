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

        /// <summary>
        /// 這一趟傳輸用的 file 連線。**每一趟都是新的一條** ——
        /// 🔴 <see cref="NetConnection.Close"/> 會把 <c>_closed</c> 永久上鎖(<c>Interlocked.Exchange</c>),
        /// 而 <c>BeginConnect</c> 不會把它解開。所以「留一個實例反覆 Close/BeginConnect」是行不通的:
        /// 第一次 <see cref="Reset"/> 之後那條連線就永遠是 closed,下一幀的 Tick 立刻判定「連線中斷」。
        /// (實機驗證就是這樣抓到的:上傳在 25 毫秒內失敗,而錯誤訊息裡的 LastError 是空的 ——
        ///  因為根本沒有發生過連線錯誤,是我們自己把它關掉的。)
        /// </summary>
        private NetConnection _link;
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

        /// <summary>
        /// 壓縮版的暫存目錄(上傳用)。每個要傳的檔在這裡有一份 <c>&lt;index&gt;.z</c>,
        /// 線路上送的就是它 —— 見 <see cref="PackCompression"/>。
        ///
        /// 為什麼壓成檔案而不是邊傳邊壓:收端要**照長度**切出每個檔的邊界,所以清單送出去的時候
        /// 就得知道每個檔壓完有多長。串流壓縮到最後一刻才知道,那個數字就趕不上清單。
        /// </summary>
        private string _packedDir = "";

        // ---- 下載 ----
        private string _destFolder = "";

        /// <summary>
        /// 這一趟在傳什麼:<c>NetProto.BlobKindSong</c>(預設)或 <c>BlobKindModel</c>。
        ///
        /// 管線本身完全通用(內容尋址、chunk、逐檔驗 hash),只有**三個點**要看它:
        /// 掃資料夾算清單用哪張白名單、算 packId 用哪套規則、以及收檔時重驗路徑用哪張白名單。
        /// 這三個點兩邊不一致的話,症狀是「上傳永遠被 server 拒絕」而錯誤訊息指向路徑或 hash。
        /// </summary>
        private string _kind = NetProto.BlobKindSong;

        public string Kind => _kind;
        private bool IsModel => string.Equals(_kind, NetProto.BlobKindModel, StringComparison.Ordinal);
        private List<PackFileEntry> _incoming;
        private int _inCursor;
        private long _inReceived;            // 目前這個檔已收到幾 bytes
        private FileStream _writeStream;
        private string _writePath = "";
        private long _recvBytes, _totalRecvBytes;

        public NetTransferState State { get; private set; } = NetTransferState.Idle;
        public string Error { get; private set; } = "";
        public string PackId => _packId;

        /// <summary>
        /// 最後一次「真的有進展」的時刻(<c>Time.realtimeSinceStartup * 1000</c>)。
        /// 進展 = 收到對面的任何一個 frame,或成功把一塊 chunk 排進送件匣。見 <see cref="CheckStall"/>。
        /// </summary>
        private float _lastAdvanceMs;

        /// <summary>
        /// 這一趟的失敗**重試有機會成功嗎**(呼叫端用它決定要不要再跑一次)。
        ///
        /// 只有「線路問題」算:停滯逾時(兩邊任一側判的)、連線中斷。
        /// 內容問題(sha256 不符、清單不安全、寫不進磁碟)一律 false —— 重試一百次也是同樣的結果,
        /// 只是把 server 的頻寬與玩家的時間燒掉。
        /// </summary>
        public bool Retryable { get; private set; }

        /// <summary>下載的目的資料夾(匯入時要重新掃描它)。上傳時是空的。</summary>
        public string DestFolder => _destFolder;

        public bool IsBusy => State != NetTransferState.Idle && State != NetTransferState.Done && State != NetTransferState.Failed;
        public bool IsUploading => _uploading;

        /// <summary>
        /// 現在還在用連線嗎。<see cref="NetTransferState.Verifying"/> / <see cref="NetTransferState.Importing"/>
        /// 是純本機的收尾(算 hash、重掃歌庫),那時連線已經關了 —— 所以任何「連線斷了嗎」的判斷
        /// 都必須先問這個,否則會把成功的傳輸判成失敗。
        /// </summary>
        private bool IsNetworkPhase
            => State == NetTransferState.Connecting || State == NetTransferState.Hashing
            || State == NetTransferState.Negotiating || State == NetTransferState.Uploading
            || State == NetTransferState.Downloading;

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
        public void BeginUpload(string host, int port, string sessionKey, string packId, string folder,
                                string kind = NetProto.BlobKindSong)
        {
            Reset();
            _kind = kind ?? NetProto.BlobKindSong;
            _uploading = true;
            _sessionKey = sessionKey ?? "";
            _packId = packId ?? "";
            _srcFolder = folder ?? "";
            State = NetTransferState.Connecting;
            _link = new NetConnection();   // 每趟一條新的(見 _link 的註解)

            // 清單的 hash 先在背景算(一首歌的音檔幾十 MB,主執行緒算會明顯卡一下),
            // **順便把每個檔壓好** —— 反正這一趟已經要把整個資料夾讀過一遍。
            // SongPackScan / ModelPackId / PackCompression 都是純 System.IO,沒有任何 Unity API
            // → 可以安全地在 worker thread 上跑(🔴 所以暫存目錄不能用 Application.temporaryCachePath,
            //   那是 Unity API;路徑要在主執行緒這裡就決定好)。
            var src = _srcFolder;
            bool model = IsModel;
            _packedDir = Path.Combine(Path.GetTempPath(),
                                      "sdo_pack_" + Guid.NewGuid().ToString("N"));
            var packed = _packedDir;
            _hashTask = Task.Run(() =>
            {
                var files = model ? ScanModelFolder(src) : SongPackScan.Enumerate(src, hashEverything: true);
                PackAll(src, files, packed);
                return files;
            });

            // 檔案連線與 control 連線是**同一台 server 的同一個 port** → 加密設定當然要一樣。
            // (漏掉這裡的話 server 開了 TLS,傳檔那條會用明文去撞 TLS 握手,錯誤訊息只有「Eof」。)
            _link.BeginConnect(host, port, 5000,
                               Sdo.Settings.RoomConfig.serverTls,
                               Sdo.Settings.RoomConfig.serverCertFingerprint);
        }

        /// <summary>缺歌的人:把 <paramref name="packId"/> 下載到 <paramref name="destFolder"/>。</summary>
        public void BeginDownload(string host, int port, string sessionKey, string packId, string destFolder,
                                  string kind = NetProto.BlobKindSong)
        {
            Reset();
            _kind = kind ?? NetProto.BlobKindSong;
            _uploading = false;
            _sessionKey = sessionKey ?? "";
            _packId = packId ?? "";
            _destFolder = destFolder ?? "";
            State = NetTransferState.Connecting;
            _link = new NetConnection();   // 每趟一條新的(見 _link 的註解)
            // 檔案連線與 control 連線是**同一台 server 的同一個 port** → 加密設定當然要一樣。
            // (漏掉這裡的話 server 開了 TLS,傳檔那條會用明文去撞 TLS 握手,錯誤訊息只有「Eof」。)
            _link.BeginConnect(host, port, 5000,
                               Sdo.Settings.RoomConfig.serverTls,
                               Sdo.Settings.RoomConfig.serverCertFingerprint);
        }

        /// <summary>模型資料夾 → 上傳清單(每個檔都算 SHA-256)。在 worker thread 上跑,所以只能用純
        /// <c>System.IO</c> —— <see cref="ModelPackId.ScanFolder"/> 正是那樣寫的,零 UnityEngine。</summary>
        private static List<PackFileEntry> ScanModelFolder(string dir)
        {
            List<PackFileEntry> files;
            PackScanStats stats;
            return ModelPackId.ScanFolder(dir, out files, out stats) ? files : new List<PackFileEntry>();
        }

        /// <summary>
        /// 把清單上的每個檔壓一份到 <paramref name="packedDir"/>(檔名就是它在清單裡的 index)。
        /// 在 worker thread 上跑,所以只能碰純 <c>System.IO</c>。
        ///
        /// 壓不動的(已經是 png/jpg/ogg 那類)會**照原樣傳**:壓縮版沒有比較小就不值得多一次
        /// 解壓,而且 <c>CompressedLength = 0</c> 正是「這個檔不壓縮」的表示法。
        /// 長度 0 的檔一塊 chunk 都不會傳(兩端都有對稱的跳過邏輯),更不需要壓。
        /// </summary>
        private static void PackAll(string srcFolder, List<PackFileEntry> files, string packedDir)
        {
            if (files == null || files.Count == 0) return;
            try { Directory.CreateDirectory(packedDir); }
            catch { return; }   // 壓不了就整批照原樣傳(CompressedLength 全是 0)

            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                if (f.Length <= 0) continue;

                var srcPath = Path.Combine(srcFolder, f.RelPath.Replace('/', Path.DirectorySeparatorChar));
                var dstPath = Path.Combine(packedDir, i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                                      + PackCompression.Extension);
                long clen = PackCompression.CompressFile(srcPath, dstPath);
                if (clen <= 0 || clen >= f.Length)
                {
                    // 壓不動 → 丟掉壓縮版,照原檔傳。
                    try { if (File.Exists(dstPath)) File.Delete(dstPath); } catch { }
                    continue;
                }
                f.CompressedLength = clen;
                files[i] = f;   // struct → 要寫回去
            }
        }

        public void Cancel(string why)
        {
            if (!IsBusy) return;
            Fail(string.IsNullOrEmpty(why) ? "取消" : why);
        }

        private void Reset()
        {
            if (_link != null) { _link.Close("restart"); _link = null; }
            CloseStreams();
            State = NetTransferState.Idle;
            Error = "";
            Retryable = false;
            _lastAdvanceMs = Now();
            _manifest = null; _hashTask = null; _need = null; _needCursor = 0;
            _incoming = null; _inCursor = 0; _inReceived = 0;
            _sentBytes = _totalSendBytes = _recvBytes = _totalRecvBytes = 0;
            _writePath = "";
            DropPackedDir();   // 上一趟壓好的那些 .z(不清的話系統暫存區會一直長)
        }

        // ================= 每幀 =================

        public void Tick()
        {
            if (!IsBusy) return;

            // 驗證/匯入階段**已經離開網路**(FinishDownload 收完檔案就把連線關掉了)→ 完全不碰 link。
            if (!IsNetworkPhase) return;

            if (_link == null) { Fail("沒有連線"); return; }

            // 🔴 先把收件匣清乾淨,**再**判斷連線是不是斷了。順序反過來的話,server 用
            // bye{reason} 說明理由之後才關 socket —— 而我們會先看到 socket 關了就直接 Fail("Eof"),
            // 那封已經躺在收件匣裡的 bye 永遠不會被讀到。實機驗證時就吃到這個:
            // client 只說「連線中斷(Eof)」,真正的原因(密碼不符)只有 server 的 log 看得到。
            PumpInbox();
            if (!IsBusy) return;

            // 🔴 這裡**必須再問一次階段**,不能只靠函式開頭那道。PumpInbox 會就地處理
            // blobDownloadDone → FinishDownload → 驗證 → State=Importing 並把連線關掉,
            // 全部發生在**同一次 Tick 之內** → 開頭那道早就過去了,接著這個「連線斷了嗎」
            // 就把剛剛成功的下載判成失敗。實機第六輪就是這樣:log 上一行「下載完成並驗證通過」,
            // 下一行「傳檔失敗:連線中斷:」(而且 LastError 是空的 —— 因為是我們自己關的)。
            if (!IsNetworkPhase) return;

            if (_link.State == NetLinkState.Failed || _link.IsClosed)
            {
                // 線路問題 → 重試有機會成功(見 Retryable)。
                Retryable = true;
                Fail("連線中斷:" + _link.LastError);
                return;
            }

            // 🔴 停滯偵測放在這裡(所有網路階段之前),不是放在 Tick 的最後 ——
            // 下面 Connecting 那一段是 return 收尾的,擺在尾巴的話握手階段就漏掉了。
            CheckStall();
            if (!IsBusy) return;

            if (State == NetTransferState.Connecting)
            {
                if (!_link.IsConnected) return;
                var hello = JObj.New()
                    .Str(NetProto.FieldType, NetProto.Hello)
                    .Int(NetProto.FieldRequest, ++_rq)
                    .Int("proto", NetProto.Version)
                    .Str("role", NetProto.RoleFile)
                    .Str("sessionKey", _sessionKey);
                // 🔴 進站密碼:file 連線也要送。server 對**每一條**連線都檢查密碼(不分角色),
                // 少了它 server 直接 bye,而這邊只看到「連線中斷(Eof)」—— 完全指不到密碼。
                // (實機驗證就是這樣:client 說 Eof,server 說「密碼不符,client 送的是空值」。)
                // 直接讀 RoomConfig 而不是從外面傳進來:控制連線用的也是同一個來源,不可能漂移。
                var pw = Sdo.Settings.RoomConfig.serverPassword;
                if (!string.IsNullOrEmpty(pw)) hello.Str("password", pw);
                // 🔴 token 同理,而且是**同一個坑的第二次**:server 的 token 檢查也在 OnHello 的最前面、
                // 不分角色。少了它 → 公網 server 上傳歌曲一律失敗。
                // (實機驗證抓到的:server「連線 #3 token 認證失敗,拒絕」/ client「傳檔失敗:badToken」,
                //  但畫面上毫無異狀 —— 那次兩台剛好都有那首歌,所以沒人發現歌其實沒傳上去。)
                var tok = Sdo.Settings.RoomConfig.serverToken;
                if (!string.IsNullOrEmpty(tok)) hello.Str("authToken", tok);
                _link.Send(hello);
                State = _uploading ? NetTransferState.Hashing : NetTransferState.Negotiating;
                if (!_uploading) RequestDownload();
                return;
            }

            PumpInbox();
            if (!IsBusy) return;

            KeepAlive();
            if (State == NetTransferState.Hashing) TickHashing();
            else if (State == NetTransferState.Uploading) TickUpload();
        }

        /// <summary>
        /// **停滯逾時。** 進了傳輸階段之後這麼久沒有任何進展就判定卡死並失敗。
        ///
        /// 🔴 為什麼不能只靠「連線斷了嗎」:半開連線(NAT/路由器悄悄丟掉 session、對方主機斷電)
        /// 上的 socket **不會報錯** —— TCP 要重傳好幾分鐘才放棄,而在那之前
        /// <c>_link.IsClosed</c> 是 false、我們的 ping 也「送得出去」(只是沒有人收)。
        /// 結果是這裡永遠停在 <see cref="NetTransferState.Downloading"/>,
        /// <c>NetSongTransfer.ReportProgress</c> 每 500 ms 繼續告訴 server「我在下載」,
        /// 於是那個座位在 server 眼中**永遠**是 downloading:按準備被 R17 拒、房主也開不了場(R12),
        /// 而畫面上進度條停在 99% 看起來就像傳完了。實機 log 上的樣子是
        /// 「下載開始」之後 server 再也沒有印出「下載完成」,而 client 連按三次準備都是 badState。
        ///
        /// 失敗比卡住好:失敗會走 <c>Fail</c> → 刪掉半成品 → 呼叫端重報 missing,
        /// 缺歌徽章回來、玩家看得到 toast,而且還能重試。
        /// </summary>
        private void CheckStall()
        {
            if (!IsNetworkPhase) return;
            float idle = Now() - _lastAdvanceMs;
            if (idle < NetLimits.BlobStallTimeoutMs) return;

            Retryable = true;
            Fail((_uploading ? "上傳" : "下載") + "停滯超過 " + (NetLimits.BlobStallTimeoutMs / 1000)
                 + " 秒(對方沒有回應)—— 階段=" + State
                 + " 已" + (_uploading ? "送 " + _sentBytes + "/" + _totalSendBytes
                                       : "收 " + _recvBytes + "/" + _totalRecvBytes) + " bytes");
        }

        /// <summary>有進展 → 把停滯計時器歸零。</summary>
        private void Advance() => _lastAdvanceMs = Now();

        private static float Now() => Time.realtimeSinceStartup * 1000f;

        /// <summary>
        /// 定期 ping,讓 server 知道這條 file 連線還活著。
        ///
        /// 🔴 非做不可,而且**下載方向才是重點**:server 的 <c>SweepDeadConnections</c> 看的是
        /// 「多久沒**收到**這條連線的東西」(<see cref="NetLimits.PingTimeoutMs"/> 15 秒)。
        /// 下載時我們送出 blobDownloadBegin 之後就一路只收不送 → 15 秒後 server 判定斷線把它砍掉,
        /// 下載永遠走不完。localhost 上一首 5 MB 的歌 150 毫秒就傳完,所以**測不出來**;
        /// 一首 40 MB 的歌走真的網路一定會踩到。
        /// (上傳方向剛好一直有 chunk 進去,所以不會被砍 —— 但還是一起 ping,不要靠這種巧合。)
        /// </summary>
        private void KeepAlive()
        {
            float now = Time.realtimeSinceStartup * 1000f;
            if (now - _lastPingMs < NetLimits.PingIntervalMs) return;
            _lastPingMs = now;
            _link.Send(JObj.New().Str(NetProto.FieldType, NetProto.Ping).Num("t0", now));
        }

        private float _lastPingMs;

        /// <summary>hash 算完就把清單送出去。</summary>
        private void TickHashing()
        {
            if (_hashTask == null) { Fail("沒有檔案清單"); return; }
            if (!_hashTask.IsCompleted)
            {
                // 算 hash 是純本機的 CPU 工作(在背景執行緒上),與「對面還在不在」無關。
                // 不歸零計時器的話,一首很大的歌在慢磁碟上會被停滯逾時誤殺。
                Advance();
                return;
            }

            if (_hashTask.IsFaulted) { Fail("算檔案清單失敗"); return; }
            _manifest = _hashTask.Result;
            _hashTask = null;

            if (_manifest == null || _manifest.Count == 0) { Fail("這個歌曲資料夾沒有可以傳的檔案"); return; }

            // 自己先驗一次:重算的 packId 與房間宣稱的一致嗎?
            // 不一致代表資料夾在選歌之後被動過(或掃描快取過時)—— server 也會擋(那是它的職責),
            // 但在這裡擋掉可以省下整趟上傳,而且錯誤訊息指得到真正的原因。
            string recomputed = IsModel ? ModelPackId.Compute(_manifest) : SongPackId.Compute(_manifest);
            if (!string.Equals(recomputed, _packId, StringComparison.Ordinal))
            {
                Fail(IsModel ? "模型資料夾的內容與宣告的不一致" : "歌曲資料夾的內容與選歌時不一致");
                return;
            }

            var arr = JArr.New();
            for (int i = 0; i < _manifest.Count; i++)
            {
                var o = JObj.New()
                    .Str("path", _manifest[i].RelPath)
                    .Long("len", _manifest[i].Length)
                    .Str("sha256", _manifest[i].Sha256 ?? "");
                // 壓得動的才帶 clen —— 沒帶就是「照原始長度傳」(見 NetProto.FieldCompressedLength)。
                if (_manifest[i].IsCompressed)
                    o.Long(NetProto.FieldCompressedLength, _manifest[i].CompressedLength);
                arr.Add(o);
            }

            _link.Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.BlobUploadBegin)
                .Int(NetProto.FieldRequest, ++_rq)
                .Str("packId", _packId)
                .Str(NetProto.FieldBlobKind, _kind)
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
                // 排進送件匣就算進展。**排不進去(佇列滿了)就不算** —— 那正是
                // 「對面不收了、寫執行緒卡在 socket 上」的樣子,停滯逾時要抓的就是它。
                Advance();
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

            // 壓得動的送壓縮版(PackAll 已經壓在暫存目錄裡,檔名就是 index),其餘照原檔送。
            // 🔴 這個判斷要與清單裡送出去的 clen 完全一致 —— 一邊說壓了、一邊送原檔的話,
            //    收端會照 clen 切,切出來的每個檔都從錯的位移開始,而錯誤訊息只會說「內容與 sha256 不符」。
            string full = _manifest[idx].IsCompressed
                ? Path.Combine(_packedDir, idx.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                           + PackCompression.Extension)
                : Path.Combine(_srcFolder, _manifest[idx].RelPath.Replace('/', Path.DirectorySeparatorChar));
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
                    Advance();
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
            // 談判階段(等 manifest / 等 blobUploadDone)沒有 chunk 可以當「還活著」的訊號,
            // 所以任何一則**協定訊息**都算進展。
            //
            // 🔴 但 pong 不算 —— 它是我們自己的 ping 打回來的,一定會準時到,拿它當進展的話
            // 停滯逾時就永遠不會觸發(而「連線通、但檔案不再流動」正是要抓的那一種卡死)。
            // pong 沒有出現在下面的 switch 裡,所以這裡不特判就自然排除了。
            switch (type)
            {
                case NetProto.Welcome:
                    Advance();
                    break;      // file 連線認親成功;真正的請求在 Tick 裡發

                case NetProto.BlobUploadAccept:
                    Advance();
                    OnUploadAccept(node);
                    break;

                case NetProto.BlobUploadDone:
                    State = NetTransferState.Done;
                    // 完成就把 file 連線收掉。留著不關的話它一路不再收發任何東西,
                    // 15 秒後被 server 的 ping 掃描當成斷線(server log 會出現「ping 逾時」),
                    // 而且那段時間白佔一條連線額度。實機第六輪的 log 就有兩行那個。
                    if (_link != null) { _link.Close("uploadDone"); _link = null; }
                    break;

                case NetProto.BlobManifest:
                    Advance();
                    OnManifest(node);
                    break;

                case NetProto.BlobDownloadDone:
                    FinishDownload();
                    break;

                case NetProto.BlobError:
                {
                    // server 那一側先判了停滯(NetLimits.BlobStallTimeoutServerMs)—— 那是線路問題,可以重試。
                    string code = NetJson.Str(node, "code");
                    if (string.Equals(code, NetProto.BlobErrStalled, StringComparison.Ordinal)) Retryable = true;
                    Fail("server 拒絕(" + code + "):" + NetJson.Str(node, "msg"));
                    break;
                }

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
                _totalSendBytes += _manifest[idx].WireLength;   // 進度條要反映線路上真正要送的量
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
                // >0 = 這個檔壓縮傳,照這個長度收(見 NetProto.FieldCompressedLength)。
                // 🔴 不能因為「不比原始小」就自作主張當成不壓縮 —— 送端是照它切的,
                // 判斷不一致的話後面每個檔都錯位,而症狀只會是「內容與 sha256 不符」。
                long clen = NetJson.Long(arr[i], NetProto.FieldCompressedLength);
                if (clen < 0) clen = 0;

                // 🔴 server 給的路徑也要驗 —— 這條路徑會直接變成本機的檔案名稱。
                // 我們信任自己人開的 server,但「信任」不是「把寫檔位置交給對面決定」。
                // 🔴 收端自己再驗一次,而且要用**這一趟的** kind 那張白名單。server 已經驗過了,
                // 但那是 server 的職責 —— 我們要往自己的磁碟上寫檔,不能把「對方一定是照規矩來的」
                // 當成前提(而且一個被入侵的 server 正是這條防線存在的理由)。
                bool allowed = IsModel ? ModelPackFilter.IsTransferable(rel, len)
                                       : SongPackFilter.IsTransferable(rel, len);
                if (!SafeRelPath.IsSafe(rel) || !allowed)
                {
                    Fail("server 給的檔案清單不安全:" + rel);
                    return;
                }
                _incoming.Add(new PackFileEntry(rel, len, sha, clen));
                _totalRecvBytes += clen > 0 ? clen : len;   // 進度條看線路上真正要收的量
            }

            if (_incoming.Count == 0) { Fail("server 給的清單是空的"); return; }

            long limitBytes = (long)Mathf.Max(1, Sdo.Settings.RoomConfig.netMaxDownloadMb) * 1024L * 1024L;
            if (_totalRecvBytes > limitBytes)
            {
                Fail("這首歌超過下載上限(" + (_totalRecvBytes / (1024 * 1024)) + " MB)");
                return;
            }

            // 🔴 **解壓後**的總量也要有上限。上面那道看的是線路上的量,而 deflate 的壓縮比可以到 1000:1 ——
            // 一個被入侵的 server 可以用 40 MB 的合法流量把收端的磁碟塞爆(zip bomb),
            // 而每一個檔的 sha256 都要等寫完才驗得出來,那時磁碟早就滿了。
            long rawTotal = 0;
            for (int i = 0; i < _incoming.Count; i++) rawTotal += _incoming[i].Length;
            long rawCap = IsModel ? ModelPackId.MaxPackBytes : NetLimits.DefaultMaxBlobBytes;
            if (rawTotal > rawCap)
            {
                Fail("解壓後的總量超過上限(" + (rawTotal / (1024 * 1024)) + " MB)");
                return;
            }

            try { Directory.CreateDirectory(_destFolder); }
            catch (Exception ex) { Fail("建不了資料夾:" + ex.Message); return; }

            _inCursor = 0;
            _inReceived = 0;
            _recvBytes = 0;
            State = NetTransferState.Downloading;
            SkipEmptyIncoming();   // 空檔案可能排在最前面 —— 等第一塊 chunk 才處理就太晚了
            if (!IsBusy) return;
            Debug.Log("[net] 開始下載 " + _incoming.Count + " 個檔、" + (_totalRecvBytes / 1024) + " KB → " + _destFolder);
        }

        private void WriteIncoming(byte[] payload)
        {
            if (_incoming == null || _inCursor >= _incoming.Count) return;   // 多餘的 chunk:忽略
            if (_writeStream == null && !OpenNextDownloadFile()) return;

            var f = _incoming[_inCursor];
            // 🔴 收滿的判準是**線路長度**(壓縮的話就是壓縮後的長度),不是原始長度 ——
            //    照原始長度收的話,壓縮檔會在收到原始長度那一刻被當成收完,後面每個檔全部錯位。
            long wire = f.WireLength;
            long remain = wire - _inReceived;
            int n = (int)Math.Min(payload.Length, remain);

            try { _writeStream.Write(payload, 0, n); }
            catch (Exception ex) { Fail("寫不進檔案:" + ex.Message); return; }

            _inReceived += n;
            _recvBytes += n;

            if (_inReceived < wire) return;

            CloseStreams();
            if (f.IsCompressed && !Inflate(f)) return;   // .z 收滿了 → 就地解壓成正式檔
            _inCursor++;
            _inReceived = 0;
            SkipEmptyIncoming();
        }

        /// <summary>
        /// 把清單上「長度 0」的檔案就地建好並跳過。
        ///
        /// 🔴 送端是照長度切 chunk 的 → 空檔案**一塊 chunk 都不會來**。不主動跳過的話 _inCursor
        /// 會永遠停在那一格,而後面的 chunk 會被寫進**這個**檔案(位移全錯),最後
        /// blobDownloadDone 一到就是「檔案沒收完」。歌曲資料夾裡有個空的 .ini 就會踩到。
        /// (server 端有對稱的處理 —— 見 Hub.Blobs.cs 的 OpenNextUploadFile。)
        /// </summary>
        private void SkipEmptyIncoming()
        {
            if (_incoming == null) return;
            while (_inCursor < _incoming.Count && _incoming[_inCursor].Length == 0)
            {
                var rel = _incoming[_inCursor].RelPath.Replace('/', Path.DirectorySeparatorChar);
                var full = Path.Combine(_destFolder, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    using (File.Create(full)) { }        // 0 byte 檔案,內容就是「沒有內容」
                }
                catch (Exception ex) { Fail("建不了空檔案 " + rel + ":" + ex.Message); return; }
                _inCursor++;
                _inReceived = 0;
            }
        }

        private bool OpenNextDownloadFile()
        {
            if (_inCursor >= _incoming.Count) return false;
            var f = _incoming[_inCursor];
            var rel = f.RelPath.Replace('/', Path.DirectorySeparatorChar);
            // 壓縮的先落在 <正式檔名>.z,收滿再就地解壓(見 Inflate)。這樣正式檔名在解壓成功之前
            // 根本不存在 —— 半個檔不會被誤認成「已經有這個檔了」。
            _writePath = Path.Combine(_destFolder, rel) + (f.IsCompressed ? PackCompression.Extension : "");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_writePath));
                _writeStream = new FileStream(_writePath, FileMode.Create, FileAccess.Write, FileShare.None);
            }
            catch (Exception ex) { Fail("建不了檔案 " + rel + ":" + ex.Message); return false; }
            return true;
        }

        /// <summary>
        /// 剛收滿的那個 <c>.z</c> → 解壓成正式檔,然後把 <c>.z</c> 刪掉。
        ///
        /// 解壓失敗就是硬失敗:內容已經壞了,繼續收下去只會讓 <see cref="FinishDownload"/> 的 sha256
        /// 比對指向這個檔,而真正的原因(壓縮流壞掉)只有這裡看得到。
        /// </summary>
        private bool Inflate(PackFileEntry f)
        {
            var rel = f.RelPath.Replace('/', Path.DirectorySeparatorChar);
            var outPath = Path.Combine(_destFolder, rel);
            var zPath = outPath + PackCompression.Extension;

            if (!PackCompression.DecompressFile(zPath, outPath))
            {
                TryDeleteFile(zPath);
                Fail("解壓失敗:" + f.RelPath);
                return false;
            }
            TryDeleteFile(zPath);
            return true;
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
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
            if (_link != null) _link.Close("done");
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
            if (_link != null) _link.Close("failed");
            // 🔴 失敗的下載要把半成品資料夾刪掉。留著的話下一次歌庫掃描會把它當成一首**正常的歌**
            // 收進目錄(掃描器只看有沒有譜面檔,不知道那份譜只寫了一半)→ 玩家選到它就是壞譜,
            // 而那時完全看不出跟「某次下載失敗」有關。上傳失敗不用清(我們沒在本機寫任何東西)。
            if (!_uploading) DiscardPartialDownload();
            Debug.LogWarning("[net] 傳檔失敗:" + Error);
        }

        /// <summary>把下載到一半的資料夾整個刪掉(見 <see cref="Fail"/> 的註解)。</summary>
        private void DiscardPartialDownload()
        {
            if (string.IsNullOrEmpty(_destFolder)) return;
            try { if (Directory.Exists(_destFolder)) Directory.Delete(_destFolder, true); }
            catch (Exception ex) { Debug.LogWarning("[net] 清不掉半成品資料夾 " + _destFolder + ":" + ex.Message); }
        }

        private void CloseStreams()
        {
            if (_readStream != null) { try { _readStream.Dispose(); } catch { } _readStream = null; }
            if (_writeStream != null) { try { _writeStream.Dispose(); } catch { } _writeStream = null; }
        }

        public void Dispose()
        {
            CloseStreams();
            DropPackedDir();
            if (_link != null) { _link.Close("dispose"); _link = null; }
        }

        /// <summary>丟掉這一趟壓好的那些 <c>.z</c>(整個暫存目錄)。</summary>
        private void DropPackedDir()
        {
            if (string.IsNullOrEmpty(_packedDir)) return;
            var d = _packedDir;
            _packedDir = "";
            // 🔴 背景那個壓縮 task 可能還在寫這個目錄(取消/失敗會走到這裡)——
            // 刪不掉就算了,系統暫存區本來就會被清,總比為了刪它去等一個可能永遠不回來的 task 好。
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
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
