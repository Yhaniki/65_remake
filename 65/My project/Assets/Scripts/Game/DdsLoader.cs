using System;
using UnityEngine;

namespace Sdo.Game
{
    public enum DdsAlphaMode
    {
        Opaque,
        Cutout,
        Blend,
    }

    /// <summary>
    /// DDS loader for SDO textures. DXT1/DXT5 (BC1/BC3) go straight to a compressed Texture2D; DXT3 (BC2) —
    /// which Unity has no native TextureFormat for — is CPU-decoded to RGBA32 (ported from bms_sdo/dds_codec).
    /// Many stage textures (REN audience, DENG lights…) are DXT3, so without this they showed as white squares.
    /// Header: "DDS " | DDS_HEADER(124): dwHeight@12, dwWidth@16, pixelformat.fourCC@84. Base mip only.
    /// </summary>
    public static class DdsLoader
    {
        /// <summary>
        /// True if a DDS carries non-trivial alpha (transparent or partly-transparent texels) — the signal the
        /// original used (per-material flag 0x20000) to ALPHA-BLEND a stage prop's "去背" cut-out instead of
        /// painting it opaque. DXT1 / uncompressed reports opaque; DXT3/DXT5 are sampled and report alpha when any
        /// texel drops below <paramref name="threshold"/> (default 250 ≈ "not fully solid"). Cheap: early-outs on
        /// the first transparent block. Pure (byte[] -> bool), so it's unit-tested.
        /// </summary>
        public static bool HasAlpha(byte[] d, int threshold = 250)
        {
            return GetAlphaMode(d, threshold) != DdsAlphaMode.Opaque;
        }

        public static DdsAlphaMode GetAlphaMode(byte[] d, int threshold = 250, int softLow = 8, int softHigh = 247)
        {
            if (d == null || d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || d[3] != ' ') return DdsAlphaMode.Opaque;
            string fourcc = System.Text.Encoding.ASCII.GetString(d, 84, 4);
            int height = BitConverter.ToInt32(d, 12), width = BitConverter.ToInt32(d, 16);
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return DdsAlphaMode.Opaque;

            int bw = Math.Max(1, (width + 3) / 4), bh = Math.Max(1, (height + 3) / 4);
            int bi = 128;
            DdsAlphaMode mode = DdsAlphaMode.Opaque;
            if (fourcc == "DXT3")
            {
                for (int b = 0; b < bw * bh && bi + 16 <= d.Length; b++, bi += 16)
                    for (int k = 0; k < 8; k++)
                    {
                        int ab = d[bi + k];
                        mode = AccumulateAlphaMode((ab & 0xF) * 255 / 15, mode, threshold, softLow, softHigh);
                        if (mode == DdsAlphaMode.Blend) return mode;
                        mode = AccumulateAlphaMode(((ab >> 4) & 0xF) * 255 / 15, mode, threshold, softLow, softHigh);
                        if (mode == DdsAlphaMode.Blend) return mode;
                    }
                return mode;
            }
            if (fourcc == "DXT5")
            {
                for (int b = 0; b < bw * bh && bi + 16 <= d.Length; b++, bi += 16)
                {
                    int a0 = d[bi], a1 = d[bi + 1];
                    ulong bits = 0;
                    for (int k = 0; k < 6; k++) bits |= (ulong)d[bi + 2 + k] << (8 * k);
                    for (int i = 0; i < 16; i++)
                    {
                        int code = (int)((bits >> (i * 3)) & 7);
                        mode = AccumulateAlphaMode(Dxt5Alpha(a0, a1, code), mode, threshold, softLow, softHigh);
                        if (mode == DdsAlphaMode.Blend) return mode;
                    }
                }
                return mode;
            }

            uint pf = BitConverter.ToUInt32(d, 80);
            if ((pf & 0x4u) == 0 && (pf & 0x40u) != 0)
            {
                uint am = BitConverter.ToUInt32(d, 104);
                int bits = BitConverter.ToInt32(d, 88);
                if (bits == 32 && am != 0)
                {
                    int off = 128;
                    if (off + width * height * 4 > d.Length) return DdsAlphaMode.Opaque;
                    int shift = MaskShift(am);
                    uint max = am >> shift;
                    for (int i = 0; i < width * height; i++)
                    {
                        uint px = (uint)(d[off + i * 4] | (d[off + i * 4 + 1] << 8) | (d[off + i * 4 + 2] << 16) | (d[off + i * 4 + 3] << 24));
                        int alpha = max == 0 ? 255 : (int)(((px & am) >> shift) * 255 / max);
                        mode = AccumulateAlphaMode(alpha, mode, threshold, softLow, softHigh);
                        if (mode == DdsAlphaMode.Blend) return mode;
                    }
                    return mode;
                }
            }
            return DdsAlphaMode.Opaque;
        }

        /// <summary>
        /// Alpha classification for STAGE SCENE.MSH materials, by the alpha DISTRIBUTION rather than "first soft pixel
        /// wins". <see cref="GetAlphaMode"/> flips to Blend on the very first soft texel, so a hard-edged fence / truss
        /// / floor that has a few anti-aliased edge texels renders as a non-occluding alpha-BLEND (ZWrite Off) — the
        /// background then bleeds THROUGH it (SCN0020 fixed cam5: the floor / buildings / lights showed in front of the
        /// foreground truss + handrail + dance-floor). Buckets:
        ///   • ANY real hole (hardTransp ≥ 3% of texels) with a solid body (≤75% of visible soft) → Cutout
        ///     (clip the holes, the opaque body writes depth and occludes) — trusses, CHAIN-LINK fences, people
        ///     billboards, AND atlases with a PUNCHED SCREEN HOLE (DALABA = DJ-console UI: a clean alpha-0 rectangle
        ///     over the small video screen so the TV plays THROUGH it; classing it Opaque would render the artist's
        ///     reference idol RGB under that hole and cover the video — exactly the SCN0020 "橘色螢幕擋住" bug). The
        ///     threshold is low (3%, not 15%) because a single screen-hole is only a few % of a big console texture;
        ///   • mostly soft, almost no opaque core (soft > 75% of visible)                         → Blend
        ///     (glass, additive glows — their visible texels are an 84–86% soft gradient);
        ///   • otherwise (≈0% hard-transparent)                                                   → Opaque
        ///     (solid surface whose handful of soft texels are just DXT quantisation — e.g. the dance-floor grid).
        /// Cutout/Opaque both write depth, so the foreground stops being see-through (lights stop bleeding through it).
        /// </summary>
        public static DdsAlphaMode GetSceneAlphaMode(byte[] d)
        {
            if (!AlphaCounts(d, out int total, out int visible, out int soft, out int hardTransp, out _) || visible == 0)
                return DdsAlphaMode.Opaque;
            float softOfVisible = soft / (float)visible;
            float hardTranspFrac = hardTransp / (float)total;
            if (hardTranspFrac >= 0.03f && softOfVisible <= 0.75f) return DdsAlphaMode.Cutout;
            if (softOfVisible >= 0.30f) return DdsAlphaMode.Blend;
            return DdsAlphaMode.Opaque;
        }

        /// <summary>Fraction of ALL texels that are (near-)fully transparent (a≤8). A body garment (coat/pant/one) that
        /// comes back with a HUGE fraction (e.g. 璀璨繁星 男褲 = 94%) has a broken/spurious alpha channel — the RGB art
        /// is fine but the alpha was left at 0 — which the cutout/blend path renders as a see-through wireframe. No real
        /// garment is mostly holes, so the caller forces such textures opaque. Returns 0 for alpha-less formats (DXT1).</summary>
        public static float HardTransparentFraction(byte[] d)
            => AlphaCounts(d, out int total, out _, out _, out int hardTransp, out _) && total > 0 ? hardTransp / (float)total : 0f;

