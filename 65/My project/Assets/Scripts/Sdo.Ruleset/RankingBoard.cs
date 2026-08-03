using System;
using System.Collections.Generic;

namespace Sdo.Ruleset
{
    /// <summary>
    /// One player on the in-game ranking board: a display name, the current score, and whether
    /// this is the locally-controlled player. Pure data — no UnityEngine types (Sdo.Ruleset is
    /// <c>noEngineReferences</c>); colours/positions are decided by the renderer in Sdo.Game.
    /// </summary>
    public readonly struct PlayerEntry
    {
        public readonly string Name;
        public readonly long Score;
        public readonly bool IsLocal;
        /// <summary>平手時誰在前的 key —— 線上填**座位序**(見 <see cref="RankingBoard"/> 的 tie-break)。
        /// <see cref="NoSeat"/> = 不知道座位(離線/假對手/名單裡查不到的人) → 退回名單順序。</summary>
        public readonly int Seat;

        /// <summary>「沒有座位資料」。排序時排在所有有座位的人後面,彼此再照名單順序。</summary>
        public const int NoSeat = int.MaxValue;

        /// <summary>這位是不是**站在領隊格**的那個(台上最前面那位,server 的 <c>leaderUserId</c>)。
        /// 同分時他排第一 —— 理由見 <see cref="RankingBoard"/> 的 tie-break 說明。</summary>
        public readonly bool IsLeader;

        public PlayerEntry(string name, long score, bool isLocal, int seat = NoSeat, bool isLeader = false)
        {
            Name = name;
            Score = score;
            IsLocal = isLocal;
            Seat = seat;
            IsLeader = isLeader;
        }
    }

    /// <summary>
    /// Pure ranking logic for the gameplay roster (right-side name+score list and the centre
    /// "rank N / M" readout). Ordering is fully deterministic so the list never flickers on ties:
    /// higher score first; on equal score the lower <see cref="PlayerEntry.Seat"/> wins; otherwise
    /// the lower original index wins. This total order means the sort need not be stable.
    ///
    /// 🔴 平手一定要照**全場一致**的規則,不能「本機先」。本機先是每台各自成立的規則 —— 同分時兩台都會
    /// 覺得自己是第一名,於是兩邊都做勝利定格動作,而結算面板用的是 server 的權威名次,就變成
    /// 「面板寫我第 2 名,人卻在跳勝利動作」(使用者回報)。
    ///
    /// 🔴 平手的第一順位是 <see cref="PlayerEntry.IsLeader"/>(**台上站最前面**那位),座位序只是它之後
    /// 的第二順位。領隊格由 <c>LiveLeaderTracker</c> 決定,而它同分時**不換位**(嚴格領先才換)——
    /// 被追平的那一刻站最前面的是「一路領先到最後」的那位,座位序不一定最小。只照座位序判,同分收場
    /// 就會變成「台上站前面的是 A,第一名跟勝利定格卻給了 B」(使用者回報)。server 端同一條規則,
    /// 見 <c>ResultRowOrder</c> —— 這份是 server 權威名次到達前的暫定值,兩邊必須算得出同一個人。
    /// </summary>
    public static class RankingBoard
    {
        /// <summary>
        /// Compare players a, b (by original index) for descending-rank order:
        /// returns &lt;0 if a outranks b, &gt;0 if b outranks a.
        /// </summary>
        private static int Compare(IReadOnlyList<PlayerEntry> players, int a, int b)
        {
            long sa = players[a].Score, sb = players[b].Score;
            if (sa != sb) return sb.CompareTo(sa);          // higher score first
            bool la = players[a].IsLeader, lb = players[b].IsLeader;
            if (la != lb) return la ? -1 : 1;               // ties: 台上站最前面那位先(同 server 的 ResultRowOrder)
            int ta = players[a].Seat, tb = players[b].Seat;
            if (ta != tb) return ta.CompareTo(tb);          // then lower seat first
            return a.CompareTo(b);                          // stable fallback: original order
        }

