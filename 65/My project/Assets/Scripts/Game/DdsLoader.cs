using System;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// DDS loader for SDO textures. DXT1/DXT5 (BC1/BC3) go straight to a compressed Texture2D; DXT3 (BC2) —
    /// which Unity has no native TextureFormat for — is CPU-decoded to RGBA32 (ported from bms_sdo/dds_codec).
    /// Many stage textures (REN audience, DENG lights…) are DXT3, so without this they showed as white squares.
    /// Header: "DDS " | DDS_HEADER(124): dwHeight@12, dwWidth@16, pixelformat.fourCC@84. Base mip only.
    /// </summary>
    public static class DdsLoader
    {
        public static Texture2D Load(byte[] d)
        {
            if (d == null || d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || d[3] != ' ') return null;
            int height = BitConverter.ToInt32(d, 12);
            int width = BitConverter.ToInt32(d, 16);
            string fourcc = System.Text.Encoding.ASCII.GetString(d, 84, 4);
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return null;

            if (fourcc == "DXT3") return DecodeDxt3(d, 128, width, height);

            TextureFormat fmt; int blockBytes;
            switch (fourcc)
            {
                case "DXT1": fmt = TextureFormat.DXT1; blockBytes = 8; break;
                case "DXT5": fmt = TextureFormat.DXT5; blockBytes = 16; break;
                default: return null;
            }
            int baseSize = Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockBytes;
            if (128 + baseSize > d.Length) return null;
            var raw = new byte[baseSize]; Array.Copy(d, 128, raw, 0, baseSize);
            var tex = new Texture2D(width, height, fmt, false) { wrapMode = TextureWrapMode.Clamp };
            tex.LoadRawTextureData(raw); tex.Apply(false, true);
            return tex;
        }

        // BC2: 16-byte blocks = 8 bytes 4-bit alpha + 8 bytes DXT1-style colour (always 4-colour, no 1-bit alpha)
        private static Texture2D DecodeDxt3(byte[] d, int off, int w, int h)
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
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px); tex.Apply(false, true);
            return tex;
        }

        private static Color32 From565(ushort c) =>
            new Color32((byte)(((c >> 11) & 0x1F) * 255 / 31), (byte)(((c >> 5) & 0x3F) * 255 / 63), (byte)((c & 0x1F) * 255 / 31), 255);
    }
}