        /// <summary>Fraction of ALL texels whose alpha is GENUINELY intermediate (<see cref="MidLo"/>&lt;a&lt;<see cref="MidHi"/>
        /// ≈ 0.13..0.87). A real sheer FABRIC (lace / mesh / organza) is translucent everywhere → a HIGH fraction here
        /// (Flower Lace Dress 024976_WOMAN_ONE ≈ 0.27-0.35). Two look-alikes score LOW and must NOT be treated as sheer:
        /// a SOLID dress with hard lace-hem holes is BIMODAL — mass at a≈0 and a≈1, few midtones (眉画犹思 037888 ≈ 0.09);
        /// a broken all-0 alpha channel is ≈0. The garment builder routes a high fraction to alpha-BLEND so the skin shows
        /// through the fabric (see <see cref="SdoAvatarBuilder.GarmentAlphaMode"/>). Returns 0 for alpha-less DXT1.</summary>
        public static float TranslucentFraction(byte[] d)
            => AlphaCounts(d, out int total, out _, out _, out _, out int mid) && total > 0 ? mid / (float)total : 0f;

        /// <summary>Everything the avatar/garment material pipeline needs to know about a DDS's alpha, gathered in ONE
        /// pass. <see cref="SdoAvatarBuilder.ResolveDds"/> used to call <see cref="HasAlpha"/>, <see cref="LooksLikeAdditiveGlow"/>,
        /// <see cref="GetSceneAlphaMode"/>, <see cref="HardTransparentFraction"/> and <see cref="TranslucentFraction"/> —
        /// FOUR full walks of the same alpha data (plus the glow pass) per texture. At 商城 scroll rates (a card = 1-3
        /// textures, a page = 8 cards) that was several ms of pure repeat work per card.</summary>
        public struct AlphaStats
        {
            public bool HasAlpha;            // == GetAlphaMode(d) != Opaque (any texel below the 250 threshold)
            public bool AdditiveGlow;        // == LooksLikeAdditiveGlow(d) (only probed when HasAlpha)
            public DdsAlphaMode Scene;       // == GetSceneAlphaMode(d)
            public float HardTransp;         // == HardTransparentFraction(d)
            public float Translucent;        // == TranslucentFraction(d)
        }

        /// <summary>One-pass equivalent of HasAlpha + GetSceneAlphaMode + HardTransparentFraction + TranslucentFraction
        /// (+ LooksLikeAdditiveGlow when there IS alpha). Same results as calling them individually — the alpha walk is
        /// simply shared. Alpha-less formats (DXT1 / unsupported) report the opaque defaults.</summary>
        public static AlphaStats Analyze(byte[] d)
        {
            var s = new AlphaStats { Scene = DdsAlphaMode.Opaque };
            if (!AlphaCounts(d, out int total, out int visible, out int soft, out int hardTransp, out int mid, out int belowThresh) || total == 0)
                return s;
            s.HasAlpha = belowThresh > 0;
            s.HardTransp = hardTransp / (float)total;
            s.Translucent = mid / (float)total;
            if (visible > 0)
            {
                float softOfVisible = soft / (float)visible;
                s.Scene = (s.HardTransp >= 0.03f && softOfVisible <= 0.75f) ? DdsAlphaMode.Cutout
                        : softOfVisible >= 0.30f ? DdsAlphaMode.Blend
                        : DdsAlphaMode.Opaque;
            }
            if (s.HasAlpha) s.AdditiveGlow = LooksLikeAdditiveGlow(d);
            return s;
        }

        // "genuinely translucent" alpha band: exclude near-transparent (holes / AA fringe) AND near-opaque (solid body /
        // AA fringe) so only real sheer-fabric midtones are counted — the signal that separates lace from a solid dress.
        private const int MidLo = 32, MidHi = 224;

        // Walk the base-mip alpha and count: total texels, visible (a>8), soft (8<a<247), hardTransp (a<=8), and mid
        // (genuinely translucent, MidLo<a<MidHi). Mirrors GetAlphaMode's DXT3 / DXT5 / 32-bit-uncompressed decode paths
        // (no early-out). Returns false for formats with no alpha (DXT1 / unsupported) — caller treats those as Opaque.
        private static bool AlphaCounts(byte[] d, out int total, out int visible, out int soft, out int hardTransp, out int mid)
            => AlphaCounts(d, out total, out visible, out soft, out hardTransp, out mid, out _);

        // <paramref name="belowThresh"/> = texels with a &lt; 250, i.e. exactly what makes GetAlphaMode report non-Opaque
        // (HasAlpha). Counted here so Analyze can answer HasAlpha from the SAME walk instead of a second early-out scan.
        private static bool AlphaCounts(byte[] d, out int total, out int visible, out int soft, out int hardTransp, out int mid, out int belowThresh)
        {
            total = visible = soft = hardTransp = mid = belowThresh = 0;
            if (d == null || d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || d[3] != ' ') return false;
            string fourcc = System.Text.Encoding.ASCII.GetString(d, 84, 4);
            int height = BitConverter.ToInt32(d, 12), width = BitConverter.ToInt32(d, 16);
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return false;
            int bw = Math.Max(1, (width + 3) / 4), bh = Math.Max(1, (height + 3) / 4), bi = 128;
            int t = 0, v = 0, s = 0, ht = 0, mt = 0, bt = 0;
            if (fourcc == "DXT3")
            {
                for (int b = 0; b < bw * bh && bi + 16 <= d.Length; b++, bi += 16)
                    for (int k = 0; k < 8; k++)
                    {
                        int ab = d[bi + k];
                        int a0t = (ab & 0xF) * 255 / 15; t++; if (a0t <= 8) ht++; else { v++; if (a0t < 247) s++; } if (a0t > MidLo && a0t < MidHi) mt++; if (a0t < 250) bt++;
                        int a1t = ((ab >> 4) & 0xF) * 255 / 15; t++; if (a1t <= 8) ht++; else { v++; if (a1t < 247) s++; } if (a1t > MidLo && a1t < MidHi) mt++; if (a1t < 250) bt++;
                    }
            }
            else if (fourcc == "DXT5")
            {
                for (int b = 0; b < bw * bh && bi + 16 <= d.Length; b++, bi += 16)
                {
                    int a0 = d[bi], a1 = d[bi + 1]; ulong bits = 0;
                    for (int k = 0; k < 6; k++) bits |= (ulong)d[bi + 2 + k] << (8 * k);
                    for (int i = 0; i < 16; i++)
                    {
                        int a = Dxt5Alpha(a0, a1, (int)((bits >> (i * 3)) & 7));
                        t++; if (a <= 8) ht++; else { v++; if (a < 247) s++; } if (a > MidLo && a < MidHi) mt++; if (a < 250) bt++;
                    }
                }
            }
            else
            {
                uint pf = BitConverter.ToUInt32(d, 80);
                uint am = BitConverter.ToUInt32(d, 104);
                int bitc = BitConverter.ToInt32(d, 88);
                if ((pf & 0x4u) != 0 || (pf & 0x40u) == 0 || bitc != 32 || am == 0 || 128 + width * height * 4 > d.Length) return false;
                int off = 128, shift = MaskShift(am); uint max = am >> shift;
                for (int i = 0; i < width * height; i++)
                {
                    uint px = (uint)(d[off + i * 4] | (d[off + i * 4 + 1] << 8) | (d[off + i * 4 + 2] << 16) | (d[off + i * 4 + 3] << 24));
                    int a = max == 0 ? 255 : (int)(((px & am) >> shift) * 255 / max);
                    t++; if (a <= 8) ht++; else { v++; if (a < 247) s++; } if (a > MidLo && a < MidHi) mt++; if (a < 250) bt++;
                }
            }
            total = t; visible = v; soft = s; hardTransp = ht; mid = mt; belowThresh = bt;
            return t > 0;
        }

