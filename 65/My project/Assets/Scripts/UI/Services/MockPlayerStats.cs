using System;
using Sdo.Settings;

namespace Sdo.UI.Services
{
    /// <summary>
    /// 房間裡「其他人」的戰績。官方是伺服器下發（離線 EXE 連 PlayerInformationDlg 的程式碼都沒有），
    /// 所以單機版拿不到真資料 —— 這裡由玩家 id 決定性地生一組看起來合理的數字，讓右鍵看別人的面板不是空的。
    ///
    /// 決定性 = 同一個 id 永遠得到同一組數字（跟 ScreenGameplay 的 SimOpponentScore 同一套想法），
    /// 所以看兩次不會跳動。接上線之後整個 class 直接丟掉，改用封包欄位填 <see cref="PlayerStats"/>。
    /// </summary>
    public static class MockPlayerStats
    {
        /// <summary>由玩家 id 生一組決定性的假戰績。</summary>
        public static PlayerStats For(string playerId)
        {
            var rng = new Random(Seed(playerId));

            var st = new PlayerStats();
            st.games = 30 + rng.Next(370);                      // 30..399 場
            double winRate = 0.08 + rng.NextDouble() * 0.40;    // 8%..48% 勝率
            st.wins = (int)Math.Round(st.games * winRate);

            long notes = (long)st.games * (240 + rng.Next(260));   // 每場 240..499 個 note
            double perfectShare = 0.42 + rng.NextDouble() * 0.34;  // 42%..76%
            double coolShare = 0.14 + rng.NextDouble() * 0.22;     // 14%..36%
            double badShare = 0.02 + rng.NextDouble() * 0.07;      // 2%..9%
            double sum = perfectShare + coolShare + badShare;
            if (sum > 0.98)                                        // 留至少 2% 給 miss
            {
                double k = 0.98 / sum;
                perfectShare *= k; coolShare *= k; badShare *= k;
            }

            st.perfect = (long)Math.Round(notes * perfectShare);
            st.cool = (long)Math.Round(notes * coolShare);
            st.bad = (long)Math.Round(notes * badShare);
            st.miss = Math.Max(0, notes - st.perfect - st.cool - st.bad);

            st.bestScore = 40000 + rng.Next(160000);
            st.totalScore = st.bestScore / 2 * st.games;
            return st;
        }

        /// <summary>由 id 生等級（1..60，決定性）。</summary>
        public static int LevelFor(string playerId) => 1 + Math.Abs(Seed(playerId) / 7) % 60;

        // FNV-1a：跨平台穩定（string.GetHashCode 在不同 runtime/版本可能不同，會讓「決定性」跑掉）。
        private static int Seed(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                if (s != null)
                    foreach (char c in s) { h ^= c; h *= 16777619u; }
                return (int)(h & 0x7fffffff);
            }
        }
    }
}
