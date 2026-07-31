using System;

namespace Sdo.Osu
{
    /// <summary>
    /// Builds the song-select CD disc image out of a song's cover art — the same disc the official songs ship as
    /// ICONS/&lt;id&gt;.PNG, so an external song's disc is indistinguishable from an official one.
    ///
    /// Geometry is taken verbatim from the CD generator tool (bms/tools/cd_generator_gui.py), which measured it off
    /// the game's own cd.png: at the 237px reference size the cover is cut to a disc of r=106, the white rim runs
    /// r=103..109, and the two-tone hub is six alternating light/dark bands out to r=32. The light bands are drawn at
    /// <see cref="DefaultHubAlpha"/> (≈80%) so the cover shows through the hub, and the spindle hole is left filled —
    /// the tool's defaults.
    ///
    /// The cover is NEVER stretched: it is scaled until it covers the disc and the overflow is cropped away
    /// (a 4:1 StepMania banner keeps its proportions — its height is matched to the disc and the sides are cut off).
    ///
    /// Pure (RGBA bytes in, RGBA bytes out) and orientation-agnostic — the disc is radially symmetric and the crop is
    /// centred, so callers may pass either top-down (PNG) or bottom-up (Unity) rows and get the same convention back.
    /// </summary>
    public static class CdImageComposer
    {
        /// <summary>Canvas of the official ICONS discs — square, so the output is always size×size.</summary>
        public const int DefaultSize = 237;

        /// <summary>Alpha of the light hub bands (0–255). 204 ≈ 80%: the cover reads through the hub.</summary>
        public const int DefaultHubAlpha = 204;

        // Radii measured at the 237px reference (cd_generator_gui.py).
        public const double RefSize = 237.0;
        public const double RingInnerRef = 103.0;
        public const double RingOuterRef = 109.0;
        /// <summary>The cover is cut at the MIDDLE of the white rim, so its edge hides under the rim instead of
        /// meeting the transparent outside (that seam is what shows up as a dark fringe).</summary>
        public const double DiscRadiusRef = (RingInnerRef + RingOuterRef) / 2.0;

        // Hub: two colours, six bands (light, dark, light, dark, light, dark) given by their outer radius.
        private static readonly double[] HubBandOuterRef = { 16.0, 18.0, 21.0, 23.0, 29.0, 32.0 };
        private static readonly byte[] HubLight = { 216, 221, 231 };
        private static readonly byte[] HubDark = { 133, 148, 181 };

        /// <summary>Edge smoothing: the disc/rim/band edges are rasterised at N× and box-filtered down.</summary>
        public const int DefaultSupersample = 3;

        /// <summary>Radius (in output pixels) the cover art is cut to.</summary>
        public static double DiscRadius(int size) => DiscRadiusRef * size / RefSize;

