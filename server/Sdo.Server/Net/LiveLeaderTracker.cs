using System.Collections.Generic;
using Sdo.Net.Server;

namespace Sdo.Server.Net
{
    /// <summary>
    /// 這一場「誰站在領隊格」的權威來源。client 收到 frames 的 <c>leaderUserId</c> 直接照用
    /// (見 <c>Sdo.Ruleset.FormationAssignment.ResolveLeader</c>),所以每台看到的中央那一格是同一個人。
    ///
    /// 🔴 為什麼不是「比一下誰分數高」那麼簡單:每個人的 frame 是 5 Hz、lossy、各自的時鐘,
    /// server 手上 A 的最新一筆可能是歌曲時間 10000ms 的、B 的是 9600ms 的。直接比大小就是
    /// **拿不同時刻的分數比** —— 那 400ms 落差在高 combo 下值好幾千分,於是 leader 每 200ms
    /// 交替一次(畫面上兩個人一直互搶中央那格)。
    ///
    /// 舊版靠「挑戰者要領先 300 分才換」擋這個。門檻式防抖的有效條件是「門檻 > 雜訊振幅」,
    /// 而這裡的雜訊振幅 = 時間落差 × 當下得分率 —— 它跟著 combo 一起長,門檻永遠追不上;
    /// 調大又會讓真正的超車遲遲不換位,低分階段還會直接鎖死 leader。所以整個換掉。
    ///
    /// 現在照 osu 的多人排行榜做法(<c>osu.Game/Online/Spectator/SpectatorScoreProcessor.cs</c>
    /// 的 <c>UpdateScore</c> + <c>osu.Game/Screens/Play/Leaderboards/MultiplayerLeaderboardProvider.cs</c>
    /// 的 <c>sort</c> 節流),三層、沒有任何分數門檻:
    ///
    ///   1. **同一時刻取樣**(根治):每個人的 (歌曲時間, 分數) 存成序列,比較時挑一個共同的
    ///      參考時間 tRef,各取「不晚於 tRef 的最後一筆」(sample-and-hold,絕不用未來的資料)。
    ///      osu 是 <c>BinarySearch(ReferenceClock.CurrentTime)</c> 然後退一格,語義一樣。
    ///   2. **換人節流**(<see cref="ReconsiderIntervalMs"/>):leader 最多這麼久換一次。這一層
    ///      與分數增量大小**無關**,所以 combo 再大也不會失效 —— 這正是門檻式防抖做不到的事。
    ///   3. **決定性 tie-break**:同分照 (seat, userId) 排。同分不換位(嚴格領先才換)。
    /// </summary>
    public sealed class LiveLeaderTracker
    {
        /// <summary>
        /// 取樣點從「全場最新的歌曲時間」往回退多久(ms)。要 ≥ client 送 frame 的間隔(200ms)
        /// 再加上抖動與掉包的餘裕,否則跑在最前面的那個人有樣本、別人還沒有 → 又變成拿不同時刻比。
        /// 2.5 個間隔。
        /// </summary>
        public const double AlignWindowMs = 500.0;

        /// <summary>
        /// leader 換人的最小間隔,單位是**歌曲時間** ms(不是牆上時間)。
        ///
        /// 用歌曲時間有三個好處:沒有新 frame 進來就沒有新分數、本來也不需要重新考慮;整場暫停/
        /// 卡頓時它自然跟著停;而且 <see cref="Resolve"/> 不必注入時鐘 → 結果只由收到的 frame
        /// 決定,測試是決定性的(不會因為 5 Hz push 剛好落在哪裡而飄)。數字取 osu 的 sort 節流。
        /// </summary>
        public const double ReconsiderIntervalMs = 1000.0;

        /// <summary>
        /// 每個人保留多久的歷史(歌曲時間 ms)。只要比 <see cref="AlignWindowMs"/> 長就夠取樣;
        /// 留寬一點是為了某人掉包一整段之後,別人推進的 tRef 還能在他的序列裡找到 hold 值。
        /// 4 秒 × 5 Hz ≈ 20 筆/人。
        /// </summary>
        private const double HistoryMs = 4000.0;

        private struct Sample
        {
            public double TMs;
            public long Score;
        }

        private readonly NetMatchPlayerSnapshot[] _players;
        private readonly Dictionary<int, List<Sample>> _history = new Dictionary<int, List<Sample>>();

        /// <summary>上次換 leader 時的 tRef(歌曲時間)。<c>-∞</c> = 還沒換過 → 第一次不用等節流。</summary>
        private double _lastSwitchTMs = double.NegativeInfinity;

        public int CurrentUserId { get; private set; }

