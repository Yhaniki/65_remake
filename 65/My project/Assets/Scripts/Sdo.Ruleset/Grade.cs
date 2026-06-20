namespace Sdo.Ruleset
{
    /// <summary>
    /// Letter grade for the result screen "成绩" column, from hit accuracy (%). Banded after the decompiled
    /// result screen's accuracy thresholds (Gameplay_FillResultStats 023:5228-5305 keys off 95/90/85/80…),
    /// mapped to the remake spec's S / A+ / A / B / C / D letters (docs/screens/05-game-arena/result-screen.md).
    /// Pure logic — unit-tested.
    /// </summary>
    public static class Grade
    {
        public static string FromAccuracy(double accuracyPercent)
        {
            double a = accuracyPercent;
            if (a >= 100.0) return "S";
            if (a >= 95.0) return "A+";
            if (a >= 90.0) return "A";
            if (a >= 80.0) return "B";
            if (a >= 65.0) return "C";
            return "D";
        }
    }
}
