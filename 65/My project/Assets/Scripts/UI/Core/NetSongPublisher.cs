using Sdo.Game;
using Sdo.Net;
using Sdo.UI.Catalog;

namespace Sdo.UI.Core
{
    /// <summary>
    /// 把「本機選好的那首歌」翻成連線協定的 <see cref="NetSongRef"/>,交給 server 發給全房。
    ///
    /// 🔴 為什麼一定要有這一步:server 用「這間房選了哪首歌」當很多規則的前提 ——
    /// 沒歌就不能按準備(R17)、不能開始(R12),而換歌會清掉所有人的準備與「有沒有這首歌」(R9)。
    /// 只把歌存在本機 session 的話,兩台看得到歌名(那是本機畫的),但 server 眼中這間房**沒有歌**
    /// → 沒有人按得下準備、房主按開始只會收到「請先選擇歌曲」,而畫面上明明有歌。
    /// (實機兩開就是這樣卡住的:OnlineRoomService.SetSong(string) 只印一行警告,而沒有人呼叫
    ///  Ctx.Net.SetSong(NetSongRef)。)
    ///
    /// 外部歌(osu/SM)要靠 packId 才能跨電腦比對,那是 M5(缺歌傳檔)的事;這裡先做官方歌,
    /// 外部歌照樣填得出顯示欄位,只是 packId 還空著。
    /// </summary>
    public static class NetSongPublisher
    {
        // ---- 「我有沒有這首歌」的回報 ------------------------------------------------------------------------
        // 🔴 這一步少了的話**整個連線對戰都動不了**,而且症狀完全指不到原因:
        //    server 把每個人的 availability 預設成 Unknown,而
        //      • 按準備要求 avail == have(R17)→ 回 badState
        //      • 參與者集合 = 「(房主 或 已準備) 且 avail == have」(R12)→ 房主按開始也回 badState
        //    畫面上一切正常(有歌、有人、有開始鈕),按下去卻只有一個沒有文字的 badState。
        //    實機兩開就是卡在這裡查了很久。
        //
        //    完整的缺歌流程(沒有就自動下載)是 M5;這裡做的是**最小但必要**的那一半:
        //    有就說 have、沒有就說 missing。
        private static string _lastKey;
        private static Availability _lastState = Availability.Unknown;

        /// <summary>
        /// 忘掉上次回報過什麼 → 下一次 <see cref="ReportAvailability"/> 一定會重送。
        ///
        /// 下載完成的那一刻一定要叫它:本機的答案從 missing 變成 have,而「狀態沒變就不重送」的記憶
        /// 只記得**回報過的值**(那時是 missing → 現在算出 have,其實會送)。真正的問題是下載期間
        /// 我們送過 downloading/importing 而那些不是這個記憶在管的欄位 —— 保險起見清掉,
        /// 免得留在「已經 100% 了但頭貼還是缺歌、按不了準備」。
        /// </summary>
        public static void ForceReport()
        {
            _lastKey = null;
            _lastState = Availability.Unknown;
        }