        public LiveLeaderTracker(NetMatchPlayerSnapshot[] players)
        {
            _players = players ?? new NetMatchPlayerSnapshot[0];
            CurrentUserId = FirstUserId(_players);
            for (int i = 0; i < _players.Length; i++) _history[_players[i].UserId] = new List<Sample>();
        }

        /// <summary>
        /// 收一筆分數快照。<paramref name="tMs"/> 是**歌曲時間**(client 自己的 chart time,
        /// frame 訊息裡的 <c>tMs</c>)—— 沒有它就無法做同一時刻取樣。
        /// </summary>
        public void Record(int[] activeUserIds, int userId, double tMs, long score)
        {
            if (!Contains(activeUserIds, userId)) return;

            List<Sample> series;
            if (!_history.TryGetValue(userId, out series))
            {
                series = new List<Sample>();
                _history[userId] = series;
            }

            // 同一條連線上的 frame 是有序的,所以正常路徑就是 append。時間沒有前進(client 時鐘
            // 回退、或同一個 tMs 送了兩筆)時**不能**讓時間軸倒退 —— 序列亂序的話取樣就會從
            // 那筆倒退的資料開始命中,分數會憑空跳。這種時候只更新最後一筆的分數。
            if (series.Count > 0 && tMs <= series[series.Count - 1].TMs)
            {
                var last = series[series.Count - 1];
                last.Score = score;
                series[series.Count - 1] = last;
            }
            else
            {
                series.Add(new Sample { TMs = tMs, Score = score });
            }

            Trim(series);
        }

        /// <summary>
        /// 依目前收到的資料算出權威 leader。<paramref name="activeUserIds"/> = 這一場還在場上的人
        /// (斷線/被踢的人已經不在裡面)。
        /// </summary>
        public int Resolve(int[] activeUserIds)
        {
            int firstUserId = FirstActiveUserId(activeUserIds);
            if (firstUserId == 0)
            {
                CurrentUserId = 0;
                return 0;
            }

            double latest = LatestActiveTMs(activeUserIds);
            double tRef = latest - AlignWindowMs;   // latest 是 -∞ 時 tRef 也是 -∞

            // 現在的 leader 已經不在場上(斷線/被踢/改旁觀)。這是**補位**不是換位 —— 領隊格不能
            // 空著,所以不受節流限制。
            //
            // 🔴 補位要挑「當下取樣點上分數最高的那位」,不是座位序最前的那位。tRef 在上面已經算好,
            // 每個人的對齊分數 ScoreAt() 隨手可得,挑對人是零成本的;挑錯人的代價是玩家看到**兩次**
            // 換位(先滑到站錯的人、等滿一輪節流才滑到真正的第一名),而中央前排那格是鏡頭錨點
            // (ScreenGameplay 的 _camAnchorSpot),所以鏡頭也會跟著多跑一趟 —— 正是這個模組要消滅的抖動。
            //
            // 開場(誰都還沒有樣本)時所有人的取樣分數都是 0,tie-break 退化成座位序 → 與
            // <see cref="FirstActiveUserId"/> 同解,所以這個改法是舊行為的嚴格超集。
            if (!Contains(activeUserIds, CurrentUserId))
            {
                long ignored;
                int backfill = BestAt(activeUserIds, tRef, 0, out ignored);
                CurrentUserId = backfill != 0 ? backfill : firstUserId;
                _lastSwitchTMs = tRef;   // 補位也算動過一格,後面照樣要等一輪節流才准再換
                return CurrentUserId;
            }

            if (double.IsNegativeInfinity(latest)) return CurrentUserId;   // 一筆 frame 都還沒進來

            // 換人節流。放在取樣之前:被擋住的這一輪連比都不用比。
            if (tRef - _lastSwitchTMs < ReconsiderIntervalMs) return CurrentUserId;

            long currentScore = ScoreAt(CurrentUserId, tRef);
            long challengerScore;
            int challenger = BestAt(activeUserIds, tRef, CurrentUserId, out challengerScore);

            // **嚴格**領先才換。時間對齊之後同一時刻的分數比較是精確的,不需要門檻去遮蓋時間錯位;
            // 真的同分就不動(誰在領隊格誰留著)。
            if (challenger != 0 && challengerScore > currentScore)
            {
                CurrentUserId = challenger;
                _lastSwitchTMs = tRef;
            }

            return CurrentUserId;
        }

