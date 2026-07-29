namespace Sdo.Server.Net
{
    /// <summary>Deterministic ordering for authoritative result rows.</summary>
    public static class ResultRowOrder
    {
        public static int Compare(long leftScore, int leftSeat, int leftUserId,
                                  long rightScore, int rightSeat, int rightUserId)
        {
            int byScore = rightScore.CompareTo(leftScore);
            if (byScore != 0) return byScore;

            int bySeat = leftSeat.CompareTo(rightSeat);
            return bySeat != 0 ? bySeat : leftUserId.CompareTo(rightUserId);
        }
    }
}
