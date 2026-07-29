using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Sdo.Game;
using Sdo.Game.Net;
using Sdo.Net;
using Sdo.Settings;
using Sdo.UI.Util;
using UnityEngine;

namespace Sdo.UI.Core
{
    /// <summary>
    /// 缺歌傳檔的**編排**:什麼時候該上傳、什麼時候該下載、下載完怎麼接進歌庫。
    /// 實際搬位元組的是 <see cref="NetSongFetcher"/>。
    ///
    /// 兩條路:
    ///   • **房主**選了一首外部歌 → 傳上去。第二次有人開同一首歌時 server 會回「一個檔都不用傳」,
    ///     所以這裡不需要先問「你有嗎」—— 上傳流程本身就是去重的入口。
    ///   • **缺歌的座位玩家** → 先問 server 有沒有(<c>blobQuery</c>),有才下載。
    ///     🔴 一定要先問:房主的上傳與別人的「我缺歌」幾乎同時發生,直接下載會撲空,
    ///     而撲空一次就把這首歌記成「試過了」的話,那個人會**永久**停在「缺歌」。
    ///     收到 <c>blobAvailable</c>(上傳完成的房內廣播)會再問一次。
    ///   • 旁觀者不自動下載(需求 10)。
    /// </summary>
    public static class NetSongTransfer
    {
        /// <summary>下載來的歌放這個分類 —— 選歌畫面會自然把它列成一個群組。</summary>
        public const string ConnectGroup = "connect";

        private static NetSongFetcher _fx;
        private static NetClient _wired;

        /// <summary>已經處理過的 packId(避免同一首歌一直重試)。換歌就清掉。</summary>
        private static string _handledPack;
        private static bool _queryPending;
        private static bool _serverHasPack;
        private static bool _importing;
        private static float _lastReportMs;
        private static string _queriedPack;
        private static string _transferSongKey;
        private static int _transferGeneration;

        /// <summary>「server 還沒有這首歌」之後隔多久再問一次(見 <see cref="MaybeStart"/> 的節流註解)。</summary>
        private const float QueryRetrySec = 2f;
        private static float _lastQueryAt = -99f;

        /// <summary>userId → (0..1, 是不是上傳)。頭貼下方的跑條讀它。</summary>
        private static readonly Dictionary<int, KeyValuePair<float, bool>> _bars
            = new Dictionary<int, KeyValuePair<float, bool>>();

        /// <summary>本機正在傳檔嗎(房間畫面用來畫自己那格的跑條)。</summary>
        public static bool Active => _fx != null && _fx.IsBusy;

        public static float Progress => _fx != null ? _fx.Progress : 0f;
        public static bool IsUploading => _fx != null && _fx.IsUploading;

        /// <summary>
        /// 這個人的傳檔進度(0..1)。回 false = 沒有在傳。
        ///
        /// 兩個來源:自己的看本機的 fetcher(每幀都是最新的),別人的看 server 轉播的
        /// <c>blobProgress</c>(500 ms 一次)。刻意不用 roomState 的 availProgress 當唯一來源 ——
        /// 上傳中的房主 avail 一直是 have(它當然有這首歌),那個欄位表達不了「它正在上傳」。
        /// </summary>
        public static bool TryProgressOf(AppContext ctx, int userId, out float frac, out bool uploading)
        {
            frac = 0f; uploading = false;
            if (ctx == null || ctx.Net == null) return false;

            if (userId == ctx.Net.UserId && Active)
            {
                frac = _fx.Progress;
                uploading = _fx.IsUploading;
                return true;
            }

            KeyValuePair<float, bool> row;
            if (!_bars.TryGetValue(userId, out row)) return false;
            if (row.Key >= 1f) return false;              // 傳完了就不畫
            frac = row.Key; uploading = row.Value;
            return true;
        }

