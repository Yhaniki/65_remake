namespace Sdo.Ruleset
{
    /// <summary>
    /// Round-end rewards (經驗值 EXP / G幣 coins / 榮譽 honor-points) for the result screen.
    /// Ported verbatim from the Arrowgene Dance!Online server emulator
    /// (server/.../game/ScoreUser.java — getCharacterExperience / getCharacterCoins / getCharacterPoints).
    ///
    /// IMPORTANT provenance note: only the SCORE formula (see <see cref="ScoreProcessor.ServerScore"/>) is a
    /// genuine reverse-engineering of the official client's Lua. These EXP / coin / point formulas are the
    /// EMULATOR authors' RECONSTRUCTION (community guess, German dev comments, hard-coded 30/100/20 caps), NOT
    /// confirmed official server numbers — the real server formula is not available. Kept faithful so the remake
    /// reproduces the emulator's behaviour; tune the constants if a better reference surfaces.
    ///
    /// Inputs:
    ///   bad, miss  — the local player's Bad + Miss counts (a clean run = 0 → the top tier).
    ///   place      — 1-based finishing rank (1 = winner).  Java used a 0-based place; (players - place + 1)
    ///                reproduces its (activeUsers - place0): winner → ×players, last → ×1.
    ///   players    — number of active players in the room (≥ 1).
    ///   level      — character level (the emulator scales coins/points by floor(level × 1.1)).
    /// Pure logic — unit-tested.
    /// </summary>
    public static class Reward
    {
        /// <summary>Rank multiplier: winner (place 1) → players, last place → 1. Never negative.</summary>
        private static int PlaceFactor(int place, int players)
        {
            int f = players - (place - 1);   // == activeUsers - place0based
            return f < 0 ? 0 : f;
        }

        /// <summary>EXP earned: base 30 for a clean run, else 30 − (bad+miss)/5 floored at 10, × rank factor.</summary>
        public static int Experience(int bad, int miss, int place, int players)
        {
            int faults = bad + miss;
            int baseXp = faults == 0 ? 30 : 30 - faults / 5;
            if (baseXp < 10) baseXp = 10;
            int xp = baseXp * PlaceFactor(place, players);
            return xp < 0 ? 0 : xp;
        }

        /// <summary>
        /// G幣 (coins) earned. The emulator SHIPS this disabled (getCharacterCoins returns 0; the body is
        /// commented out). This is that commented-out body: 1 coin only on a clean run, × rank factor × floor(level×1.1).
        /// </summary>
        public static int Coins(int bad, int miss, int place, int players, int level)
        {
            int coins = (bad + miss) == 0 ? 1 : 0;
            coins *= PlaceFactor(place, players) * (int)(level * 1.1);
            return coins < 0 ? 0 : coins;
        }

        /// <summary>Honor points: base 100 for a clean run, else 100 − (bad+miss)/20 floored at 20, × rank factor × floor(level×1.1).</summary>
        public static int Points(int bad, int miss, int place, int players, int level)
        {
            int faults = bad + miss;
            int baseP = faults == 0 ? 100 : 100 - faults / 20;
            if (baseP < 20) baseP = 20;
            int pts = baseP * PlaceFactor(place, players) * (int)(level * 1.1);
            return pts < 0 ? 0 : pts;
        }
    }
}