        /// <summary>
        /// 每次房間快照呼叫一次:「我有沒有這首歌」跟 server 眼中的不一樣就重送。
        ///
        /// 🔴 判斷依據是**server 快照裡我那個座位的 avail**,不是本機記著「上次送了什麼」。
        /// 純本機的記憶會與 server 失去同步而且再也回不來 —— 最實際的一條路徑:
        /// 去旁觀再坐回座位。座位重建之後 server 那邊的 avail 是 unknown,而歌沒有換
        /// (key 一樣、我算出來的答案也一樣 have)→ 本機記憶說「送過了」→ 永遠不補送 →
        /// server 眼中我永遠是 unknown → 我按不了準備、房主也開不了場,而畫面上一切正常。
        /// 拿 server 的值當基準就沒有這個問題:它是自我修復的。
        /// </summary>
        public static void ReportAvailability(AppContext ctx)
        {
            if (ctx == null || ctx.Net == null || ctx.Rooms == null) return;
            if (!ctx.Net.IsConnected || !ctx.Net.InRoom) return;
            var snap = ctx.Net.Room;
            var song = snap != null ? snap.Song : null;
            if (song == null || !song.HasSong) { _lastKey = null; _lastState = Availability.Unknown; return; }

            // server 對官方歌是拿 gn 當 key 比對的(NetRoom.MatchesCurrentSong),外部歌才用 packId。
            string key = song.Official ? song.Gn : song.PackId;
            if (string.IsNullOrEmpty(key)) return;

            // 傳檔進行中 → 回報權交給 NetSongTransfer(它在送 downloading/importing 與進度)。
            // 這裡插手會把那些過渡狀態蓋成 missing,進度條就死了。
            if (NetSongTransfer.Active) return;

            var state = HaveSong(ctx, song) ? Availability.Have : Availability.Missing;

            // server 眼中我現在是什麼?**座位與旁觀席都要查** —— 拿 server 的值當基準才是自我修復的。
            //
            // 🔴 旁觀者以前走的是「本機記憶」那條退路,而那正是上面那段註解警告過的失效模式,只是換了個方向:
            //    在座位上回報過 have(_lastState=Have)→ 轉去旁觀(server 端的旁觀席是新的一筆,avail=unknown)
            //    → 歌沒換(key 一樣、算出來的答案一樣 have)→ 記憶說「送過了」→ 永遠不補送 →
            //    server 眼中這個旁觀者沒有這首歌 → 他不在 SpectatorsWithSong() 裡 → 收不到 matchStarting →
            //    別人都開場了,他還留在房間裡。實機回報就是這個(而且時好時壞:先旁觀才選歌就正常,
            //    因為那時 key 會變、記憶擋不住)。
            var serverAvail = ServerAvailOf(snap, ctx.Net.UserId);
            if (serverAvail.HasValue)
            {
                if (serverAvail.Value == state) { _lastKey = key; _lastState = state; return; }   // 已經一致
            }
            else if (_lastKey == key && _lastState == state) return;   // 快照裡還沒有我(剛進房的競態)→ 只好靠記憶

            _lastKey = key; _lastState = state;
            UnityEngine.Debug.Log("[net] 回報可用性:" + state + " (" + key + ")");
            ctx.Net.SetAvailability(key, state);
        }

        /// <summary>
        /// server 快照裡「我」的 avail —— 坐著就是座位那格,旁觀就是旁觀席那筆(兩者 server 都有維護,
        /// 見 <c>NetRoom.SetAvailability</c>)。兩邊都找不到 = 這份快照還沒帶到我 → null(呼叫端退回本機記憶)。
        /// </summary>
        private static Availability? ServerAvailOf(NetRoomSnapshot snap, int userId)
        {
            var seat = snap.SeatOf(userId);
            if (seat != null) return seat.Avail;
            int si = snap.SpectatorIndexOf(userId);
            if (si >= 0 && snap.Spectators != null && si < snap.Spectators.Length) return snap.Spectators[si].Avail;
            return null;
        }

