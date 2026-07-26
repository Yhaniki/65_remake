using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Pixel sizing for every "render 3D into a RenderTexture, then show it in the UI" surface (the room backdrop, the
    /// gameplay stage backdrop, the gender-select dancer, the shop/wardrobe previews).
    ///
    /// The trap this exists to close: those RTs used to be sized from their LOGICAL slot aspect (e.g. the backdrop's
    /// <c>Screen.height × 4/3</c>), but the UI is a fixed 800×600 frame that AspectController stretches — non-uniformly,
    /// in <see cref="AspectMode.Stretch"/> — across the whole window. So a slot's real estate on screen is
    /// <c>slotW × Screen.width/800</c> by <c>slotH × Screen.height/600</c>, and on any window that isn't exactly 4:3
    /// those two scales DIFFER. An RT sized to the logical aspect therefore ends up narrower (or shorter) than the pixels
    /// it is stretched across, and the RawImage magnifies it — soft 3D. (Symptom that led here: the room avatar looked
    /// blurry at every window size while the 192×152 head-portrait RT, which is DOWNsampled into its 96×76 slot, stayed
    /// crisp. Diagnosing this class of bug = compare RT pixels against the slot's on-screen pixels, per axis.)
    ///
    /// Two rules for any caller:
    ///  1. Size the RT with <see cref="SlotRtSize"/> (window-derived, oversampled) — never from the logical aspect.
    ///  2. PIN <see cref="Camera.aspect"/> to the LOGICAL slot aspect. The projection used to be inferred from the RT
    ///     shape; once the RT follows the window, an unpinned camera would widen its field of view with the window and
    ///     the composition would drift. Pinning reproduces exactly what AspectController does to the front-end cameras,
    ///     so pixel count is the only thing that changes.
    /// </summary>
    public static class RtSizing
    {
        public const float LogicalW = 800f, LogicalH = 600f;   // the official SDO frame every UI slot is authored in
        public const int MaxDim = 4096;                        // per-axis cap: a 4K window ×1.5 would be 5760 wide
        public const float DefaultSupersample = 1.5f;           // render above window size, filter down → crisp
        public const float ResizeSettleSeconds = 0.15f;         // window must hold still this long before re-allocating

        /// <summary>Pixel size for an RT that fills a <paramref name="slotW"/>×<paramref name="slotH"/> slot of the
        /// logical 800×600 frame, given the current window. Oversampled by <paramref name="supersample"/> (clamped to
        /// 1..2), floored at the slot's own logical size (a tiny window must not render below the original art) and
        /// capped per axis at <see cref="MaxDim"/>. Pass the full 800×600 for a full-screen backdrop.</summary>
        public static void SlotRtSize(int screenW, int screenH, float slotW, float slotH, float supersample,
                                      out int w, out int h)
        {
            float ss = Mathf.Clamp(supersample, 1f, 2f);
            float sx = Mathf.Max(1, screenW) / LogicalW;   // logical→screen scale, per axis: they differ off 4:3
            float sy = Mathf.Max(1, screenH) / LogicalH;
            int floorW = Mathf.Max(1, Mathf.RoundToInt(slotW));
            int floorH = Mathf.Max(1, Mathf.RoundToInt(slotH));
            w = Mathf.Clamp(Mathf.RoundToInt(slotW * sx * ss), floorW, MaxDim);
            h = Mathf.Clamp(Mathf.RoundToInt(slotH * sy * ss), floorH, MaxDim);
        }

        /// <summary>Re-allocate <paramref name="rt"/> to w×h IN PLACE, keeping the same RenderTexture instance so every
        /// RawImage/material already pointing at it stays wired (the callers assign the texture once, at build time).
        /// Returns false if nothing changed. A created RT must be Released before width/height are writable.</summary>
        public static bool Apply(RenderTexture rt, int w, int h)
        {
            if (rt == null || (rt.width == w && rt.height == h)) return false;
            rt.Release();
            rt.width = w; rt.height = h;
            rt.Create();
            return true;
        }
    }

    /// <summary>
    /// Debounce for window-resize-driven RT re-allocation. Dragging a window edge changes Screen.width/height EVERY
    /// frame; re-allocating a multi-megabyte 4×MSAA RT per frame stutters the drag, so the new size must hold still for
    /// <see cref="RtSizing.ResizeSettleSeconds"/> first. During the drag the old RT keeps being upscaled (i.e. it looks
    /// like it did before any of this), then snaps sharp once the size settles.
    /// Pure logic — "now" is passed in — so it is unit-testable without a running player.
    /// </summary>
    public struct RtResizeTracker
    {
        private int _w, _h;
        private float _pendingAt;   // time the settled size may be applied at; &lt;0 = nothing pending

        /// <summary>Seed with the window size the RT was just built for.</summary>
        public void Reset(int screenW, int screenH)
        {
            _w = screenW; _h = screenH; _pendingAt = -1f;
        }

        /// <summary>Call once per frame. Returns true on the single frame the RT should be re-allocated.</summary>
        public bool Tick(int screenW, int screenH, float now)
        {
            if (screenW != _w || screenH != _h)
            {
                _w = screenW; _h = screenH;
                _pendingAt = now + RtSizing.ResizeSettleSeconds;   // still moving — restart the settle window
                return false;
            }
            if (_pendingAt < 0f || now < _pendingAt) return false;
            _pendingAt = -1f;
            return true;
        }
    }
}
