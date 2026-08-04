using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Pure-geometry tests for <see cref="NotePanelLayout"/> — the note-panel placement resolved from
    /// 掉落方式 (向上/向下/傾斜) × NOTES面板位置 (屏幕左邊/屏幕中央). Covers the three official screens
    /// (向上置中 / 向下置中 / 向下左邊) plus the fourth combination and the int-overload clamping.
    /// </summary>
    public class NotePanelLayoutTests
    {
        // ---- constants derive correctly from the frame/board sizes ----

        [Test]
        public void Constants_Match_Design_Frame()
        {
            Assert.AreEqual(70f, NotePanelLayout.TopJudgeY, 1e-4f);
            Assert.AreEqual(530f, NotePanelLayout.BottomJudgeY, 1e-4f);           // mirror of 70 about 300
            Assert.AreEqual(0f, NotePanelLayout.LeftOffsetX, 1e-4f);
            Assert.AreEqual(242.5f, NotePanelLayout.CenterOffsetX, 1e-4f);        // (800 − 315) / 2
        }

        [Test]
        public void BottomJudge_Mirrors_TopJudge_About_BoardCentre()
        {
            // board is 600 tall; the down-scroll receptor is the vertical mirror of the up-scroll one.
            Assert.AreEqual(600f - NotePanelLayout.TopJudgeY, NotePanelLayout.BottomJudgeY, 1e-4f);
        }

        // ---- note clip band: the hidden strip mirrors with the drop direction ----

        [Test]
        public void ClipBand_Up_Hides_Strip_At_The_Top()   // 向上: [30, 600]
        {
            var up = NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: true);
            Assert.AreEqual(NotePanelLayout.ClipMargin, up.ClipTopY, 1e-4f);        // 30px strip behind the top frame/HP bar
            Assert.AreEqual(NotePanelLayout.BoardHeight, up.ClipBottomY, 1e-4f);    // down to the board bottom (600)
        }

        [Test]
        public void ClipBand_Down_Mirrors_Strip_To_The_Bottom()   // 向下: [0, 570]
        {
            var down = NotePanelLayout.Resolve(NoteDropDirection.Down, panelLeft: false);
            Assert.AreEqual(0f, down.ClipTopY, 1e-4f);                                              // notes emerge flush from the top
            Assert.AreEqual(NotePanelLayout.BoardHeight - NotePanelLayout.ClipMargin, down.ClipBottomY, 1e-4f);  // 570: hidden strip now at the bottom (flipped frame)
        }

        [Test]
        public void ClipBand_Reflects_About_BoardCentre_When_Flipped()
        {
            // the whole band is the up band reflected about y300 (600 − y), just like BottomJudgeY mirrors TopJudgeY.
            var up = NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: true);
            var down = NotePanelLayout.Resolve(NoteDropDirection.Down, panelLeft: true);
            Assert.AreEqual(NotePanelLayout.BoardHeight - up.ClipBottomY, down.ClipTopY, 1e-4f);
            Assert.AreEqual(NotePanelLayout.BoardHeight - up.ClipTopY, down.ClipBottomY, 1e-4f);
        }

        [Test]
        public void ClipBand_Horizontal_Anchor_Does_Not_Move_It()
        {
            // clip band is purely vertical — 屏幕左邊/置中 must not change it.
            var left = NotePanelLayout.Resolve(NoteDropDirection.Down, panelLeft: true);
            var center = NotePanelLayout.Resolve(NoteDropDirection.Down, panelLeft: false);
            Assert.AreEqual(left.ClipTopY, center.ClipTopY, 1e-4f);
            Assert.AreEqual(left.ClipBottomY, center.ClipBottomY, 1e-4f);
        }

        [Test]
        public void ClipBand_Tilt_Matches_Up()
        {
            var tilt = NotePanelLayout.Resolve(NoteDropDirection.Tilt, panelLeft: true);
            var up = NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: true);
            Assert.AreEqual(up.ClipTopY, tilt.ClipTopY, 1e-4f);
            Assert.AreEqual(up.ClipBottomY, tilt.ClipBottomY, 1e-4f);
        }

        // ---- the four (drop × horizontal) combinations ----

        [Test]
        public void Up_Left_Is_The_Official_Default()   // panelLeft default true + dropDirection default 0
        {
            var l = NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: true);
            Assert.AreEqual(0f, l.OffsetX, 1e-4f);
            Assert.AreEqual(70f, l.JudgeLineY, 1e-4f);
            Assert.AreEqual(+1, l.ScrollSign);
            Assert.IsFalse(l.Bottom);
        }

        [Test]
        public void Up_Center_Screen()   // 向上置中
        {
            var l = NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: false);
            Assert.AreEqual(242.5f, l.OffsetX, 1e-4f);
            Assert.AreEqual(70f, l.JudgeLineY, 1e-4f);
            Assert.AreEqual(+1, l.ScrollSign);
            Assert.IsFalse(l.Bottom);
        }

        [Test]
        public void Down_Center_Screen()   // 向下置中
        {
            var l = NotePanelLayout.Resolve(NoteDropDirection.Down, panelLeft: false);
            Assert.AreEqual(242.5f, l.OffsetX, 1e-4f);
            Assert.AreEqual(530f, l.JudgeLineY, 1e-4f);
            Assert.AreEqual(-1, l.ScrollSign);
            Assert.IsTrue(l.Bottom);
        }

        [Test]
        public void Down_Left_Screen()   // 向下左邊
        {
            var l = NotePanelLayout.Resolve(NoteDropDirection.Down, panelLeft: true);
            Assert.AreEqual(0f, l.OffsetX, 1e-4f);
            Assert.AreEqual(530f, l.JudgeLineY, 1e-4f);
            Assert.AreEqual(-1, l.ScrollSign);
            Assert.IsTrue(l.Bottom);
        }

        // ---- 傾斜 (tilt): no researched visual yet → behaves like 向上 (top + up-scroll); 房間下拉也不再列它 ----

        [Test]
        public void Tilt_Behaves_Like_Up_For_Now()
        {
            var tilt = NotePanelLayout.Resolve(NoteDropDirection.Tilt, panelLeft: true);
            var up = NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: true);
            Assert.AreEqual(up.JudgeLineY, tilt.JudgeLineY, 1e-4f);
            Assert.AreEqual(up.ScrollSign, tilt.ScrollSign);
            Assert.AreEqual(up.Bottom, tilt.Bottom);
        }

        // ---- 房間 win2「掉落方式」下拉：由上而下＝向上 / 向下，傾斜不上架 ----

        [Test]
        public void Menu_Lists_Up_Then_Down_Only()
        {
            Assert.AreEqual(2, NotePanelLayout.MenuRowCount);
            Assert.AreEqual(NoteDropDirection.Up, NotePanelLayout.FromMenuRow(0));    // 第一列＝向上
            Assert.AreEqual(NoteDropDirection.Down, NotePanelLayout.FromMenuRow(1));  // 第二列＝向下
        }

        [Test]
        public void Menu_Row_Round_Trips_The_Stored_Value()
        {
            Assert.AreEqual(0, NotePanelLayout.MenuRow((int)NoteDropDirection.Up));
            Assert.AreEqual(1, NotePanelLayout.MenuRow((int)NoteDropDirection.Down));
            for (int row = 0; row < NotePanelLayout.MenuRowCount; row++)
                Assert.AreEqual(row, NotePanelLayout.MenuRow((int)NotePanelLayout.FromMenuRow(row)));
        }

        [Test]
        public void Menu_Row_Falls_Back_To_Up_For_Values_Not_In_The_Menu()
        {
            // 舊 config.ini 可能存著 2＝傾斜（選單已不列它）；比照 Resolve 的處置退回「向上」那一列。
            Assert.AreEqual(0, NotePanelLayout.MenuRow((int)NoteDropDirection.Tilt));
            Assert.AreEqual(0, NotePanelLayout.MenuRow(-1));
            Assert.AreEqual(0, NotePanelLayout.MenuRow(99));
        }

        [Test]
        public void Menu_Row_Index_Is_Clamped()
        {
            Assert.AreEqual(NoteDropDirection.Up, NotePanelLayout.FromMenuRow(-3));
            Assert.AreEqual(NoteDropDirection.Down, NotePanelLayout.FromMenuRow(7));
        }

        // ---- horizontal anchor only moves X, never the vertical fields ----

        [Test]
        public void Horizontal_Anchor_Only_Changes_OffsetX()
        {
            var left = NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: true);
            var center = NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: false);
            Assert.AreNotEqual(left.OffsetX, center.OffsetX);
            Assert.AreEqual(left.JudgeLineY, center.JudgeLineY, 1e-4f);
            Assert.AreEqual(left.ScrollSign, center.ScrollSign);
            Assert.AreEqual(left.Bottom, center.Bottom);
        }

        [Test]
        public void Drop_Direction_Only_Changes_Vertical_Fields()
        {
            var up = NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: false);
            var down = NotePanelLayout.Resolve(NoteDropDirection.Down, panelLeft: false);
            Assert.AreEqual(up.OffsetX, down.OffsetX, 1e-4f);   // same horizontal anchor
            Assert.AreNotEqual(up.JudgeLineY, down.JudgeLineY);
            Assert.AreNotEqual(up.ScrollSign, down.ScrollSign);
        }

        // ---- int overload (raw GameSession.DropDirection) clamps and matches the enum overload ----

        [Test]
        public void IntOverload_Matches_EnumOverload()
        {
            for (int d = 0; d <= 2; d++)
            {
                var byInt = NotePanelLayout.Resolve(d, panelLeft: true);
                var byEnum = NotePanelLayout.Resolve((NoteDropDirection)d, panelLeft: true);
                Assert.AreEqual(byEnum.JudgeLineY, byInt.JudgeLineY, 1e-4f, $"drop={d}");
                Assert.AreEqual(byEnum.ScrollSign, byInt.ScrollSign, $"drop={d}");
                Assert.AreEqual(byEnum.OffsetX, byInt.OffsetX, 1e-4f, $"drop={d}");
            }
        }

        // ---- ShowTime 一律靠左（氣條/徽章/SPACE/ENERGYSCORE 是絕對座標，board 置中會壓到它們）----

        [Test]
        public void Showtime_Forces_Left_Even_When_The_Player_Picked_Centre()
        {
            Assert.IsTrue(NotePanelLayout.EffectivePanelLeft(panelLeft: false, showtime: true), "ShowTime 忽略置中");
            Assert.AreEqual(NotePanelLayout.LeftOffsetX,
                            NotePanelLayout.Resolve(NoteDropDirection.Up,
                                NotePanelLayout.EffectivePanelLeft(false, true)).OffsetX, 1e-4f);
        }

        [Test]
        public void Non_Showtime_Keeps_The_Player_Setting()
        {
            // 一般模式完全照舊：置中就是置中、靠左就是靠左（這條壞掉＝把所有人的置中都吃掉了）。
            Assert.IsFalse(NotePanelLayout.EffectivePanelLeft(panelLeft: false, showtime: false));
            Assert.IsTrue(NotePanelLayout.EffectivePanelLeft(panelLeft: true, showtime: false));
            Assert.AreEqual(NotePanelLayout.CenterOffsetX,
                            NotePanelLayout.Resolve(NoteDropDirection.Up,
                                NotePanelLayout.EffectivePanelLeft(false, false)).OffsetX, 1e-4f);
        }

        [Test]
        public void Showtime_Does_Not_Touch_The_Drop_Direction()
        {
            // 只擋水平位置；ShowTime 一樣能向下（受擊線/捲動方向不受影響）。
            var st = NotePanelLayout.Resolve(NoteDropDirection.Down, NotePanelLayout.EffectivePanelLeft(false, true));
            Assert.AreEqual(NotePanelLayout.LeftOffsetX, st.OffsetX, 1e-4f);
            Assert.AreEqual(NotePanelLayout.BottomJudgeY, st.JudgeLineY, 1e-4f);
            Assert.AreEqual(-1, st.ScrollSign);
            Assert.IsTrue(st.Bottom);
        }

        [Test]
        public void IntOverload_Clamps_OutOfRange()
        {
            // negative → 向上, anything ≥2 → 傾斜 (== 向上 behaviour, 傾斜沒實作), never throws
            Assert.IsFalse(NotePanelLayout.Resolve(-5, panelLeft: true).Bottom);   // clamped to Up
            Assert.IsFalse(NotePanelLayout.Resolve(99, panelLeft: true).Bottom);   // clamped to Tilt → 比照向上
            Assert.AreEqual(NotePanelLayout.TopJudgeY, NotePanelLayout.Resolve(-5, true).JudgeLineY, 1e-4f);
            Assert.AreEqual(NotePanelLayout.TopJudgeY, NotePanelLayout.Resolve(99, true).JudgeLineY, 1e-4f);
        }
    }
}
