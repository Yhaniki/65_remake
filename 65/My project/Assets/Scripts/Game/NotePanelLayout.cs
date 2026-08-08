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
        /// <summary>受擊端邊界(design px)給軌條光用：**判定區格子的上邊框那條線**。NOTES_BOARD1 的板面雖然從 y12
        /// 起(0..11 是缺角)，但 y12..34 那一段是血條佔的黑帶，判定區的四個圓角格子是從 y36 才開始
        /// （量測：36..38 有一條橫框線，下邊框在 y99；格子中心 67 ≈ judgeLineY 70）。
        /// 光條從 y12 起會讓它的上緣**凸在判定格上框之上**——而且 NoteClip 帶([30,600])會把它硬切在 y30，
        /// 那 6px 是滿亮的一條硬邊橫跨在框線外，實機一眼就看得出沒對齊。改成 36 = 貼齊判定區上邊緣。</summary>
        public const float ClickStripTopMargin = 36f;
        /// <summary>NOTES_BOARD_CLICK{1..4}.PNG 原生高(design px)。帶子是 600−36 = 564，所以呼叫端把它拉伸
        /// 1.1%（舊的 12 起點要拉 5.4%）—— 落在一條平滑漸層上，看不出來。</summary>
        public const float ClickStripArtHeight = 558f;

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

        /// <summary>Vertical band (design-Y) covered by the per-lane click-flash glow strip (NOTES_BOARD_CLICK{1..4},
        /// 67×558) and the track-wide MISS wash that reuses the same art. The strip's BRIGHT end is the one at the
        /// receptors; the art fades out toward the far end. 兩端各自釘在該釘的地方：
        /// <list type="bullet">
        ///   <item><b>亮端</b>貼齊**判定區格子的邊框線** (<see cref="ClickStripTopMargin"/> = 36)。從板面起點 12 起會
        ///         凸在判定框之外，而且 NoteClip 帶([30,600])會把它硬切成一條滿亮的橫邊 —— 使用者實機回報的
        ///         「打擊時綠色/粉紅的軌道線比判定區的線高、凸出來」就是這個。</item>
        ///   <item><b>淡端</b>補到板子的另一頭，不留一段沒有光的板面。</item>
        /// </list>
        /// 向上 [36, 600] ↔ 向下 [0, 564]，兩個方向是彼此的鏡射(繞 y300)。
        /// <para>帶長 564 比貼圖的 <see cref="ClickStripArtHeight"/> 558 多 6px，呼叫端據此拉伸 1.1%
        /// （舊的 12 起點要拉 5.4%）。兩個方向拉一樣多，所以漸層看起來一致；而且它落在一條平滑漸層、
        /// 遠端 alpha 只剩 27/255 的尾巴上，看不出來。</para></summary>
        /// <param name="topY">Top edge (smaller design-Y) of the band.</param>
        /// <param name="bottomY">Bottom edge (larger design-Y) of the band.</param>
        public void ClickStripBand(out float topY, out float bottomY)
        {
            topY = Bottom ? 0f : ClickStripTopMargin;
            bottomY = Bottom ? BoardHeight - ClickStripTopMargin : BoardHeight;
        }

        /// <summary>受擊圖示(JUDGELINE，100×100 畫 1:1)的一半高 —— 它是**繞著判定線置中**的，所以判定區實際佔
        /// [judgeLineY ± 50]（向上＝[20,120]）。擋板的深度上限要看這個邊緣，不是判定線本身。</summary>
        public const float ReceptorHalf = 50f;

        /// <summary>擋板(lane cover)最深能伸到哪：從板子的**遠端**(音符進場那一頭)量到**受擊圖示的外緣**
        /// （600 − (70+50) = 480），不是量到判定線。停在判定線(530)的話滿檔會把 100×100 的受擊箭頭切掉一半 ——
        /// 那是玩家用來瞄準的東西，任何設定都不該蓋掉它。向下只是整塊繞 y300 鏡射，兩個方向一樣深。</summary>
        public const float LaneCoverMaxDepth = BoardHeight - TopJudgeY - ReceptorHalf;   // 480

        /// <summary>擋板的長度百分比(0..100，＝設定面板那根拉桿) → 深度 design px。純函式。</summary>
        public static float LaneCoverDepth(float percent)
            => Clamp(percent, 0f, 100f) / 100f * LaneCoverMaxDepth;

        /// <summary>
        /// 擋板圖要露出來的比例(0..1)——**從圖的下緣往上算**。
        ///
        /// 🔴 擋板不是「把整張圖壓扁塞進帶子」，而是像官方一樣：圖是**固定尺寸的一張畫**，拉桿只決定
        /// 露出它下面多少。證據是 iidx.org 對幾張圖的註記 ——「Initiation 的**下半部**整片全黑
        /// (up to white number 126)」「Exhaust Hype 的下半部是深藍」「獅子霞麗ノ舞 的**下面 30%** 幾乎全白」：
        /// 擋板短的時候看到的是**圖的下面那一段**，所以是裁切、不是縮放。
        ///
        /// 素材(290×484)本來就照滿檔的帶子(276×480)畫的，所以裁出來那一段直接鋪進帶子，
        /// 縱向比例在任何長度下都一樣(480/484)，不會有壓扁感。
        /// </summary>
        /// <param name="depth">目前的深度(design px)，見 <see cref="LaneCoverDepth"/>。</param>
        public static float LaneCoverVisibleFraction(float depth)
            => LaneCoverMaxDepth <= 0f ? 0f : Clamp(depth, 0f, LaneCoverMaxDepth) / LaneCoverMaxDepth;

        /// <summary>
        /// 擋板(lane cover)佔的縱向帶(design-Y)。它從板子的**遠端** —— 音符進場的那一頭、受擊線的對面 ——
        /// 往判定區長 <paramref name="depth"/> px：
        /// <list type="bullet">
        ///   <item>向上(受擊線在頂)：音符由下往上跑 → 擋板在**板底**，帶 = [600−depth, 600]。</item>
        ///   <item>向下(受擊線在底)：音符由上往下掉 → 擋板鏡射到**板頂**，帶 = [0, depth]。</item>
        /// </list>
        /// 🔴 <b>只有位置鏡射，圖本身不鏡射</b>：呼叫端畫這張圖時 <c>flipY</c> 永遠是 false。板子/軌條光/紅幕那幾張
        /// 是「亮端要對著受擊線」的漸層，翻轉才對；擋板是一張獨立的**畫**（IIDX 那種 lane cover），翻過來就是
        /// 一張上下顛倒的畫，兩個方向都該照原樣看。
        /// <para>圖怎麼貼進這條帶子：**畫面朝上、下緣釘在帶子的下緣**（design-Y 較大的那一端），只露出
        /// <see cref="LaneCoverVisibleFraction"/> 那麼多。兩個方向都是「短擋板＝看到圖的下面一段」，跟官方一致：
        /// 向上時圖釘在板底、往上長出來；向下時圖跟著前緣一起往下滑，上面超出板頂的部分裁掉。</para>
        /// </summary>
        /// <param name="depth">伸出來多長(design px)；自動夾在 [0, <see cref="LaneCoverMaxDepth"/>]。</param>
        /// <param name="topY">帶的上緣(較小的 design-Y)。</param>
        /// <param name="bottomY">帶的下緣(較大的 design-Y)。</param>
        public void LaneCoverBand(float depth, out float topY, out float bottomY)
        {
            depth = Clamp(depth, 0f, LaneCoverMaxDepth);
            topY = Bottom ? 0f : BoardHeight - depth;
            bottomY = Bottom ? depth : BoardHeight;
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

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
