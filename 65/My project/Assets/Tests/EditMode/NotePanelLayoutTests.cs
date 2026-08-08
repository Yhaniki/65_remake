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

        // ---- 擋板 (lane cover)：從**遠端**往受擊線長；位置隨掉落方式鏡射，圖不鏡射 ----

        [Test]
        public void LaneCover_MaxDepth_Stops_At_The_Receptor_Edge_Not_The_Judge_Line()
        {
            // 600 − (70 + 50)：滿檔停在受擊圖示的外緣，不是判定線 —— 停在 530 會把 100×100 的受擊箭頭切一半。
            Assert.AreEqual(480f, NotePanelLayout.LaneCoverMaxDepth, 1e-4f);
        }

        [Test]
        public void LaneCoverDepth_Maps_Percent_Onto_MaxDepth_And_Clamps()
        {
            Assert.AreEqual(0f, NotePanelLayout.LaneCoverDepth(0f), 1e-4f);
            Assert.AreEqual(240f, NotePanelLayout.LaneCoverDepth(50f), 1e-4f);
            Assert.AreEqual(480f, NotePanelLayout.LaneCoverDepth(100f), 1e-4f);
            Assert.AreEqual(0f, NotePanelLayout.LaneCoverDepth(-20f), 1e-4f);      // 夾回 0
            Assert.AreEqual(480f, NotePanelLayout.LaneCoverDepth(1000f), 1e-4f);   // 夾回 100%
        }

        [Test]
        public void LaneCoverVisibleFraction_Tracks_Depth_Over_MaxDepth()
        {
            // 擋板是「固定尺寸的一張畫，露出下面 frac」，不是把整張壓扁 —— frac 就是深度佔滿檔的比例。
            Assert.AreEqual(0f, NotePanelLayout.LaneCoverVisibleFraction(0f), 1e-4f);
            Assert.AreEqual(0.5f, NotePanelLayout.LaneCoverVisibleFraction(240f), 1e-4f);
            Assert.AreEqual(1f, NotePanelLayout.LaneCoverVisibleFraction(NotePanelLayout.LaneCoverMaxDepth), 1e-4f);
            Assert.AreEqual(0f, NotePanelLayout.LaneCoverVisibleFraction(-10f), 1e-4f);
            Assert.AreEqual(1f, NotePanelLayout.LaneCoverVisibleFraction(9999f), 1e-4f);
        }

        [Test]
        public void LaneCover_Picture_Keeps_Its_Aspect_At_Every_Length()
        {
            // 這是「裁切」與「壓扁」的分水嶺：帶高 = 滿檔帶高 × frac、露出的圖高 = 原圖高 × frac，
            // 兩者同比例 → 縱向縮放在任何長度下都一樣，圖不會被壓扁。
            const float artH = 484f;   // 附的素材高(290×484)
            float baseScale = -1f;
            for (float pct = 5f; pct <= 100f; pct += 5f)
            {
                float depth = NotePanelLayout.LaneCoverDepth(pct);
                float frac = NotePanelLayout.LaneCoverVisibleFraction(depth);
                float scale = depth / (artH * frac);      // 帶高 ÷ 露出的圖高
                if (baseScale < 0f) baseScale = scale;
                Assert.AreEqual(baseScale, scale, 1e-4f, pct + "% 的縱向縮放跟其它長度不一致＝圖被壓扁了");
            }
            Assert.AreEqual(NotePanelLayout.LaneCoverMaxDepth / artH, baseScale, 1e-4f);
        }

        [Test]
        public void LaneCoverBand_Up_Grows_From_The_Board_Bottom()   // 向上：音符由下進場 → 擋板在板底
        {
            var up = NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: true);
            up.LaneCoverBand(200f, out float top, out float bottom);
            Assert.AreEqual(400f, top, 1e-4f);                                   // 600 − 200
            Assert.AreEqual(NotePanelLayout.BoardHeight, bottom, 1e-4f);         // 貼齊板底
        }

        [Test]
        public void LaneCoverBand_Down_Mirrors_To_The_Board_Top()   // 向下：音符由上進場 → 擋板鏡射到板頂
        {
            var down = NotePanelLayout.Resolve(NoteDropDirection.Down, panelLeft: true);
            down.LaneCoverBand(200f, out float top, out float bottom);
            Assert.AreEqual(0f, top, 1e-4f);
            Assert.AreEqual(200f, bottom, 1e-4f);
        }

        [Test]
        public void LaneCoverBand_Up_And_Down_Are_Mirrors_About_The_Board_Centre()
        {
            var up = NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: false);
            var down = NotePanelLayout.Resolve(NoteDropDirection.Down, panelLeft: false);
            for (float depth = 0f; depth <= NotePanelLayout.LaneCoverMaxDepth; depth += 53f)
            {
                up.LaneCoverBand(depth, out float ut, out float ub);
                down.LaneCoverBand(depth, out float dt, out float db);
                // 繞 y300 鏡射：向上的上緣 ↔ 向下的下緣。
                Assert.AreEqual(NotePanelLayout.BoardHeight - ut, db, 1e-4f, "depth " + depth);
                Assert.AreEqual(NotePanelLayout.BoardHeight - ub, dt, 1e-4f, "depth " + depth);
                Assert.AreEqual(depth, ub - ut, 1e-4f, "向上帶長 depth " + depth);
                Assert.AreEqual(depth, db - dt, 1e-4f, "向下帶長 depth " + depth);
            }
        }

        [Test]
        public void LaneCoverBand_Full_Never_Clips_The_Receptor_Graphic()
        {
            float full = NotePanelLayout.LaneCoverMaxDepth;
            // 向上：受擊圖示佔 [20,120]，擋板內緣要停在 120（＝判定線 70 + 半高 50）。
            var up = NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: true);
            up.LaneCoverBand(full, out float ut, out _);
            Assert.AreEqual(up.JudgeLineY + NotePanelLayout.ReceptorHalf, ut, 1e-4f);
            // 向下：受擊圖示佔 [480,580]，擋板內緣停在 480。
            var down = NotePanelLayout.Resolve(NoteDropDirection.Down, panelLeft: true);
            down.LaneCoverBand(full, out _, out float db);
            Assert.AreEqual(down.JudgeLineY - NotePanelLayout.ReceptorHalf, db, 1e-4f);
        }

        [Test]
        public void LaneCoverBand_Clamps_Depth_Into_The_Board()
        {
            var up = NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: true);
            up.LaneCoverBand(-50f, out float t0, out float b0);
            Assert.AreEqual(0f, b0 - t0, 1e-4f);                       // 負的 → 沒有帶
            up.LaneCoverBand(9999f, out float t1, out float b1);
            Assert.AreEqual(NotePanelLayout.LaneCoverMaxDepth, b1 - t1, 1e-4f);
            // 再深也不會咬進受擊圖示（判定線 70 + 半高 50 = 120）
            Assert.AreEqual(NotePanelLayout.TopJudgeY + NotePanelLayout.ReceptorHalf, t1, 1e-4f);
        }

        [Test]
        public void LaneCoverBand_Stays_Inside_The_Note_Clip_Band()
        {
            // 擋板不掛 NoteClip 遮罩（它自己算帶子），所以「本來就落在音符可見帶內」要有測試盯著。
            foreach (var drop in new[] { NoteDropDirection.Up, NoteDropDirection.Down })
            {
                var l = NotePanelLayout.Resolve(drop, panelLeft: true);
                l.LaneCoverBand(NotePanelLayout.LaneCoverMaxDepth, out float t, out float b);
                Assert.GreaterOrEqual(t, l.ClipTopY, drop + " 擋板上緣跑出音符可見帶");
                Assert.LessOrEqual(b, l.ClipBottomY, drop + " 擋板下緣跑出音符可見帶");
            }
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

        // ---- lane click-flash / MISS-wash band (NOTES_BOARD_CLICK, 67×558) ----

        private const float ShippedStripH = 558f;   // notes_board_click{1..4}.png native height

        [Test]
        public void ClickStrip_Runs_From_The_Judgment_Cell_Frame_To_The_Board_Bottom()   // 向上: [36, 600]
        {
            NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: true)
                           .ClickStripBand(out float top, out float bottom);
            Assert.AreEqual(NotePanelLayout.ClickStripTopMargin, top, 1e-4f);   // 亮端＝判定區格子的上邊框 36
            Assert.AreEqual(NotePanelLayout.BoardHeight, bottom, 1e-4f);        // 淡端補到板底，不留一段沒有光的板面
        }

        [Test]
        public void ClickStrip_Down_Is_The_Exact_Mirror_Of_Up()   // 向下: [0, 564]
        {
            NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: true).ClickStripBand(out float ut, out float ub);
            NotePanelLayout.Resolve(NoteDropDirection.Down, panelLeft: false).ClickStripBand(out float dt, out float db);
            Assert.AreEqual(NotePanelLayout.BoardHeight - ub, dt, 1e-4f);   // 600−600 = 0：淡端貼齊板頂
            Assert.AreEqual(NotePanelLayout.BoardHeight - ut, db, 1e-4f);   // 600−36 = 564：亮端跟著受擊線翻到板底
            Assert.AreEqual(ub - ut, db - dt, 1e-4f);                       // 長度相同 → 兩邊拉伸量一致
        }

        [Test]
        public void ClickStrip_Stretch_Is_Tiny_And_Equal_In_Both_Directions()
        {
            // 帶子(564)比貼圖(558)長 6px，呼叫端拉伸 1.1% 去補滿板子。舊版從板面 12 起算要拉 5.4%，
            // 亮端還因此凸到判定格上框之外(被 NoteClip 帶硬切出一條亮邊)——實機一眼看得出沒對齊。
            NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: true).ClickStripBand(out float ut, out float ub);
            NotePanelLayout.Resolve(NoteDropDirection.Down, panelLeft: true).ClickStripBand(out float dt, out float db);
            float upStretch = (ub - ut) / ShippedStripH, downStretch = (db - dt) / ShippedStripH;
            Assert.AreEqual(upStretch, downStretch, 1e-4f, "兩邊拉伸量必須一樣，否則向上/向下的漸層看起來不同");
            Assert.Greater(upStretch, 1f, "帶子要比貼圖長(才補得滿板底)");
            Assert.Less(upStretch, 1.02f, "拉伸幅度要 ≤2%(舊的 12 起點是 5.4%)");
        }

        [Test]
        public void ClickStrip_Bright_End_Aligns_With_The_Judgment_Cell_Frame_In_Both_Directions()
        {
            // 亮端到受擊線的距離，兩個方向必須一樣(＝有跟著顛倒)；而且那個距離就是判定格上框到格心的 34px，
            // 也就是「光條上緣貼齊判定區的線」這件事的數值版。
            var up = NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: true);
            var down = NotePanelLayout.Resolve(NoteDropDirection.Down, panelLeft: true);
            up.ClickStripBand(out float ut, out _);
            down.ClickStripBand(out _, out float db);
            Assert.AreEqual(up.JudgeLineY - ut, db - down.JudgeLineY, 1e-4f);
            Assert.AreEqual(up.JudgeLineY - NotePanelLayout.ClickStripTopMargin, up.JudgeLineY - ut, 1e-4f);
        }

        [Test]
        public void ClickStrip_Bright_End_Clears_The_Note_Clip_Band()
        {
            // NoteClip 帶([30,600] / [0,570])不能切到軌條光——被切到就會在判定框外留一條滿亮的硬邊。
            foreach (var drop in new[] { NoteDropDirection.Up, NoteDropDirection.Down, NoteDropDirection.Tilt })
            {
                var l = NotePanelLayout.Resolve(drop, panelLeft: true);
                l.ClickStripBand(out float top, out float bottom);
                Assert.GreaterOrEqual(top, l.ClipTopY, drop + "：亮/淡端不可落在 clip 帶之外");
                Assert.LessOrEqual(bottom, l.ClipBottomY, drop.ToString());
            }
        }

        [Test]
        public void ClickStrip_Horizontal_Anchor_Does_Not_Move_It()
        {
            NotePanelLayout.Resolve(NoteDropDirection.Down, panelLeft: true).ClickStripBand(out float lt, out float lb);
            NotePanelLayout.Resolve(NoteDropDirection.Down, panelLeft: false).ClickStripBand(out float ct, out float cb);
            Assert.AreEqual(lt, ct, 1e-4f);
            Assert.AreEqual(lb, cb, 1e-4f);
        }

        [Test]
        public void ClickStrip_Tilt_Matches_Up()
        {
            NotePanelLayout.Resolve(NoteDropDirection.Tilt, panelLeft: true).ClickStripBand(out float tt, out float tb);
            NotePanelLayout.Resolve(NoteDropDirection.Up, panelLeft: true).ClickStripBand(out float ut, out float ub);
            Assert.AreEqual(ut, tt, 1e-4f);
            Assert.AreEqual(ub, tb, 1e-4f);
        }

        [Test]
        public void ClickStrip_Band_Stays_Inside_The_Board()
        {
            foreach (var drop in new[] { NoteDropDirection.Up, NoteDropDirection.Down, NoteDropDirection.Tilt })
            {
                NotePanelLayout.Resolve(drop, panelLeft: true).ClickStripBand(out float top, out float bottom);
                Assert.GreaterOrEqual(top, 0f, drop.ToString());
                Assert.LessOrEqual(bottom, NotePanelLayout.BoardHeight, drop.ToString());
                Assert.Greater(bottom, top, drop.ToString());
            }
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
