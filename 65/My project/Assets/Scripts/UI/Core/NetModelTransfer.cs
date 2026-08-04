using System;
using System.Collections.Generic;
using System.IO;
using Sdo.Game;
using Sdo.Game.Net;
using Sdo.Net;
using Sdo.Osu;
using Sdo.Settings;
using UnityEngine;

namespace Sdo.UI.Core
{
    /// <summary>
    /// MMD 模型的傳檔**編排**:什麼時候該把自己的模型推上去、什麼時候該去拉別人的、拉到之後怎麼接上。
    /// 真的在搬位元組的是 <see cref="NetSongFetcher"/>(同一條管線,只是 <c>kind=model</c>)。
    ///
    /// 與缺歌那條(<see cref="NetSongTransfer"/>)的三個關鍵差別:
    ///
    /// 1. **沒有「等不到就玩不了」的壓力。** 缺歌會擋住整場比賽;模型只是外觀 —— 拉不到就是看到對方的
    ///    SDO 穿搭,遊戲照跑。所以這條路上的每一個決定都偏保守:失敗不重試到底、絕不插隊到歌前面。
    /// 2. **上傳的起點是「我穿著它」,不是「有人喊缺」。** 進房間就推一次(靠 packId 去重,server
    ///    已經有就是零上傳)。理由:別人一看到我就需要它,而「等他喊缺再傳」會讓每個新進房的人
    ///    都先看到一次 SDO 穿搭再變身,閃一下。
    /// 3. **本機 MMD 顯示關掉 = 這整條完全不動。** 不查詢、不下載、不上傳。使用者關掉 MMD 的意思
    ///    就是「我不要這個功能」,那不該還在背景吃他的流量與磁碟。
    ///
    /// 🔴 <b>永遠讓歌先走。</b>同時只有一條 file 連線在傳才不會互相搶頻寬,而缺歌的人按不了準備、
    /// 房主開不了場 —— 模型晚三十秒到完全沒有代價。所以只要 <see cref="NetSongTransfer.Active"/>
    /// 就一步都不動。
    /// </summary>
    public static class NetModelTransfer
    {
        private static NetSongFetcher _fx;
        private static NetClient _wired;

        /// <summary>這個工作階段已經推上去過的 packId —— 同一份模型不必每次進房都重推一次。</summary>
        private static string _uploadedPack;

        /// <summary>這個工作階段已經放棄的 packId(下載失敗 / server 沒有 / 不是合法模型包)。
        /// 沒有這個集合的話,一個永遠拿不到的模型會被每幀重試,而畫面上完全看不出來。</summary>
        private static readonly HashSet<string> _givenUp = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>server 手上有哪些包(問過才知道)。</summary>
        private static readonly HashSet<string> _serverHas = new HashSet<string>(StringComparer.Ordinal);

        private static readonly List<string> _missing = new List<string>();

        private static string _queriedPack;
        private static float _lastQueryAt = -99f;
        private static string _downloadingPack;

        /// <summary>問過「server 有沒有」之後多久可以再問。與缺歌那條同一個理由:不節流會撞 server 的限流。</summary>
        private const float QueryRetrySec = 3f;

        /// <summary>送出 blobQuery 之後等回覆的上限。超過就當成那一問掉了。</summary>
        private const float QueryTimeoutSec = 8f;

        /// <summary>本機正在傳模型嗎(給 UI 顯示用)。</summary>
        public static bool Active => _fx != null && _fx.IsBusy;

        /// <summary>正在下載的那個模型的進度 0..1(沒在下載就是 0)。</summary>
        public static float Progress => _fx != null ? _fx.Progress : 0f;

        /// <summary>每幀呼叫(<c>FrontendApp.Update</c>)。</summary>
        public static void Tick(AppContext ctx)
        {
            if (ctx == null || ctx.Net == null) return;
            Wire(ctx.Net);

            // MMD 顯示關著 → 這條路完全不動(見類別說明第 3 點)。
            if (!RoomConfig.mmdEnabled) return;

            if (_fx != null)
            {
                _fx.Tick();
                if (_fx.State == NetTransferState.Importing) { FinishDownload(); return; }
                if (_fx.State == NetTransferState.Done) { Debug.Log("[mmd-net] 傳輸完成:" + _fx.PackId); Clear(); return; }
                if (_fx.State == NetTransferState.Failed)
                {
                    // 模型拉不到就是看到對方的 SDO 穿搭 —— 不是災難,不值得無限重試。記下來別再試。
                    Debug.LogWarning("[mmd-net] 傳輸失敗 " + _fx.PackId + ":" + _fx.Error);
                    if (!_fx.IsUploading && !string.IsNullOrEmpty(_fx.PackId)) _givenUp.Add(_fx.PackId);
                    Clear();
                    return;
                }
                if (_fx.IsBusy) return;
            }

            // 歌永遠優先(見類別說明)。
            if (NetSongTransfer.Active) return;

            var net = ctx.Net;
            if (!net.IsConnected || !net.InRoom) return;

            if (TryUploadMine(ctx)) return;
            TryFetchMissing(ctx);
        }

        // ================= 上傳自己的 =================

