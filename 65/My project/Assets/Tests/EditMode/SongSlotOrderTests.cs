using NUnit.Framework;
using Sdo.Game;
using Sdo.Osu;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// 選了哪套難度算法（<see cref="RoomConfig.difficultyCalc"/>）就整體都照那套 —— 包含「哪張譜排進
    /// 簡單/普通/困難」（<see cref="SongCatalog.Entry.SortSlotsByDisplayLevel"/>）。掃描期是用 osu 等級分槽的，
    /// 兩套算法的高低順序不見得一樣，不重排就會看到困難格的數字比普通格小。
    /// </summary>
    public class SongSlotOrderTests
    {
        private string _saved;

        [SetUp] public void SetUp() { _saved = RoomConfig.difficultyCalc; }
        [TearDown] public void TearDown() { RoomConfig.difficultyCalc = _saved; }

        // osu 等級遞增（10/20/30）但 MSD 遞減（22/18/14）→ 兩套算法的順序完全相反。
        private static SongCatalog.Entry Flipped(SongFormat format = SongFormat.Osu)
            => new SongCatalog.Entry
            {
                gn = "x.gn", external = true, chartFormat = (int)format,
                diffEasy = 10, diffNormal = 20, diffHard = 30,
                notesEasy = 100, notesNormal = 200, notesHard = 300,
                durEasy = 61, durNormal = 62, durHard = 63,
                msdEasy = 22f, msdNormal = 18f, msdHard = 14f,
                chartEasy = "e.osu", chartNormal = "n.osu", chartHard = "h.osu",
                chartIdxEasy = 1, chartIdxNormal = 2, chartIdxHard = 3,
            };

        [Test]
        public void OsuMode_LeavesTheScannedOrderAlone()
        {
            RoomConfig.difficultyCalc = "osu";
            var e = Flipped();
            e.SortSlotsByDisplayLevel();
            Assert.AreEqual(new[] { 10, 20, 30 }, new[] { e.Diff(0), e.Diff(1), e.Diff(2) });
            Assert.AreEqual("h.osu", e.ChartPath(2), "掃描期就是照 osu 等級分的槽 → 不動");
        }

        [Test]
        public void MinaCalcMode_ReordersSoHardIsTheHardest()
        {
            RoomConfig.difficultyCalc = "minacalc";
            var e = Flipped();
            e.SortSlotsByDisplayLevel();

            // MSD 22 最難 → 困難格；14 最簡單 → 簡單格
            Assert.AreEqual(22f, e.Msd(2), 1e-4f);
            Assert.AreEqual(18f, e.Msd(1), 1e-4f);
            Assert.AreEqual(14f, e.Msd(0), 1e-4f);
            Assert.GreaterOrEqual(e.DisplayLevel(2), e.DisplayLevel(1), "困難 ≥ 普通");
            Assert.GreaterOrEqual(e.DisplayLevel(1), e.DisplayLevel(0), "普通 ≥ 簡單");
        }

        [Test]
        public void Reorder_MovesTheWholeSlot_NotJustTheNumber()
        {
            RoomConfig.difficultyCalc = "minacalc";
            var e = Flipped();
            e.SortSlotsByDisplayLevel();

            // 原本的 easy 那張（MSD 22 / 100 notes / e.osu / idx 1 / 61s）整組搬到困難格
            Assert.AreEqual("e.osu", e.ChartPath(2), "譜面路徑要跟著搬，否則選困難卻開到別張譜");
            Assert.AreEqual(1, e.ChartIndex(2), ".sm 的 #NOTES 索引也要跟著搬");
            Assert.AreEqual(100, e.NoteCount(2));
            Assert.AreEqual(61, e.DurationSec(2));
            Assert.AreEqual(10, e.Diff(2), "osu 等級欄位跟著走（切回 osu 模式時就是這個值）");
        }

        [Test]
        public void Reorder_KeepsEmptySlotsLow()
        {
            RoomConfig.difficultyCalc = "minacalc";
            // 只有兩張譜（掃描期 hard-first → 普通+困難有譜，簡單空）
            var e = new SongCatalog.Entry
            {
                gn = "x.gn", external = true, chartFormat = (int)SongFormat.Osu,
                diffEasy = -1, diffNormal = 20, diffHard = 30,
                notesNormal = 200, notesHard = 300,
                msdNormal = 25f, msdHard = 15f,
                chartEasy = "", chartNormal = "n.osu", chartHard = "h.osu",
            };
            e.SortSlotsByDisplayLevel();

            Assert.IsFalse(e.HasChart(0), "譜不足三張 → 簡單格仍然是空的（灰列）");
            Assert.IsTrue(e.HasChart(1));
            Assert.IsTrue(e.HasChart(2));
            Assert.AreEqual("n.osu", e.ChartPath(2), "MSD 25 那張才是困難");
            Assert.AreEqual("h.osu", e.ChartPath(1));
        }

        [Test]
        public void Reorder_SingleChart_StaysInHard()
        {
            RoomConfig.difficultyCalc = "minacalc";
            var e = new SongCatalog.Entry
            {
                gn = "x.gn", external = true, chartFormat = (int)SongFormat.Osu,
                diffEasy = -1, diffNormal = -1, diffHard = 30, notesHard = 300, msdHard = 20f,
                chartEasy = "", chartNormal = "", chartHard = "h.osu",
            };
            e.SortSlotsByDisplayLevel();
            Assert.AreEqual("h.osu", e.ChartPath(2));
            Assert.IsFalse(e.HasChart(0));
            Assert.IsFalse(e.HasChart(1));
        }

        [Test]
        public void GnPack_IsNeverReordered()
        {
            // .gn 歌包的三格是譜面自己的易/普/難。有些歌只有簡單格有譜（sdom2140k）——
            // 拿去 hard-first 重排會把它搬進困難格，等於竄改官方難度。
            RoomConfig.difficultyCalc = "minacalc";
            var e = new SongCatalog.Entry
            {
                gn = "x.gn", external = true, chartFormat = (int)SongFormat.Gn,
                diffEasy = 5, diffNormal = -1, diffHard = -1,
                notesEasy = 3417, chartEasy = "e.gn",
            };
            e.SortSlotsByDisplayLevel();
            Assert.IsTrue(e.HasChart(0), "只有簡單格有譜 → 就是留在簡單格");
            Assert.IsFalse(e.HasChart(2));
            Assert.AreEqual("e.gn", e.ChartPath(0));
        }

        [Test]
        public void OfficialSong_IsNeverReordered()
        {
            RoomConfig.difficultyCalc = "minacalc";
            var e = new SongCatalog.Entry
            {
                gn = "sdom0001k.gn", external = false,
                diffEasy = 3, diffNormal = 4, diffHard = 5,
                notesEasy = 510, notesNormal = 600, notesHard = 700,
            };
            e.SortSlotsByDisplayLevel();
            Assert.AreEqual(new[] { 3, 4, 5 }, new[] { e.Diff(0), e.Diff(1), e.Diff(2) });
        }

        [Test]
        public void Reorder_NeedsMsdOnEveryChart()
        {
            // 有譜卻算不出 MSD（空譜/太短）→ DisplayLevel 會退回 osu 等級，兩種尺度混排只會更亂 → 不動。
            RoomConfig.difficultyCalc = "minacalc";
            var e = Flipped();
            e.msdNormal = 0f;
            e.SortSlotsByDisplayLevel();
            Assert.AreEqual(new[] { "e.osu", "n.osu", "h.osu" }, new[] { e.ChartPath(0), e.ChartPath(1), e.ChartPath(2) });
        }

        [Test]
        public void Reorder_LeavesAnAlreadyAscendingSongAlone()
        {
            // 同級（同 MSD）不該互換 —— Assign 的 tie-break 會照音符數重排，那是沒必要的搬動。
            RoomConfig.difficultyCalc = "minacalc";
            var e = Flipped();
            e.msdEasy = e.msdNormal = e.msdHard = 20f;
            e.SortSlotsByDisplayLevel();
            Assert.AreEqual(new[] { "e.osu", "n.osu", "h.osu" }, new[] { e.ChartPath(0), e.ChartPath(1), e.ChartPath(2) });
        }

        // ---- 掃描期：一首歌有 4 個以上難度時，「留哪三張」也照現行算法（ExternalSongScanner.SlotLevels）----

        private static ExternalSongScanner.ChartStats St(int level, float msd)
            => new ExternalSongScanner.ChartStats { Level = level, Msd = msd, DurationSec = 100 };

        [Test]
        public void SlotLevels_OsuMode_UsesTheStarLevel()
        {
            var stats = new[] { St(10, 25f), St(20, 20f), St(30, 15f) };
            Assert.AreEqual(new[] { 10, 20, 30 }, ExternalSongScanner.SlotLevels(stats, byMsd: false).ToArray());
        }

        [Test]
        public void SlotLevels_MinaCalcMode_UsesTheMsdLevel()
        {
            var stats = new[] { St(10, 25f), St(20, 20f), St(30, 15f) };
            var lv = ExternalSongScanner.SlotLevels(stats, byMsd: true);
            Assert.AreEqual(new[] { ManiaMsd.ToLevel(25f), ManiaMsd.ToLevel(20f), ManiaMsd.ToLevel(15f) }, lv.ToArray());
            Assert.Greater(lv[0], lv[2], "MSD 高的才是難的 —— 跟 osu 等級的順序剛好相反");
        }

        [Test]
        public void SlotLevels_FallsBackWhenAnyChartHasNoMsd()
        {
            // 一張算不出 MSD（空譜/太短）就整首退回 osu 等級：兩種尺度混著比，排出來只會更亂。
            var stats = new[] { St(10, 25f), St(20, 0f), St(30, 15f) };
            Assert.AreEqual(new[] { 10, 20, 30 }, ExternalSongScanner.SlotLevels(stats, byMsd: true).ToArray());
        }

        [Test]
        public void SlotLevels_NullSafe()
            => Assert.AreEqual(0, ExternalSongScanner.SlotLevels(null, byMsd: true).Count);

        [Test]
        public void SlotLevels_PicksADifferentTopThree_WhenTheCalcDisagrees()
        {
            // 五個難度的 osu 圖：osu 等級最高的三張是 #2#3#4，但 MSD 最高的三張是 #0#1#4 → 選了哪套就留哪三張。
            var stats = new[] { St(10, 30f), St(12, 28f), St(40, 12f), St(38, 13f), St(36, 26f) };
            var counts = new[] { 100, 100, 100, 100, 100 };

            var byOsu = ExternalDifficultyPicker.Assign(ExternalSongScanner.SlotLevels(stats, false), counts);
            CollectionAssert.AreEquivalent(new[] { 2, 3, 4 }, byOsu);

            var byMsd = ExternalDifficultyPicker.Assign(ExternalSongScanner.SlotLevels(stats, true), counts);
            CollectionAssert.AreEquivalent(new[] { 0, 1, 4 }, byMsd);
        }

        [Test]
        public void Fingerprint_IsUnaffectedByTheSlotOrder()
        {
            // 換一套難度算法時磁碟上什麼都沒變 → 掃描差異不該把整個歌庫報成「已更新」。
            var a = new SongCatalog.Entry
            {
                gn = "x.gn", external = true, title = "t",
                chartEasy = "e.osu", notesEasy = 100, diffEasy = 10,
                chartHard = "h.osu", notesHard = 300, diffHard = 30,
            };
            var b = new SongCatalog.Entry
            {
                gn = "x.gn", external = true, title = "t",
                chartEasy = "h.osu", notesEasy = 300, diffEasy = 30,
                chartHard = "e.osu", notesHard = 100, diffHard = 10,
            };
            Assert.AreEqual(ExternalSongLibrary.Fingerprint(a), ExternalSongLibrary.Fingerprint(b));
        }

        [Test]
        public void Reorder_IsIdempotent()
        {
            RoomConfig.difficultyCalc = "minacalc";
            var e = Flipped();
            e.SortSlotsByDisplayLevel();
            var after = new[] { e.ChartPath(0), e.ChartPath(1), e.ChartPath(2) };
            e.SortSlotsByDisplayLevel();
            Assert.AreEqual(after, new[] { e.ChartPath(0), e.ChartPath(1), e.ChartPath(2) }, "排過的再排一次不該再動");
        }
    }
}
