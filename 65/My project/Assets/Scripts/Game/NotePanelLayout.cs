namespace Sdo.Game
{
    /// <summary>掉落方式 (Room win2「掉落方式」→ <c>GameSession.DropDirection</c>) — where the receptors sit and which
    /// way the notes travel toward them.</summary>
    public enum NoteDropDirection
    {
        /// <summary>向上：receptors at the TOP, notes rise UP to them (official default / the only Phase-1 direction).</summary>
        Up = 0,
        /// <summary>向下：receptors at the BOTTOM, notes fall DOWN to them.</summary>
        Down = 1,
        /// <summary>傾斜：斜向/旋轉 — 官方視覺尚未考據，暫時比照 向下 (bottom + down-scroll)。</summary>
        Tilt = 2,
    }

    /// <summary>
    /// Note-panel placement — pure geometry (no Unity types, fully unit-testable) resolved from the two orthogonal
    /// player settings that position the gameplay note board:
    /// <list type="bullet">
    ///   <item>掉落方式 (vertical) — <see cref="NoteDropDirection"/> from Room win2「掉落方式」下拉：向上/向下/傾斜.</item>
    ///   <item>NOTES面板位置 (horizontal) — <c>GameSettings.gameplay.notesPanelLeft</c> from OPTION 遊戲 頁：
    ///         <c>true</c>=屏幕左邊 (default, board at design x 0..315) / <c>false</c>=屏幕中央 (band centred).</item>
    /// </list>
    /// Official screens combine the two: 向上置中 / 向下置中 / 向下左邊 (docs/…/scroll-directions.md). Consumed by
    /// <c>ScreenGameplay</c> (<c>_panelOffsetX</c> / <c>judgeLineY</c> / <c>_scrollSign</c>); all coordinates are in
    /// SdoLayout's 800×600 top-left design space, so <see cref="OffsetX"/> adds straight onto any panel-relative X and
    /// <see cref="JudgeLineY"/> is a design-Y.
    /// </summary>
    public readonly struct NotePanelLayout
    {
        /// <summary>800×600 design-frame width (= SdoLayout.Width).</summary>
        public const float FrameWidth = 800f;
        /// <summary>NOTES_BOARD1.PNG native width; the board is always drawn 1:1 (never scaled), so this is fixed.</summary>
        public const float BoardWidth = 315f;
        /// <summary>Official up-scroll receptor / hit-line Y (design px). notes_board1 is 600 tall; 70 ≈ just under the HP bar.</summary>
        public const float TopJudgeY = 70f;
        /// <summary>Down-scroll receptor Y — the mirror of <see cref="TopJudgeY"/> about the board's vertical centre (600/2 = 300).</summary>
        public const float BottomJudgeY = 600f - TopJudgeY;   // 530
        /// <summary>Left-anchored board X offset (default): the band occupies design x 0..315.</summary>
        public const float LeftOffsetX = 0f;
        /// <summary>Centred board X offset: (frame − board)/2 so the 315-wide band sits centred (157.5→400) in the 800 frame.</summary>
        public const float CenterOffsetX = (FrameWidth - BoardWidth) / 2f;   // 242.5
        /// <summary>notes_board1 height (design px); the clip band's far edge and the mirror axis (÷2 = 300).</summary>
        public const float BoardHeight = 600f;
        /// <summary>Hidden strip (design px) at the receptor/frame end of the play band: notes are masked out here so
        /// they slip behind the chamfered board frame + HP bar rather than poking past the top of the board.</summary>
        public const float ClipMargin = 30f;

        /// <summary>Design-px added to EVERY panel-relative X (board / receptors / notes / HP bar / score / combo).
        /// 0 = 屏幕左邊, +242.5 = 屏幕中央.</summary>
        public readonly float OffsetX;
        /// <summary>Receptor / hit-line Y (design px): <see cref="TopJudgeY"/> for 向上, <see cref="BottomJudgeY"/> for 向下/傾斜.</summary>
        public readonly float JudgeLineY;
        /// <summary>+1 = notes approach the judge line from BELOW (up-scroll, 向上); −1 = from ABOVE (down-scroll, 向下/傾斜).</summary>
        public readonly int ScrollSign;
        /// <summary><c>true</c> = receptors sit at the bottom (向下 / 傾斜); <c>false</c> = at the top (向上).</summary>
        public readonly bool Bottom;
        /// <summary>Top edge (smaller design-Y) of the note clip band — notes are masked to [<see cref="ClipTopY"/>,
        /// <see cref="ClipBottomY"/>]. 向上: <see cref="ClipMargin"/> (hidden strip behind the top frame/HP bar).
        /// 向下 flips the whole board about the centre (300), so the strip mirrors to the far end → 0.</summary>
        public readonly float ClipTopY;
        /// <summary>Bottom edge (larger design-Y) of the note clip band. 向上: <see cref="BoardHeight"/> (frame bottom).
        /// 向下: <c>BoardHeight − ClipMargin</c> (570) — the hidden strip is now at the bottom, behind the flipped frame.</summary>
        public readonly float ClipBottomY;

        public NotePanelLayout(float offsetX, float judgeLineY, int scrollSign, bool bottom)
        {
            OffsetX = offsetX;
            JudgeLineY = judgeLineY;
            ScrollSign = scrollSign;
            Bottom = bottom;
            // The clip band mirrors with the drop direction (like JudgeLineY): the ClipMargin hidden strip sits at the
            // receptor/frame end, so 向上 = [30, 600] and 向下 = [0, 570] (the whole band reflected about y300).
            ClipTopY = bottom ? 0f : ClipMargin;
            ClipBottomY = bottom ? BoardHeight - ClipMargin : BoardHeight;
        }

        /// <summary>Resolve the panel layout from the two player settings.</summary>
        /// <param name="drop">掉落方式 (向上/向下/傾斜).</param>
        /// <param name="panelLeft">OPTION「NOTES面板位置」：<c>true</c>=屏幕左邊 / <c>false</c>=屏幕中央.</param>
        public static NotePanelLayout Resolve(NoteDropDirection drop, bool panelLeft)
        {
            bool bottom = drop != NoteDropDirection.Up;   // 向下 & 傾斜 → bottom receptors + down-scroll
            return new NotePanelLayout(
                offsetX: panelLeft ? LeftOffsetX : CenterOffsetX,
                judgeLineY: bottom ? BottomJudgeY : TopJudgeY,
                scrollSign: bottom ? -1 : +1,
                bottom: bottom);
        }

        /// <summary>Convenience overload taking the raw <c>GameSession.DropDirection</c> int (clamped to 0..2).</summary>
        public static NotePanelLayout Resolve(int dropDirection, bool panelLeft)
            => Resolve((NoteDropDirection)Clamp(dropDirection, 0, 2), panelLeft);

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