        /// <summary>
        /// Heuristic for mapobj textures that are authored as light/glow sprites, not ordinary cut-out art.
        /// These should be additive: soft alpha dominates, visible pixels are very bright, and there is little
        /// hard opaque silhouette. This keeps people/sign/billboard alpha blended while stage bulbs, lasers and
        /// sweeping light decals add energy to the scene like the original fixed-function render path.
        /// </summary>
        /// <summary>
        /// A DXT1 (alpha-less) texture that is a GLOW on a BLACK background — the form SDO ships flying-wing frames in
        /// (FLY Pink Butterfly 008448 女翅膀: a pink/blue paisley wing + white sparkles, pure-black border). It carries
        /// no alpha, so straight blend draws the black background as a solid rectangle and the edges never fade
        /// (使用者:「邊緣透明度沒做出來」). The correct draw is ADDITIVE (Blend One One): black adds nothing → transparent,
        /// the wing/sparkles show, and edges fade with their own brightness. Detected by: DXT1, a DARK outer border
        /// (mean luminance &lt; 24 over the frame edge) AND real bright content inside (&gt;5% of texels above 120).
        /// Returns false for DXT3/DXT5 (those carry alpha → the cutout/blend path handles them) and for solid DXT1.
        /// </summary>
        public static bool LooksLikeDarkGlowDxt1(byte[] d)
        {
            if (d == null || d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || d[3] != ' ') return false;
            if (System.Text.Encoding.ASCII.GetString(d, 84, 4) != "DXT1") return false;
            int h = BitConverter.ToInt32(d, 12), w = BitConverter.ToInt32(d, 16);
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return false;
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            if (128 + bw * bh * 8 > d.Length) return false;
            long borderLum = 0; int borderCnt = 0, bright = 0, dark = 0, total = 0;
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    int bo = 128 + (by * bw + bx) * 8;
                    ushort c0 = (ushort)(d[bo] | (d[bo + 1] << 8)), c1 = (ushort)(d[bo + 2] | (d[bo + 3] << 8));
                    int r0 = (c0 >> 11) * 255 / 31, g0 = ((c0 >> 5) & 63) * 255 / 63, b0 = (c0 & 31) * 255 / 31;
                    int r1 = (c1 >> 11) * 255 / 31, g1 = ((c1 >> 5) & 63) * 255 / 63, b1 = (c1 & 31) * 255 / 31;
                    uint bits = BitConverter.ToUInt32(d, bo + 4);
                    for (int t = 0; t < 16; t++)
                    {
                        int px = bx * 4 + (t & 3), py = by * 4 + (t >> 2);
                        if (px >= w || py >= h) continue;
                        int code = (int)((bits >> (2 * t)) & 3);
                        int r, g, b;
                        if (code == 0) { r = r0; g = g0; b = b0; }
                        else if (code == 1) { r = r1; g = g1; b = b1; }
                        else if (c0 > c1) { int n = code == 2 ? 2 : 1, m = 3 - n; r = (n * r0 + m * r1) / 3; g = (n * g0 + m * g1) / 3; b = (n * b0 + m * b1) / 3; }
                        else if (code == 2) { r = (r0 + r1) / 2; g = (g0 + g1) / 2; b = (b0 + b1) / 2; }
                        else { r = g = b = 0; }
                        int lum = (r * 30 + g * 59 + b * 11) / 100;
                        total++;
                        if (lum > 120) bright++;
                        if (lum < 16) dark++;
                        bool edge = px < w / 8 || px >= w - w / 8 || py < h / 8 || py >= h - h / 8;
                        if (edge) { borderLum += lum; borderCnt++; }
                    }
                }
            if (total == 0 || borderCnt == 0) return false;
            double borderMean = borderLum / (double)borderCnt, darkFrac = dark / (double)total, brightFrac = bright / (double)total;
            LastGlowStats = (borderMean, darkFrac, brightFrac);
            // Glow-on-black = LOTS of bright content sitting on a LOT of near-black — a BIMODAL frame. The original "dark
            // BORDER" test was too fragile: 008448 FLY Pink Butterfly decorates its edges with white sparkle stars, so its
            // border mean is NOT dark (≈55) even though ~1/3 of the frame is pure black (使用者:「邊緣去背沒修好」). But a
            // dark-BACKGROUND fraction ALONE also mis-fires: a dark opaque DXT1 (024977 鞋: darkFrac 0.40) has just as much
            // black — what separates a glow is that it ALSO has a big BRIGHT population (butterfly brightFrac≈0.42 vs the
            // shoe's 0.14). So require both. The original dark-border clause is kept as an OR so nothing it caught regresses.
            return (borderMean < 24.0 && brightFrac > 0.05) || (darkFrac > 0.20 && brightFrac > 0.25);
        }

        /// <summary>Diagnostic: (border-mean-luminance, dark-texel-fraction, bright-texel-fraction) from the last
        /// <see cref="LooksLikeDarkGlowDxt1"/> call. Lets a test calibrate the glow thresholds against real frames.</summary>
        public static (double border, double dark, double bright) LastGlowStats;

        /// <summary>Decode a glow-on-black DXT1 (a <see cref="LooksLikeDarkGlowDxt1"/> wing frame) DE-BACKED: a brightness
        /// alpha is derived so the pure-black background is transparent and the coloured wing stays SOLID (使用者 wants a
        /// de-backed wing, NOT the washed-out additive glow). Returns null for non-DXT1. Draw it with a normal
        /// alpha-blend shader.</summary>
        public static Texture2D LoadDxt1BlackKeyed(byte[] d)
        {
            if (d == null || d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || d[3] != ' ') return null;
            if (System.Text.Encoding.ASCII.GetString(d, 84, 4) != "DXT1") return null;
            int h = BitConverter.ToInt32(d, 12), w = BitConverter.ToInt32(d, 16);
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;
            return DecodeDxt1(d, 128, w, h, false, false, false, false, false, keyBlack: true);
        }

