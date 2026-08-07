using System.Collections.Generic;

namespace Sdo.Ruleset
{
    /// <summary>這一場的一個**座位**(server 的 match participants,開場前就完整)。</summary>
    public readonly struct RosterSeat
    {
        public readonly int UserId;
        public readonly string Name;
        public RosterSeat(int userId, string name) { UserId = userId; Name = name; }
    }

    /// <summary>server 推來的一筆**即時分數**(gameplay frame)。開場後才會陸續出現。</summary>
    public readonly struct RosterScore
    {
        public readonly int UserId;
        public readonly string Name;
        public readonly long Score;
        public RosterScore(int userId, string name, long score) { UserId = userId; Name = name; Score = score; }
    }

    /// <summary>
    /// 把右側名單組出來:**名字來自座位表,分數來自 frame**。
    ///
    /// 🔴 名單的底稿一定要是這一場的座位表(match participants),不能是「已經收到分數的人」——
    /// 後者要等對方那台真的開始送 frame 才有,於是開場前幾秒名單上只有自己一個人,別人的名字
    /// 要到歌都跑了 5~6 秒才一個個冒出來(使用者回報)。座位表在 matchStarting 就到齊了,
    /// 拿它當底稿的話一進場就是完整名單,frame 到了只是把 0 換成真分數。
    ///
    /// 順序照座位序,平手序(<see cref="PlayerEntry.Seat"/>)也是座位序 —— 與 server 的
    /// <c>ResultRowOrder</c> 同一把尺,兩台才會排出同一個人(見 <see cref="RankingBoard"/>)。
    /// </summary>
    public static class GameplayRoster
    {
        /// <summary>
        /// 重建 <paramref name="into"/>(會先清空)。
        /// </summary>
        /// <param name="seats">這一場的座位表,依座位序。null/空 = 沒有名單(離線)。</param>
        /// <param name="localSeat">本機在座位表裡的位置;&lt;0 = 旁觀者(名單裡不能有自己)。</param>
        /// <param name="localScore">本機那一列要畫的分數(呼叫端已對齊到遠端那幾筆的時刻)。</param>
        /// <param name="live">server 推來的遠端分數(可為 null/空 —— 開場就是這個狀態)。</param>
        /// <param name="leaderUserId">站在領隊格的人;0 = 還沒有(離線)。</param>
        /// <param name="maxRows">名單最多幾列(HUD 的 PKSCORE 位數上限)。</param>
        public static void Build(List<PlayerEntry> into,
                                 IReadOnlyList<RosterSeat> seats, int localSeat,
                                 string localName, long localScore,
                                 IReadOnlyList<RosterScore> live,
                                 int leaderUserId, int maxRows)
        {
            if (into == null) return;
            into.Clear();
            int seatCount = seats?.Count ?? 0;

            if (localSeat >= 0 && into.Count < maxRows)
            {
                int myId = localSeat < seatCount ? seats[localSeat].UserId : 0;
                into.Add(new PlayerEntry(localName ?? "", localScore, true, localSeat,
                                         leaderUserId != 0 && leaderUserId == myId));
            }

            for (int i = 0; i < seatCount && into.Count < maxRows; i++)
            {
                if (i == localSeat) continue;
                var s = seats[i];
                // 名字以座位表為準,分數以 frame 為準;frame 也帶名字(改名/座位表缺名時它比較新)。
                string name = s.Name ?? "";
                long score = 0L;
                int liveIdx = IndexOfUser(live, s.UserId);
                if (liveIdx >= 0)
                {
                    score = live[liveIdx].Score;
                    if (!string.IsNullOrEmpty(live[liveIdx].Name)) name = live[liveIdx].Name;
                }
                into.Add(new PlayerEntry(name, score, false, i,
                                         leaderUserId != 0 && leaderUserId == s.UserId));
            }

            // frame 裡有座位表沒有的人(協定上不該發生,但真的發生時他的分數不能憑空消失)。
            // 沒有座位 → 平手序退回名單順序(PlayerEntry.NoSeat)。
            int liveCount = live?.Count ?? 0;
            for (int i = 0; i < liveCount && into.Count < maxRows; i++)
            {
                var r = live[i];
                if (IndexOfSeat(seats, r.UserId) >= 0) continue;
                into.Add(new PlayerEntry(r.Name ?? "", r.Score, false, PlayerEntry.NoSeat,
                                         leaderUserId != 0 && leaderUserId == r.UserId));
            }
        }

        private static int IndexOfUser(IReadOnlyList<RosterScore> live, int userId)
        {
            int n = live?.Count ?? 0;
            if (userId == 0) return -1;   // 0 = 沒有 server id(離線),不能拿來配對
            for (int i = 0; i < n; i++) if (live[i].UserId == userId) return i;
            return -1;
        }

        private static int IndexOfSeat(IReadOnlyList<RosterSeat> seats, int userId)
        {
            int n = seats?.Count ?? 0;
            if (userId == 0) return -1;
            for (int i = 0; i < n; i++) if (seats[i].UserId == userId) return i;
            return -1;
        }
    }
}