        /// <summary>每幀呼叫(<c>FrontendApp.Update</c>)。<paramref name="runner"/> 用來跑歌庫重新掃描的 coroutine。</summary>
        public static void Tick(AppContext ctx, MonoBehaviour runner)
        {
            if (ctx == null || ctx.Net == null) return;
            Wire(ctx.Net);

            if (_fx != null)
            {
                _fx.Tick();
                ReportProgress(ctx);

                if (_fx.State == NetTransferState.Importing && !_importing && runner != null)
                {
                    var importingFx = _fx;
                    int importingGeneration = _transferGeneration;
                    string importingPack = importingFx.PackId;
                    string importingSongKey = _transferSongKey;
                    _importing = true;
                    runner.StartCoroutine(ImportCo(ctx, runner, importingFx, importingGeneration,
                                                   importingPack, importingSongKey));
                    return;
                }

                if (_fx.State == NetTransferState.Done)
                {
                    Debug.Log("[net] 傳檔完成:" + _fx.PackId);
                    InvalidateActiveTransfer();
                    NetSongPublisher.ForceReport();          // 立刻重新回報 have/missing
                    NetSongPublisher.ReportAvailability(ctx);
                    return;
                }
                if (_fx.State == NetTransferState.Failed)
                {
                    Toast.Show("歌曲傳輸失敗:" + _fx.Error, 4f);
                    InvalidateActiveTransfer();
                    // 🔴 一定要把「我到底有沒有這首歌」重報一次。下載開始時我們已經告訴 server
                    // downloading 了 —— 失敗之後不重報的話,那個座位會**永遠停在 downloading**:
                    // 按準備被 R17 拒絕(它要求 avail==have)、房主也開不了場(R12),
                    // 而畫面上沒有任何東西說明原因(進度條也不動了)。重報會送出 missing,
                    // 至少狀態是誠實的,而且缺歌徽章會回來。
                    NetSongPublisher.ForceReport();
                    NetSongPublisher.ReportAvailability(ctx);
                    return;
                }
                if (_fx.IsBusy) return;                      // 還在傳,不要開第二件事
            }

            MaybeStart(ctx);
        }

        /// <summary>
        /// 房間換歌了 → 清掉「這首歌處理過了」的記憶。每次房間快照都會呼叫。
        ///
        /// 🔴 判斷「換了嗎」要記**房間上一首是什麼**,不能拿 _handledPack 比:
        /// 沒有在傳檔的人(本來就有歌)_handledPack 永遠是 null → 每一份快照都會被當成換歌,
        /// 於是每份快照都把 _bars 清掉。而下載中的人每 500ms 送一次進度 → 每次都產生新快照 →
        /// 別人的跑條會一直閃掉重來(看起來像進度條壞了)。
        /// </summary>
        public static void OnRoomSong(string packKey)
        {
            if (string.Equals(_roomPack, packKey, StringComparison.Ordinal)) return;   // 還是同一首 → 什麼都不動
            // A transfer/import belongs to the song that started it. Once the room selects another pack, keeping
            // A alive lets its eventual Done/MarkImported path report availability for B.
            InvalidateActiveTransfer();
            _roomPack = packKey;
            _handledPack = null;
            _serverHasPack = false;
            _queryPending = false;
            _queriedPack = null;
            _lastQueryAt = -99f;
            _lastReportMs = 0f;
            _bars.Clear();
        }

        /// <summary>房間上一次快照裡的歌(判斷「換歌了嗎」用,見 <see cref="OnRoomSong"/>)。</summary>
        private static string _roomPack;

        /// <summary>離開房間 / 斷線。</summary>
        public static void Reset()
        {
            InvalidateActiveTransfer();
            _handledPack = null;
            _roomPack = null;
            _serverHasPack = false;
            _queryPending = false;
            _queriedPack = null;
            _lastQueryAt = -99f;
            _lastReportMs = 0f;
            _bars.Clear();
            NetSongPublisher.ForceReport();
        }

        // ================= 決定要做什麼 =================