        /// <summary>
        /// Indices into <paramref name="players"/> ordered best-rank-first. Never null
        /// (empty array for an empty/null roster). Does not mutate the input.
        /// </summary>
        public static int[] SortedIndices(IReadOnlyList<PlayerEntry> players)
        {
            int n = players?.Count ?? 0;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) => Compare(players, a, b));
            return order;
        }

        /// <summary>
        /// **畫在畫面上的**名次:同分並列,而且**不跳號**(密集排名 1,1,2 —— 使用者指定。不是競賽排名的 1,1,3)。
        /// <paramref name="scoresDesc"/> 要先照分數由高到低排好(<see cref="SortedIndices"/> 的順序)。
        ///
        /// 🔴 與 <see cref="Compare"/>/<see cref="LocalRank"/> 的**嚴格**順序是兩回事,而且必須並存:
        /// 場上的輸贏定格只能有一個第一名(平手照座位序,兩台才會指向同一個人),
        /// 但寫在結算面板與「N / M」上的名次,同分要一樣(使用者指定)。
        /// 結算面板那面 YOU WIN 旗跟的是**這一份**(見 <see cref="IsWinningPlace"/>),不是嚴格順序。
        /// </summary>
        public static int[] DisplayRanks(IReadOnlyList<long> scoresDesc)
        {
            int n = scoresDesc?.Count ?? 0;
            var ranks = new int[n];
            for (int i = 0; i < n; i++)
                ranks[i] = i == 0 ? 1
                         : (scoresDesc[i] == scoresDesc[i - 1] ? ranks[i - 1] : ranks[i - 1] + 1);
            return ranks;
        }

        /// <summary>
        /// 本機**畫面上**的名次(同分並列、不跳號;1-based)。沒有本機那一列(旁觀)= 0,
        /// 與 <see cref="LocalRank"/> 同一個契約。名單不必先排序 —— 它自己數「比我高的有**幾種**分數」。
        /// </summary>
        public static (int rank, int total) LocalDisplayRank(IReadOnlyList<PlayerEntry> players)
        {
            int n = players?.Count ?? 0;
            int localIdx = -1;
            for (int i = 0; i < n; i++)
                if (players[i].IsLocal) { localIdx = i; break; }
            if (localIdx < 0) return (0, n);

            long mine = players[localIdx].Score;
            int betterScores = 0;   // 比我高的**相異**分數有幾種 → 密集排名 = 它 + 1(同分的人數不影響)
            for (int i = 0; i < n; i++)
            {
                if (players[i].Score <= mine) continue;
                bool seen = false;
                for (int k = 0; k < i; k++)
                    if (players[k].Score == players[i].Score) { seen = true; break; }
                if (!seen) betterScores++;
            }
            return (betterScores + 1, n);
        }

        /// <summary>
        /// 這一局有幾個名次算「贏」——**前半,無條件捨去**(使用者指定:奇數人數時贏的比輸的少一個):
        /// 2人→1、3人→1、4人→2、5人→2、6人→3。
        ///
        /// 🔴 至少留一個贏家:單人局(離線 solo)捨去會變成 0,那個人自己就是第一名卻拿到 LOSE 旗。
        /// 人數 ≤0(沒有這一局)才是沒有贏家。
        /// </summary>
        public static int WinnerCount(int players) => players <= 0 ? 0 : Math.Max(1, players / 2);

        /// <summary>
        /// 結算面板要不要對這個人出 YOU WIN 旗(使用者指定)。
        ///
        /// 🔴 用的是 <see cref="DisplayRanks"/> 的**畫面名次**(同分並列、不跳號),不是嚴格名次 ——
        /// 所以兩個人打成平手就是兩個第一名,兩台都出 YOU WIN;6 人局「兩個第一、兩個第二、兩個第三」
        /// 的並列名次全都 ≤3,六個人也全都出 YOU WIN。
        ///
        /// 🔴 與**場上的勝利定格 / 短曲**是兩回事,不要合併:那邊只能有一個第一名(嚴格名次,平手照
        /// 座位序,見 <see cref="Compare"/>),否則兩台會同時演勝利動作。這裡只管結算面板那面旗。
        ///
        /// <paramref name="displayRank"/> ≤ 0(旁觀 / 名單裡找不到自己)一律 false。
        /// </summary>
        public static bool IsWinningPlace(int displayRank, int players)
            => displayRank > 0 && displayRank <= WinnerCount(players);

        /// <summary>
        /// 本機是不是**並列**第一(同分也算)。名單裡沒有本機(旁觀)= false。
        ///
        /// 🔴 這條與 <see cref="LocalRank"/> 是**兩件不同的事**,不要合併:場上的勝利定格
        /// 只能有一個第一名(平手照座位序,見 <see cref="Compare"/>),但**勝負場的記錄**是
        /// 「同分兩邊都記勝場」(使用者指定)—— 兩個人打成平手,誰也沒輸給誰。
        /// </summary>
        public static bool LocalTiedForTop(IReadOnlyList<PlayerEntry> players)
        {
            int n = players?.Count ?? 0;
            long top = long.MinValue, local = 0L;
            bool haveLocal = false;
            for (int i = 0; i < n; i++)
            {
                long s = players[i].Score;
                if (s > top) top = s;
                if (players[i].IsLocal && !haveLocal) { local = s; haveLocal = true; }
            }
            return haveLocal && local >= top;
        }

        /// <summary>
        /// 1-based rank of the local player among everyone, and the total player count.
        /// Returns rank 0 when there is no local entry (total still reflects the roster size).
        /// </summary>
        public static (int rank, int total) LocalRank(IReadOnlyList<PlayerEntry> players)
        {
            int n = players?.Count ?? 0;
            int localIdx = -1;
            for (int i = 0; i < n; i++)
            {
                if (players[i].IsLocal) { localIdx = i; break; }
            }
            if (localIdx < 0) return (0, n);

            int better = 0;   // how many players outrank the local player
            for (int i = 0; i < n; i++)
            {
                if (i != localIdx && Compare(players, i, localIdx) < 0) better++;
            }
            return (better + 1, n);
        }
    }
}
