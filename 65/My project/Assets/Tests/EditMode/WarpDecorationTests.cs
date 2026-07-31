using NUnit.Framework;
using Sdo.Osu;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    /// <summary>
    /// 「歌曲變速」關閉(constantScroll)時,warp(負 BPM)掃掉的裝飾音不畫 —— <see cref="WarpDecoration"/>。
    /// 最後一條測的是**動機**:同一批音符在變速關的模式下確實會疊成一坨。
    /// </summary>
    public class WarpDecorationTests
    {
        private static OsuHitObject Fake(int lane = 0, int t = 2000, bool bomb = false)
            => new OsuHitObject(lane, t, null, bomb, isFake: true, scrollTimeMs: t - 0.5);

        private static OsuHitObject Real(int lane = 0, int t = 2000, bool bomb = false)
            => new OsuHitObject(lane, t, null, bomb);

        [Test]
        public void Hidden_Only_When_Song_Speed_Is_Off()
        {
            Assert.IsTrue(WarpDecoration.IsHidden(Fake(), constantScroll: true));
            Assert.IsFalse(WarpDecoration.IsHidden(Fake(), constantScroll: false), "歌曲變速開 → warp 窗還在,照畫");
        }

        [Test]
        public void Hidden_Never_Touches_Playable_Notes()
        {
            Assert.IsFalse(WarpDecoration.IsHidden(Real(), constantScroll: true));
            Assert.IsFalse(WarpDecoration.IsHidden(Real(bomb: true), constantScroll: true), "warp 外的炸彈是要躲的目標,不能藏");
        }

        [Test]
        public void Hidden_Off_In_The_Chart_Editor()
        {
            Assert.IsFalse(WarpDecoration.IsHidden(Fake(), constantScroll: true, editorMode: true), "編輯器要看得到譜上所有東西");
        }

        [Test]
        public void CanRetire_Waits_For_The_Judgment_Time_To_Pass()
        {
            var n = Fake(t: 2000);
            Assert.IsFalse(WarpDecoration.CanRetire(n, 1999.0, bombCursorMs: 1e9), "還沒到 → 留著");
            Assert.IsFalse(WarpDecoration.CanRetire(n, 2000.0, bombCursorMs: 1e9), "剛好在那一刻也還不收");
            Assert.IsTrue(WarpDecoration.CanRetire(n, 2000.1, bombCursorMs: 1e9));
        }

        [Test]
        public void CanRetire_Keeps_A_Warp_Mine_Until_The_Crossing_Cursor_Passes_It()
        {
            // warp 炸彈是「按住穿過 warp = 自動打擊」的觸發器:顯示端跑在 TickBombs 前面,提早收掉的話同一幀的
            // 跨線偵測只會看到 Done,gimmick 整批不發生。
            var mine = Fake(t: 2000, bomb: true);
            Assert.IsFalse(WarpDecoration.CanRetire(mine, 2016.0, bombCursorMs: 1990.0), "跨線游標還沒到 → 這一幀留著");
            Assert.IsTrue(WarpDecoration.CanRetire(mine, 2032.0, bombCursorMs: 2016.0), "游標過了 → 下一幀就收得掉");
        }

        // ---------------------------------------------------------------------------------------------------
        // 為什麼要藏:「歌曲變速」關 = ManiaScroll 丟掉所有 timing point,連 SmChart 給 warp 補的 1ms 超高速
        // 顯示窗也一起沒了 —— 那個窗正是「被跳過的拍子照拍距鋪開」的唯一機制。
        // ---------------------------------------------------------------------------------------------------

        // beats 4..8 是 -120、8..12 是 120 → 播放頭在 2000ms 這一瞬間從 beat 4 跳到 beat 12(同 SmChartTests.Warp)。
        private const string WarpSm =
            "#TITLE:W;\n#OFFSET:0;\n#BPMS:0=120,4=-120,8=120;\n" +
            "#NOTES:\n     dance-single:\n     :\n     Easy:\n     1:\n     0,0,0,0,0:\n" +
            "1000\n1000\n1000\n1000\n,\n" +
            "1000\n1000\n1000\n1000\n,\n" +
            "1000\n1000\n1000\n1000\n,\n" +
            "1000\n1000\n1000\n1000\n;\n";

        [Test]
        public void Warp_Notes_Collapse_Into_One_Spot_When_Song_Speed_Is_Off()
        {
            var map = SmChart.ToBeatmap(SmChart.Parse(WarpSm), 0);
            // 120 BPM × speed 1.0 × 1.6 = 192 px/s → 一拍(500ms)= 96 px
            var varying = ManiaScroll.Build(map, 1.0, constantScroll: false, referenceBpm: 120.0);
            var constant = ManiaScroll.Build(map, 1.0, constantScroll: true, referenceBpm: 120.0);

            for (int i = 5; i <= 10; i++)   // warp 內的 7 顆(beats 5..11)兩兩相鄰
            {
                double a = map.HitObjects[i].ScrollTimeMs, b = map.HitObjects[i + 1].ScrollTimeMs;
                Assert.AreEqual(96.0, varying.PixelDistance(a, b), 1.0, $"變速開:beat {i}→{i + 1} 該差一整拍");
                Assert.Less(constant.PixelDistance(a, b), 1.0, $"變速關:beat {i}→{i + 1} 疊在一起(所以才要藏)");
            }
        }
    }
}
