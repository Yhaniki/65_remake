namespace Sdo.Server.Net
{
    /// <summary>
    /// Deterministic ordering for authoritative result rows.
    ///
    /// 🔴 同分時**站在領隊格的那位排第一**(<paramref name="leaderUserId"/>),不是座位序最小的那位。
    /// 理由是這兩件事在畫面上是同一件事,不能各判各的:整場的領隊格由
    /// <see cref="LiveLeaderTracker"/> 決定,而它同分時**不換位**(嚴格領先才換,「誰在領隊格誰留著」)——
    /// 於是被追平的那一刻,站在最前面的是「一路領先到最後」的那位,他的座位序不一定最小。
    /// 這裡若照座位序判,同分收場時就會變成:台上站最前面的是 A,面板第一名與勝利定格卻給了 B
    /// (使用者回報「結算同分不是站前面的人做 win 動作」)。名次面板、場上定格與領隊格必須指向同一個人。
    ///
    /// leader 之外仍照 (seat, userId) —— 三人以上同分時剩下那幾位還是要有決定性的順序,
    /// 而且 leader 只有一位。<paramref name="leaderUserId"/> 傳 0 = 不知道領隊是誰(單機/還沒開打),
    /// 退回原本的座位序規則。
    /// </summary>
    public static class ResultRowOrder
    {
        public static int Compare(long leftScore, int leftSeat, int leftUserId,
                                  long rightScore, int rightSeat, int rightUserId,
                                  int leaderUserId = 0)
        {
            int byScore = rightScore.CompareTo(leftScore);
            if (byScore != 0) return byScore;

            if (leaderUserId != 0)
            {
                bool leftIsLeader = leftUserId == leaderUserId;
                bool rightIsLeader = rightUserId == leaderUserId;
                if (leftIsLeader != rightIsLeader) return leftIsLeader ? -1 : 1;
            }

            int bySeat = leftSeat.CompareTo(rightSeat);
            return bySeat != 0 ? bySeat : leftUserId.CompareTo(rightUserId);
        }
    }
}