        public static bool LooksLikeAdditiveGlow(byte[] d)
        {
            if (d == null || d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || d[3] != ' ') return false;
            string fourcc = System.Text.Encoding.ASCII.GetString(d, 84, 4);
            int height = BitConverter.ToInt32(d, 12), width = BitConverter.ToInt32(d, 16);
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return false;

            GlowStats s = new GlowStats { Total = width * height };
            if (fourcc == "DXT3")
            {
                if (!CollectDxtGlowStats(d, 128, width, height, dxt5Alpha: false, ref s)) return false;
            }
            else if (fourcc == "DXT5")
            {
                if (!CollectDxtGlowStats(d, 128, width, height, dxt5Alpha: true, ref s)) return false;
            }
            else
            {
                uint pf = BitConverter.ToUInt32(d, 80);
                if ((pf & 0x4u) != 0 || (pf & 0x40u) == 0) return false;
                uint rm = BitConverter.ToUInt32(d, 92), gm = BitConverter.ToUInt32(d, 96);
                uint bm = BitConverter.ToUInt32(d, 100), am = BitConverter.ToUInt32(d, 104);
                int bits = BitConverter.ToInt32(d, 88);
                if (bits != 32 || am == 0 || !CollectUncompressedGlowStats(d, 128, width, height, rm, gm, bm, am, ref s)) return false;
            }

            if (s.Visible == 0 || s.Soft == 0) return false;
            float meanLum = s.LumSum / (float)s.Visible;
            float visibleRatio = s.Visible / (float)s.Total;
            float softOfVisible = s.Soft / (float)s.Visible;
            float opaqueOfVisible = s.Opaque / (float)s.Visible;
            if (visibleRatio < 0.03f) return false;
            // Radial-gradient glow sprite (e.g. GUANG1_, SCN0016 SS_.DDS floor strips): transparent outer border
            // (visibleRatio < 0.95), nearly-entirely soft alpha (no hard opaque silhouette). Additive blend makes
            // even dim RGB glow. Thresholds are intentionally relaxed (~0.90 soft / <0.10 opaque) to capture
            // gradient-strip textures whose DXT3 quantisation pushes a small fraction of mid-alpha pixels past the
            // "fully opaque" boundary (SS_.DDS: softOfVisible≈0.939, opaqueOfVisible≈0.061).
            if (visibleRatio < 0.95f && softOfVisible >= 0.90f && opaqueOfVisible < 0.10f && meanLum >= 40f) return true;
            if (meanLum < 180f) return false;
            return softOfVisible >= 0.45f && opaqueOfVisible <= 0.65f;
        }

        private static DdsAlphaMode AccumulateAlphaMode(int alpha, DdsAlphaMode mode, int threshold, int softLow, int softHigh)
        {
            if (alpha > softLow && alpha < softHigh) return DdsAlphaMode.Blend;
            return alpha < threshold ? DdsAlphaMode.Cutout : mode;
        }

        private struct GlowStats
        {
            public int Total, Visible, Soft, Opaque;
            public long LumSum;
        }

        private static void AddGlowPixel(ref GlowStats s, Color32 c, int alpha)
        {
            if (alpha <= 8) return;
            s.Visible++;
            if (alpha < 240) s.Soft++;
            if (alpha > 240) s.Opaque++;
            s.LumSum += Math.Max(c.r, Math.Max(c.g, c.b));
        }

