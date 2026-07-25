using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Guards <see cref="DdsLoader.DecodeToPixels"/> — the readable-pixel decode the external-song CD disc uses so a
    /// song folder's .dds cover can be shown (<see cref="ExternalCdImage"/>). Before this, that path only read 32-bit
    /// uncompressed DDS, so a compressed disc (e.g. lapis/12956.DDS is DXT3) came back null and the disc showed NONE.
    /// These build one-block DDS files by hand and pin the colour + alpha of each layout (DXT1 opaque, DXT1 punch-through,
    /// DXT3, DXT5, 32-bit uncompressed), plus a real-data smoke check against the shipped lapis disc when present.
    /// </summary>
    public class DdsDecodeToPixelsTests
    {
        // A 128-byte DDS header with the given size / pixel-format fields; block/pixel bytes are appended by the caller.
        private static byte[] Header(int w, int h, string fourcc, uint pfFlags, int bits,
                                     uint rm = 0, uint gm = 0, uint bm = 0, uint am = 0)
        {
            var d = new byte[128];
            d[0] = (byte)'D'; d[1] = (byte)'D'; d[2] = (byte)'S'; d[3] = (byte)' ';
            BitConverter.GetBytes(124).CopyTo(d, 4);       // dwSize
            BitConverter.GetBytes(h).CopyTo(d, 12);        // dwHeight
            BitConverter.GetBytes(w).CopyTo(d, 16);        // dwWidth
            BitConverter.GetBytes(pfFlags).CopyTo(d, 80);  // ddspf.dwFlags
            if (fourcc != null) System.Text.Encoding.ASCII.GetBytes(fourcc).CopyTo(d, 84);
            BitConverter.GetBytes(bits).CopyTo(d, 88);     // ddspf.dwRGBBitCount
            BitConverter.GetBytes(rm).CopyTo(d, 92);
            BitConverter.GetBytes(gm).CopyTo(d, 96);
            BitConverter.GetBytes(bm).CopyTo(d, 100);
            BitConverter.GetBytes(am).CopyTo(d, 104);
            return d;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Array.Copy(a, r, a.Length); Array.Copy(b, 0, r, a.Length, b.Length);
            return r;
        }

        private const ushort Red565 = 0xF800, Green565 = 0x07E0, Blue565 = 0x001F;

        [Test]
        public void Dxt3_SolidRedOpaque_DecodesRedAlpha255()
        {
            // One 4x4 block: colour index 0 → c0 (red); every 4-bit alpha nibble = 0xF (opaque).
            var block = new byte[16];
            for (int i = 0; i < 8; i++) block[i] = 0xFF;                 // alpha: all 0xF nibbles
            block[8] = (byte)(Red565 & 0xFF); block[9] = (byte)(Red565 >> 8);   // c0 = red
            // c1 = 0, indices (bytes 12..15) = 0 → all texels select c0
            var d = Concat(Header(4, 4, "DXT3", 0x4u, 0), block);

            var px = DdsLoader.DecodeToPixels(d, out int w, out int h);
            Assert.IsNotNull(px);
            Assert.AreEqual(4, w); Assert.AreEqual(4, h); Assert.AreEqual(16, px.Length);
            foreach (var c in px) { Assert.AreEqual(255, c.r); Assert.AreEqual(0, c.g); Assert.AreEqual(0, c.b); Assert.AreEqual(255, c.a); }
        }

        [Test]
        public void Dxt3_ZeroAlpha_DecodesTransparent()
        {
            var block = new byte[16];                                   // alpha bytes 0 → every texel alpha 0
            block[8] = (byte)(Red565 & 0xFF); block[9] = (byte)(Red565 >> 8);
            var d = Concat(Header(4, 4, "DXT3", 0x4u, 0), block);

            var px = DdsLoader.DecodeToPixels(d, out _, out _);
            Assert.IsNotNull(px);
            foreach (var c in px) Assert.AreEqual(0, c.a);
        }

        [Test]
        public void Dxt1_C0GreaterThanC1_SolidOpaque()
        {
            // c0 (red) > c1 (blue) → 4-colour opaque mode; indices 0 → c0.
            var block = new byte[8];
            block[0] = (byte)(Red565 & 0xFF); block[1] = (byte)(Red565 >> 8);
            block[2] = (byte)(Blue565 & 0xFF); block[3] = (byte)(Blue565 >> 8);
            var d = Concat(Header(4, 4, "DXT1", 0x4u, 0), block);

            var px = DdsLoader.DecodeToPixels(d, out _, out _);
            Assert.IsNotNull(px);
            foreach (var c in px) { Assert.AreEqual(255, c.r); Assert.AreEqual(0, c.g); Assert.AreEqual(0, c.b); Assert.AreEqual(255, c.a); }
        }

        [Test]
        public void Dxt1_C0LessThanC1_Index3_PunchesTransparent()
        {
            // c0 (0) <= c1 (0xFFFF) → 3-colour + transparent 4th index. All indices = 3 → alpha 0 (round-disc corners).
            var block = new byte[8];
            block[2] = 0xFF; block[3] = 0xFF;                           // c1 = 0xFFFF, c0 = 0
            block[4] = 0xFF; block[5] = 0xFF; block[6] = 0xFF; block[7] = 0xFF;   // indices all = 3
            var d = Concat(Header(4, 4, "DXT1", 0x4u, 0), block);

            var px = DdsLoader.DecodeToPixels(d, out _, out _);
            Assert.IsNotNull(px);
            foreach (var c in px) Assert.AreEqual(0, c.a);
        }

        [Test]
        public void Dxt5_A0_SolidGreenOpaque()
        {
            // Alpha: a0 = 255, a1 = 0, all 3-bit codes 0 → a0 (255). Colour c0 = green, indices 0.
            var block = new byte[16];
            block[0] = 255; block[1] = 0;                               // a0, a1; alpha code bytes 2..7 = 0 → code 0 = a0
            block[8] = (byte)(Green565 & 0xFF); block[9] = (byte)(Green565 >> 8);   // c0 = green
            var d = Concat(Header(4, 4, "DXT5", 0x4u, 0), block);

            var px = DdsLoader.DecodeToPixels(d, out _, out _);
            Assert.IsNotNull(px);
            foreach (var c in px) { Assert.AreEqual(0, c.r); Assert.AreEqual(255, c.g); Assert.AreEqual(0, c.b); Assert.AreEqual(255, c.a); }
        }

        [Test]
        public void Uncompressed32_A8R8G8B8_DecodesChannels()
        {
            // 2x2 A8R8G8B8: DDPF_RGB|DDPF_ALPHAPIXELS (0x41), 32-bit, D3D BGRA byte order in file.
            uint rm = 0x00FF0000, gm = 0x0000FF00, bm = 0x000000FF, am = 0xFF000000;
            var h = Header(2, 2, null, 0x41u, 32, rm, gm, bm, am);
            var body = new byte[2 * 2 * 4];
            for (int i = 0; i < 4; i++)                                 // pixel (R=10,G=20,B=30,A=40) → bytes B,G,R,A
            { body[i * 4] = 30; body[i * 4 + 1] = 20; body[i * 4 + 2] = 10; body[i * 4 + 3] = 40; }
            var d = Concat(h, body);

            var px = DdsLoader.DecodeToPixels(d, out int w, out int hh);
            Assert.IsNotNull(px);
            Assert.AreEqual(2, w); Assert.AreEqual(2, hh);
            foreach (var c in px) { Assert.AreEqual(10, c.r); Assert.AreEqual(20, c.g); Assert.AreEqual(30, c.b); Assert.AreEqual(40, c.a); }
        }

        [Test]
        public void NullOrTruncated_ReturnsNull()
        {
            Assert.IsNull(DdsLoader.DecodeToPixels(null, out _, out _));
            Assert.IsNull(DdsLoader.DecodeToPixels(new byte[64], out _, out _));         // shorter than a header
            var noBlocks = Header(4, 4, "DXT3", 0x4u, 0);                                // header claims 4x4 but no block bytes
            Assert.IsNull(DdsLoader.DecodeToPixels(noBlocks, out _, out _));
        }

        [Test]
        public void RealLapisDisc_DecodesUpright240WithTransparentCorners()
        {
            // The song folder that motivated this: a DXT3 disc that the old 32-bit-only path returned null for.
            string path = Path.Combine(Application.dataPath, "..", "Songs", "test2", "lapis", "12956.DDS");
            if (!File.Exists(path)) Assert.Ignore("lapis/12956.DDS not present in this checkout");

            var px = DdsLoader.DecodeToPixels(File.ReadAllBytes(path), out int w, out int h);
            Assert.IsNotNull(px, "compressed CD disc must now decode");
            Assert.AreEqual(240, w); Assert.AreEqual(240, h);

            int transparent = 0, opaque = 0;
            foreach (var c in px) { if (c.a <= 8) transparent++; else if (c.a >= 247) opaque++; }
            Assert.Greater(opaque, px.Length / 4, "the disc art itself must be present");
            Assert.Greater(transparent, 0, "a round disc keys out its corners");
        }
    }
}
