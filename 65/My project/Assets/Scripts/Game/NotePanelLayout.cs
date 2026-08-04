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
        /// <summary>傾斜：斜向/旋轉 — 官方視覺尚未考據，**暫時比照 向上** (top + up-scroll)；
        /// 房間下拉把它排在 向上 與 向下 之間，選了等於沒換方向。</summary>
        Tilt = 2,
    }

    /// <summary>
    /// Note-panel placement — pure geometry (no Unity types, fully unit-testable) resolved from the two orthogonal
    /// player settings that position the gameplay note board:
    /// <list type="bullet">
    ///   <item>掉落方式 (vertical) — <see cref="NoteDropDirection"/> from Room win2「掉落方式」下拉：向上/向下
    ///         （傾斜沒實作 → 不上架，舊設定檔存的值比照向上）.</item>
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
        /// <summary>Receptor / hit-line Y (design px): <see cref="TopJudgeY"/> for 向上/傾斜, <see cref="BottomJudgeY"/> for 向下.</summary>
        public readonly float JudgeLineY;
        /// <summary>+1 = notes approach the judge line from BELOW (up-scroll, 向上/傾斜); −1 = from ABOVE (down-scroll, 向下).</summary>
        public readonly int ScrollSign;
        /// <summary><c>true</c> = receptors sit at the bottom (向下); <c>false</c> = at the top (向上 / 傾斜).</summary>
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
            bool bottom = drop == NoteDropDirection.Down;   // 只有 向下 → bottom receptors + down-scroll（傾斜尚無實作 → 比照向上）
            return new NotePanelLayout(
                offsetX: panelLeft ? LeftOffsetX : CenterOffsetX,
                judgeLineY: bottom ? BottomJudgeY : TopJudgeY,
                scrollSign: bottom ? -1 : +1,
                bottom: bottom);
        }

        /// <summary>Convenience overload taking the raw <c>GameSession.DropDirection</c> int (clamped to 0..2).</summary>
        public static NotePanelLayout Resolve(int dropDirection, bool panelLeft)
            => Resolve((NoteDropDirection)Clamp(dropDirection, 0, 2), panelLeft);

        // ---- 房間 win2「掉落方式」下拉的選項 ----
        // 清單由上而下＝向上 / 向下。傾斜沒有實作（比照向上），所以**不上架**——選單不列它。
        // 但值本身仍是官方語意 0=向上 1=向下 2=傾斜：舊的 config.ini 可能存著 2，讀進來 MenuRow 找不到就退回第 0 列
        // （向上），正好對上 Resolve 對傾斜的處置，設定檔不必跟著改。
        private static readonly int[] MenuValues = { (int)NoteDropDirection.Up, (int)NoteDropDirection.Down };

        /// <summary>下拉清單的列數（＝掉落方式選項數）。</summary>
        public static int MenuRowCount => MenuValues.Length;

        /// <summary>清單第 <paramref name="row"/> 列 → 掉落方式值（超出範圍夾回兩端）。</summary>
        public static NoteDropDirection FromMenuRow(int row)
            => (NoteDropDirection)MenuValues[Clamp(row, 0, MenuValues.Length - 1)];

        /// <summary>掉落方式值 → 清單第幾列（不在選單裡的值——例如舊設定檔的 2＝傾斜——退回第 0 列＝向上）。</summary>
        public static int MenuRow(int dropDirection)
        {
            for (int i = 0; i < MenuValues.Length; i++)
                if (MenuValues[i] == dropDirection) return i;
            return 0;
        }

        /// <summary>實際生效的「NOTES面板位置」：**ShowTime 模式一律靠左**，玩家選的置中在該模式直接忽略。
        /// 理由是 ShowTime 專屬 HUD（氣條 MyEnergy 框、×2/×4/×8 徽章、SPACE 提示、ENERGYSCORE/ENERGYBONUS 數字）
        /// 全部照官方 PLAYSHOWTIME XML 的**絕對座標**擺，不吃 <see cref="OffsetX"/> 的面板位移 —— board 一移到中央
        /// (+242.5) 就會壓在那一整組上。只擋遊戲內取用，不動使用者存的設定（下一局非 ShowTime 仍然置中）。
        /// 掉落方式（向上/向下/傾斜）不受影響，ShowTime 一樣可以向下。</summary>
        /// <param name="panelLeft">玩家在 OPTION 遊戲頁選的：<c>true</c>=屏幕左邊 / <c>false</c>=屏幕中央.</param>
        /// <param name="showtime">是否 ShowTime 模式（房間/選歌 模式＝2）.</param>
        public static bool EffectivePanelLeft(bool panelLeft, bool showtime) => panelLeft || showtime;

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