        private static bool CollectDxtGlowStats(byte[] d, int off, int w, int h, bool dxt5Alpha, ref GlowStats s)
        {
            int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
            int blockBytes = dxt5Alpha ? 16 : 16;
            if (off + bw * bh * blockBytes > d.Length) return false;
            int bi = off;
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++, bi += blockBytes)
                {
                    int colorOff = dxt5Alpha ? bi + 8 : bi + 8;
                    ushort c0 = (ushort)(d[colorOff] | (d[colorOff + 1] << 8));
                    ushort c1 = (ushort)(d[colorOff + 2] | (d[colorOff + 3] << 8));
                    uint bits = (uint)(d[colorOff + 4] | (d[colorOff + 5] << 8) | (d[colorOff + 6] << 16) | (d[colorOff + 7] << 24));
                    Color32 p0 = From565(c0), p1 = From565(c1);
                    Color32 p2 = new Color32((byte)((2 * p0.r + p1.r) / 3), (byte)((2 * p0.g + p1.g) / 3), (byte)((2 * p0.b + p1.b) / 3), 255);
                    Color32 p3 = new Color32((byte)((p0.r + 2 * p1.r) / 3), (byte)((p0.g + 2 * p1.g) / 3), (byte)((p0.b + 2 * p1.b) / 3), 255);

                    ulong aBits = 0; int a0 = 255, a1 = 255;
                    if (dxt5Alpha)
                    {
                        a0 = d[bi]; a1 = d[bi + 1];
                        for (int k = 0; k < 6; k++) aBits |= (ulong)d[bi + 2 + k] << (8 * k);
                    }

                    for (int i = 0; i < 16; i++)
                    {
                        int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
                        if (x >= w || y >= h) continue;
                        int sel = (int)((bits >> (i * 2)) & 3);
                        Color32 col = sel == 0 ? p0 : sel == 1 ? p1 : sel == 2 ? p2 : p3;
                        int alpha;
                        if (dxt5Alpha)
                        {
                            int code = (int)((aBits >> (i * 3)) & 7);
                            alpha = Dxt5Alpha(a0, a1, code);
                        }
                        else
                        {
                            int ab = d[bi + (i >> 1)];
                            int nib = (i & 1) == 0 ? (ab & 0xF) : ((ab >> 4) & 0xF);
                            alpha = (nib * 255 + 7) / 15;
                        }
                        AddGlowPixel(ref s, col, alpha);
                    }
                }
            return true;
        }

        private static bool CollectUncompressedGlowStats(byte[] d, int off, int w, int h, uint rm, uint gm, uint bm, uint am, ref GlowStats s)
        {
            if (off + w * h * 4 > d.Length) return false;
            int rs = MaskShift(rm), gs = MaskShift(gm), bs = MaskShift(bm), as_ = MaskShift(am);
            uint rMax = rm >> rs, gMax = gm >> gs, bMax = bm >> bs, aMax = am >> as_;
            if (rMax == 0 || gMax == 0 || bMax == 0 || aMax == 0) return false;
            for (int i = 0; i < w * h; i++)
            {
                int p = off + i * 4;
                uint v = (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
                var col = new Color32(
                    (byte)(((v & rm) >> rs) * 255 / rMax),
                    (byte)(((v & gm) >> gs) * 255 / gMax),
                    (byte)(((v & bm) >> bs) * 255 / bMax),
                    255);
                int alpha = (int)(((v & am) >> as_) * 255 / aMax);
                AddGlowPixel(ref s, col, alpha);
            }
            return true;
        }

        private static int Dxt5Alpha(int a0, int a1, int code)
        {
            if (code == 0) return a0;
            if (code == 1) return a1;
            if (a0 > a1) return ((8 - code) * a0 + (code - 1) * a1) / 7;
            if (code == 6) return 0;
            if (code == 7) return 255;
            return ((6 - code) * a0 + (code - 1) * a1) / 5;
        }

        /// <summary>Alpha-debanding strength for a glow DDS with banded 4-bit alpha (see <see cref="SmoothAlpha"/>).</summary>
        public enum AlphaSmooth { None, PreserveDetail, Full }

        public static Texture2D Load(byte[] d) => Load(d, false);
        public static Texture2D Load(byte[] d, bool bleedAlphaEdges, AlphaSmooth smooth)
        {
            // smooth only affects the DXT3 hand-decode (4-bit alpha banding); other formats are already smooth.
            if (smooth != AlphaSmooth.None && d != null && d.Length >= 128 && d[0] == 'D' && d[1] == 'D' && d[2] == 'S' && d[3] == ' '
                && System.Text.Encoding.ASCII.GetString(d, 84, 4) == "DXT3")
            {
                int hgt = BitConverter.ToInt32(d, 12), wid = BitConverter.ToInt32(d, 16);
                if (wid > 0 && hgt > 0 && wid <= 4096 && hgt <= 4096) return DecodeDxt3(d, 128, wid, hgt, bleedAlphaEdges, smooth);
            }
            return Load(d, bleedAlphaEdges);
        }

        /// <param name="bleedAlphaEdges">When true, the decoded RGB is dilated outward into transparent /
        /// semi-transparent texels (alpha unchanged) so a white/light MATTE behind a cut-out can't bleed a halo
        /// at the silhouette. Used for alpha-blended mapobj billboards (SCN0026 背景汽車) whose DXT3 art carries a
        /// white background — straight SrcAlpha blend would otherwise composite that white as a fringe. No-op on
        /// fully-opaque textures (nothing to bleed into), so it is safe to pass for any mapobj texture.</param>
        public static Texture2D Load(byte[] d, bool bleedAlphaEdges)
        {
            if (d == null || d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || d[3] != ' ') return null;
            int height = BitConverter.ToInt32(d, 12);
            int width = BitConverter.ToInt32(d, 16);
            string fourcc = System.Text.Encoding.ASCII.GetString(d, 84, 4);
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return null;

            if (fourcc == "DXT3") return DecodeDxt3(d, 128, width, height, bleedAlphaEdges);
            // DXT1 is decoded BY HAND to RGBA32 (opaque, alpha 255) — NOT via Unity's native TextureFormat.DXT1.
            // On this project's d3d12 / Unity 6 path the native BC1 upload sampled with alpha 0, so the scene's
            // alpha-cutout shader (Sdo/SceneVertexCutout: clip(a-0.5)) discarded EVERY opaque DXT1 material — the
            // SCN0010 floor / sky / columns / lanterns vanished to black while the DXT3 props (own manual decode)
            // stayed. Manual decode gives correct RGB + alpha 255 (SDO's stage DXT1 carry no real alpha; see HasAlpha),
            // same orientation as the native path, so UVs are unchanged and no scene regresses. (DXT1 is opaque, so
            // the bleed flag is a no-op for it — passed through for a uniform call site.)
            if (fourcc == "DXT1") return DecodeDxt1(d, 128, width, height, false, bleedAlphaEdges);

            // UNCOMPRESSED 32-bit RGB(A): fourcc is empty and DDPF_RGB (0x40) is set. SDO's extracted/packaged
            // SCN0010 floor `dimian1.dds` is X8R8G8B8 (1 MB, masks R=00ff0000 G=0000ff00 B=000000ff A=0), NOT the
            // DXT1 copy under assets/Datas — so it hit `default: return null` and the floor rendered untextured/black.
            uint pf = BitConverter.ToUInt32(d, 80);
            if ((pf & 0x4u) == 0 && (pf & 0x40u) != 0)
                return DecodeUncompressed(d, 128, width, height,
                    BitConverter.ToUInt32(d, 92), BitConverter.ToUInt32(d, 96),
                    BitConverter.ToUInt32(d, 100), BitConverter.ToUInt32(d, 104),
                    BitConverter.ToInt32(d, 88), bleedAlphaEdges);

            TextureFormat fmt; int blockBytes;
            switch (fourcc)
            {
                case "DXT5": fmt = TextureFormat.DXT5; blockBytes = 16; break;
                default: return null;
            }
            int baseSize = Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockBytes;
            if (128 + baseSize > d.Length) return null;
            var raw = new byte[baseSize]; Array.Copy(d, 128, raw, 0, baseSize);
            var tex = new Texture2D(width, height, fmt, false) { wrapMode = TextureWrapMode.Repeat };   // D3D default is WRAP; tiling UVs (FIFA crowd u=-7.75..7.32, floors) need it
            tex.LoadRawTextureData(raw); tex.Apply(false, true);
            return tex;
        }

        // Uncompressed 32-bit DDS (D3D A8R8G8B8 / X8R8G8B8 etc.) decoded via the pixel-format channel masks → RGBA32.
        // Amask 0 → opaque (255). Same row order as the block decoders (DDS top row → texture row 0) so UVs match.
        private static Texture2D DecodeUncompressed(byte[] d, int off, int w, int h, uint rm, uint gm, uint bm, uint am, int bits, bool bleed = false)
        {
            if (bits != 32) return null;                 // only 32-bit handled (SDO's uncompressed stage textures are all 32-bit)
            if (off + w * h * 4 > d.Length) return null;
            int rs = MaskShift(rm), gs = MaskShift(gm), bs = MaskShift(bm), as_ = MaskShift(am);
            var px = new Color32[w * h];
            for (int i = 0; i < w * h; i++)
            {
                int p = off + i * 4;
                uint v = (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
                px[i] = new Color32((byte)((v & rm) >> rs), (byte)((v & gm) >> gs), (byte)((v & bm) >> bs),
                                    am != 0 ? (byte)((v & am) >> as_) : (byte)255);
            }
            if (bleed) BleedAlphaEdges(px, w, h);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat };
            tex.SetPixels32(px); tex.Apply(false, true);
            return tex;
        }
        private static int MaskShift(uint mask) { if (mask == 0) return 0; int s = 0; while ((mask & 1) == 0) { mask >>= 1; s++; } return s; }

        /// <summary>
        /// Decode a Targa (.TGA) image → RGBA32. Handles true-colour uncompressed (type 2) and RLE (type 10), 24/32-bit
        /// BGR(A). SDO ships some avatar texanim frames as TGA (e.g. the 花雨飞翼 wing glow 023921_woman_chibang2_/3_.tga),
        /// which Unity's Texture2D.LoadImage can't read (PNG/JPG only) — so decode by hand. Origin is normalised to
        /// top-left (TGA descriptor bit 5 = 0 means bottom-left → rows are flipped) so UVs match the DDS path. Returns
        /// null on an unsupported header rather than throwing.
        /// </summary>
        /// <summary>
        /// The alpha class of a 32-bit TGA, using the SAME distribution buckets as <see cref="GetSceneAlphaMode"/>.
        /// SDO ships many animated-frame textures as TGA rather than DDS — including CLOTH frame sets (the SHINE Sexy
        /// Demon / Purple Lace garments animate from 017234_man_chibang*.tga). Those frames carry a transparent
        /// background, so a caller that keeps its opaque shader paints that background as solid white rectangles
        /// (使用者:「閃爍貼圖動畫沒去背」). Returns Opaque for 24-bit / unreadable data.
        /// </summary>
        public static DdsAlphaMode GetTgaAlphaMode(byte[] d)
        {
            if (d == null || d.Length < 18 || d[16] != 32) return DdsAlphaMode.Opaque;   // only 32-bit carries alpha
            var tex = LoadTgaPixels(d, out int w, out int h);
            if (tex == null || w <= 0 || h <= 0) return DdsAlphaMode.Opaque;
            int total = tex.Length, visible = 0, soft = 0, hardTransp = 0;
            for (int i = 0; i < total; i++)
            {
                byte a = tex[i].a;
                if (a > 8) visible++;
                if (a > 8 && a < 247) soft++;
                if (a <= 8) hardTransp++;
            }
            if (total == 0 || visible == 0) return DdsAlphaMode.Opaque;
            float softOfVisible = soft / (float)visible, hardFrac = hardTransp / (float)total;
            if (hardFrac >= 0.03f && softOfVisible <= 0.75f) return DdsAlphaMode.Cutout;
            if (softOfVisible >= 0.30f) return DdsAlphaMode.Blend;
            return DdsAlphaMode.Opaque;
        }

        /// <summary>Decode a TGA to pixels in TOP-LEFT row order without creating a Texture2D (so the data stays
        /// readable for analysis). Shared by <see cref="LoadTga"/> and <see cref="GetTgaAlphaMode"/>.</summary>
        private static Color32[] LoadTgaPixels(byte[] d, out int w, out int h)
        {
            w = h = 0;
            if (d == null || d.Length < 18) return null;
            int idLen = d[0], cmapType = d[1], imgType = d[2];
            int cmapLen = d[5] | (d[6] << 8), cmapEntBits = d[7];
            w = d[12] | (d[13] << 8); h = d[14] | (d[15] << 8);
            int bpp = d[16], desc = d[17];
            if (w <= 0 || h <= 0 || (bpp != 24 && bpp != 32)) return null;
            if (imgType != 2 && imgType != 10) return null;
            int bytesPP = bpp / 8;
            int off = 18 + idLen + (cmapType != 0 ? cmapLen * ((cmapEntBits + 7) / 8) : 0);
            bool topLeft = (desc & 0x20) != 0;
            int total = w * h;
            var lin = new Color32[total];
            if (imgType == 2)
            {
                if (off + total * bytesPP > d.Length) return null;
                int si = off;
                for (int i = 0; i < total; i++, si += bytesPP)
                    lin[i] = new Color32(d[si + 2], d[si + 1], d[si], bytesPP == 4 ? d[si + 3] : (byte)255);
            }
            else
            {
                int si = off, count = 0;
                while (count < total && si < d.Length)
                {
                    int packet = d[si++]; int n = (packet & 0x7f) + 1;
                    if ((packet & 0x80) != 0)
                    {
                        if (si + bytesPP > d.Length) break;
                        var c = new Color32(d[si + 2], d[si + 1], d[si], bytesPP == 4 ? d[si + 3] : (byte)255);
                        si += bytesPP;
                        for (int k = 0; k < n && count < total; k++) lin[count++] = c;
                    }
                    else
                    {
                        for (int k = 0; k < n && count < total; k++, si += bytesPP)
                        {
                            if (si + bytesPP > d.Length) break;
                            lin[count++] = new Color32(d[si + 2], d[si + 1], d[si], bytesPP == 4 ? d[si + 3] : (byte)255);
                        }
                    }
                }
            }
            if (topLeft) return lin;
            var flipped = new Color32[total];                       // bottom-left origin → flip rows
            for (int y = 0; y < h; y++)
                System.Array.Copy(lin, (h - 1 - y) * w, flipped, y * w, w);
            return flipped;
        }

        public static Texture2D LoadTga(byte[] d)
        {
            if (d == null || d.Length < 18) return null;
            int idLen = d[0], cmapType = d[1], imgType = d[2];
            int cmapLen = d[5] | (d[6] << 8), cmapEntBits = d[7];
            int w = d[12] | (d[13] << 8), h = d[14] | (d[15] << 8);
            int bpp = d[16], desc = d[17];
            if (w <= 0 || h <= 0 || (bpp != 24 && bpp != 32)) return null;
            if (imgType != 2 && imgType != 10) return null;   // only true-colour (uncompressed / RLE)
            int bytesPP = bpp / 8;
            int off = 18 + idLen + (cmapType != 0 ? cmapLen * ((cmapEntBits + 7) / 8) : 0);
            bool topLeft = (desc & 0x20) != 0;   // bit5: 1 = origin top-left, 0 = bottom-left (flip)
            int total = w * h;
            var lin = new Color32[total];   // in stored (row) order
            if (imgType == 2)
            {
                if (off + total * bytesPP > d.Length) return null;
                int si = off;
                for (int i = 0; i < total; i++, si += bytesPP)
                    lin[i] = new Color32(d[si + 2], d[si + 1], d[si], bytesPP == 4 ? d[si + 3] : (byte)255);
            }
            else   // type 10 — RLE packets
            {
                int si = off, count = 0;
                while (count < total && si < d.Length)
                {
                    int packet = d[si++]; int n = (packet & 0x7f) + 1;
                    if ((packet & 0x80) != 0)   // run-length: one pixel × n
                    {
                        if (si + bytesPP > d.Length) break;
                        var c = new Color32(d[si + 2], d[si + 1], d[si], bytesPP == 4 ? d[si + 3] : (byte)255);
                        si += bytesPP;
                        for (int k = 0; k < n && count < total; k++) lin[count++] = c;
                    }
                    else   // raw: n literal pixels
                    {
                        for (int k = 0; k < n && count < total; k++, si += bytesPP)
                        {
                            if (si + bytesPP > d.Length) break;
                            lin[count++] = new Color32(d[si + 2], d[si + 1], d[si], bytesPP == 4 ? d[si + 3] : (byte)255);
                        }
                    }
                }
            }
            var px = new Color32[total];
            for (int y = 0; y < h; y++)
            {
                int dst = (topLeft ? y : (h - 1 - y)) * w;
                Array.Copy(lin, y * w, px, dst, w);
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px); tex.Apply(false, true);
            return tex;
        }

        // BC1: 8-byte blocks (c0,c1 565 + 16×2-bit indices). c0>c1 → 4 opaque colours; c0≤c1 → 3 colours + a 4th
        // index that BC1 treats as transparent-black — but SDO's stage DXT1 textures are OPAQUE (their 1-bit alpha
        // isn't used; see HasAlpha), so we force alpha 255 for every texel. Decoded to RGBA32 because Unity's native
        // TextureFormat.DXT1 sampled a=0 on this d3d12 path, which the scene's alpha-cutout shader then clipped away.
        /// <summary>Load a DXT1 DDS as an RGBA32 texture that HONORS the BC1 1-bit punch-through alpha (the transparent
        /// index in c0&lt;=c1 blocks → alpha 0), for textures that actually carry cut-out transparency — e.g. the 3D-note
        /// glyphs (3DNOTES\NOTES*/JUDGELINE*) whose arrow sits on a transparent background. The stock <see cref="Load(byte[])"/>
        /// forces DXT1 opaque (SDO's STAGE tiles carry no real alpha and the cutout shader needs alpha 255); this path is
        /// only for the note glyphs, so their transparent surround doesn't render as a black square. RGB is edge-bled into
        /// the transparent texels so a bilinear-filtered sprite gets no dark halo at the silhouette.</summary>
        public static Texture2D LoadDxt1Alpha(byte[] d, bool flipV = true, bool desilver = false)
        {
            if (d == null || d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || d[3] != ' ') return null;
            int height = BitConverter.ToInt32(d, 12), width = BitConverter.ToInt32(d, 16);
            if (System.Text.Encoding.ASCII.GetString(d, 84, 4) != "DXT1") return null;
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return null;
            // The 3D-note glyphs are a glowing arrow on BLACK: only the arrow's anti-aliased edge blocks use BC1
            // punch-through; the flat surround is stored in the 4-colour OPAQUE mode (black, alpha 255), so a plain
            // alpha-blended sprite shows a black square ("沒有去背"). lumAlpha derives alpha from luminance (glow-on-black
            // → the black keys out, the bright arrow stays), matching the official's additive glow look. bleed dilates
            // RGB into the keyed-out texels (no dark fringe). flipV: a full-rect SPRITE needs the row flip (DDS-top→row0
            // = Unity bottom); a MESH texture (hold body) passes flipV=false (its UVs already match). In-decode because
            // the texture uploads non-readable, so a post-hoc GetPixels32 flip/key would throw.
            return DecodeDxt1(d, 128, width, height, punchAlpha: true, bleed: true, flipV: flipV, keyBg: true, desilver: desilver);
        }

        // BC1 decode. punchAlpha=false (default): the transparent index is forced BLACK-OPAQUE — SDO's stage DXT1 carry
        // no real alpha and the scene cutout shader needs alpha 255 (see the Load() note). punchAlpha=true: the c0<=c1
        // transparent index becomes alpha 0 (real BC1 punch-through). keyBg: recompute alpha by COLOUR DISTANCE from the
        // texture's dominant (background) colour — the 3D-note glyphs bake the arrow on a solid surround that is NOT
        // pure black (NOTES/LONG = (65,49,49); JUDGELINE = black), so a luminance threshold can't key both; keying the
        // actual bg colour removes the square and keeps the arrow. bleed dilates RGB into transparent texels; flipV
        // reverses rows so a full-rect sprite shows upright.
        private static Texture2D DecodeDxt1(byte[] d, int off, int w, int h, bool punchAlpha = false, bool bleed = false, bool flipV = false, bool keyBg = false, bool desilver = false, bool keyBlack = false)
        {
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            if (off + bw * bh * 8 > d.Length) return null;
            var px = new Color32[w * h];
            int bi = off;
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++, bi += 8)
                {
                    ushort c0 = (ushort)(d[bi] | (d[bi + 1] << 8));
                    ushort c1 = (ushort)(d[bi + 2] | (d[bi + 3] << 8));
                    uint bits = (uint)(d[bi + 4] | (d[bi + 5] << 8) | (d[bi + 6] << 16) | (d[bi + 7] << 24));
                    Color32 p0 = From565(c0), p1 = From565(c1), p2, p3;
                    if (c0 > c1)
                    {
                        p2 = new Color32((byte)((2 * p0.r + p1.r) / 3), (byte)((2 * p0.g + p1.g) / 3), (byte)((2 * p0.b + p1.b) / 3), 255);
                        p3 = new Color32((byte)((p0.r + 2 * p1.r) / 3), (byte)((p0.g + 2 * p1.g) / 3), (byte)((p0.b + 2 * p1.b) / 3), 255);
                    }
                    else
                    {
                        p2 = new Color32((byte)((p0.r + p1.r) / 2), (byte)((p0.g + p1.g) / 2), (byte)((p0.b + p1.b) / 2), 255);
                        p3 = punchAlpha ? new Color32(0, 0, 0, 0)         // real BC1 transparent (note glyphs)
                                        : new Color32(0, 0, 0, 255);      // stage DXT1: forced opaque
                    }
                    for (int i = 0; i < 16; i++)
                    {
                        int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
                        if (x >= w || y >= h) continue;
                        int sel = (int)((bits >> (i * 2)) & 3);
                        px[y * w + x] = sel == 0 ? p0 : sel == 1 ? p1 : sel == 2 ? p2 : p3;
                    }
                }
            // background key-out: alpha = colour-distance ramp from the DOMINANT colour (the flat surround, which fills
            // >half the tile). Found via a coarse 4-bit/channel histogram (no allocations beyond one int[4096]). Distance
            // ≤36 → transparent, ≥84 → opaque, linear between → the bright arrow stays, the (non-black) surround keys out.
            if (keyBg)
            {
                var hist = new int[4096];
                for (int i = 0; i < px.Length; i++)
                    hist[((px[i].r >> 4) << 8) | ((px[i].g >> 4) << 4) | (px[i].b >> 4)]++;
                int best = 0, bestN = -1;
                for (int k = 0; k < hist.Length; k++) if (hist[k] > bestN) { bestN = hist[k]; best = k; }
                int br = ((best >> 8) & 0xf) * 17, bg = ((best >> 4) & 0xf) * 17, bb = (best & 0xf) * 17;
                Debug.Log($"[dds-key] bg=({br},{bg},{bb}) dominates {bestN * 100 / px.Length}% of {w}x{h}");   // diag: if a note still shows a box, this says what bg it keyed
                for (int i = 0; i < px.Length; i++)
                {
                    int dr = px[i].r - br, dg = px[i].g - bg, db = px[i].b - bb;
                    double dist = Math.Sqrt(dr * dr + dg * dg + db * db);
                    px[i].a = (byte)(dist <= 32.0 ? 0 : dist >= 54.0 ? 255 : (int)((dist - 32.0) * 255.0 / 22.0));   // crisp ramp → no dark halo
                }
            }
            // BLACK key-out (glow-on-black wings): DERIVE an alpha from brightness so the pure-black background becomes
            // transparent and the coloured wing stays SOLID — the correct de-back for a DXT1 that carries no alpha
            // (008448 FLY Pink Butterfly). Deterministic (keys BLACK, not the histogram-dominant colour like keyBg, so a
            // wing whose own body colour fills more than the surround can't be keyed out by mistake). Metric = max(r,g,b)
            // ("is this texel non-black"), NOT luminance — a saturated dark colour (e.g. deep blue 0,0,180: luma≈20) must
            // stay opaque. Ramp 8→48 gives an anti-aliased silhouette without hollowing the darker paisley of the wing.
            if (keyBlack)
                for (int i = 0; i < px.Length; i++)
                {
                    int b = Math.Max(px[i].r, Math.Max(px[i].g, px[i].b));
                    px[i].a = (byte)(b <= 8 ? 0 : b >= 48 ? 255 : (b - 8) * 255 / 40);
                }
            if (desilver)
            {
                // The LONG end-cap's OUTER chevron is solid white/silver (opaque → survives the cut-out) = the 白邊.
                // Drop bright, low-saturation texels so only the coloured (magenta/gold) chevrons of the cap remain.
                for (int i = 0; i < px.Length; i++)
                {
                    if (px[i].a == 0) continue;
                    int mx = Math.Max(px[i].r, Math.Max(px[i].g, px[i].b)), mn = Math.Min(px[i].r, Math.Min(px[i].g, px[i].b));
                    if ((px[i].r + px[i].g + px[i].b) / 3 >= 150 && (mx - mn) <= 55) px[i].a = 0;
                }
            }
            if (flipV)
            {
                var f = new Color32[px.Length];
                for (int y = 0; y < h; y++) Array.Copy(px, y * w, f, (h - 1 - y) * w, w);
                px = f;
            }
            if (bleed) BleedAlphaEdges(px, w, h);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat };   // D3D default WRAP; tiling floor/sky UVs need it
            tex.SetPixels32(px); tex.Apply(false, true);
            return tex;
        }

        // BC2: 16-byte blocks = 8 bytes 4-bit alpha + 8 bytes DXT1-style colour (always 4-colour, no 1-bit alpha)
        /// <summary>Low-pass the ALPHA channel to reconstruct a smooth gradient from DXT3's 4-bit (≤16-level) alpha — a
        /// soft glow quantised to ~9-12 levels renders as concentric "tree-ring" bands (年輪). Alpha only; RGB untouched.
        /// Two strengths (the SCN0022 ghost vs searchlight need different treatment):
        ///   preserveDetail=TRUE  → a single BILATERAL pass: a texel averages only neighbours whose alpha is within
        ///     <c>rangeThreshold</c>, so the small quantisation steps (~17) merge into a fade while high-contrast detail
        ///     (the ghost's eye/mouth holes, ≫threshold) stays sharp. Used for sprites that carry shape in their alpha.
        ///   preserveDetail=FALSE → a stronger 3-pass separable BOX blur: for a pure gradient with NO detail to keep (the
        ///     searchlight beam), it flattens every step. The bilateral is too weak on a big 256px beam (rings survive).
        /// Radius scales with the texture (≈max(w,h)/40).</summary>
        public static void SmoothAlpha(Color32[] px, int w, int h, bool preserveDetail = true)
        {
            if (px == null || w <= 0 || h <= 0 || px.Length < w * h) return;
            int radius = Math.Max(1, Math.Min(8, (int)Math.Round(Math.Max(w, h) / 40.0)));
            if (preserveDetail)
            {
                const int rangeThreshold = 28;   // > the ~17 quantisation step (bands merge), ≪ the face-hole contrast
                                                 // (~110, eyes/mouth kept). ONE pass — a 2nd pass washed the low-contrast mouth.
                var a = new byte[w * h];
                for (int i = 0; i < w * h; i++) a[i] = px[i].a;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int c = a[y * w + x], sum = 0, cnt = 0;
                        for (int dy = -radius; dy <= radius; dy++)
                        {
                            int yy = y + dy; if (yy < 0 || yy >= h) continue;
                            for (int dx = -radius; dx <= radius; dx++)
                            {
                                int xx = x + dx; if (xx < 0 || xx >= w) continue;
                                int v = a[yy * w + xx];
                                int diff = v - c; if (diff < 0) diff = -diff;
                                if (diff <= rangeThreshold) { sum += v; cnt++; }
                            }
                        }
                        px[y * w + x].a = cnt > 0 ? (byte)((sum + cnt / 2) / cnt) : (byte)c;
                    }
            }
            else
            {
                var a = new float[w * h];
                for (int i = 0; i < w * h; i++) a[i] = px[i].a;
                var tmp = new float[w * h];
                for (int pass = 0; pass < 3; pass++)
                {
                    for (int y = 0; y < h; y++)          // horizontal
                        for (int x = 0; x < w; x++)
                        {
                            float sum = 0f; int cnt = 0;
                            for (int dx = -radius; dx <= radius; dx++)
                            {
                                int xx = x + dx; if (xx < 0 || xx >= w) continue;
                                sum += a[y * w + xx]; cnt++;
                            }
                            tmp[y * w + x] = sum / cnt;
                        }
                    for (int y = 0; y < h; y++)          // vertical
                        for (int x = 0; x < w; x++)
                        {
                            float sum = 0f; int cnt = 0;
                            for (int dy = -radius; dy <= radius; dy++)
                            {
                                int yy = y + dy; if (yy < 0 || yy >= h) continue;
                                sum += tmp[yy * w + x]; cnt++;
                            }
                            a[y * w + x] = sum / cnt;
                        }
                }
                for (int i = 0; i < w * h; i++) { int v = (int)(a[i] + 0.5f); px[i].a = (byte)(v < 0 ? 0 : v > 255 ? 255 : v); }
            }
        }

        private static Texture2D DecodeDxt3(byte[] d, int off, int w, int h, bool bleed = false, AlphaSmooth smooth = AlphaSmooth.None)
        {
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            if (off + bw * bh * 16 > d.Length) return null;
            var px = new Color32[w * h];
            int bi = off;
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++, bi += 16)
                {
                    ushort c0 = (ushort)(d[bi + 8] | (d[bi + 9] << 8));
                    ushort c1 = (ushort)(d[bi + 10] | (d[bi + 11] << 8));
                    uint bits = (uint)(d[bi + 12] | (d[bi + 13] << 8) | (d[bi + 14] << 16) | (d[bi + 15] << 24));
                    Color32 p0 = From565(c0), p1 = From565(c1);
                    Color32 p2 = new Color32((byte)((2 * p0.r + p1.r) / 3), (byte)((2 * p0.g + p1.g) / 3), (byte)((2 * p0.b + p1.b) / 3), 255);
                    Color32 p3 = new Color32((byte)((p0.r + 2 * p1.r) / 3), (byte)((p0.g + 2 * p1.g) / 3), (byte)((p0.b + 2 * p1.b) / 3), 255);
                    for (int i = 0; i < 16; i++)
                    {
                        int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
                        if (x >= w || y >= h) continue;
                        Color32 col; int sel = (int)((bits >> (i * 2)) & 3);
                        col = sel == 0 ? p0 : sel == 1 ? p1 : sel == 2 ? p2 : p3;
                        int ab = d[bi + (i >> 1)]; int nib = (i & 1) == 0 ? (ab & 0xF) : ((ab >> 4) & 0xF);
                        px[y * w + x] = new Color32(col.r, col.g, col.b, (byte)((nib * 255 + 7) / 15));
                    }
                }
            if (smooth != AlphaSmooth.None) SmoothAlpha(px, w, h, smooth == AlphaSmooth.PreserveDetail);   // BEFORE the edge bleed
            if (bleed) BleedAlphaEdges(px, w, h);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat };   // D3D default is WRAP; tiling UVs (FIFA crowd u=-7.75..7.32, floors) need it
            tex.SetPixels32(px); tex.Apply(false, true);
            return tex;
        }

        /// <summary>
        /// Alpha edge-bleed (a.k.a. RGB dilation / matte expansion). Copies RGB outward from strongly-opaque texels
        /// (alpha ≥ <paramref name="seedAlpha"/>) into transparent / soft-edge texels over a few passes, leaving the
        /// alpha channel untouched. The DXT3/DXT1 background behind a cut-out keeps whatever RGB the artist painted
        /// (often a WHITE matte); under straight SrcAlpha/InvSrcAlpha blending those white texels composite a halo
        /// at the silhouette (SCN0026 背景汽車). After bleeding, the edge texels carry the prop's own colour instead,
        /// so the blended edge is clean and the cut-out shape (the alpha) is unchanged. Pure (Color32[] -> mutated),
        /// so it is unit-tested. No-op when every texel is already opaque, or when nothing is opaque enough to seed.
        /// </summary>
        public static void BleedAlphaEdges(Color32[] px, int w, int h, int seedAlpha = 200, int passes = 6)
        {
            if (px == null || w <= 0 || h <= 0 || px.Length < w * h) return;
            var filled = new bool[w * h];
            int seeds = 0;
            for (int i = 0; i < w * h; i++) if (px[i].a >= seedAlpha) { filled[i] = true; seeds++; }
            if (seeds == 0 || seeds == w * h) return;   // nothing to bleed from, or fully opaque -> unchanged
            var r = new byte[w * h]; var g = new byte[w * h]; var b = new byte[w * h];
            for (int i = 0; i < w * h; i++) { r[i] = px[i].r; g[i] = px[i].g; b[i] = px[i].b; }
            var pend = new System.Collections.Generic.List<(int idx, byte r, byte g, byte b)>();
            for (int pass = 0; pass < passes; pass++)
            {
                pend.Clear();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (filled[i]) continue;
                        int sr = 0, sg = 0, sb = 0, cnt = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                int j = ny * w + nx;
                                if (filled[j]) { sr += r[j]; sg += g[j]; sb += b[j]; cnt++; }
                            }
                        if (cnt > 0) pend.Add((i, (byte)(sr / cnt), (byte)(sg / cnt), (byte)(sb / cnt)));
                    }
                if (pend.Count == 0) break;   // fully propagated
                foreach (var e in pend) { r[e.idx] = e.r; g[e.idx] = e.g; b[e.idx] = e.b; filled[e.idx] = true; }
            }
            for (int i = 0; i < w * h; i++) px[i] = new Color32(r[i], g[i], b[i], px[i].a);
        }

        private static Color32 From565(ushort c) =>
            new Color32((byte)(((c >> 11) & 0x1F) * 255 / 31), (byte)(((c >> 5) & 0x3F) * 255 / 63), (byte)((c & 0x1F) * 255 / 31), 255);
    }
}
