using System;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// Covers the mapobj transparency / texture-animation pure logic added for the "去背 + missing-texture"
    /// stage-asset pass: DDS alpha detection, the _TexAnimEx material/.an parser, and the new frame-swap entries.
    /// </summary>
    public class MapobjTransparencyTests
    {
        // Minimal valid DDS: 128-byte header ("DDS ", dims, fourCC@84) + one 4x4 block of payload.
        private static byte[] MakeDds(string fourcc, byte[] block, int w = 4, int h = 4)
        {
            var d = new byte[128 + block.Length];
            d[0] = (byte)'D'; d[1] = (byte)'D'; d[2] = (byte)'S'; d[3] = (byte)' ';
            BitConverter.GetBytes(h).CopyTo(d, 12);
            BitConverter.GetBytes(w).CopyTo(d, 16);
            System.Text.Encoding.ASCII.GetBytes(fourcc).CopyTo(d, 84);
            block.CopyTo(d, 128);
            return d;
        }

        private static byte[] MakeDxt3ColorBlock(byte alphaNibble, ushort color565)
        {
            var block = new byte[16];
            byte a = (byte)(alphaNibble | (alphaNibble << 4));
            for (int i = 0; i < 8; i++) block[i] = a;
            BitConverter.GetBytes(color565).CopyTo(block, 8);
            BitConverter.GetBytes((ushort)0).CopyTo(block, 10);
            return block;
        }

        [Test]
        public void HasAlpha_Dxt3_AllOpaque_IsFalse()
        {
            var block = new byte[16];
            for (int i = 0; i < 8; i++) block[i] = 0xFF;   // every 4-bit alpha nibble = 15 -> 255
            Assert.IsFalse(DdsLoader.HasAlpha(MakeDds("DXT3", block)));
        }

        [Test]
        public void HasAlpha_Dxt3_WithTransparentTexel_IsTrue()
        {
            var block = new byte[16];
            for (int i = 0; i < 8; i++) block[i] = 0xFF;
            block[0] = 0x00;   // one texel fully transparent
            Assert.IsTrue(DdsLoader.HasAlpha(MakeDds("DXT3", block)));
        }

        [Test]
        public void HasAlpha_Dxt5_OpaqueEndpoints_IsFalse()
        {
            var block = new byte[16]; block[0] = 255; block[1] = 255;   // a0=a1=255, no interpolation below 255
            Assert.IsFalse(DdsLoader.HasAlpha(MakeDds("DXT5", block)));
        }

        [Test]
        public void HasAlpha_Dxt5_SixCodePalette_IsTrue()
        {
            var block = new byte[16]; block[0] = 0; block[1] = 255;   // a0<a1 -> palette includes 0 -> transparent reachable
            Assert.IsTrue(DdsLoader.HasAlpha(MakeDds("DXT5", block)));
        }

        [Test]
        public void HasAlpha_Dxt1_IsAlwaysOpaque()
        {
            Assert.IsFalse(DdsLoader.HasAlpha(MakeDds("DXT1", new byte[8])));
        }

        [Test]
        public void GetAlphaMode_Dxt3_HardTransparent_IsCutout()
        {
            var block = new byte[16];
            for (int i = 0; i < 8; i++) block[i] = 0xFF;
            block[0] = 0xF0;   // one 0-alpha nibble, the rest opaque
            Assert.AreEqual(DdsAlphaMode.Cutout, DdsLoader.GetAlphaMode(MakeDds("DXT3", block)));
        }

        [Test]
        public void GetAlphaMode_Dxt3_SoftAlpha_IsBlend()
        {
            var block = new byte[16];
            for (int i = 0; i < 8; i++) block[i] = 0x99;   // alpha 153/255
            Assert.AreEqual(DdsAlphaMode.Blend, DdsLoader.GetAlphaMode(MakeDds("DXT3", block)));
        }

        [Test]
        public void GetAlphaMode_Dxt5_SelectedSoftAlpha_IsBlend()
        {
            var block = new byte[16];
            block[0] = 255; block[1] = 0;
            block[2] = 0x02;   // first 3-bit selector = code 2 -> interpolated alpha
            Assert.AreEqual(DdsAlphaMode.Blend, DdsLoader.GetAlphaMode(MakeDds("DXT5", block)));
        }

        [Test]
        public void LooksLikeAdditiveGlow_Detects_Soft_Bright_Dxt3()
        {
            Assert.IsTrue(DdsLoader.LooksLikeAdditiveGlow(MakeDds("DXT3", MakeDxt3ColorBlock(9, 0xffff))));
        }

        [Test]
        public void LooksLikeAdditiveGlow_Detects_SoftRadialGradient_Dxt3()
        {
            // Radial-gradient glow sprite: transparent border (pixels 0-7 alpha=0) + soft centre (pixels 8-15 alpha≈136).
            // visibleRatio=0.5<0.95, softOfVisible=1.0, opaqueOfVisible=0, meanLum=132 → additive path fires.
            // Models GUANG1_.DDS (86.7% visible, 100% soft, 0% opaque, meanLum=81).
            var block = new byte[16];
            // pixels 0-7: alpha nibble=0 (bytes 0-3 stay zero)
            // pixels 8-15: alpha nibble=8 → alpha=(8*255+7)/15=136 (soft)
            block[4] = 0x88; block[5] = 0x88; block[6] = 0x88; block[7] = 0x88;
            BitConverter.GetBytes((ushort)0x8410).CopyTo(block, 8);  // medium gray, MaxLum≈132
            Assert.IsTrue(DdsLoader.LooksLikeAdditiveGlow(MakeDds("DXT3", block)));
        }

        [Test]
        public void LooksLikeAdditiveGlow_Rejects_Opaque_Or_Dim_Dxt3()
        {
            // All-opaque (alpha=255): Soft==0, returns false immediately.
            Assert.IsFalse(DdsLoader.LooksLikeAdditiveGlow(MakeDds("DXT3", MakeDxt3ColorBlock(15, 0xffff))));
            // Uniform soft alpha (all 16 pixels visible, visibleRatio=1.0): NOT < 0.95, does not hit the radial-gradient
            // path; meanLum≈66 < 180, so the bright-glow path also fails → false.
            Assert.IsFalse(DdsLoader.LooksLikeAdditiveGlow(MakeDds("DXT3", MakeDxt3ColorBlock(9, 0x4208))));
        }

        [Test]
        public void HasAlpha_NotDds_IsFalse()
        {
            Assert.IsFalse(DdsLoader.HasAlpha(null));
            Assert.IsFalse(DdsLoader.HasAlpha(new byte[16]));   // too short / no magic
        }

        // 5×5: opaque red core, white transparent matte around it (the SCN0026 car-billboard shape in miniature).
        private static Color32[] RedOnWhiteMatte(int w, int h)
        {
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 0);   // white, fully transparent
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                    px[y * w + x] = new Color32(220, 10, 10, 255);                        // opaque red core
            return px;
        }

        [Test]
        public void BleedAlphaEdges_Replaces_White_Matte_With_Prop_Colour()
        {
            const int w = 5, h = 5;
            var px = RedOnWhiteMatte(w, h);
            DdsLoader.BleedAlphaEdges(px, w, h);
            // a transparent matte texel adjacent to the red core is now reddish, not white — alpha untouched.
            var edge = px[0 * w + 1];   // top edge, directly above a core texel
            Assert.Less(edge.b, 120, "blue channel of the matte should drop (no longer white)");
            Assert.Greater(edge.r, edge.b, "matte takes on the red prop colour");
            Assert.AreEqual(0, edge.a, "alpha (the cut-out shape) is unchanged");
            // the opaque core is left exactly as authored.
            var core = px[2 * w + 2];
            Assert.AreEqual(220, core.r); Assert.AreEqual(10, core.g); Assert.AreEqual(255, core.a);
        }

        [Test]
        public void BleedAlphaEdges_FullyOpaque_IsNoOp()
        {
            var px = new Color32[9];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32((byte)(i * 10), 5, 5, 255);
            var copy = (Color32[])px.Clone();
            DdsLoader.BleedAlphaEdges(px, 3, 3);
            for (int i = 0; i < px.Length; i++)
            {
                Assert.AreEqual(copy[i].r, px[i].r); Assert.AreEqual(copy[i].g, px[i].g);
                Assert.AreEqual(copy[i].b, px[i].b); Assert.AreEqual(copy[i].a, px[i].a);
            }
        }

        [Test]
        public void TexAnimEx_Parses_Name_And_Interval()
        {
            Assert.IsTrue(TexAnimEx.TryParse("_texanimex(fangzi3)1300_0.dds", out var s));
            Assert.AreEqual("fangzi3", s.Name);
            Assert.AreEqual(1300f, s.IntervalMs, 1e-3f);

            Assert.IsTrue(TexAnimEx.TryParse("_TEXANIMEX(DENG1)1200_0_.dds", out var s2));
            Assert.AreEqual("DENG1", s2.Name);
            Assert.AreEqual(1200f, s2.IntervalMs, 1e-3f);

            Assert.IsTrue(TexAnimEx.TryParse("_texanimex(cs2)200_0_.dds", out var s3));
            Assert.AreEqual("cs2", s3.Name);
            Assert.AreEqual(200f, s3.IntervalMs, 1e-3f);

            // 翅膀/wings — 花雨飞翼 023921: NAME carries a trailing underscore, interval 200. The ".an" is <NAME>.an.
            Assert.IsTrue(TexAnimEx.TryParse("_texanimex(023921_woman_chibang1_)200_1.dds", out var w));
            Assert.AreEqual("023921_woman_chibang1_", w.Name);
            Assert.AreEqual(200f, w.IntervalMs, 1e-3f);
        }

        [Test]
        public void TexAnimEx_Rejects_Ordinary_Material_Names()
        {
            Assert.IsFalse(TexAnimEx.TryParse("guanggao.dds", out _));
            Assert.IsFalse(TexAnimEx.TryParse("", out _));
            Assert.IsFalse(TexAnimEx.TryParse(null, out _));
        }

        [Test]
        public void TexAnimEx_ParseAn_Splits_Dds_Lines()
        {
            var frames = TexAnimEx.ParseAn("fangzi3an.dds\r\nfangzi3liang.dds");
            Assert.AreEqual(2, frames.Length);
            Assert.AreEqual("fangzi3an.dds", frames[0]);
            Assert.AreEqual("fangzi3liang.dds", frames[1]);
            // tolerates trailing whitespace / blank lines / non-image noise
            var f2 = TexAnimEx.ParseAn("  a.dds \n\n  b.dds  \r\nnote\n");
            Assert.AreEqual(2, f2.Length);
            Assert.AreEqual("a.dds", f2[0]);
            Assert.AreEqual("b.dds", f2[1]);
        }

        [Test]
        public void TexAnimEx_ParseAn_Accepts_Tga_Frames()
        {
            // 翅膀/wings ship their glow frames as .tga (花雨飞翼 023921_WOMAN_CHIBANG1_.AN) — dropping these was why
            // the wings fell back to the flat beige colour. .tga/.bmp/.png must all be kept; non-image lines ignored.
            var frames = TexAnimEx.ParseAn("023921_woman_chibang1_.tga\r\n023921_woman_chibang2_.tga\r\n023921_woman_chibang3_.tga\r\n");
            Assert.AreEqual(3, frames.Length);
            Assert.AreEqual("023921_woman_chibang1_.tga", frames[0]);
            Assert.AreEqual("023921_woman_chibang3_.tga", frames[2]);

            var mixed = TexAnimEx.ParseAn("a.tga\nb.dds\nc.bmp\nd.png\nreadme\n");
            Assert.AreEqual(4, mixed.Length);
        }

        [Test]
        public void TexAnim_Christmas_Billboards_Are_Animated_Transparent()
        {
            // SCN0005's reindeer + ground decal: placeholder MSH materials -> must be driven by the frame sequence.
            var christmas = SceneMapobjTexAnimCatalog.Find("SCN0005", "CHRISTMAS");
            Assert.IsNotNull(christmas);
            Assert.AreEqual(4, christmas.Frames.Length);
            Assert.AreEqual("CHRISTMAS001.dds", christmas.Frames[0]);
            Assert.IsTrue(christmas.Transparent);

            Assert.IsNotNull(SceneMapobjCatalog.ForFolder("SCN0005"));   // sanity: the scene mounts these props
            Assert.IsNotNull(SceneMapobjTexAnimCatalog.Find("SCN0005", "MERRYCHRISTMAS"));
        }

        [Test]
        public void TexAnim_Boat_Screen_Is_Opaque_But_Water_Ripple_Transparent()
        {
            var screen = SceneMapobjTexAnimCatalog.Find("SCN0018", "BOAT_SCREEN");
            Assert.IsNotNull(screen);
            Assert.IsFalse(screen.Transparent, "the boat video screen is opaque");
            Assert.AreEqual(4, screen.Frames.Length);

            var shuimo = SceneMapobjTexAnimCatalog.Find("SCN0018", "SHUIMO");
            Assert.IsNotNull(shuimo);
            Assert.IsTrue(shuimo.Transparent, "the ink-water ripple is an alpha cut-out");
        }

        [Test]
        public void SaloonDeng_Marquee_Pattern_Is_Verbatim()
        {
            // SCN0021 ceiling light bars run a shared 198×12 on/off table (DAT_005518e8), NOT per-prop cyclers — so
            // they're driven by SaloonDengMarquee, not the tex-anim catalog. Verify the embedded table verbatim.
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0021", "DENG1"), "deng is the marquee's job, not the catalog");
            Assert.IsNotNull(SceneMapobjCatalog.ForFolder("SCN0021"));   // sanity: the scene still mounts the deng meshes

            var lit = SaloonDengPattern.Lit;
            Assert.AreEqual(198, SaloonDengPattern.Rows);
            Assert.AreEqual(12, SaloonDengPattern.Bars);
            Assert.AreEqual(198, lit.Length);
            Assert.AreEqual(100f, SaloonDengPattern.IntervalMs);

            // rows 0..11 = a single light sweeping bar0 -> bar11 (left -> right across the dome).
            for (int r = 0; r < 12; r++)
            {
                int onCount = 0, onBar = -1;
                for (int b = 0; b < 12; b++) if (lit[r][b]) { onCount++; onBar = b; }
                Assert.AreEqual(1, onCount, "row " + r + " lights exactly one bar");
                Assert.AreEqual(r, onBar, "the lit bar sweeps left->right");
            }
            // tail blackout (final rows all off) and total lit-cell count (576 ones in the EXE).
            for (int b = 0; b < 12; b++) Assert.IsFalse(lit[197][b], "row 197 is full blackout");
            int total = 0;
            foreach (var row in lit) foreach (var on in row) if (on) total++;
            Assert.AreEqual(576, total, "lit-cell count matches DAT_005518e8");
        }

        [Test]
        public void TexAnim_Lookup_Misses_Are_Null()
        {
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0005", "NOT_A_PROP"));
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN9999", "CHRISTMAS"));
        }

        [Test]
        public void SceneEft_Scn0008_Is_The_Magic_Circle()
        {
            var fx = SceneEftCatalog.ForFolder("SCN0008");
            Assert.AreEqual(1, fx.Count);
            Assert.AreEqual("kikkai_3", fx[0].Eft);
            Assert.AreEqual(0f, fx[0].X); Assert.AreEqual(0f, fx[0].Y); Assert.AreEqual(0f, fx[0].Z);
            Assert.AreEqual(180f, fx[0].Ey, 1e-3f);   // Y-rotated 180° (decompiled 0x43340000)
            Assert.AreEqual(40f, fx[0].Scale, 1e-3f); // scale 40 (0x42200000)
        }

        [Test]
        public void SceneEft_Multi_And_Empty()
        {
            var scn0011 = SceneEftCatalog.ForFolder("SCN0011");
            Assert.AreEqual(8, scn0011.Count);   // 2 bgl + 2 gravcolor + 4 stagelightb
            for (int i = 4; i < scn0011.Count; i++)
            {
                Assert.AreEqual(0f, scn0011[i].Ex, 1e-3f);
                Assert.AreEqual(0f, scn0011[i].Ey, 1e-3f);
                Assert.AreEqual(0f, scn0011[i].Ez, 1e-3f);
            }
            Assert.AreEqual(5, SceneEftCatalog.ForFolder("SCN0014").Count);   // aurora + 4 bubbles
            Assert.AreEqual(0, SceneEftCatalog.ForFolder("SCN0009").Count);   // GUATAN scene has no bg EFT
            Assert.AreEqual(0, SceneEftCatalog.ForFolder(null).Count);
            // personal room + wedding hall share the same effect set
            Assert.AreEqual(3, SceneEftCatalog.ForFolder("SCN0037").Count);
            Assert.AreEqual(SceneEftCatalog.ForFolder("SCN0037").Count, SceneEftCatalog.ForFolder("SCN0038").Count);
        }

        [Test]
        public void SceneEft_Scn0015_Fire3_And_Booklights()
        {
            var fx = SceneEftCatalog.ForFolder("SCN0015");
            Assert.AreEqual(1, fx.Count);   // fire3; booklight is bone-attached to SHU1-4

            // fire3: from decompiled Effect_Play(0x35) + Effect_SetTransformAnimated coords
            var fire3 = fx[0];
            Assert.AreEqual("fire3", fire3.Eft);
            Assert.AreEqual(55.15f, fire3.X, 0.01f);
            Assert.AreEqual(339.83f, fire3.Y, 0.01f);
            Assert.AreEqual(1237.66f, fire3.Z, 0.01f);
            Assert.AreEqual(100f, fire3.Scale, 1e-3f);
        }

        [Test]
        public void SceneAttachedEft_Scn0015_Booklights_Follow_Official_Bones()
        {
            var map = new[]
            {
                ("15_SHU1", "Plane29"),
                ("15_SHU2", "Plane66"),
                ("15_SHU3", "Plane33"),
                ("15_SHU4", "Plane31"),
            };

            foreach (var (mapobj, bone) in map)
            {
                var attached = SceneAttachedEftCatalog.ForMapobj("SCN0015", mapobj);
                Assert.AreEqual(1, attached.Count);
                Assert.AreEqual("booklight", attached[0].Eft);
                Assert.AreEqual(bone, attached[0].Bone);
                Assert.AreEqual(0f, attached[0].Offset.x, 1e-3f);
                Assert.AreEqual(-5f, attached[0].Offset.y, 1e-3f);
                Assert.AreEqual(0f, attached[0].Offset.z, 1e-3f);
                Assert.AreEqual(20f, attached[0].Scale, 1e-3f);
            }
        }

        [Test]
        public void SceneEftRender_Scn0015_Profiles_Fire_And_Book_Glow()
        {
            var flame = SceneEftRenderCatalog.Find("fire3", 2, 84);
            Assert.Greater(flame.RgbMul, 1f);
            Assert.Greater(flame.AlphaMul, 1f);
            Assert.AreEqual(1f, flame.ScaleMul.x, 1e-3f);
            Assert.AreEqual(1f, flame.ScaleMul.y, 1e-3f);

            var halo = SceneEftRenderCatalog.Find("fire3", 4, 30);
            Assert.Less(halo.RgbMul, 1f);
            Assert.Less(halo.AlphaMul, 1f);
            Assert.Greater(halo.ScaleMul.x, 0.5f);
            Assert.Greater(halo.ScaleMul.y, 0.5f);

            var book = SceneEftRenderCatalog.Find("booklight", 2, 31);
            Assert.Less(book.RgbMul, 1f);
            Assert.Less(book.AlphaMul, 1f);
            Assert.Greater(book.ScaleMul.x, 1.5f);
            Assert.Less(book.ScaleMul.x, 2f);
            Assert.AreEqual(1f, SceneEftRenderCatalog.Find("booklight", 0, 0).ScaleMul.x, 1e-3f);
        }

        [Test]
        public void SceneUvScroll_Scn0015_WindowBeam_Uses_Mapobj_Target()
        {
            var speed = SceneMapobjUvScrollCatalog.Find("SCN0015", "15_UV");
            Assert.AreEqual(0f, speed.x, 1e-6f);
            Assert.AreEqual(0.06f, speed.y, 1e-6f);
            Assert.IsTrue(SceneMapobjUvScrollCatalog.UsesAdditiveOverlay("SCN0015", "15_UV"));
            Assert.AreEqual(0f, SceneMapobjUvScrollCatalog.Find("SCN0015", "15_HUA").sqrMagnitude, 1e-6f);
            Assert.IsFalse(SceneMapobjUvScrollCatalog.UsesAdditiveOverlay("SCN0015", "15_HUA"));
        }
    }
}