        private static void MaybeStart(AppContext ctx)
        {
            var net = ctx.Net;
            if (!net.IsConnected || !net.InRoom) return;

            var snap = net.Room;
            var song = snap != null ? snap.Song : null;
            if (song == null || !song.HasSong || song.Official) return;      // 官方歌不用傳
            if (string.IsNullOrEmpty(song.PackId)) return;                   // 沒有跨機器身分 → 傳不了
            if (_handledPack == song.PackId) return;

            if (net.IsHost)
            {
                StartUpload(ctx, song);
                return;
            }

            // 缺歌的座位玩家才下載。旁觀者不自動下載(需求 10)。
            if (net.IsSpectating) return;
            if (!RoomConfig.netAutoDownload) return;

            var me = snap.SeatOf(net.UserId);
            if (me == null) return;
            if (me.Avail == Availability.Have) return;

            if (!_serverHasPack)
            {
                // 🔴 問過而且答案是「沒有」→ 要等一下再問。
                // 少了這個節流的話:房主正在上傳的那幾十秒裡,我們每一幀都送一次 blobQuery
                // (回覆一到就把 _queryPending 清掉了)→ 一秒幾十次,直接撞 server 的 control 限流
                // 32/s(R19)→ **被 server 以 rateLimit 踢下線**。而症狀是「缺歌的人會莫名斷線」,
                // 完全指不到一個查詢迴圈。
                // 房主上傳完會廣播 blobAvailable(OnBlobAvailable),所以正常情況下不必靠輪詢 ——
                // 這個輪詢只是後備(例如我進房時上傳已經完成、又沒收到廣播)。
                if (_queryPending) return;
                float now = Time.realtimeSinceStartup;
                if (now - _lastQueryAt < QueryRetrySec) return;
                _lastQueryAt = now;
                _queryPending = true;
                _queriedPack = song.PackId;
                net.SendBlobQuery(song.PackId);
                return;
            }
            StartDownload(ctx, song);
        }

        private static void StartUpload(AppContext ctx, NetSongRef song)
        {
            var folder = ctx.Session != null ? ctx.Session.ExternalFolderPath : null;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                // 房主選的歌不在本機?那是選歌畫面的問題,不是傳檔的問題 —— 記成處理過,不要每幀重試。
                _handledPack = song.PackId;
                return;
            }

            _handledPack = song.PackId;
            var fx = new NetSongFetcher();
            fx.BeginUpload(RoomConfig.serverAddress, RoomConfig.serverPort, ctx.Net.SessionKey, song.PackId, folder);
            ActivateTransfer(fx, song.SongKey);
            Debug.Log("[net] 開始上傳歌曲 " + song.Title + "(" + song.PackId + ")");
        }

        private static void StartDownload(AppContext ctx, NetSongRef song)
        {
            var dest = Path.Combine(SdoExtracted.SongsDir, ConnectGroup,
                                    NetSongFetcher.ConnectFolderName(song.Title, song.Artist, song.PackId));

            _handledPack = song.PackId;
            var fx = new NetSongFetcher();
            fx.BeginDownload(RoomConfig.serverAddress, RoomConfig.serverPort, ctx.Net.SessionKey, song.PackId, dest);
            ActivateTransfer(fx, song.SongKey);
            ctx.Net.SetAvailability(song.PackId, Availability.Downloading, 0f);
            Debug.Log("[net] 開始下載歌曲 " + song.Title + " → " + dest);
        }

        // ================= 進度回報 =================

        private static void ReportProgress(AppContext ctx)
        {
            if (_fx == null || _fx.State != NetTransferState.Downloading) return;
            float now = Time.realtimeSinceStartup * 1000f;
            if (now - _lastReportMs < NetLimits.AvailProgressThrottleMs) return;
            _lastReportMs = now;
            ctx.Net.SetAvailability(_fx.PackId, Availability.Downloading, _fx.Progress);
        }

        // ================= 下載完 → 接進歌庫 =================

