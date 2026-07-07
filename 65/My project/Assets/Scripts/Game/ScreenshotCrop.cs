using System;

namespace Sdo.Game
{
    /// <summary>
    /// Screenshot crop math (pure / Unity-free so it's unit-tested). Maps a normalized viewport rect
    /// (bottom-left origin, 0..1 — the actual game frame, minus any pillar/letterbox bars) onto integer pixel
    /// coordinates of a captured image, clamped in bounds.
    /// </summary>
    public static class ScreenshotCrop
    {
        /// <summary>
        /// Convert a normalized viewport rect into an integer pixel rect clamped to a <paramref name="texW"/>×
        /// <paramref name="texH"/> image. Returns true when the result is a strict sub-rect (cropping is needed);
        /// false when it covers the whole image (bars absent → encode as-is).
        /// </summary>
        public static bool PixelRect(float nx, float ny, float nw, float nh, int texW, int texH,
                                     out int x, out int y, out int w, out int h)
        {
            x = Clamp(Round(nx * texW), 0, Math.Max(0, texW));
            y = Clamp(Round(ny * texH), 0, Math.Max(0, texH));
            w = Clamp(Round(nw * texW), 1, Math.Max(1, texW - x));
            h = Clamp(Round(nh * texH), 1, Math.Max(1, texH - y));
            return !(x == 0 && y == 0 && w == texW && h == texH);
        }

        private static int Round(float v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
