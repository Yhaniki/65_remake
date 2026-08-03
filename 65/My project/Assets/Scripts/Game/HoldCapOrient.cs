namespace Sdo.Game
{
    /// <summary>
    /// Vertical-flip rule for a long/hold note's END-CAP sprite. Two families of note-skin art:
    /// <list type="bullet">
    /// <item><b>Combined "funnel" caps</b> (NOTEIMAGE_5/8/9/10: <c>updown_long_bottom</c> / <c>rightleft_long_bottom</c>) —
    /// one shared cap per lane pair that must point AWAY from the judge line, so it flips with the scroll direction
    /// (向下 = notes fall, cap sits at the top → flip so the taper points up/outward).</item>
    /// <item><b>Per-lane arrow caps</b> (NOTEIMAGE_6: <c>up/down/left/right_long_bottom</c>) — a mini-arrow already drawn to
    /// match the lane's own arrow direction (like the note head, which is never scroll-flipped). These must NOT take the
    /// scroll flip — flipping the up-lane arrow in 向下 makes it point the wrong way.</item>
    /// </list>
    /// A per-skin <paramref name="bakedFlip"/> corrects art that ships upside-down (NOTEIMAGE_8's updown cap is stored
    /// inverted vs every other skin) and always applies. Kept pure + unit-tested because the draw path clobbers the
    /// sprite's flipY every frame and this rule has regressed twice.
    ///
    /// 🔴 這條規則只管**只有一張尾帽**的 skin。skin 若另外附了「上緣封口」版(<c>*_long_head</c> —
    /// NOTEIMAGE_6/11/showtime),尾端在上時直接改用那張、不翻轉,根本不會走到這裡(見
    /// <c>ScreenGameplay._holdCapHead</c>)。per-lane 的箭頭尾帽正是因為翻不得才需要那張:
    /// 不翻 → 向下捲時封口的黑邊正對長條(接縫黑線),翻了 → 上下軌的箭頭指反。
    /// </summary>
    public static class HoldCapOrient
    {
        /// <param name="perLane">cap art is a per-lane pre-drawn glyph (oriented per lane) → never scroll-flipped.</param>
        /// <param name="bakedFlip">cap texture ships upside-down for this skin/lane → always flipped.</param>
        /// <param name="downScroll">向下 (scrollSign &lt; 0): notes fall, cap sits at the top of the hold.</param>
        public static bool FlipY(bool perLane, bool bakedFlip, bool downScroll)
            => perLane ? bakedFlip : (downScroll ^ bakedFlip);
    }
}