        private static bool HaveSong(AppContext ctx, NetSongRef song)
        {
            if (song.Official) return SongCatalog.Get(song.Gn) != null;

            // 外部歌:身分是 packId(資料夾內容的指紋)+ songKey(資料夾裡的哪一首)。
            // 用它去查自己的歌庫 —— 這就是「我到底有沒有房主選的那一份」的唯一正確問法:
            // 歌名相同不代表譜相同,而拿到不對的譜的症狀是「音符跟音樂差半拍」。
            if (!string.IsNullOrEmpty(song.PackId))
            {
                var hit = ExternalSongLibrary.FindByPack(song.PackId, song.SongKey);
                // 🔴 目錄說有,還要**檔案真的在**才算有。目錄可能是舊的:
                //   • 掃描快取(external_song_cache.json)存在 DATA/CACHE,**同一個 data root 的**兩份 client
                //     共用同一份 → 另一份 client 掃到的資料夾會出現在我的目錄裡(兩開測試時 B 用
                //     SDO_DATA_ROOT 指到別棵樹,那就各自一份);
                //   • 玩家把歌資料夾刪掉/搬走,而下一次掃描還沒跑。
                // 謊報 have 的後果很具體:server 會把我納入這一場(R12 要求 avail==have),
                // 然後我開場載不到譜 → 那台卡在載入畫面,全房等我逾時(R15)。寧可說沒有。
                return hit != null && ChartFileExists(hit);
            }

            // 沒有 packId(房主的歌庫掃描還沒算出來,或對面是舊版)→ 只認「這首就是我自己選的那首」。
            // 寧可說沒有,也不要謊報 have 然後在開場時載不到譜。
            var s = ctx.Session;
            return s != null && s.IsExternalSong && !string.IsNullOrEmpty(song.SongKey)
                   && string.Equals(s.ExternalSongKey, song.SongKey, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// 這筆目錄紀錄的譜面檔真的還在磁碟上嗎(見 <see cref="HaveSong"/> 的註解)。
        /// 三個難度槽只要有一個在就算有 —— 少一個槽是「難度不全」,不是「沒有這首歌」。
        /// </summary>
        private static bool ChartFileExists(SongCatalog.Entry e)
        {
            if (e == null) return false;
            for (int slot = 0; slot < 3; slot++)
            {
                var p = e.ChartPath(slot);
                if (string.IsNullOrEmpty(p)) continue;
                try { if (System.IO.File.Exists(p)) return true; }
                catch { /* 路徑壞掉 = 當它不存在 */ }
            }
            return false;
        }

        /// <summary>把 session 現在選的歌轉成 wire 格式。沒選歌 → null。</summary>
        public static NetSongRef FromSession(GameSession s)
        {
            if (s == null || !s.HasSong) return null;
            var song = new NetSongRef
            {
                Official = !s.IsExternalSong,
                Gn = s.SongGn ?? "",
                FileId = s.SongFileId,
                ChartIndex = (int)s.Difficulty,
                Difficulty = (int)s.Difficulty,
                Title = s.SongTitle ?? "",
                Artist = s.SongArtist ?? "",
                // 隨機難度:Title 是「隨機難度 X」的標籤。收端要靠這個旗標才知道別去查目錄
                // (抽到的歌就在 Gn 裡,查了就等於揭曉)。
                RandomTitle = s.SongIsRandom,
            };

            // 顯示用的等級/BPM/音符數也一起帶上。
            // 為什麼:缺歌的人**沒有**這首歌,查不到自己的目錄 → 房間面板就只能顯示歌名而沒有等級/BPM。
            // 帶著走一份,那台就算沒有歌也顯示得完整(而它正是最需要知道「這是什麼歌」的人)。
            // 隨機難度**不帶** —— 那會直接把抽到的歌的等級/BPM 洩漏出去。
            var meta = s.SongIsRandom ? null : SongCatalog.Get(s.SongGn);
            if (meta != null)
            {
                if (meta.bpm > 0f) song.Bpm = meta.bpm;
                song.Level = meta.DisplayLevel((int)s.Difficulty);
                song.NoteCount = meta.NoteCount((int)s.Difficulty);
            }
            if (s.IsExternalSong)
            {
                song.SongKey = s.ExternalSongKey ?? "";
                // 🔴 ChartRelPath 是**相對歌曲資料夾**的路徑,不是本機的絕對路徑。
                // session 存的是絕對路徑(GameSession.ExternalChartPath 的註解就寫著 absolute),
                // 直接塞進去的話 server 會用 SafeRelPath.IsSafe 擋掉(它不收磁碟機代號)→
                // 整個 setSong 回 badState「bad song ref」,而畫面上只是「選了歌但房間沒歌」。
                // (實機驗證時就是這樣抓到的:單元測試都綠,因為沒有一條測到「絕對路徑進 wire」。)
                song.ChartRelPath = ToChartRelPath(s.ExternalFolderPath, s.ExternalChartPath);
                song.ChartIndex = s.ExternalChartIndex;
                song.Level = s.ExternalLevel;

                // 跨電腦的身分。session 自己不存 packId —— 它是掃描時算好蓋在 catalog entry 上的,
                // 所以從那裡拿(gn 在本機是唯一的,只是換台電腦就不同,這也正是需要 packId 的原因)。
                // 用 SongCatalog.Get 直查而不是上面的 meta:隨機難度時 meta 是刻意留 null 的
                // (不洩漏等級/BPM),但 packId 是身分、非有不可 —— 少了它 server 會直接拒絕整個 setSong。
                var ext = SongCatalog.Get(s.SongGn);
                song.PackId = ext != null ? (ext.packId ?? "") : "";
            }
            return song;
        }

        /// <summary>
        /// 絕對譜面路徑 → 相對歌曲資料夾的路徑(小寫、<c>/</c> 分隔),協定用的形式。純函式。
        ///
        /// 兩邊都必須是同一個歌曲資料夾底下的檔案。切不出相對路徑時(理論上不會 —— 譜就在那個資料夾裡)
        /// 退回只用檔名:那仍然是一個合法且能用的相對路徑,而回空字串會讓 server 直接拒絕整個 setSong。
        /// </summary>
        public static string ToChartRelPath(string folderAbs, string chartAbs)
        {
            if (string.IsNullOrEmpty(chartAbs)) return "";

            string chart = chartAbs.Replace('\\', '/');
            string folder = (folderAbs ?? "").Replace('\\', '/').TrimEnd('/');
            if (folder.Length > 0 && chart.Length > folder.Length + 1
                && chart.StartsWith(folder + "/", System.StringComparison.OrdinalIgnoreCase))
                return Sdo.Osu.SafeRelPath.Normalize(chart.Substring(folder.Length + 1));

            int slash = chart.LastIndexOf('/');
            return Sdo.Osu.SafeRelPath.Normalize(slash >= 0 ? chart.Substring(slash + 1) : chart);
        }

        /// <summary>
        /// server 手上那一份,與本機想送的這一份,對「**房間選了什麼**」而言是同一件事嗎?(純函式)
        ///
        /// 一般情形就是同一張譜**同一個難度**(<see cref="NetSongRef.SameChoiceAs"/>)。
        ///
        /// 🔴 多出來的那一條是**隨機難度**:每一局開場房主都會抽一首新的官方歌
        /// (<c>NetResolvedRound.RandomSong</c> → <c>RoomScreen.BuildRandomSongPick</c>),server echo 回來時
        /// 會寫進自己的 session,所以打完一局回房時這裡的 gn 已經跟 server 手上那份不一樣了。
        /// 但房間**設定**沒有變(還是同一個「隨機難度 X」)—— 送出去只會被 server 當成換歌:
        /// 全房的準備與「有沒有這首歌」被清掉(R9)、缺歌的人莫名其妙再傳一次歌。
        ///
        /// 標籤(Title)仍然要比:把難度區間從「隨機難度 3」改成「隨機難度 5」是真的改了房間設定,必須送。
        ///
        /// 🔴 指定歌要比的是 <see cref="NetSongRef.SameChoiceAs"/>(**含難度**)而不是 SameChartAs:
        /// 官方歌三個難度共用同一個 gn,只問「同一張譜嗎」的話「同一首歌 easy → hard」會被當成沒變 →
        /// 永遠不送 → server 手上的難度停在進房時預設的 easy,開場再把房主自己的難度蓋回去。
        /// </summary>
        public static bool SameRoomSelection(NetSongRef want, NetSongRef current)
        {
            if (want == null || current == null) return false;
            // 🔴 隨機難度先判斷,而且**不能**先問 SameChartAs:重抽剛好又抽到同一首歌時 gn 會一樣,
            // 那時「隨機難度 3 → 隨機難度 5」就會被當成沒變 → server 手上的標籤永遠停在舊區間。
            if (want.RandomTitle != current.RandomTitle) return false;          // 隨機 ↔ 指定歌:換了
            if (want.RandomTitle)                                               // 都是隨機:只看標籤(哪個難度區間)
                return string.Equals(want.Title, current.Title, System.StringComparison.Ordinal);
            return want.SameChoiceAs(current);                                  // 指定歌:同一張譜**同一個難度**才算沒變
        }

        /// <summary>
        /// 房主把選好的歌發給 server。非房主/離線 → 什麼都不做(server 也會擋,R7)。
        ///
        /// 🔴 **server 手上已經是這張譜就不送**。RoomScreen.OnShow 每次都會呼叫這裡,而「回房間畫面」
        /// 包含遊戲結束回來 —— 重送會被 server 當成換歌,把所有人的 avail 打回 unknown,
        /// 於是缺歌的人莫名其妙又傳一次歌。(server 端同樣擋了一道,兩邊都擋是因為
        /// 這條路徑上的無謂訊息本來就不該送出去。)
        /// </summary>
        public static void Publish(AppContext ctx)
        {
            if (ctx == null || ctx.Net == null) return;
            if (!ctx.Net.IsConnected || !ctx.Net.InRoom || !ctx.Net.IsHost) return;
            var song = FromSession(ctx.Session);
            if (song == null) return;
            var current = ctx.Net.Room != null ? ctx.Net.Room.Song : null;
            if (SameRoomSelection(song, current)) return;   // 房間選的東西沒變 → 不送

            // 🔴 送出前先自己驗一次,而且**要說出是哪一條規則擋的**。
            // server 收到不合格的參照只會回一個沒有細節的 badState「bad song ref」,而畫面上
            // 的症狀是「選了歌,但按開始沒反應」—— 從那裡完全推不回「某個檔名太長」。
            // (實機踩過兩次:第一次是絕對路徑進了 wire,第二次是 osu 的譜面檔名超過
            //  SafeRelPath.MaxSegmentLength。兩次都是靠 server log 才找到的。)
            if (!song.Official)
            {
                string why;
                if (!Sdo.Osu.SafeRelPath.IsSafe(song.ChartRelPath, out why))
                {
                    UnityEngine.Debug.LogError("[net] 這首歌的譜面路徑過不了驗證,server 會拒絕整個 setSong:"
                                               + why + "(路徑=" + song.ChartRelPath + ")");
                    return;
                }
                if (string.IsNullOrEmpty(song.PackId))
                {
                    // packId 是外部歌的跨機器身分,少了它 server 一樣拒收。空的通常代表
                    // 歌庫掃描還沒算出這個資料夾的指紋(而不是這首歌有問題)。
                    UnityEngine.Debug.LogError("[net] 這首外部歌還沒有 packId(歌庫掃描沒算出來?),server 會拒絕整個 setSong:"
                                               + song.Title);
                    return;
                }
            }

            UnityEngine.Debug.Log("[net] 發布歌曲給 server:" + song.Title + " (gn=" + song.Gn + ")");
            ctx.Net.SetSong(song);
        }

        /// <summary>
        /// 「server 那邊還沒有歌就補發一次」—— 由房間快照的回呼每次呼叫。
        ///
        /// 🔴 為什麼不能只在進房那一刻發一次:進房時房間可能還沒建好(createRoom 要等 server 回 roomState),
        /// 那一刻 <c>InRoom</c> 還是 false → 發布被靜默跳過、而且永遠不會再試。
        /// 它也順便處理另外兩種情形:房主中途被轉給我、以及 server 把歌清掉之後。
        /// </summary>
        public static void PublishIfRoomHasNone(AppContext ctx)
        {
            if (ctx == null || ctx.Net == null || ctx.Rooms == null) return;
            if (!ctx.Net.IsConnected || !ctx.Net.InRoom || !ctx.Net.IsHost) return;
            var room = ctx.Rooms.CurrentRoom;
            if (room == null || !string.IsNullOrEmpty(room.SongTitle)) return;   // 已經有歌 → 不再送(不會迴圈)
            Publish(ctx);
        }
    }
}
