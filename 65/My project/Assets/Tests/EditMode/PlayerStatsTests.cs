using NUnit.Framework;
using Sdo.Settings;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    /// <summary>
    /// 官方 PlayerInformationDlg「技术统计」分頁那六列（勝率/命中率/Perfect率/Cool率/Bad率/Miss率）的算法。
    /// 官方是伺服器統計的，離線版本機累計 → 這裡把語意釘死。
    /// </summary>
    public class PlayerStatsTests
    {
        [Test]
        public void NewProfile_AllRatesZero_NoDivideByZero()
        {
            var st = new PlayerStats();
            Assert.AreEqual(0, st.TotalNotes);
            Assert.AreEqual(0.0, st.WinRate, 1e-9);
            Assert.AreEqual(0.0, st.HitRate, 1e-9);
            Assert.AreEqual(0.0, st.PerfectRate, 1e-9);
            Assert.AreEqual(0.0, st.MissRate, 1e-9);
            Assert.AreEqual(0, st.AverageScore);
            Assert.AreEqual("0.00%", PlayerStats.FormatPercent(st.WinRate));
            Assert.AreEqual(0f, PlayerStats.BarFill(st.WinRate));
        }

        [Test]
        public void AddGame_Accumulates()
        {
            var st = new PlayerStats();
            st.AddGame(50, 30, 15, 5, won: true, score: 1000);
            st.AddGame(60, 20, 10, 10, won: false, score: 3000);

            Assert.AreEqual(2, st.games);
            Assert.AreEqual(1, st.wins);
            Assert.AreEqual(110, st.perfect);
            Assert.AreEqual(50, st.cool);
            Assert.AreEqual(25, st.bad);
            Assert.AreEqual(15, st.miss);
            Assert.AreEqual(200, st.TotalNotes);
            Assert.AreEqual(3000, st.bestScore);
            Assert.AreEqual(4000, st.totalScore);
            Assert.AreEqual(2000, st.AverageScore);
        }

        [Test]
        public void Rates_AreOverTotalNotes_HitRateExcludesMissOnly()
        {
            var st = new PlayerStats();
            st.AddGame(perfect: 50, cool: 25, bad: 15, miss: 10, won: true, score: 0);   // 100 notes

            Assert.AreEqual(50.0, st.PerfectRate, 1e-9);
            Assert.AreEqual(25.0, st.CoolRate, 1e-9);
            Assert.AreEqual(15.0, st.BadRate, 1e-9);
            Assert.AreEqual(10.0, st.MissRate, 1e-9);
            Assert.AreEqual(90.0, st.HitRate, 1e-9);   // 命中 = 非 MISS = Perfect+Cool+Bad
            Assert.AreEqual(100.0, st.PerfectRate + st.CoolRate + st.BadRate + st.MissRate, 1e-9);
        }

        [Test]
        public void WinRate_IsWinsOverGames()
        {
            var st = new PlayerStats();
            for (int i = 0; i < 4; i++) st.AddGame(1, 0, 0, 0, won: i == 0, score: 10);
            Assert.AreEqual(25.0, st.WinRate, 1e-9);
            Assert.AreEqual("25.00%", PlayerStats.FormatPercent(st.WinRate));
        }

        [Test]
        public void AddGame_ClampsNegativeInput()
        {
            var st = new PlayerStats();
            st.AddGame(-5, -1, -1, -1, won: false, score: -100);
            Assert.AreEqual(1, st.games);
            Assert.AreEqual(0, st.TotalNotes);
            Assert.AreEqual(0, st.bestScore);
        }

        [Test]
        public void FormatPercent_IsTwoDecimalsWithSign()   // 官方 XML labeltext 預設 "0.00%"
        {
            Assert.AreEqual("0.00%", PlayerStats.FormatPercent(0));
            Assert.AreEqual("33.33%", PlayerStats.FormatPercent(100.0 / 3.0));
            Assert.AreEqual("100.00%", PlayerStats.FormatPercent(100));
            Assert.AreEqual("0.00%", PlayerStats.FormatPercent(double.NaN));
        }

        [Test]
        public void BarFill_IsClampedFraction()   // 官方 ProgressBar minrange=0 maxrange=99
        {
            Assert.AreEqual(0f, PlayerStats.BarFill(-1f), 1e-6);
            Assert.AreEqual(0.5f, PlayerStats.BarFill(50.0), 1e-6);
            Assert.AreEqual(1f, PlayerStats.BarFill(140.0), 1e-6);
        }

        [Test]
        public void Profile_Sanitize_FillsMissingStats()   // 舊 profile.json 沒有 stats 欄位
        {
            var p = new UserProfile { stats = null, level = 0 };
            p.Sanitize();
            Assert.IsNotNull(p.stats);
            Assert.AreEqual(1, p.level);
        }
    }

    /// <summary>房間裡其他人的假身分/假戰績：必須是決定性的（同一個人每次點開都一樣）。</summary>
    public class MockPlayerStatsTests
    {
        [Test]
        public void SameId_SameStats()
        {
            var a = MockPlayerStats.For("room03");
            var b = MockPlayerStats.For("room03");
            Assert.AreEqual(a.games, b.games);
            Assert.AreEqual(a.wins, b.wins);
            Assert.AreEqual(a.perfect, b.perfect);
            Assert.AreEqual(a.miss, b.miss);
            Assert.AreEqual(MockPlayerStats.LevelFor("room03"), MockPlayerStats.LevelFor("room03"));
        }

        [Test]
        public void DifferentIds_DifferentStats()
        {
            Assert.AreNotEqual(MockPlayerStats.For("room01").games, MockPlayerStats.For("room07").games);
        }

        [Test]
        public void Rates_AreSane()
        {
            for (int slot = 1; slot <= 15; slot++)
            {
                var st = MockPlayerStats.For(RoomOccupants.IdFor(slot));
                Assert.Greater(st.games, 0, "slot " + slot);
                Assert.LessOrEqual(st.wins, st.games, "slot " + slot);
                Assert.Greater(st.TotalNotes, 0, "slot " + slot);
                Assert.GreaterOrEqual(st.miss, 0, "slot " + slot);
                Assert.AreEqual(100.0, st.PerfectRate + st.CoolRate + st.BadRate + st.MissRate, 1e-6, "slot " + slot);
                Assert.That(MockPlayerStats.LevelFor(RoomOccupants.IdFor(slot)), Is.InRange(1, 60));
            }
        }

        [Test]
        public void RoomOccupants_IdsAndNames_AreStableAndUnique()
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            for (int slot = 1; slot <= 15; slot++)
            {
                Assert.AreEqual(RoomOccupants.IdFor(slot), RoomOccupants.IdFor(slot));
                Assert.IsTrue(seen.Add(RoomOccupants.IdFor(slot)), "id 撞號 slot " + slot);
                Assert.IsNotEmpty(RoomOccupants.NameFor(slot));
            }
        }
    }
}