        /// <summary>
        /// 在歌曲時間 <paramref name="tMs"/> 那一刻,還在場上的人裡面分數最高的是誰
        /// (<paramref name="excludeUserId"/> 不參加,傳 0 = 誰都不排除)。
        ///
        /// 同分照 (seat, userId) —— 必須是決定性的:leader 會被推給所有 client 當成隊形的輸入,
        /// 靠字典/陣列順序決定的話同一份資料在不同人的畫面上會排出不同的第一名。
        /// 回 0 = 沒有符合的人。
        /// </summary>
        private int BestAt(int[] activeUserIds, double tMs, int excludeUserId, out long bestScore)
        {
            NetMatchPlayerSnapshot best = null;
            bestScore = long.MinValue;

            for (int i = 0; i < _players.Length; i++)
            {
                var player = _players[i];
                if (!Contains(activeUserIds, player.UserId) || player.UserId == excludeUserId) continue;

                long score = ScoreAt(player.UserId, tMs);
                if (best == null || score > bestScore ||
                    (score == bestScore && ComesFirst(player, best)))
                {
                    best = player;
                    bestScore = score;
                }
            }

            if (best == null) bestScore = 0;
            return best != null ? best.UserId : 0;
        }

        /// <summary>
        /// 這位玩家在歌曲時間 <paramref name="tMs"/> 的分數 —— 「不晚於它的最後一筆」(sample-and-hold)。
        /// 那個時刻還沒有任何一筆(剛開場、或這個人的第一筆比 tRef 還晚)就是 0 = 開場分數。
        /// </summary>
        private long ScoreAt(int userId, double tMs)
        {
            List<Sample> series;
            if (!_history.TryGetValue(userId, out series) || series.Count == 0) return 0;

            // 序列只有 20 筆上下,而且答案通常就在最後幾筆 → 從後往前掃比 BinarySearch 快也好讀。
            for (int i = series.Count - 1; i >= 0; i--)
                if (series[i].TMs <= tMs) return series[i].Score;
            return 0;
        }

        /// <summary>
        /// 還在場上的人裡面,最新的那個歌曲時間。一筆都沒有就回 <c>-∞</c>。
        ///
        /// 🔴 取 **max** 而不是 min 是刻意的。osu 的參考時鐘是本機的譜面播放時鐘,server 沒有那種
        /// 東西,只能從收到的 frame 推。用 min 的話,一個打完了/卡住了/掉線中的玩家會把 tRef 永遠
        /// 釘在他最後一筆上 → 整場排名凍結。用 max 再退一個 <see cref="AlignWindowMs"/>,對齊效果
        /// 一樣(所有人在同一個 tRef 取樣),而落後的人只是繼續 hold 住他最後的分數 —— 那正是他
        /// 現在的真實分數。
        /// </summary>
        private double LatestActiveTMs(int[] activeUserIds)
        {
            double latest = double.NegativeInfinity;
            for (int i = 0; i < _players.Length; i++)
            {
                var player = _players[i];
                if (!Contains(activeUserIds, player.UserId)) continue;

                List<Sample> series;
                if (!_history.TryGetValue(player.UserId, out series) || series.Count == 0) continue;

                double t = series[series.Count - 1].TMs;
                if (t > latest) latest = t;
            }
            return latest;
        }

        private static void Trim(List<Sample> series)
        {
            double cutoff = series[series.Count - 1].TMs - HistoryMs;
            int keepFrom = 0;
            while (keepFrom < series.Count && series[keepFrom].TMs < cutoff) keepFrom++;
            // 多留 cutoff 之前的那一筆:sample-and-hold 在 cutoff 附近的時刻要靠它。
            if (keepFrom > 0) keepFrom--;
            if (keepFrom > 0) series.RemoveRange(0, keepFrom);
        }

        private static int FirstUserId(NetMatchPlayerSnapshot[] players)
        {
            if (players == null || players.Length == 0) return 0;

            var first = players[0];
            for (int i = 1; i < players.Length; i++)
                if (ComesFirst(players[i], first)) first = players[i];
            return first.UserId;
        }

        private int FirstActiveUserId(int[] activeUserIds)
        {
            NetMatchPlayerSnapshot first = null;
            for (int i = 0; i < _players.Length; i++)
            {
                var player = _players[i];
                if (!Contains(activeUserIds, player.UserId)) continue;
                if (first == null || ComesFirst(player, first)) first = player;
            }
            return first != null ? first.UserId : 0;
        }

        private static bool Contains(int[] userIds, int userId)
        {
            if (userIds == null) return false;
            for (int i = 0; i < userIds.Length; i++)
                if (userIds[i] == userId) return true;
            return false;
        }

        private static bool ComesFirst(NetMatchPlayerSnapshot a, NetMatchPlayerSnapshot b)
            => a.Seat < b.Seat || (a.Seat == b.Seat && a.UserId < b.UserId);
    }
}
