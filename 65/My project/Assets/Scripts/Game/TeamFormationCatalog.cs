using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// The team / versus floor formations, reproduced VERBATIM from the decompiled offline client. When players
    /// carry a team tag (player record +0x48 ∈ {1,2,3}) the setup function (FUN_00471f20) switches on the resolved
    /// game mode (DAT_00674f04+0x8c) and reads a per-team sub-table in the contiguous region 0x582be8..0x582cc0
    /// (18 × Vector3 = three 6-slot blocks, 0x0c stride, y = 0):
    ///   • mode 1 (block @0x582be8) = 2v2   — 2 teams × 2 (its 3rd row is zeroed / unused)
    ///   • mode 3 (block @0x582c30) = 2v2v2 — 3 teams × 2
    ///   • mode 2 (block @0x582c78) = 3v3   — 2 teams × 3
    /// which mode is picked is decided by counting each team's members (023_gameplay_00482340: e.g. team1==2 &amp;&amp;
    /// team2==2 &amp;&amp; team3==2 → 2v2v2). Within a team, member 0 sits at z=0 (front) and the rest fall back to z=50;
    /// the front member is the team's leader anchor (CStateGaming+0xfc/0x100/0x104 track the first member per team).
    /// Positions are floor offsets relative to the dance anchor, exactly like <see cref="FormationCatalog"/>.
    /// </summary>
    public static class TeamFormationCatalog
    {
        public enum Layout { V2v2, V3v3, V2v2v2 }

        public static readonly Layout[] All = { Layout.V2v2, Layout.V3v3, Layout.V2v2v2 };

        public static string Name(Layout l)
        {
            switch (l)
            {
                case Layout.V2v2: return "2v2";
                case Layout.V3v3: return "3v3";
                default: return "2v2v2";
            }
        }

        // [team][member] — member 0 = front/leader (z=0), others behind (z=50). Verbatim EXE values.
        private static readonly Vector3[][][] Layouts =
        {
            // 2v2  (mode 1 @0x582be8)
            new[]
            {
                new[] { V(-25, 0), V(-75, 50) },   // team 0 (left)
                new[] { V(25, 0), V(75, 50) },     // team 1 (right)
            },
            // 3v3  (mode 2 @0x582c78)
            new[]
            {
                new[] { V(-60, 0), V(-90, 50), V(-30, 50) },   // team 0 (left)
                new[] { V(60, 0), V(30, 50), V(90, 50) },      // team 1 (right)
            },
            // 2v2v2  (mode 3 @0x582c30)
            new[]
            {
                new[] { V(-45, 0), V(-75, 50) },   // team 0 (left)
                new[] { V(15, 0), V(-15, 50) },    // team 1 (centre)
                new[] { V(75, 0), V(45, 50) },     // team 2 (right)
            },
        };

        private static Vector3 V(float x, float z) => new Vector3(x, 0f, z);

        /// <summary>Number of teams in a layout (2v2 → 2, 3v3 → 2, 2v2v2 → 3).</summary>
        public static int TeamCount(Layout l) => Layouts[(int)l].Length;

        /// <summary>Total dancers across all teams (2v2 → 4, 3v3 → 6, 2v2v2 → 6).</summary>
        public static int TotalDancers(Layout l)
        {
            int n = 0;
            foreach (var team in Layouts[(int)l]) n += team.Length;
            return n;
        }

        /// <summary>Per-team floor offsets: result[team][member], member 0 = the team's front/leader. Returns a
        /// deep copy the caller may mutate.</summary>
        public static Vector3[][] GetTeams(Layout l)
        {
            var src = Layouts[(int)l];
            var outArr = new Vector3[src.Length][];
            for (int t = 0; t < src.Length; t++)
            {
                outArr[t] = new Vector3[src[t].Length];
                System.Array.Copy(src[t], outArr[t], src[t].Length);
            }
            return outArr;
        }
    }
}