        /// <summary>
        /// Compose the disc. <paramref name="srcRgba"/> is width×height×4 (RGBA8, row-major). Returns
        /// size×size×4, or null when the source is unusable.
        /// </summary>
        public static byte[] Compose(byte[] srcRgba, int srcW, int srcH, int size = DefaultSize,
            int hubAlpha = DefaultHubAlpha, int supersample = DefaultSupersample)
        {
            if (srcRgba == null || srcW <= 0 || srcH <= 0 || size <= 0) return null;
            if (srcRgba.Length < srcW * srcH * 4) return null;

            int ss = supersample < 1 ? 1 : supersample;
            int a = hubAlpha < 0 ? 0 : (hubAlpha > 255 ? 255 : hubAlpha);
            double k = size / RefSize;                       // reference px → output px
            double c = (size - 1) / 2.0;                     // disc centre
            double rDisc = DiscRadiusRef * k;
            double rRingIn = RingInnerRef * k;
            double rRingOut = RingOuterRef * k + 0.65 * k;   // the tool's half-pixel overshoot, scaled

            // COVER, not stretch: blow the cover up until it covers the square, then crop what hangs over, centred.
            double scale = Math.Max(size / (double)srcW, size / (double)srcH);
            double offX = (srcW * scale - size) / 2.0;
            double offY = (srcH * scale - size) / 2.0;

            var outRgba = new byte[size * size * 4];
            double subs = ss * ss;

            for (int py = 0; py < size; py++)
                for (int px = 0; px < size; px++)
                {
                    // Accumulate the subsamples PREMULTIPLIED — averaging straight RGBA would pull the transparent
                    // outside (rgb=0) into the rim and ring the disc in black.
                    double sr = 0, sg = 0, sb = 0, sa = 0;
                    for (int sy = 0; sy < ss; sy++)
                        for (int sx = 0; sx < ss; sx++)
                        {
                            double fx = px + (sx + 0.5) / ss - 0.5;
                            double fy = py + (sy + 0.5) / ss - 0.5;
                            double dx = fx - c, dy = fy - c;
                            double d = Math.Sqrt(dx * dx + dy * dy);
                            if (d > rRingOut) continue;   // outside the disc → transparent

                            double r = 0, g = 0, b = 0, al = 0;
                            if (d <= rDisc)
                            {
                                // cover art, sampled through the crop
                                double u = (fx + 0.5 + offX) / scale - 0.5;
                                double v = (fy + 0.5 + offY) / scale - 0.5;
                                Sample(srcRgba, srcW, srcH, u, v, out r, out g, out b, out al);
                                r *= al; g *= al; b *= al;   // premultiply the cover before the hub goes over it
                            }

                            // hub + rim, src-over the cover
                            if (d >= rRingIn && d <= rRingOut)
                            {
                                r = 255; g = 255; b = 255; al = 1.0;
                            }
                            else
                            {
                                int band = BandAt(d, k);
                                if (band >= 0)
                                {
                                    bool light = (band % 2) == 0;
                                    byte[] col = light ? HubLight : HubDark;
                                    double ha = light ? a / 255.0 : 1.0;
                                    r = col[0] * ha + r * (1 - ha);
                                    g = col[1] * ha + g * (1 - ha);
                                    b = col[2] * ha + b * (1 - ha);
                                    al = ha + al * (1 - ha);
                                }
                            }

                            sr += r; sg += g; sb += b; sa += al;
                        }

                    int o = (py * size + px) * 4;
                    double aa = sa / subs;
                    if (aa <= 0.0) { outRgba[o] = outRgba[o + 1] = outRgba[o + 2] = outRgba[o + 3] = 0; continue; }
                    outRgba[o] = Byte(sr / subs / aa);       // un-premultiply
                    outRgba[o + 1] = Byte(sg / subs / aa);
                    outRgba[o + 2] = Byte(sb / subs / aa);
                    outRgba[o + 3] = Byte(aa * 255.0);
                }

            return outRgba;
        }

        /// <summary>Index of the hub band a radius falls in (0 = innermost light band), or −1 outside the hub.</summary>
        private static int BandAt(double d, double k)
        {
            for (int i = 0; i < HubBandOuterRef.Length; i++)
                if (d < HubBandOuterRef[i] * k) return i;
            return -1;
        }

        // Bilinear source sample at pixel-centre coordinates, clamped at the edges. Alpha comes back 0..1.
        private static void Sample(byte[] src, int w, int h, double u, double v,
            out double r, out double g, out double b, out double a)
        {
            int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
            double tx = u - x0, ty = v - y0;
            int x1 = Clamp(x0 + 1, w), y1 = Clamp(y0 + 1, h);
            x0 = Clamp(x0, w); y0 = Clamp(y0, h);

            int i00 = (y0 * w + x0) * 4, i10 = (y0 * w + x1) * 4;
            int i01 = (y1 * w + x0) * 4, i11 = (y1 * w + x1) * 4;
            double w00 = (1 - tx) * (1 - ty), w10 = tx * (1 - ty), w01 = (1 - tx) * ty, w11 = tx * ty;

            r = src[i00] * w00 + src[i10] * w10 + src[i01] * w01 + src[i11] * w11;
            g = src[i00 + 1] * w00 + src[i10 + 1] * w10 + src[i01 + 1] * w01 + src[i11 + 1] * w11;
            b = src[i00 + 2] * w00 + src[i10 + 2] * w10 + src[i01 + 2] * w01 + src[i11 + 2] * w11;
            a = (src[i00 + 3] * w00 + src[i10 + 3] * w10 + src[i01 + 3] * w01 + src[i11 + 3] * w11) / 255.0;
        }

        private static int Clamp(int i, int n) => i < 0 ? 0 : (i >= n ? n - 1 : i);

        private static byte Byte(double v) => (byte)(v < 0 ? 0 : (v > 255 ? 255 : Math.Round(v)));
    }
}