        /// <summary>
        /// 重新掃描歌庫把新歌接進來。**不用重開遊戲**(需求 8)。
        ///
        /// <c>ScanAndRegisterCo</c> 本來就是可以重複跑的(它每次重建整份外部歌清單),
        /// 所以這裡直接再跑一次就好。掃完用 packId 再確認一次真的找得到 ——
        /// 找不到就是「檔案下載對了但這個格式我們解析不出來」,那要說出來,
        /// 不能讓玩家停在「已經 100% 了但還是缺歌」。
        /// </summary>
        private static IEnumerator ImportCo(AppContext ctx, MonoBehaviour runner, NetSongFetcher fx,
                                            int generation, string packId, string songKey)
        {
            if (!IsCurrentTransfer(fx, generation, packId))
            {
                if (generation == _transferGeneration) _importing = false;
                yield break;
            }

            ctx.Net.SetAvailability(packId, Availability.Importing, 1f);
            yield return runner.StartCoroutine(ExternalSongLibrary.ScanAndRegisterCo(null));

            // ScanAndRegisterCo yields for many frames. A room-song change during that time invalidates this
            // generation; stale A must not look up, mark, or clear B's importing flag.
            if (!IsCurrentTransfer(fx, generation, packId)) yield break;

            var found = ExternalSongLibrary.FindByPack(packId, songKey);
            if (found == null)
            {
                // songKey 對不上還有救:同一個資料夾裡只有一首歌時 songKey 是空字串,
                // 而兩邊的分組結果理論上一致 —— 但格式解析失敗時就是真的找不到。
                fx.MarkImported(false, "下載完成但歌庫接不進來(格式可能不支援)");
            }
            else
            {
                Debug.Log("[net] 歌庫已接上下載的歌:" + found.title);
                fx.MarkImported(true);
            }
            _importing = false;
        }

        private static void ActivateTransfer(NetSongFetcher fx, string songKey)
        {
            _transferGeneration++;
            _fx = fx;
            _transferSongKey = songKey ?? "";
            _importing = false;
        }

        private static void InvalidateActiveTransfer()
        {
            var old = _fx;
            _fx = null;
            _transferGeneration++;
            _transferSongKey = null;
            _importing = false;
            if (old != null) old.Dispose();
        }

        private static bool IsCurrentTransfer(NetSongFetcher fx, int generation, string packId)
            => generation == _transferGeneration
            && ReferenceEquals(_fx, fx)
            && fx != null
            && string.Equals(fx.PackId, packId, StringComparison.Ordinal);

        // ================= 事件接線 =================

        private static void Wire(NetClient net)
        {
            if (_wired == net) return;
            _wired = net;
            net.BlobInfoReceived += OnBlobInfo;
            net.BlobAvailable += OnBlobAvailable;
            net.BlobProgress += OnBlobProgress;
            net.RoomLeft += _ => Reset();
            net.Disconnected += _ => Reset();
        }

        private static void OnBlobInfo(string packId, bool have)
        {
            if (!_queryPending
                || !string.Equals(packId, _queriedPack, StringComparison.Ordinal)
                || !string.Equals(packId, _roomPack, StringComparison.Ordinal))
                return;

            _queryPending = false;
            _queriedPack = null;
            _serverHasPack = have;
        }

        private static void OnBlobAvailable(string packId)
        {
            if (!string.Equals(packId, _roomPack, StringComparison.Ordinal)) return;
            // 房主上傳完了 → 現在可以下載。清掉「處理過」的記憶,讓 MaybeStart 重新評估。
            //
            // 🔴 但**房主自己也收得到這個廣播**(它是房內廣播)→ 清掉記憶之後 MaybeStart 會叫它
            // 再上傳一次。第二次雖然是「零個檔」很便宜,但那是個沒有理由的迴圈(實機 log 上就是
            // 「開始收上傳:0/7 個檔」出現兩次)。只有真的還缺歌的人才需要重新評估。
            _serverHasPack = true;
            _queryPending = false;
            _queriedPack = null;
            if (_wired != null && _wired.IsHost) return;
            if (_handledPack == packId && (_fx == null || !_fx.IsBusy)) _handledPack = null;
        }

        private static void OnBlobProgress(int userId, float frac, bool uploading)
            => _bars[userId] = new KeyValuePair<float, bool>(frac, uploading);
    }
}
