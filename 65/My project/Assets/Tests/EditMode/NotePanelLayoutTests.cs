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
        public void ClipBand_Tilt_Matches_Down()
        {
            var tilt = NotePanelLayout.Resolve(NoteDropDirection.Tilt, panelLeft: true);
            var down = NotePanelLayout.Resolve(NoteDropDirection.Down, panelLeft: true);
            Assert.AreEqual(down.ClipTopY, tilt.ClipTopY, 1e-4f);
            Assert.AreEqual(down.ClipBottomY, tilt.ClipBottomY, 1e-4f);
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

        // ---- 傾斜 (tilt): no researched visual yet → behaves like 向下 (bottom + down-scroll) ----

        [Test]
        public void Tilt_Behaves_Like_Down_For_Now()
        {
            var tilt = NotePanelLayout.Resolve(NoteDropDirection.Tilt, panelLeft: true);
            var down = NotePanelLayout.Resolve(NoteDropDirection.Down, panelLeft: true);
            Assert.AreEqual(down.JudgeLineY, tilt.JudgeLineY, 1e-4f);
            Assert.AreEqual(down.ScrollSign, tilt.ScrollSign);
            Assert.AreEqual(down.Bottom, tilt.Bottom);
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

        [Test]
        public void IntOverload_Clamps_OutOfRange()
        {
            // negative → 向上, anything ≥2 → 傾斜 (== 向下 behaviour), never throws
            Assert.IsFalse(NotePanelLayout.Resolve(-5, panelLeft: true).Bottom);   // clamped to Up
            Assert.IsTrue(NotePanelLayout.Resolve(99, panelLeft: true).Bottom);    // clamped to Tilt
            Assert.AreEqual(NotePanelLayout.TopJudgeY, NotePanelLayout.Resolve(-5, true).JudgeLineY, 1e-4f);
            Assert.AreEqual(NotePanelLayout.BottomJudgeY, NotePanelLayout.Resolve(99, true).JudgeLineY, 1e-4f);
        }
    }
}
