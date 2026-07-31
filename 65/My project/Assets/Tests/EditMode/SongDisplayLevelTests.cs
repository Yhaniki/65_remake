using NUnit.Framework;
using Sdo.Game;
using Sdo.Osu;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// 難度數字的顯示規則(<see cref="SongCatalog.Entry.DisplayLevel"/>)。重點是**新的等級算法只套用在難度得自己
    /// 重算的外部譜面**(osu / StepMania / Malody):.gn 不論來自官方 DATA/MUSIC 還是玩家丟進外部資料夾的歌包,
    /// 都自帶檔頭寫死的難度,切到哪套計算器都不准改。
    /// </summary>
    public class SongDisplayLevelTests
    {
        private string _saved;

        [SetUp] public void SetUp() { _saved = RoomConfig.difficultyCalc; }
        [TearDown] public void TearDown() { RoomConfig.difficultyCalc = _saved; }

        // 三個難度槽都填同一組值,方便逐槽驗。
        private static SongCatalog.Entry Entry(bool external, SongFormat format, int level, float msd)
            => new SongCatalog.Entry
            {
                gn = "x.gn", external = external, chartFormat = (int)format,
                diffEasy = level, diffNormal = level + 1, diffHard = level + 2,
                notesEasy = 100, notesNormal = 200, notesHard = 300,
                msdEasy = msd, msdNormal = msd, msdHard = msd,
            };

        [Test]
        public void External_Osu_Song_Uses_MinaCalc_Level_When_Selected()
        {
            var e = Entry(external: true, format: SongFormat.Osu, level: 40, msd: 20f);
            RoomConfig.difficultyCalc = "osu";
            Assert.AreEqual(40, e.DisplayLevel(0), "osu 模式 → 星數等級");

            RoomConfig.difficultyCalc = "minacalc";
            Assert.AreEqual(ManiaMsd.ToLevel(20f), e.DisplayLevel(0));
            Assert.AreNotEqual(40, e.DisplayLevel(0), "minacalc 模式下外部 osu 譜的顯示值應該換算過");
        }

        [Test]
        public void External_Sm_And_Malody_Songs_Follow_The_Same_Rule()
        {
            RoomConfig.difficultyCalc = "minacalc";
            foreach (var fmt in new[] { SongFormat.Sm, SongFormat.Malody })
            {
                var e = Entry(external: true, format: fmt, level: 40, msd: 18f);
                Assert.AreEqual(ManiaMsd.ToLevel(18f), e.DisplayLevel(1), fmt + " 也是要自己算難度的外部譜");
            }
        }

        [Test]
        public void Official_Gn_Keeps_Its_Own_Level_In_Every_Mode()
        {
            // 官方 DATA/MUSIC 的 .gn:external=false,連 msd 欄位都不會有值。
            var e = Entry(external: false, format: SongFormat.None, level: 12, msd: 0f);
            foreach (var calc in new[] { "osu", "minacalc" })
            {
                RoomConfig.difficultyCalc = calc;
                Assert.AreEqual(12, e.DisplayLevel(0), calc);
                Assert.AreEqual(13, e.DisplayLevel(1), calc);
                Assert.AreEqual(14, e.DisplayLevel(2), calc);
            }
        }

        [Test]
        public void External_Gn_Pack_Keeps_Its_Header_Level_In_Every_Mode()
        {
            // 玩家丟進 Songs/ 的 .gn 歌包:external=true,但難度一樣是檔頭寫的,不准被換算掉。
            var e = Entry(external: true, format: SongFormat.Gn, level: 25, msd: 0f);
            foreach (var calc in new[] { "osu", "minacalc" })
            {
                RoomConfig.difficultyCalc = calc;
                Assert.AreEqual(25, e.DisplayLevel(0), calc);
                Assert.AreEqual(26, e.DisplayLevel(1), calc);
                Assert.AreEqual(27, e.DisplayLevel(2), calc);
            }
        }

        [Test]
        public void External_Gn_Pack_Ignores_A_Stray_Msd()
        {
            // 掃描器不會給 .gn 算 MSD(留 0),但就算哪天有人塞了值進來(舊快取、手改),.gn 的等級照樣不動。
            var e = Entry(external: true, format: SongFormat.Gn, level: 25, msd: 22f);
            RoomConfig.difficultyCalc = "minacalc";
            Assert.AreEqual(25, e.DisplayLevel(0), ".gn 的難度是檔頭的值,MSD 不該蓋過它");
        }

        [Test]
        public void Unknown_Msd_Falls_Back_To_The_Star_Level()
        {
            // 外部 osu 譜但 MSD 沒算出來(空譜/太短) → 退回星數等級,不會顯示 0。
            var e = Entry(external: true, format: SongFormat.Osu, level: 33, msd: 0f);
            RoomConfig.difficultyCalc = "minacalc";
            Assert.AreEqual(33, e.DisplayLevel(0));
        }
    }
}