        private static bool TryUploadMine(AppContext ctx)
        {
            if (!RoomConfig.mmdShareModel) return false;
            string mine = MmdAvatarSwap.LocalPackId;          // 分享關掉時它本來就是空的(連算都不算)
            if (string.IsNullOrEmpty(mine)) return false;
            if (string.Equals(_uploadedPack, mine, StringComparison.Ordinal)) return false;
            if (_givenUp.Contains(mine)) return false;

            string dir = MmdAvatarSwap.ModelDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { _givenUp.Add(mine); return false; }

            // 自己先驗一次。server 也會驗(那是它的職責),但在這裡擋掉可以省下整趟上傳,
            // 而且錯誤訊息指得到真正的原因(「你的模型資料夾裡有 .exe」而不是「badPath」)。
            List<PackFileEntry> files; PackScanStats stats;
            if (!ModelPackId.ScanFolder(dir, out files, out stats))
            {
                Debug.LogWarning("[mmd-net] 掃不到可以分享的檔案,不上傳:" + dir);
                _givenUp.Add(mine);
                return false;
            }
            string why;
            if (!ModelPackId.IsValidPack(files, out why))
            {
                Debug.LogWarning("[mmd-net] 這份模型不能分享(" + why + ")—— 別人會看到你的 SDO 穿搭:" + dir);
                _givenUp.Add(mine);
                return false;
            }

            _uploadedPack = mine;   // 先記:失敗了也不要每幀重推
            var fx = new NetSongFetcher();
            fx.BeginUpload(RoomConfig.serverAddress, RoomConfig.serverPort, ctx.Net.SessionKey,
                           mine, dir, NetProto.BlobKindModel);
            _fx = fx;
            Debug.Log($"[mmd-net] 開始分享自己的模型 {MmdAvatarSwap.ModelName}({files.Count} 檔、{stats.IncludedBytes / 1024} KB)");
            return true;
        }

        // ================= 下載別人的 =================

        private static void TryFetchMissing(AppContext ctx)
        {
            MmdAvatarSwap.CollectMissingPacks(_missing);
            if (_missing.Count == 0) return;

            string want = null;
            for (int i = 0; i < _missing.Count; i++)
                if (!_givenUp.Contains(_missing[i])) { want = _missing[i]; break; }
            if (want == null) return;

            if (!_serverHas.Contains(want))
            {
                float now = Time.realtimeSinceStartup;
                if (string.Equals(_queriedPack, want, StringComparison.Ordinal) && now - _lastQueryAt < QueryTimeoutSec) return;
                if (now - _lastQueryAt < QueryRetrySec) return;
                _lastQueryAt = now;
                _queriedPack = want;
                ctx.Net.SendBlobQuery(want);
                return;
            }

            string dest = MmdModelStore.NetDirFor(want);
            if (string.IsNullOrEmpty(dest)) { _givenUp.Add(want); return; }

            _downloadingPack = want;
            var fx = new NetSongFetcher();
            fx.BeginDownload(RoomConfig.serverAddress, RoomConfig.serverPort, ctx.Net.SessionKey,
                             want, dest, NetProto.BlobKindModel);
            _fx = fx;
            Debug.Log("[mmd-net] 開始下載別人的模型 " + want + " → " + dest);
        }

        /// <summary>
        /// 下載的位元組都落地了 → 驗一次它真的是一份模型,然後把在等它的角色接上去。
        ///
        /// 驗證是必要的:走到這裡表示每個檔的 SHA-256 都對得上**宣稱的清單**,但那份清單是 server 給的。
        /// 我們自己重算一次 packId,對不上就整包丟掉 —— 否則一個被入侵的 server 可以讓每個 client 都
        /// 把一包別的東西當成「那個人的模型」存進磁碟。
        /// </summary>
        private static void FinishDownload()
        {
            var fx = _fx;
            string pack = fx.PackId;
            string dir = fx.DestFolder;

            string got = ModelPackId.ForFolder(dir);
            if (!string.Equals(got, pack, StringComparison.Ordinal))
            {
                Debug.LogWarning("[mmd-net] 下載回來的內容與宣稱的 packId 不符,丟掉:" + pack);
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                _givenUp.Add(pack);
                fx.MarkImported(false, "packId 不符");
                Clear();
                return;
            }

            fx.MarkImported(true);
            Debug.Log("[mmd-net] 模型裝好了:" + pack + " → " + dir);
            MmdAvatarSwap.OnPackInstalled(pack);   // 當場換上,不重建角色
            _downloadingPack = null;
            Clear();
        }

        // ================= 事件 / 生命週期 =================

        private static void Wire(NetClient net)
        {
            if (_wired == net) return;
            if (_wired != null) _wired.BlobInfoReceived -= OnBlobInfo;
            _wired = net;
            if (_wired != null) _wired.BlobInfoReceived += OnBlobInfo;
        }

        private static void OnBlobInfo(string packId, bool have)
        {
            if (string.IsNullOrEmpty(packId)) return;
            if (!string.Equals(_queriedPack, packId, StringComparison.Ordinal)) return;   // 不是我問的(缺歌那條也在問)
            _queriedPack = null;
            if (have) _serverHas.Add(packId);
            else
            {
                // server 沒有 = 穿它的人還沒推上來(或他把分享關掉了)。不記進 _givenUp ——
                // 他可能下一秒就推完了,而我們會在 QueryRetrySec 之後再問一次。
                Debug.Log("[mmd-net] server 還沒有這個模型,稍後再問:" + packId);
            }
        }

        /// <summary>離開房間 / 斷線。<b>不清 <see cref="_uploadedPack"/></b> —— 已經推上去的東西
        /// 換一間房還是在 server 上,重推只是白費流量(server 會回「一個檔都不用傳」,但那趟連線省得掉)。</summary>
        public static void Reset()
        {
            if (_fx != null) { _fx.Cancel("離開房間"); _fx.Dispose(); _fx = null; }
            _serverHas.Clear();
            _queriedPack = null;
            _lastQueryAt = -99f;
            _downloadingPack = null;
        }

        /// <summary>模型換了(設定面板選了別的)→ 下次進房要重推新的那一個。</summary>
        public static void OnLocalModelChanged()
        {
            _uploadedPack = null;
        }

        private static void Clear()
        {
            if (_fx != null) { _fx.Dispose(); _fx = null; }
        }
    }
}
