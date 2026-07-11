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
            if (!AlphaCounts(d, out int total, out int visible, out int soft, out int hardTransp) || visible == 0)
                return DdsAlphaMode.Opaque;
            float softOfVisible = soft / (float)visible;
            float hardTranspFrac = hardTransp / (float)total;
            if (hardTranspFrac >= 0.03f && softOfVisible <= 0.75f) return DdsAlphaMode.Cutout;
            if (softOfVisible >= 0.30f) return DdsAlphaMode.Blend;
            return DdsAlphaMode.Opaque;
        }

        // Walk the base-mip alpha and count: total texels, visible (a>8), soft (8<a<247), hardTransp (a<=8).
        // Mirrors GetAlphaMode's DXT3 / DXT5 / 32-bit-uncompressed decode paths (no early-out). Returns false for
        // formats with no alpha (DXT1 / unsupported) — caller treats those as Opaque.
        private static bool AlphaCounts(byte[] d, out int total, out int visible, out int soft, out int hardTransp)
        {
            total = visible = soft = hardTransp = 0;
            if (d == null || d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || d[3] != ' ') return false;
            string fourcc = System.Text.Encoding.ASCII.GetString(d, 84, 4);
            int height = BitConverter.ToInt32(d, 12), width = BitConverter.ToInt32(d, 16);
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return false;
            int bw = Math.Max(1, (width + 3) / 4), bh = Math.Max(1, (height + 3) / 4), bi = 128;
            int t = 0, v = 0, s = 0, ht = 0;
            if (fourcc == "DXT3")
            {
                for (int b = 0; b < bw * bh && bi + 16 <= d.Length; b++, bi += 16)
                    for (int k = 0; k < 8; k++)
                    {
                        int ab = d[bi + k];
                        int a0t = (ab & 0xF) * 255 / 15; t++; if (a0t <= 8) ht++; else { v++; if (a0t < 247) s++; }
                        int a1t = ((ab >> 4) & 0xF) * 255 / 15; t++; if (a1t <= 8) ht++; else { v++; if (a1t < 247) s++; }
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
                        t++; if (a <= 8) ht++; else { v++; if (a < 247) s++; }
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
                    t++; if (a <= 8) ht++; else { v++; if (a < 247) s++; }
                }
            }
            total = t; visible = v; soft = s; hardTransp = ht;
            return t > 0;
        }

        /// <summary>
        /// Heuristic for mapobj textures that are authored as light/glow sprites, not ordinary cut-out art.
        /// These should be additive: soft alpha dominates, visible pixels are very bright, and there is little
        /// hard opaque silhouette. This keeps people/sign/billboard alpha blended while stage bulbs, lasers and
        /// sweeping light decals add energy to the scene like the original fixed-function render path.
        /// </summary>
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

        public static Texture2D Load(byte[] d) => Load(d, false);

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
        private static Texture2D DecodeDxt1(byte[] d, int off, int w, int h, bool punchAlpha = false, bool bleed = false, bool flipV = false, bool keyBg = false, bool desilver = false)
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
        private static Texture2D DecodeDxt3(byte[] d, int off, int w, int h, bool bleed = false)
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
