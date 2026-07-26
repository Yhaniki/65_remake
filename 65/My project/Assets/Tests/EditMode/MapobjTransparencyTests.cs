using System;
using System.IO;
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

        // The mapobj depth-write fix (SCN0009 GUATAN 掛毯 穿模) routes an ANIMATED prop to the cutout (ZWrite On)
        // shader when its texture is GetSceneAlphaMode==Cutout. Pin the two decision points the gate relies on:
        [Test]
        public void GetSceneAlphaMode_Dxt3_MostlyOpaqueWithHoles_IsCutout()
        {
            // guantan.dds profile: ~91% opaque body + ~6% hard-transparent holes, almost no midtone → Cutout.
            var block = new byte[16];
            for (int i = 0; i < 8; i++) block[i] = 0xFF;   // all opaque
            block[0] = 0xF0;                               // one texel fully transparent (1/16 = 6.25% hole)
            Assert.AreEqual(DdsAlphaMode.Cutout, DdsLoader.GetSceneAlphaMode(MakeDds("DXT3", block)));
        }

        [Test]
        public void GetSceneAlphaMode_Dxt3_SoftGradient_IsBlend()
        {
            // A soft/glow cloth (uniform midtone, no hard holes) must stay Blend so the gate does NOT depth-write it.
            var block = new byte[16];
            for (int i = 0; i < 8; i++) block[i] = 0x99;   // alpha 153/255 everywhere → soft
            Assert.AreEqual(DdsAlphaMode.Blend, DdsLoader.GetSceneAlphaMode(MakeDds("DXT3", block)));
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
        public void SceneFlame_Scn0022_Is_Three_Billboards_At_150ms()
        {
            // 坟墓 (SCN0022) 鬼火/蓝火: the official draws it as 3 camera-facing BillboardSet sprites (NOT the SHAN.MSH
            // mesh, which is loaded but never linked as a child) animated at 150 ms — StageScene_UpdateLightGroups_004b0b30
            // advances a 0x96=150ms timer, index=(i+1)%3, and writes the frame to all 3 billboards from one shared counter.
            var set = SceneFlameBillboardCatalog.ForFolder("SCN0022");
            Assert.IsNotNull(set);
            Assert.AreEqual(3, set.Frames.Length);
            Assert.AreEqual("FENMUOBJ_LANHUO_01_.TGA", set.Frames[0], "smooth 8-bit TGA (the DXT3 twin bands the glow)");
            Assert.AreEqual(150f, set.IntervalMs, 1e-3f);
            Assert.AreEqual(3, set.Billboards.Length);
            // decompiled billboard positions (030 case 0x16: param_1[0x56/57/58] +0x28/2c/30) + size 100 (0x42c80000).
            Assert.AreEqual(-163.43f, set.Billboards[0].X, 0.01f);
            Assert.AreEqual(40f, set.Billboards[0].Y, 0.01f);
            Assert.AreEqual(135.51f, set.Billboards[0].Z, 0.01f);
            Assert.AreEqual(182.96f, set.Billboards[2].X, 0.01f);
            foreach (var b in set.Billboards) Assert.AreEqual(100f, b.Size, 1e-3f);

            Assert.IsNotNull(SceneFlameBillboardCatalog.ForFolder("scn0022"), "folder key is case-insensitive");
            Assert.IsNull(SceneFlameBillboardCatalog.ForFolder("SCN0008"), "only 坟墓 has flame billboards");
        }

        [Test]
        public void SceneGhost_Scn0022_Flying_Ghosts_Are_Mot_Billboards()
        {
            // 坟墓 flying ghosts (gui/gui2): .mot-driven camera-facing billboards (the official Billboard_AddEntry's them),
            // with a GUI01↔GUI02 texture swing @125 ms. Rendering the flat .mot-baked mesh foreshortened/edge-on ("硬").
            var defs = SceneGhostBillboardCatalog.ForFolder("SCN0022");
            Assert.AreEqual(2, defs.Count);
            var gui = defs[0];
            Assert.AreEqual("LABA11.MOT", gui.Mot);
            Assert.AreEqual(2, gui.Frames.Length);
            Assert.AreEqual("FENMUOBJ_GUI_GUI01_.dds", gui.Frames[0]);
            Assert.AreEqual(125f, gui.IntervalMs, 1e-3f);
            Assert.Greater(gui.Size, 0f);
            Assert.AreEqual("SCENE/MAPOBJ/FENMU/GUI2", defs[1].Dir);

            Assert.AreEqual(2, SceneGhostBillboardCatalog.ForFolder("scn0022").Count);   // folder key case-insensitive
            Assert.AreEqual(0, SceneGhostBillboardCatalog.ForFolder("SCN0008").Count);   // only 坟墓 has ghost billboards
            // the ghost swing is NOT a mapobj-mesh tex-anim (the mesh is skipped) — it lives in the ghost catalog.
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0022", "LABA11"));
        }

        [Test]
        public void SceneUvScroll_Scn0022_Spotlights_Force_AlphaBlend()
        {
            // 射光 (sheguang1-3): the MSH loader makes these additive (LooksLikeAdditiveGlow false positive → hard edge,
            // no transparency variation, like SCN0015 窗光). Force two-sided standard alpha-blend so the beam reads soft.
            foreach (var key in new[] { "SHEGUANG", "SHEGUANG2", "SHEGUANG3" })
            {
                Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.AlphaBlendOverlay,
                    SceneMapobjUvScrollCatalog.FindRenderMode("SCN0022", key), key);
                Assert.AreEqual(0f, SceneMapobjUvScrollCatalog.Find("SCN0022", key).sqrMagnitude, 1e-6f, key + " does not UV-scroll");
            }
            // it must NOT be the additive path (that was the wrong fix) and must not leak to other scenes.
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.KeepMaterial,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0022", "LABA11"));
        }

        [Test]
        public void SmoothAlpha_Debands_Gradient_But_Preserves_Sharp_Detail()
        {
            // DXT3's 4-bit alpha quantises a soft glow to ~9-16 levels → concentric "tree-ring" bands (年輪). SmoothAlpha
            // is a BILATERAL pass: it merges the small quantisation steps into a smooth fade WITHOUT erasing high-contrast
            // detail (the SCN0022 ghost's eye/mouth holes live in the alpha). Synthesise a 16-band gradient + a hard hole.
            const int w = 64, h = 32;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int level = x * 16 / w;                                  // 16 bands, step 255/15 ≈ 17
                    px[y * w + x] = new Color32(200, 210, 255, (byte)(level * 255 / 15));
                }
            // punch a hard, high-contrast "eye" hole (alpha 0) inside a bright band (alpha ≈ 136) — the "face" detail.
            for (int y = 14; y < 18; y++)
                for (int x = 34; x < 40; x++)
                    px[y * w + x] = new Color32(200, 210, 255, 0);

            int before = DistinctAlpha(px);
            DdsLoader.SmoothAlpha(px, w, h);
            int after = DistinctAlpha(px);
            Assert.LessOrEqual(before, 17, "the synthetic gradient starts banded (≤16 levels + 0)");
            Assert.Greater(after, before * 2, "de-banding introduces many intermediate alpha levels → no visible rings");
            Assert.Less(px[16 * w + 37].a, 40, "the hard hole (the face) is PRESERVED, not blurred away");
            Assert.AreEqual(200, px[16 * w + 10].r, "RGB is untouched — only alpha is smoothed");
            Assert.AreEqual(255, px[16 * w + 10].b);
        }

        private static int DistinctAlpha(Color32[] px)
        {
            var seen = new bool[256]; int n = 0;
            foreach (var p in px) if (!seen[p.a]) { seen[p.a] = true; n++; }
            return n;
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

            // booklight slot2 = 書上的發光球：載體 baseSize 0.3 乘進來後球只有 ~14 世界單位（「發光太小」），
            // 所以 scaleMul 放大它（>2×）、rgbMul 壓暗顏色，alpha 維持原樣不動。
            var book = SceneEftRenderCatalog.Find("booklight", 2, 31);
            Assert.Less(book.RgbMul, 1f);
            Assert.AreEqual(1f, book.AlphaMul, 1e-3f);
            Assert.Greater(book.ScaleMul.x, 2f);
            Assert.AreEqual(1f, SceneEftRenderCatalog.Find("booklight", 0, 0).ScaleMul.x, 1e-3f);
        }

        /// <summary>SCENE/MAPOBJ under the real data root, or null when the game data isn't present.</summary>
        private static string MapobjDir()
        {
            try
            {
                var dir = Path.Combine(SdoExtracted.Root, Path.Combine("SCENE", "MAPOBJ"));
                return Directory.Exists(dir) ? dir : null;
            }
            catch { return null; }
        }

        private static uint FlagOf(System.Collections.Generic.List<(string Name, uint Flags)> table, string dds)
        {
            foreach (var m in table)
                if (string.Equals(Path.GetFileName(m.Name.Replace('\\', '/')), dds, StringComparison.OrdinalIgnoreCase))
                    return m.Flags;
            Assert.Fail("material '" + dds + "' not in the mesh's material table");
            return 0u;
        }

        [Test]
        public void RealData_Scn0014_Beam_And_Scn0025_Water_Are_Official_Transparent()
        {
            var root = MapobjDir();
            if (root == null) Assert.Ignore("SCENE/MAPOBJ data root not found — real-data row needs the game data (data_root.txt)");

            // 舞台中間的投影光:單材質,官方旗標 1 = 透明批。
            var guang = MshLoader.ReadMaterialTable(File.ReadAllBytes(Path.Combine(root, "14_HAIDI/GUANG/GUANG.MSH")));
            Assert.IsNotEmpty(guang);
            Assert.IsTrue(MshLoader.IsOfficialTransparent(FlagOf(guang, "touyingguang_.dds")));

            // 旋轉小電視:5 個材質,只有那道光是透明的 —— 所以 OfficialMaterialAlpha 必須逐材質套,
            // 不然螢幕/框架/投影機也會跟著變半透明。
            var tv = MshLoader.ReadMaterialTable(File.ReadAllBytes(Path.Combine(root, "14_HAIDI/TV/TV.MSH")));
            Assert.GreaterOrEqual(tv.Count, 5);
            Assert.IsTrue(MshLoader.IsOfficialTransparent(FlagOf(tv, "touyingguang_.dds")), "電視射出來的光");
            foreach (var opaque in new[] { "zhuanpan.dds", "gangjia.dds", "tv.dds", "touyingji_c.dds" })
                Assert.IsFalse(MshLoader.IsOfficialTransparent(FlagOf(tv, opaque)), opaque + " 官方是實心的");
            // 逐材質閘門真正依賴的不變量:整張表「恰好一個」透明材質。只點名那 5 個的話,ReadMaterialTable
            // 尾端探測若多吐一筆非 0 的記錄,上面每條斷言都還是綠的,但電視在遊戲裡會變半透明。
            int transparentCount = 0;
            foreach (var m in tv) if (MshLoader.IsOfficialTransparent(m.Flags)) transparentCount++;
            Assert.AreEqual(1, transparentCount, "TV.MSH 只有一個材質是官方透明批");

            // 噴水池的水:旗標 0x11。
            var water = MshLoader.ReadMaterialTable(File.ReadAllBytes(Path.Combine(root, "CHUNTIAN/DONGHUA/CHUNTIANDONGHUA.MSH")));
            Assert.AreEqual(0x11u, FlagOf(water, "shui_c_.dds"));
        }

        [Test]
        public void RealData_Scn0014_Beam_Mesh_Unwraps_Around_Its_Axis_In_U()
        {
            var root = MapobjDir();
            if (root == null) Assert.Ignore("SCENE/MAPOBJ data root not found");

            // 光是 6 段圓錐:U 沿著繞軸方向鋪滿 0..1、V 只有兩排(頂圈/底圈)。這就是為什麼官方捲的是 U ——
            // 捲 U 才會讀成「光紋繞著光軸轉」。這條把「磁碟上的幾何」和「catalog 捲哪個軸」綁在一起:
            // 幾何說可捲的是 U,catalog 就必須捲 U(有人改成捲 V 這裡會紅)。
            MshLoader.Result r = null;
            try
            {
                r = MshLoader.Load(File.ReadAllBytes(Path.Combine(root, "14_HAIDI/GUANG/GUANG.MSH")));
                Assert.IsNotNull(r);
                Assert.AreEqual(1, r.Submeshes.Count);
                var uv = r.Submeshes[0].Mesh.uv;
                Assert.AreEqual(14, uv.Length, "6 段圓錐 = 14 頂點 / 12 三角形");
                float uMin = 1f, uMax = 0f;
                var vs = new System.Collections.Generic.HashSet<int>();
                foreach (var t in uv)
                {
                    uMin = Mathf.Min(uMin, t.x); uMax = Mathf.Max(uMax, t.x);
                    vs.Add(Mathf.RoundToInt(t.y * 1000f));
                }
                Assert.Less(uMin, 0.05f, "U 從接近 0 開始");
                Assert.Greater(uMax, 0.95f, "U 鋪到接近 1(繞完一圈)");
                Assert.AreEqual(2, vs.Count, "V 只有兩排 → V 不是可捲的方向");

                // 幾何 → catalog:可捲的軸是 U,所以 catalog 也必須捲 U 而不是 V。
                var beam = SceneMapobjUvScrollCatalog.Find("SCN0014", "GUANG");
                Assert.Greater(Mathf.Abs(beam.x), 0f, "光要捲 U(繞軸自轉)");
                Assert.AreEqual(0f, beam.y, 1e-6f, "V 只有兩排,捲 V 沒有意義");
            }
            finally
            {
                if (r != null) foreach (var sub in r.Submeshes) if (sub.Mesh != null) UnityEngine.Object.DestroyImmediate(sub.Mesh);
            }
        }

        [Test]
        public void RenderMode_OfficialMaterialAlpha_Is_The_Only_PerMaterial_Mode()
        {
            // 這次修正的核心設計:OfficialMaterialAlpha 逐材質看官方旗標,其餘模式維持整支道具一起套。
            // 沒有這條的話,把 AddMapobj 的逐材質迴圈改回舊的 prop-wide foreach,其他測試都還是綠的。
            var perMat = SceneMapobjUvScrollCatalog.RenderMode.OfficialMaterialAlpha;
            Assert.IsFalse(SceneMapobjUvScrollCatalog.AppliesToMaterial(perMat, 0u), "旗標 0 的材質不能被改成透明");
            Assert.IsTrue(SceneMapobjUvScrollCatalog.AppliesToMaterial(perMat, 1u));
            Assert.IsTrue(SceneMapobjUvScrollCatalog.AppliesToMaterial(perMat, 0x11u));
            // 其餘模式是整支道具的:旗標不影響它們(維持既有已驗收的場景行為)。
            foreach (var mode in new[]
            {
                SceneMapobjUvScrollCatalog.RenderMode.KeepMaterial,
                SceneMapobjUvScrollCatalog.RenderMode.AdditiveOverlay,
                SceneMapobjUvScrollCatalog.RenderMode.ForceAlphaBlend,
                SceneMapobjUvScrollCatalog.RenderMode.AlphaBlendOverlay,
                SceneMapobjUvScrollCatalog.RenderMode.SpotGlow,
            })
            {
                Assert.IsTrue(SceneMapobjUvScrollCatalog.AppliesToMaterial(mode, 0u), mode + " 是整支道具套用");
                Assert.IsTrue(SceneMapobjUvScrollCatalog.AppliesToMaterial(mode, 2u), mode.ToString());
            }
        }

        [Test]
        public void RealData_Scn0014_Tv_Beam_Is_The_Only_Material_The_Mode_Touches()
        {
            var root = MapobjDir();
            if (root == null) Assert.Ignore("SCENE/MAPOBJ data root not found");

            // 端到端釘住:拿 TV.MSH 真正的材質表跑閘門,5 個材質中只有那道光會被換成 alpha-blend。
            var tv = MshLoader.ReadMaterialTable(File.ReadAllBytes(Path.Combine(root, "14_HAIDI/TV/TV.MSH")));
            var mode = SceneMapobjUvScrollCatalog.FindRenderMode("SCN0014", "TV");
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.OfficialMaterialAlpha, mode);
            int touched = 0;
            foreach (var m in tv)
                if (SceneMapobjUvScrollCatalog.AppliesToMaterial(mode, m.Flags))
                {
                    touched++;
                    Assert.IsTrue(m.Name.ToLowerInvariant().Contains("touyingguang"),
                        "只有投影光該被改;被改到的是 " + m.Name);
                }
            Assert.AreEqual(1, touched, "螢幕/鋼架/電視/投影機必須維持實心");
        }

        [Test]
        public void RealData_Scn0025_Butterfly_Wing_Frames_Exist_On_Disk()
        {
            var root = MapobjDir();
            if (root == null) Assert.Ignore("SCENE/MAPOBJ data root not found");

            // 幀是從「道具自己那個資料夾」解析的(AddMapobj: SCENE/MAPOBJ/<group.Folder>),所以資料夾要從
            // SceneMapobjCatalog 推導、不能在測試裡另外硬編一份 —— 否則搬動道具時這條抓不到真正的失效模式
            // (幀名對、但在 runtime 實際搜尋的資料夾裡找不到)。
            int checkedGroups = 0;
            foreach (var g in SceneMapobjCatalog.ForFolder("SCN0025"))
            {
                string meshBase = Path.GetFileNameWithoutExtension(g.Msh);
                var anim = SceneMapobjTexAnimCatalog.Find("SCN0025", meshBase);
                if (anim == null) continue;                    // 水/草不是換幀道具
                checkedGroups++;
                foreach (var f in anim.Frames)
                    Assert.IsTrue(File.Exists(Path.Combine(Path.Combine(root, g.Folder), f)),
                        g.Folder + "/" + f + " 不存在 → 換幀會退回佔位貼圖(蝴蝶不拍翅膀)");
            }
            Assert.AreEqual(4, checkedGroups, "四群蝴蝶都要接上換幀");
        }

        [Test]
        public void MshLoader_IsOfficialTransparent_Matches_Engine_Mask()
        {
            // 引擎唯一的判定是 flags & 0x3f != 0(非 0 → 延後透明批)。全語料只有 0/1/2/0x11/0x12 五種值。
            Assert.IsFalse(MshLoader.IsOfficialTransparent(0u), "0 = 不透明批");
            Assert.IsTrue(MshLoader.IsOfficialTransparent(1u), "SCN0014 投影光 / SCN0016 地板光條");
            Assert.IsTrue(MshLoader.IsOfficialTransparent(2u));
            Assert.IsTrue(MshLoader.IsOfficialTransparent(0x11u), "SCN0025 噴水池的水");
            Assert.IsTrue(MshLoader.IsOfficialTransparent(0x12u));
            Assert.IsFalse(MshLoader.IsOfficialTransparent(0x40u), "遮罩外的高位元不算透明");
        }

        [Test]
        public void SceneUvScroll_Scn0014_ProjectorBeams_Defer_To_Official_Flags()
        {
            // 舞台中間的 GUANG 與旋轉電視射出的光都貼 TOUYINGGUANG_.DDS(亮 RGB + 82% 軟 alpha),但壞法不同:
            // GUANG 自己 14 頂點 → 非 volumetric → LooksLikeAdditiveGlow 勝出 → 加法疊成死白;電視那道光是 773 頂點
            // submesh 裡的一個 range,而 volumetric 是逐 SUBMESH 算的 → cutout 勝過加法 → 12 個三角形的光柱被
            // clip(a-0.5) 切成硬邊。兩種都讀作「光沒去背」,官方旗標(都是 1 = 透明批)一次修掉兩種。
            foreach (var key in new[] { "GUANG", "TV" })
                Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.OfficialMaterialAlpha,
                    SceneMapobjUvScrollCatalog.FindRenderMode("SCN0014", key), key);
            // 不能外溢到同場景其他道具/別的場景。
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.KeepMaterial,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0014", "SEA_SCREEN"));
            // 別的場景叫 TV* 的道具不能被這條規則掃到(用 AreNotEqual,免得日後 SCN0020 自己加了別的 render
            // mode 就讓這條 SCN0014 的測試莫名其妙變紅)。
            Assert.AreNotEqual(SceneMapobjUvScrollCatalog.RenderMode.OfficialMaterialAlpha,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0020", "TV1"), "只有海底那台電視");
        }

        [Test]
        public void SceneUvScroll_Scn0014_Beam_Spins_In_U_And_Coral_Splits_Into_Two_Rates()
        {
            // FUN_004b0330 每 50 ms 寫 7 個貼圖座標:3× 珊瑚樹 V=−t、3× 珊瑚枝 V=−2t、第 7 個(唯一寫 U、4× 速率)
            // = 投影光。光的 mesh 是 6 段圓錐展開(U 0.018‥0.976、V 只有兩排),所以捲 U = 光紋繞著光軸自轉,
            // 疊在 .mot 帶著整支道具繞場公轉之上(它的 .mot 只有一根動畫骨,不可能另外自轉)。
            var beam = SceneMapobjUvScrollCatalog.Find("SCN0014", "GUANG");
            Assert.AreEqual(0.32f, beam.x, 1e-6f, "U += 0.016 per 50 ms = 4× 珊瑚速率");
            Assert.AreEqual(0f, beam.y, 1e-6f, "光只捲 U(官方 V 明確寫 0)");

            foreach (var tree in new[] { "SHANHU-BAI", "SHANHU-HONG", "SHANHU-LV" })
            {
                var s = SceneMapobjUvScrollCatalog.Find("SCN0014", tree);
                Assert.AreEqual(0f, s.x, 1e-6f, tree);
                Assert.AreEqual(-0.08f, s.y, 1e-6f, tree + " 是 1× 那組");
            }
            foreach (var branch in new[] { "SHANHUZHI-BAI", "SHANHUZHI-HONG", "SHANHUZHI-LV" })
            {
                var s = SceneMapobjUvScrollCatalog.Find("SCN0014", branch);
                Assert.AreEqual(0f, s.x, 1e-6f, branch);
                Assert.AreEqual(-0.16f, s.y, 1e-6f, branch + " 是 2× 那組");
            }
        }

        [Test]
        public void SceneUvScroll_Scn0025_Fountain_Flows_And_Defers_To_Official_Flags()
        {
            // FUN_004b0d20 最後一段:每 50 ms 設 U=0、V += _DAT_00589044(0.05)⇒ +1.0 UV/s(到 1.0 歸零)。
            var speed = SceneMapobjUvScrollCatalog.Find("SCN0025", "CHUNTIANDONGHUA");
            Assert.AreEqual(0f, speed.x, 1e-6f);
            Assert.AreEqual(1.0f, speed.y, 1e-6f);
            // 水的官方旗標是 0x11(透明批)。它 227 頂點、包圍盒 364×109×358 → volumetric,所以跟電視那道光一樣
            // 走 alpha-test:SHUI_C_ 只有 33% 的 texel 過得了 clip(a-0.5),RGB 又近乎純白 → 死白一片的硬邊水花。
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.OfficialMaterialAlpha,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0025", "CHUNTIANDONGHUA"));
            Assert.IsFalse(SceneMapobjUvScrollCatalog.UsesAdditiveOverlay("SCN0025", "CHUNTIANDONGHUA"));
            // 同場景的蝴蝶/草不捲、不改材質(蝴蝶走換幀)。
            Assert.AreEqual(0f, SceneMapobjUvScrollCatalog.Find("SCN0025", "HUDEICHUNTIANDONGHUA").sqrMagnitude, 1e-6f);
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.KeepMaterial,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0025", "CAOCHUNTIANDONGHUA"));
        }

        [Test]
        public void TexAnim_Scn0025_Butterflies_Flap_At_Official_Intervals()
        {
            // 四群蝴蝶各自一組 4 幀翅膀(Scene_LoadBackground case 0x19 載 CHUNTIAN_HUDEI<N>0..3),
            // FUN_004b0d20 用各自的計時器推進:0x32/0x28/0x3c/0x1e ms、index = (i+1) & 3。
            var expect = new (string Mesh, string Prefix, float Ms)[]
            {
                ("HUDEICHUNTIANDONGHUA",  "CHUNTIAN_HUDEI1", 50f),
                ("HUDEICHUNTIANDONGHUA2", "CHUNTIAN_HUDEI2", 40f),
                ("HUDEICHUNTIANDONGHUA3", "CHUNTIAN_HUDEI3", 60f),
                ("HUDEICHUNTIANDONGHUA4", "CHUNTIAN_HUDEI4", 30f),
            };
            foreach (var (mesh, prefix, ms) in expect)
            {
                var anim = SceneMapobjTexAnimCatalog.Find("SCN0025", mesh);
                Assert.IsNotNull(anim, mesh + " 少了翅膀換幀 → 蝴蝶飛但不拍翅膀");
                Assert.AreEqual(4, anim.Frames.Length, mesh);
                Assert.AreEqual(prefix + "0.dds", anim.Frames[0], mesh + " 幀從 0 起算");
                Assert.AreEqual(prefix + "3.dds", anim.Frames[3], mesh);
                Assert.AreEqual(ms, anim.IntervalMs, 1e-3f, mesh);
                Assert.IsTrue(anim.Transparent, mesh + " 是去背的蝴蝶剪影");
                Assert.IsFalse(anim.HoldLast, mesh + " 是循環拍翅膀,不是播一次");
            }
            // 水/草不是換幀(水靠 UV 捲動、草靠 .mot)。
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0025", "CHUNTIANDONGHUA"));
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0025", "CAOCHUNTIANDONGHUA"));
        }

        [Test]
        public void SceneUvScroll_Scn0015_WindowBeam_Uses_Mapobj_Target()
        {
            var speed = SceneMapobjUvScrollCatalog.Find("SCN0015", "15_UV");
            Assert.AreEqual(0f, speed.x, 1e-6f);
            Assert.AreEqual(0.06f, speed.y, 1e-6f);
            // D3D9 實測窗光是標準 alpha-blend（SRCALPHA/INVSRCALPHA），不是 additive：MSH loader 的
            // LooksLikeAdditiveGlow 會把 GUANG1_.DDS 誤判成輝光 → 用 ForceAlphaBlend 蓋掉它。
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.ForceAlphaBlend,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0015", "15_UV"));
            Assert.IsFalse(SceneMapobjUvScrollCatalog.UsesAdditiveOverlay("SCN0015", "15_UV"));
            Assert.AreEqual(0f, SceneMapobjUvScrollCatalog.Find("SCN0015", "15_HUA").sqrMagnitude, 1e-6f);
            Assert.IsFalse(SceneMapobjUvScrollCatalog.UsesAdditiveOverlay("SCN0015", "15_HUA"));
        }

        // ── SCN0024 雪景:光球招牌變色 + 背景探照燈 ────────────────────────────────────────────────
        // 官方 Scene_LoadBackground case 0x18 做三件事:載入 donghua/biaodonghua 兩個道具、把 5 張
        // Xuejing_Donghua_biao0N_.dds 讀進 param_1[0x59],再建三顆 guang_.tga 的 200×200 光暈 billboard。
        // 每幀更新 FUN_004b0cc0 只做一件事:每 500 ms 把第 idx=(i+1)%5 張綁到 biaodonghua 的材質槽 0。

        [Test]
        public void SceneTexAnim_Scn0024_Sign_Cycles_Five_Frames_At_500ms()
        {
            var sign = SceneMapobjTexAnimCatalog.Find("SCN0024", "BIAODONGHUA");
            Assert.IsNotNull(sign, "招牌沒有換幀條目 → 卡在 MSH 的 biao1_.dds 不變色");
            Assert.AreEqual(5, sign.Frames.Length, "param_1[0x59] 是 0x14 bytes = 5 個指標");
            Assert.AreEqual("XUEJING_DONGHUA_BIAO01_.dds", sign.Frames[0]);
            Assert.AreEqual("XUEJING_DONGHUA_BIAO05_.dds", sign.Frames[4]);
            Assert.AreEqual(500f, sign.IntervalMs, 1e-3f, "FUN_004b0cc0 的計時器參數是 500 ms");
            Assert.IsTrue(sign.Transparent, "DXT3 硬去背招牌,不是實心螢幕");
            Assert.IsFalse(sign.HoldLast, "(i+1) % 5 是無限循環,不是播一次");

            // 換幀只綁在 biaodonghua(objects[1])的材質槽 0;探照燈是 .mot 驅動,不能也被掛上換幀。
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0024", "DONGHUA"), "探照燈不換貼圖");
            // 名字別的場景不能撿到(BIAODONGHUA 只有雪景有)。
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0025", "BIAODONGHUA"));
        }

        [Test]
        public void SceneUvScroll_Scn0024_Searchlight_Defers_To_Official_Flags_And_Does_Not_Scroll()
        {
            // DENGGUANG_.DDS 是亮色 + 全軟 alpha 的光錐 → LooksLikeAdditiveGlow 誤判成加法;官方旗標是 0x1。
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.OfficialMaterialAlpha,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0024", "DONGHUA"));
            Assert.IsFalse(SceneMapobjUvScrollCatalog.UsesAdditiveOverlay("SCN0024", "DONGHUA"));
            // 場景 0x18 的更新完全沒寫 0x58/0x5c,所以光柱不捲 UV(動作全來自 .mot)。
            Assert.AreEqual(0f, SceneMapobjUvScrollCatalog.Find("SCN0024", "DONGHUA").sqrMagnitude, 1e-6f);
            // 招牌不套 render mode:它靠換幀,材質由 texAnim.Transparent 決定。
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.KeepMaterial,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0024", "BIAODONGHUA"));
        }

        [Test]
        public void SceneFlames_Scn0024_Has_Three_Static_Guang_Halos()
        {
            var set = SceneFlameBillboardCatalog.ForFolder("SCN0024");
            Assert.IsNotNull(set, "三顆招牌光暈是 billboard,不是 mapobj mesh → 少了整組就不見");
            Assert.AreEqual("SCENE/SCN0024", set.FramesDir, "guang_.tga 在場景自己的資料夾,不在 MAPOBJ 下");
            Assert.AreEqual(1, set.Frames.Length, "官方只載一張,FUN_004b0cc0 沒有換它");
            Assert.AreEqual("GUANG_.TGA", set.Frames[0]);
            Assert.AreEqual(3, set.Billboards.Length);
            foreach (var b in set.Billboards) Assert.AreEqual(200f, b.Size, 1e-3f, "0x43480000 = 200");
            Assert.AreEqual(432.109f, set.Billboards[0].X, 1e-3f);
            Assert.AreEqual(210.371f, set.Billboards[0].Y, 1e-3f);
            Assert.AreEqual(-3.208f, set.Billboards[0].Z, 1e-3f);
            Assert.AreEqual(-498.497f, set.Billboards[1].X, 1e-3f);
            Assert.AreEqual(75.41f, set.Billboards[2].X, 1e-3f);
            Assert.AreEqual(314.22f, set.Billboards[2].Z, 1e-3f);
            // SCN0022 那組不能被動到(同一張表,不同場景)。
            Assert.AreEqual(3, SceneFlameBillboardCatalog.ForFolder("SCN0022").Frames.Length);
            Assert.IsNull(SceneFlameBillboardCatalog.ForFolder("SCN0023"));
        }

        [Test]
        public void RealData_Scn0024_Sign_Frames_And_Searchlight_Match_The_Original()
        {
            var root = MapobjDir();
            if (root == null) Assert.Ignore("SCENE/MAPOBJ data root not found — real-data row needs the game data (data_root.txt)");

            // 1) 5 張換幀貼圖真的在 biaodonghua 資料夾裡(catalog 幀名對得上磁碟)。
            var signDir = Path.Combine(root, "XUEJING/BIAODONGHUA");
            var sign = SceneMapobjTexAnimCatalog.Find("SCN0024", "BIAODONGHUA");
            foreach (var f in sign.Frames)
                Assert.IsTrue(File.Exists(Path.Combine(signDir, f)), "XUEJING/BIAODONGHUA/" + f + " 不存在 → 換幀會退回佔位貼圖");
            // 官方硬編的 5 張裡 01 與 03 是同一個檔,所以視覺上只有 4 個顏色 —— 這是資料本身的事實,
            // 不是我們漏抄:若哪天檔案不同了,這條會紅,提醒重新確認 catalog。
            Assert.AreEqual(new FileInfo(Path.Combine(signDir, "XUEJING_DONGHUA_BIAO01_.DDS")).Length,
                            new FileInfo(Path.Combine(signDir, "XUEJING_DONGHUA_BIAO03_.DDS")).Length);

            // 2) MSH 材質 biao1_.dds 只是作者留的佔位:它存在(所以招牌不會變白),但 exe 從來不用它。
            var signMats = MshLoader.ReadMaterialTable(File.ReadAllBytes(Path.Combine(signDir, "BIAODONGHUA.MSH")));
            Assert.AreEqual(1, signMats.Count);
            Assert.IsTrue(MshLoader.IsOfficialTransparent(FlagOf(signMats, "biao1_.dds")), "招牌是官方透明批");
            Assert.IsTrue(File.Exists(Path.Combine(signDir, "BIAO1_.DDS")), "佔位圖存在 → 症狀是「不變色」而不是「變白」");

            // 3) 探照燈:官方旗標 0x1(所以 OfficialMaterialAlpha 才對,不是 additive)。
            var beamDir = Path.Combine(root, "XUEJING/DONGHUA");
            var beamMats = MshLoader.ReadMaterialTable(File.ReadAllBytes(Path.Combine(beamDir, "DONGHUA.MSH")));
            Assert.AreEqual(1, beamMats.Count);
            Assert.IsTrue(MshLoader.IsOfficialTransparent(FlagOf(beamMats, "dengguang_.dds")));

            // 4) 三顆光暈的 guang_.tga 在場景資料夾(不是 MAPOBJ 下)。
            var guang = Path.Combine(Path.Combine(SdoExtracted.Root, "SCENE/SCN0024"), "GUANG_.TGA");
            Assert.IsTrue(File.Exists(guang), "SCENE/SCN0024/GUANG_.TGA 不在 → 三顆光暈不會生出來");
        }

        [Test]
        public void RealData_Scn0024_Searchlight_Bind_Scale_Must_Not_Be_Dropped()
        {
            var root = MapobjDir();
            if (root == null) Assert.Ignore("SCENE/MAPOBJ data root not found");

            // 探照燈的長軸被 HRC rest bind 拉長 ~3.9 倍 —— 那個拉長就是光柱本身。通用防呆
            // (maxScale > 2 且 mesh 不小於 80 單位就丟掉 scale)剛好會誤殺它,所以 ScreenGameplay 開了
            // SCN0024/DONGHUA 的例外。這條測試釘住「例外仍然必要」的前提:bind scale 真的 > 2、
            // mesh 真的 >= 80,一旦哪天 loader 改了讓通用規則自己就會放行,這條會紅,提醒把例外拿掉。
            var hrc = HrcLoader.Load(File.ReadAllBytes(Path.Combine(Path.Combine(root, "XUEJING/DONGHUA"), "DONGHUA.HRC")));
            Assert.AreEqual(3, hrc.BindWorld.Length, "DONGHUA.HRC 有 3 根骨(root / Plane01 / leaf)");
            var s = hrc.BindWorld[hrc.BindWorld.Length - 1].lossyScale;
            float maxScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
            Assert.Greater(maxScale, 2f, "葉骨的 bind scale 是 ~3.9 的長軸拉伸");
        }

        // ── SCN0028 北京之夜 (鸟巢):噴水池水流 + 路燈光暈脈動 + 舞台四顆燈光暈 + 遠景光柱/城市光暈 ──
        // 官方 Scene_LoadBackground case 0x1c 載入七個 niaochao 道具(chuan/deng1~4/feichuan/pengshui)、
        // 四組路燈光暈幀、一張 guang_.tga 光暈與四顆 billboard,並播 1+6 支 EFT;每幀更新
        // StageScene_UpdateBigBillboardSet_004b0fc0 推四個 200 ms 換幀計時器、每 2 秒補一支光柱,
        // 最後每 100 ms 捲噴水池的 U。remake 原本這四項全缺 —— 噴水池不動、路燈沒光、遠景死黑。

        [Test]
        public void SceneTexAnim_Scn0028_Four_Streetlamp_Halos_Pulse_At_200ms()
        {
            // 四組的第 0 張都叫 001_.dds(每組資料夾各有一份),deng1/deng2 三張、deng3/deng4 兩張。
            var expect = new (string Mesh, int Count, string Last)[]
            {
                ("DENG1_", 3, "NIAOCHAO_DENG1003_.dds"),
                ("DENG2_", 3, "NIAOCHAO_DENG2003_.dds"),
                ("DENG3_", 2, "NIAOCHAO_DENG3002_.dds"),
                ("DENG4_", 2, "NIAOCHAO_DENG4002_.dds"),
            };
            foreach (var (mesh, count, last) in expect)
            {
                var anim = SceneMapobjTexAnimCatalog.Find("SCN0028", mesh);
                Assert.IsNotNull(anim, mesh + " 少了換幀 → 路燈光暈卡在佔位的 001_.dds 不脈動");
                Assert.AreEqual(count, anim.Frames.Length, mesh);
                Assert.AreEqual("001_.dds", anim.Frames[0], mesh + " 第 0 張是 MSH 材質同名的佔位圖");
                Assert.AreEqual(last, anim.Frames[count - 1], mesh);
                Assert.AreEqual(200f, anim.IntervalMs, 1e-3f, mesh + " 四個計時器都是 200 ms");
                Assert.IsTrue(anim.Transparent, mesh + " 是 DXT3 去背的光暈片(各幀 RGB 相同、只有 alpha 不同)");
                Assert.IsFalse(anim.HoldLast, mesh + " 是無限循環");
            }
            // 噴水池靠 UV 捲動、船/飛船靠 .mot —— 都不能被掛上換幀。
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0028", "PENGSHUI_"));
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0028", "CHUAN"));
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0028", "FEICHUAN"));
            // 名字不能外溢:SCN0021 酒吧的 DENG1 走 SaloonDengMarquee,不是這張表。
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0021", "DENG1_"));
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0024", "DENG1_"));
        }

        [Test]
        public void SceneUvScroll_Scn0028_Fountain_Flows_In_U_And_Defers_To_Official_Flags()
        {
            // 每 100 ms:U += _DAT_00589048 (0.05)、V 明確寫 0 ⇒ +0.5 UV/s 在 U(到 1.0 歸零)。
            // 捲 U 而不是 V:mesh 的 U 跑到 1.364(超過 1 → 在 U 上重複),V 只有 0.024~0.896 一段。
            var speed = SceneMapobjUvScrollCatalog.Find("SCN0028", "PENGSHUI_");
            Assert.AreEqual(0.5f, speed.x, 1e-6f, "噴水池沒在流 → 少了這筆");
            Assert.AreEqual(0f, speed.y, 1e-6f, "官方 V 明確寫 0");
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.OfficialMaterialAlpha,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0028", "PENGSHUI_"));
            Assert.IsFalse(SceneMapobjUvScrollCatalog.UsesAdditiveOverlay("SCN0028", "PENGSHUI_"));
            // 同場景其他道具不捲、不改材質。
            foreach (var other in new[] { "DENG1_", "DENG2_", "DENG3_", "DENG4_", "CHUAN", "FEICHUAN" })
            {
                Assert.AreEqual(0f, SceneMapobjUvScrollCatalog.Find("SCN0028", other).sqrMagnitude, 1e-6f, other);
                Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.KeepMaterial,
                    SceneMapobjUvScrollCatalog.FindRenderMode("SCN0028", other), other);
            }
            // 春天那座噴水池是捲 V,不能被這條 U 覆蓋(兩座是不同軸)。
            Assert.AreEqual(0f, SceneMapobjUvScrollCatalog.Find("SCN0025", "CHUNTIANDONGHUA").x, 1e-6f);
        }

        [Test]
        public void SceneFlames_Scn0028_Has_Four_Static_Streetlamp_Halos()
        {
            var set = SceneFlameBillboardCatalog.ForFolder("SCN0028");
            Assert.IsNotNull(set, "四顆光暈是 billboard,不是 mapobj mesh → 少了整組,燈桿就只有桿子不發光");
            Assert.AreEqual("SCENE/SCN0028", set.FramesDir, "guang_.tga 在場景自己的資料夾");
            Assert.AreEqual(1, set.Frames.Length, "場景 0x1c 的更新沒有換這張");
            Assert.AreEqual("GUANG_.TGA", set.Frames[0]);
            Assert.AreEqual(4, set.Billboards.Length, "param_1[0x65..0x68] 四顆");
            foreach (var b in set.Billboards) Assert.AreEqual(100f, b.Size, 1e-3f, "0x42c80000 = 100");
            Assert.AreEqual(468.834f, set.Billboards[0].X, 1e-3f);
            Assert.AreEqual(137.709f, set.Billboards[0].Y, 1e-3f);
            Assert.AreEqual(467.449f, set.Billboards[0].Z, 1e-3f);
            Assert.AreEqual(-185.057f, set.Billboards[1].X, 1e-3f);
            Assert.AreEqual(-269.018f, set.Billboards[2].X, 1e-3f);
            Assert.AreEqual(-180.453f, set.Billboards[2].Z, 1e-3f);
            Assert.AreEqual(243.408f, set.Billboards[3].X, 1e-3f);
            Assert.AreEqual(-48.953f, set.Billboards[3].Z, 1e-3f);
            // 別的場景那兩組不能被動到(同一張表)。
            Assert.AreEqual(3, SceneFlameBillboardCatalog.ForFolder("SCN0024").Billboards.Length);
            Assert.AreEqual(3, SceneFlameBillboardCatalog.ForFolder("SCN0022").Billboards.Length);
        }

        [Test]
        public void SceneEft_Scn0028_Pillars_Stagger_Every_Two_Seconds_And_Six_City_Halos_Start_Together()
        {
            var fx = SceneEftCatalog.ForFolder("SCN0028");
            Assert.AreEqual(10, fx.Count, "4 支光柱 + 6 團光暈");

            var pillars = new System.Collections.Generic.List<SceneEftPlacement>();
            var halos = new System.Collections.Generic.List<SceneEftPlacement>();
            foreach (var e in fx)
            {
                if (e.Eft == "stage28_dengzhu") pillars.Add(e);
                else if (e.Eft == "stage28_guangyun") halos.Add(e);
                else Assert.Fail("SCN0028 只該有 stage28_dengzhu / stage28_guangyun,出現了 " + e.Eft);
            }
            Assert.AreEqual(4, pillars.Count);
            Assert.AreEqual(6, halos.Count);

            // 光柱:載入時 1 支,之後 >2000/4000/6000 ms 各補一支;scale 90 (0x42b40000)、Euler (0,0,90)。
            var delays = new[] { 0, 2000, 4000, 6000 };
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(delays[i], pillars[i].SpawnDelay, "第 " + (i + 1) + " 支光柱的延遲");
                Assert.AreEqual(90f, pillars[i].Scale, 1e-3f);
                Assert.AreEqual(219.362f, pillars[i].Y, 1e-3f, "四支同高(y 常數 0x435b5cac)");
                Assert.AreEqual(90f, pillars[i].Ez, 1e-3f);
            }
            Assert.AreEqual(293.920f, pillars[0].X, 1e-3f);
            Assert.AreEqual(3488.677f, pillars[0].Z, 1e-3f);
            Assert.AreEqual(1509.139f, pillars[3].X, 1e-3f);

            // 光暈:六團全部在載入時一起播(無延遲),scale 1500 (0x44bb8000)。
            foreach (var h in halos)
            {
                Assert.AreEqual(0, h.SpawnDelay, "光暈不分批");
                Assert.AreEqual(1500f, h.Scale, 1e-3f);
                Assert.AreEqual(90f, h.Ez, 1e-3f);
            }
            Assert.AreEqual(7.095f, halos[0].X, 1e-3f);
            Assert.AreEqual(329.066f, halos[0].Y, 1e-3f);
            Assert.AreEqual(1829.695f, halos[5].X, 1e-3f);
            Assert.AreEqual(2380.157f, halos[5].Z, 1e-3f);
        }

        [Test]
        public void RealData_Scn0028_Streetlamp_And_Fountain_Assets_Match_The_Original()
        {
            var root = MapobjDir();
            if (root == null) Assert.Ignore("SCENE/MAPOBJ data root not found — real-data row needs the game data (data_root.txt)");

            // 1) 四組換幀貼圖真的在各自的道具資料夾裡。資料夾從 SceneMapobjCatalog 推導(runtime 就是這樣找的),
            //    不在測試裡另外硬編一份 —— 否則「幀名對、但資料夾錯」這種失效模式抓不到。
            int checkedGroups = 0;
            foreach (var g in SceneMapobjCatalog.ForFolder("SCN0028"))
            {
                var anim = SceneMapobjTexAnimCatalog.Find("SCN0028", Path.GetFileNameWithoutExtension(g.Msh));
                if (anim == null) continue;
                checkedGroups++;
                foreach (var f in anim.Frames)
                    Assert.IsTrue(File.Exists(Path.Combine(Path.Combine(root, g.Folder), f)),
                        g.Folder + "/" + f + " 不存在 → 換幀會退回佔位貼圖(路燈光暈不脈動)");
            }
            Assert.AreEqual(4, checkedGroups, "四盞路燈都要接上換幀");

            // 2) 噴水池:官方材質旗標必須是透明批(OfficialMaterialAlpha 才成立),而且它「沒有」.mot ——
            //    這正是「它只能靠 UV 捲動來動」的前提。哪天資料變了,這條會紅。
            var fountainDir = Path.Combine(root, "NIAOCHAO/PENGSHUI");
            var fountainMats = MshLoader.ReadMaterialTable(File.ReadAllBytes(Path.Combine(fountainDir, "PENGSHUI_.MSH")));
            Assert.AreEqual(1, fountainMats.Count);
            Assert.IsTrue(MshLoader.IsOfficialTransparent(FlagOf(fountainMats, "pengshui_.dds")), "噴水池是官方透明批");
            foreach (var g in SceneMapobjCatalog.ForFolder("SCN0028"))
                if (g.Folder.EndsWith("PENGSHUI", StringComparison.OrdinalIgnoreCase))
                    Assert.IsNull(g.Mot, "噴水池沒有 .mot,它的動全靠 UV 捲動");

            // 3) 四顆光暈的 guang_.tga 在場景資料夾(不是 MAPOBJ 下)。
            Assert.IsTrue(File.Exists(Path.Combine(Path.Combine(SdoExtracted.Root, "SCENE/SCN0028"), "GUANG_.TGA")),
                "SCENE/SCN0028/GUANG_.TGA 不在 → 舞台四周四顆光暈不會生出來");

            // 4) 兩支 EFT 檔真的存在(id 0x5d/0x5e 從 exe 名稱表解出來的名字)。
            foreach (var name in new[] { "STAGE28_DENGZHU.EFT", "STAGE28_GUANGYUN.EFT" })
                Assert.IsTrue(File.Exists(Path.Combine(Path.Combine(SdoExtracted.Root, "3DEFT"), name)),
                    "3DEFT/" + name + " 不在 → 遠景光柱/光暈生不出來");
        }

        // ── 其餘場景漏掛的動態(反編譯逐場景比對後補齊) ──────────────────────────────────────────
        // 一次掃完 SCN0000..SCN0030 的 Scene_LoadBackground case 與每幀更新函式,把「官方有、remake 沒有」
        // 的換幀/UV 捲動/光暈 billboard/EFT 全部補上。以下每條釘住一個場景的參數來源。

        [Test]
        public void SceneTexAnim_Scn0019_CeilingRig_Cycles_Three_Frames_At_200ms()
        {
            var rig = SceneMapobjTexAnimCatalog.Find("SCN0019", "SHAN");
            Assert.IsNotNull(rig, "少了換幀 → 燈架卡在第 1 幀不閃");
            Assert.AreEqual(3, rig.Frames.Length, "param_1[0x4e] 是 3 格");
            Assert.AreEqual(200f, rig.IntervalMs, 1e-3f, "StageScene_Update3Frame_004b0940 的計時器是 200 ms");
            Assert.IsTrue(rig.Transparent, "官方材質旗標 0x1 = 透明批");
            Assert.IsFalse(rig.HoldLast);
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0019", "ZHUAN"), "只有燈架換幀");
        }

        [Test]
        public void SceneTexAnim_Scn0022_Two_Tombs_Alternate_Every_Second_In_Antiphase()
        {
            var d1 = SceneMapobjTexAnimCatalog.Find("SCN0022", "GUANG4");
            var d2 = SceneMapobjTexAnimCatalog.Find("SCN0022", "DONGHUA2");
            Assert.IsNotNull(d1, "第一座墓穴少了換幀 → 火光凍住");
            Assert.IsNotNull(d2, "第二座墓穴少了換幀");
            foreach (var d in new[] { d1, d2 })
            {
                Assert.AreEqual(2, d.Frames.Length, "兩張來回");
                Assert.AreEqual(1000f, d.IntervalMs, 1e-3f, "共用計數器 DAT_00678588,1000 ms 一次");
                Assert.IsFalse(d.Transparent, "兩幀 alpha 全 255 → 不透明換幀");
            }
            // 反相是「檔案內容對調」做出來的:兩支都照 01_ → 02_ 的檔名順序填,不能自己再加相位偏移。
            StringAssert.Contains("mubei01_", d1.Frames[0]);
            StringAssert.Contains("mubei02_", d1.Frames[1]);
            StringAssert.Contains("Dong2_mubei01_", d2.Frames[0]);
            StringAssert.Contains("Dong2_mubei02_", d2.Frames[1]);
            // 鬼火/飛鬼走 billboard 目錄,不能也被掛上 mapobj 換幀。
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0022", "SHAN"));
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0022", "LABA11"));
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0022", "SHEGUANG"), "射光不換貼圖");
        }

        [Test]
        public void SceneTexAnim_Scn0026_Bonfire_And_Sidelights_Run_At_Their_Own_Rates()
        {
            var fire = SceneMapobjTexAnimCatalog.Find("SCN0026", "HUO");
            Assert.IsNotNull(fire, "營火少了換幀 → 火焰靜止,而且畫的是佔位的第 10 張圖");
            Assert.AreEqual(9, fire.Frames.Length);
            Assert.AreEqual("lanqiuchang_huo001.dds", fire.Frames[0]);
            Assert.AreEqual("lanqiuchang_huo009.dds", fire.Frames[8]);
            Assert.AreEqual(50f, fire.IntervalMs, 1e-3f, "0x32 = 50 ms");
            Assert.IsTrue(fire.Transparent);

            var lamp = SceneMapobjTexAnimCatalog.Find("SCN0026", "XIAODENG");
            Assert.IsNotNull(lamp, "小燈少了換幀 → 停在第 1 幀");
            Assert.AreEqual(3, lamp.Frames.Length);
            Assert.AreEqual(500f, lamp.IntervalMs, 1e-3f);
            Assert.IsTrue(lamp.Transparent);
            // 車子是 .mot 驅動的,不換幀。
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0026", "CHE1"));
        }

        [Test]
        public void SceneTexAnim_Scn0029_BarNeon_Cycles_Eight_Frames_At_200ms()
        {
            var neon = SceneMapobjTexAnimCatalog.Find("SCN0029", "JIUBA");
            Assert.IsNotNull(neon, "吧台霓虹少了換幀 → 永遠停在 MSH 材質那張 00014.dds");
            Assert.AreEqual(8, neon.Frames.Length, "索引 (i+1)&7 → 8 張");
            Assert.AreEqual("00014.dds", neon.Frames[0], "第 0 張就是 MSH 的佔位材質");
            Assert.AreEqual("0008_.dds", neon.Frames[7]);
            Assert.AreEqual(200f, neon.IntervalMs, 1e-3f);
            Assert.IsFalse(neon.Transparent, "0002_~0008_ 的 alpha 全 255 → 不透明畫面");
            // 大螢幕走 UV 捲動(而且是階梯式),不是換幀。
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0029", "PINGMU"));
        }

        [Test]
        public void SceneUvScroll_Scn0018_RotatingLight_Scrolls_U_And_Keeps_Its_Material()
        {
            var speed = SceneMapobjUvScrollCatalog.Find("SCN0018", "ZHUANDENG");
            Assert.AreEqual(0.3f, speed.x, 1e-6f, "_DAT_0058903c = 0.03 / 100 ms");
            Assert.AreEqual(0f, speed.y, 1e-6f, "官方 V 明確寫 0");
            // 材質旗標是 0x0(不透明批)→ 套 OfficialMaterialAlpha 會是 no-op,徒增混淆。
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.KeepMaterial,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0018", "ZHUANDENG"));
            // 同場景四個換幀道具不捲。
            foreach (var other in new[] { "NIHONG", "BOAT_SCREEN", "SHUIMO", "WATER" })
                Assert.AreEqual(0f, SceneMapobjUvScrollCatalog.Find("SCN0018", other).sqrMagnitude, 1e-6f, other);
        }

        [Test]
        public void SceneUvScroll_Scn0016_Skyline_Is_The_Only_Officially_Forced_Additive_Prop()
        {
            // 官方 case 0x10 對 objects[30]=chengshi 手寫 +0xc=4 / +0x10=1 → SRCALPHA/ONE = 加法。
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.AdditiveOverlay,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0016", "CHENGSHI"));
            Assert.IsTrue(SceneMapobjUvScrollCatalog.UsesAdditiveOverlay("SCN0016", "CHENGSHI"));
            Assert.AreEqual(0f, SceneMapobjUvScrollCatalog.Find("SCN0016", "CHENGSHI").sqrMagnitude, 1e-6f,
                "天際線不捲 UV,這筆只帶 render mode");
            // 同場景的探照燈仍走 SpotGlow,不能被改成加法。
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.SpotGlow,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0016", "JIGUANG1"));
        }

        [Test]
        public void SceneFlames_Scn0001_And_Scn0005_Have_Static_Guangxiao_Halos()
        {
            var street = SceneFlameBillboardCatalog.ForFolder("SCN0001");
            Assert.IsNotNull(street, "兩盞路燈的燈頭光暈是 billboard,不是 mesh → 少了就只有燈桿");
            Assert.AreEqual("SCENE/SCN0001", street.FramesDir);
            Assert.AreEqual("GUANGXIAO_.TGA", street.Frames[0]);
            Assert.AreEqual(2, street.Billboards.Length);
            Assert.AreEqual(77.000f, street.Billboards[0].X, 1e-3f);
            Assert.AreEqual(-626.482f, street.Billboards[1].X, 1e-3f);
            foreach (var b in street.Billboards)
            {
                Assert.AreEqual(147.475f, b.Y, 1e-3f, "兩盞同高");
                Assert.AreEqual(100f, b.Size, 1e-3f, "0x42c80000 = 100");
            }

            var xmas = SceneFlameBillboardCatalog.ForFolder("SCN0005");
            Assert.IsNotNull(xmas);
            Assert.AreEqual(1, xmas.Billboards.Length);
            Assert.AreEqual(-97.7f, xmas.Billboards[0].X, 1e-3f);
            Assert.AreEqual(100f, xmas.Billboards[0].Size, 1e-3f);
            // 沒有光暈的場景仍要回 null(這張表是白名單,不能變成「每個場景都給一顆」)。
            Assert.IsNull(SceneFlameBillboardCatalog.ForFolder("SCN0000"));
            Assert.IsNull(SceneFlameBillboardCatalog.ForFolder("SCN0023"));
        }

        [Test]
        public void SceneEft_Scn0029_Has_Ten_Streetlamp_Glows_Plus_The_Ctor_Carnival_Pair()
        {
            var fx = SceneEftCatalog.ForFolder("SCN0029");
            Assert.AreEqual(12, fx.Count, "10 支 dengguang + ctor 的 2 支 kuanghuan");
            int lamps = 0;
            foreach (var e in fx)
            {
                if (e.Eft != "dengguang") continue;
                lamps++;
                Assert.AreEqual(251f, e.Y, 1e-3f, "十支同高(y 常數)");
                Assert.AreEqual(100f, e.Scale, 1e-3f, "0x42c80000 = 100");
                Assert.AreEqual(0, e.SpawnDelay, "十支載入時一起播,沒有 SCN0028 那種分批延遲");
                Assert.AreEqual(0f, e.Ex + e.Ey + e.Ez, 1e-3f, "旋轉恆 0");
            }
            Assert.AreEqual(10, lamps);
            Assert.AreEqual(-405f, fx[0].X, 1e-3f);
            Assert.AreEqual(628f, fx[9].X, 1e-3f);
            Assert.AreEqual("kuanghuan1", fx[10].Eft, "ctor 那兩支排在 case body 之後");
            Assert.AreEqual("kuanghuan2", fx[11].Eft);
        }

        [Test]
        public void SceneEft_Scn0005_Adds_Three_Staggered_Grav_Pillars_Next_To_The_Snow()
        {
            var fx = SceneEftCatalog.ForFolder("SCN0005");
            Assert.AreEqual(4, fx.Count, "snow + grav_1/2/3");
            Assert.AreEqual("snow", fx[0].Eft);
            Assert.AreEqual(30f, fx[0].Scale, 1e-3f, "雪的 scale 不能被動到");
            var expect = new[] { ("grav_1", 2000), ("grav_2", 4000), ("grav_3", 6000) };
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(expect[i].Item1, fx[i + 1].Eft);
                Assert.AreEqual(expect[i].Item2, fx[i + 1].SpawnDelay, "每 2 秒接力一支");
                Assert.AreEqual(8f, fx[i + 1].Scale, 1e-3f, "0x41000000 = 8.0");
            }
        }

        [Test]
        public void SceneAvatars_Scn0018_Has_Nine_Deck_Passengers_On_Two_Levels()
        {
            var npcs = SceneAvatarCatalog.ForFolder("SCN0018");
            Assert.AreEqual(9, npcs.Count, "少了整組,甲板是空的");
            int lower = 0, upper = 0;
            foreach (var n in npcs)
            {
                if (n.Pos.y < 0f) lower++; else upper++;
                Assert.IsNotNull(n.Mot, "更新函式每幀跑 LoadMotionsCycle → 九個都會動");
                StringAssert.DoesNotContain("DJ", n.Msh, "SCN0018 的 type 表沒有 4 (DJ)");
            }
            Assert.AreEqual(4, lower, "下層甲板 y=-15 四人");
            Assert.AreEqual(5, upper, "上層甲板 y=120/115 五人");
            // SCN0017 那組不能被動到(同一張表)。
            Assert.AreEqual(10, SceneAvatarCatalog.ForFolder("SCN0017").Count);
        }

        [Test]
        public void RealData_OtherScenes_Missing_Animation_Assets_All_Resolve_On_Disk()
        {
            var root = MapobjDir();
            if (root == null) Assert.Ignore("SCENE/MAPOBJ data root not found — real-data row needs the game data (data_root.txt)");

            // 1) 新補的每一組換幀,幀檔都要在該道具自己的資料夾裡(runtime 就是這樣找的)。
            var expectGroups = new System.Collections.Generic.Dictionary<string, int>
            { ["SCN0019"] = 1, ["SCN0022"] = 2, ["SCN0026"] = 2, ["SCN0029"] = 1 };
            foreach (var kv in expectGroups)
            {
                int found = 0;
                foreach (var g in SceneMapobjCatalog.ForFolder(kv.Key))
                {
                    var anim = SceneMapobjTexAnimCatalog.Find(kv.Key, Path.GetFileNameWithoutExtension(g.Msh));
                    if (anim == null) continue;
                    found++;
                    foreach (var f in anim.Frames)
                        Assert.IsTrue(File.Exists(Path.Combine(Path.Combine(root, g.Folder), f)),
                            g.Folder + "/" + f + " 不存在 → 換幀會退回佔位貼圖");
                }
                Assert.AreEqual(kv.Value, found, kv.Key + " 接上換幀的道具數不對");
            }

            // 2) 兩顆/一顆 guangxiao 光暈的 TGA 在場景資料夾裡(不是 MAPOBJ 下)。
            foreach (var folder in new[] { "SCN0001", "SCN0005" })
            {
                var set = SceneFlameBillboardCatalog.ForFolder(folder);
                Assert.IsTrue(File.Exists(Path.Combine(Path.Combine(SdoExtracted.Root, "SCENE/" + folder), set.Frames[0])),
                    "SCENE/" + folder + "/" + set.Frames[0] + " 不在 → 光暈不會生出來");
            }

            // 3) 新引用的 EFT 檔都在。
            foreach (var name in new[] { "DENGGUANG.EFT", "GRAV_1.EFT", "GRAV_2.EFT", "GRAV_3.EFT" })
                Assert.IsTrue(File.Exists(Path.Combine(Path.Combine(SdoExtracted.Root, "3DEFT"), name)),
                    "3DEFT/" + name + " 不在");

            // 4) SCN0018 旋轉燈的官方材質旗標必須是 0x0(不透明批)—— 這是「不套 OfficialMaterialAlpha」的前提。
            var zd = Path.Combine(root, "18_BOAT/ZHUANDENG");
            var zdMats = MshLoader.ReadMaterialTable(File.ReadAllBytes(Path.Combine(zd, "ZHUANDENG.MSH")));
            Assert.IsFalse(MshLoader.IsOfficialTransparent(FlagOf(zdMats, "di1.dds")), "旗標若變成透明批要重新評估 render mode");

            // 5) SCN0018 的 9 個 NPC 用的是 SCN0017 已在用的同一組資產,不需要新檔案。
            var avatarDir = Path.Combine(SdoExtracted.Root, "AVATAR");
            foreach (var n in SceneAvatarCatalog.ForFolder("SCN0018"))
                Assert.IsTrue(File.Exists(Path.Combine(avatarDir, n.Msh)), "AVATAR/" + n.Msh + " 不在");
        }

        // ── UV 動作的兩種新模式:正弦擺盪 / 停頓+快掃 ─────────────────────────────────────────────
        // 官方有三種 UV 動作,remake 原本只有等速一種,所以 SCN0004(海面/浪來回盪)、SCN0012/0013(廣告看板
        // 停 2 秒換一幅)、SCN0029(大螢幕停 5 秒滑一格)這四個場景連條目都放不進去、完全是死的。

        [Test]
        public void SceneUvScroll_Scn0004_Sea_And_Shore_Rock_As_Sine_Not_Scroll()
        {
            Assert.IsTrue(SceneMapobjUvScrollCatalog.TryFindTarget("SCN0004", "SEA", out var sea));
            Assert.AreEqual(SceneMapobjUvScrollCatalog.Motion.Sine, sea.Motion);
            Assert.AreEqual(0.5f, sea.Amplitude.x, 1e-6f, "海面是 U 軸 ±0.5(mesh 的 U 才 tiling)");
            Assert.AreEqual(0f, sea.Amplitude.y, 1e-6f);
            Assert.AreEqual(0.593f, sea.Speed.x, 1e-6f, "0.001 rad/frame × 593 fps");
            Assert.IsTrue(sea.Animates);

            Assert.IsTrue(SceneMapobjUvScrollCatalog.TryFindTarget("SCN0004", "LANG", out var wave));
            Assert.AreEqual(SceneMapobjUvScrollCatalog.Motion.Sine, wave.Motion);
            Assert.AreEqual(-0.25f, wave.Amplitude.y, 1e-6f, "岸浪是 V 軸 −0.25(官方 −sin×0.25)");
            Assert.AreEqual(2.372f, wave.Speed.y, 1e-6f, "0.004 rad/frame × 593 fps = 浪比海快 4 倍");

            // Find() 只回報「等速」速度 —— 正弦目標的 Speed 是 rad/s,餵給等速捲動會是錯的,所以必須回 zero。
            Assert.AreEqual(Vector2.zero, SceneMapobjUvScrollCatalog.Find("SCN0004", "SEA"));
            Assert.AreEqual(Vector2.zero, SceneMapobjUvScrollCatalog.Find("SCN0004", "LANG"));
            // 同場景的換幀道具不能被掃到。
            Assert.IsFalse(SceneMapobjUvScrollCatalog.TryFindTarget("SCN0004", "SEA_UP", out _));

            // ★ 兩支都必須是 OfficialMaterialAlpha。wave_.dds 會被 LooksLikeAdditiveGlow 誤判成加法,而加法那支
            // shader(Sdo/UnlitAdditiveOverlay)帶 Offset -1,-1 深度偏移 —— 岸浪是一片 1261×3051、幾乎與視線
            // 平行的大水面,斜率式偏移會把它整片往鏡頭方向擠、穿過棧橋與房子,再用加法把藍白刷上去
            // (實機截圖確認過:房子下緣與棧橋交界整條被霧化)。這條測試釘住修正,免得又被改回 KeepMaterial。
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.OfficialMaterialAlpha, sea.Mode, "海面");
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.OfficialMaterialAlpha, wave.Mode, "岸浪");
            Assert.IsFalse(SceneMapobjUvScrollCatalog.UsesAdditiveOverlay("SCN0004", "LANG"), "岸浪絕不能是加法");
            Assert.IsFalse(SceneMapobjUvScrollCatalog.UsesAdditiveOverlay("SCN0004", "SEA"));

            // ★ 只有 LANG(岸浪)排到場景之前,SEA(大洋)不能跟著壓 —— 判準是「這片水離甲板多高」:
            //   SEA 平躺在 y = −48.9,比甲板(y ≈ 0)低 49 單位,相機一定在水面之上,正常深度測試本來就不會
            //   讓它爬上甲板;而沿岸低於水面的沙灘會被它正確蓋住 = 「海水蓋在沙灘上」。壓了它反而會變成
            //   沙灘蓋掉海水。LANG 的包圍盒 y −69.5..−5.7,上緣幾乎跟甲板同高,會從木板下面戳出來,
            //   所以只有它要壓到 2400(< 場景 SceneVertexCutout 的 AlphaTest 2450)。
            //   房子/甲板與沙灘同屬一顆 SCENE.MSH(單一 renderer、單一佇列),佇列分不開它們,分開設定
            //   這兩片水是唯一能同時滿足「擋住甲板」與「別擋住沙灘」的維度。
            Assert.AreEqual(0, sea.Queue, "大洋不能壓佇列 —— 壓了沙灘就會反過來蓋掉海水");
            Assert.AreEqual(SceneMapobjUvScrollCatalog.WaterBehindSceneQueue, wave.Queue, "岸浪要排在場景之前");
            Assert.Less(wave.Queue, 2450, "必須小於場景 SceneVertexCutout 的 AlphaTest 佇列,否則會打上甲板");
            Assert.Greater(wave.Queue, 2000, "又不能低到被不透明批之前的東西蓋掉");
            // 其他場景的水/透明道具沒有這個覆寫(預設 0 = 沿用 shader 佇列),別讓它外溢。
            SceneMapobjUvScrollCatalog.TryFindTarget("SCN0025", "CHUNTIANDONGHUA", out var spring);
            Assert.AreEqual(0, spring.Queue, "春天噴水池不套佇列覆寫");
            SceneMapobjUvScrollCatalog.TryFindTarget("SCN0028", "PENGSHUI_", out var niaochao);
            Assert.AreEqual(0, niaochao.Queue, "北京之夜噴水池不套佇列覆寫");
        }

        [Test]
        public void RealData_Scn0004_Water_Materials_Are_Officially_Transparent_Not_Additive()
        {
            var root = MapobjDir();
            if (root == null) Assert.Ignore("SCENE/MAPOBJ data root not found");

            // OfficialMaterialAlpha 只對「官方旗標標成透明」的材質生效,所以這兩支的旗標必須真的是非 0 ——
            // 那正是「官方畫的是一般 SrcAlpha/InvSrcAlpha,不是加法」的證據。旗標若哪天變 0,上面那條
            // render-mode 就會靜默失效(變成 no-op),這條會先紅出來。
            var sea = MshLoader.ReadMaterialTable(File.ReadAllBytes(Path.Combine(Path.Combine(root, "SEA"), "SEA.MSH")));
            Assert.IsTrue(MshLoader.IsOfficialTransparent(FlagOf(sea, "haishuei2_.dds")), "海面是官方透明批");
            var lang = MshLoader.ReadMaterialTable(File.ReadAllBytes(Path.Combine(Path.Combine(root, "BEACH"), "LANG.MSH")));
            Assert.IsTrue(MshLoader.IsOfficialTransparent(FlagOf(lang, "wave_.dds")), "岸浪是官方透明批");
        }

        [Test]
        public void SceneUvScroll_AdBoards_And_Screen_Are_DwellStep_With_Official_Timings()
        {
            foreach (var folder in new[] { "SCN0012", "SCN0013" })
            {
                Assert.IsTrue(SceneMapobjUvScrollCatalog.TryFindTarget(folder, "FIFA_GUANGGAO", out var board), folder);
                Assert.AreEqual(SceneMapobjUvScrollCatalog.Motion.DwellStep, board.Motion, folder);
                Assert.AreEqual(2000f, board.DwellMs, 1e-3f, folder + " 停 2 秒");
                Assert.AreEqual(0.5f, board.Step.y, 1e-6f, folder + " 一次掃半格(貼圖上下各一幅廣告)");
                Assert.AreEqual(0f, board.Step.x, 1e-6f, folder + " U 恆 0");
                Assert.AreEqual(1.775f, board.Speed.y, 1e-6f, folder + " 0.003/frame × 593 fps");
                Assert.AreEqual(Vector2.zero, board.Start, folder + " 從 V=0 那幅開始");
                Assert.IsTrue(board.Animates, folder);
                Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.KeepMaterial, board.Mode, folder + " 材質旗標 0x0");
            }
            // 兩列同 key 不同 folder,不能互相干擾。
            Assert.IsFalse(SceneMapobjUvScrollCatalog.TryFindTarget("SCN0014", "FIFA_GUANGGAO", out _));

            Assert.IsTrue(SceneMapobjUvScrollCatalog.TryFindTarget("SCN0029", "PINGMU", out var screen));
            Assert.AreEqual(SceneMapobjUvScrollCatalog.Motion.DwellStep, screen.Motion);
            Assert.AreEqual(5000f, screen.DwellMs, 1e-3f, "每格停 5 秒");
            Assert.AreEqual(-0.25f, screen.Step.y, 1e-6f, "四格 → 一階 0.25,而且官方是遞減(-=)");
            Assert.Less(screen.Speed.y, 0f, "方向必須是負的,寫成正的會反著跑");
            Assert.AreEqual(-0.593f, screen.Speed.y, 1e-6f, "0.001/frame × 593 fps");
            // 同場景的吧台霓虹走換幀,不是 UV。
            Assert.IsFalse(SceneMapobjUvScrollCatalog.TryFindTarget("SCN0029", "JIUBA", out _));
        }

        [Test]
        public void SceneUvScroll_RenderModeOnly_Entries_Do_Not_Animate()
        {
            // 只為了帶 render mode 而存在的條目不能生出 MapobjUvScroll(ScreenGameplay 用 Animates 當守門)。
            foreach (var (folder, key) in new[]
                     { ("SCN0016", "JIGUANG1"), ("SCN0022", "SHEGUANG"), ("SCN0024", "DONGHUA"), ("SCN0016", "CHENGSHI") })
            {
                Assert.IsTrue(SceneMapobjUvScrollCatalog.TryFindTarget(folder, key, out var t), folder + "/" + key);
                Assert.IsFalse(t.Animates, folder + "/" + key + " 不該被當成會動的目標");
            }
            // 會動的那些仍然回 true。
            foreach (var (folder, key) in new[]
                     { ("SCN0025", "CHUNTIANDONGHUA"), ("SCN0028", "PENGSHUI_"), ("SCN0018", "ZHUANDENG") })
            {
                Assert.IsTrue(SceneMapobjUvScrollCatalog.TryFindTarget(folder, key, out var t), folder + "/" + key);
                Assert.IsTrue(t.Animates, folder + "/" + key);
            }
        }

        private static MapobjUvScroll NewScroller(SceneMapobjUvScrollCatalog.Target t)
        {
            var go = new GameObject("uvscroll-test");
            var s = go.AddComponent<MapobjUvScroll>();
            s.Init(new Material[0], t);   // 空材質陣列:狀態機照跑,不需要 shader
            return s;
        }

        [Test]
        public void MapobjUvScroll_DwellStep_Holds_First_Then_Wipes_One_Step_And_Wraps()
        {
            SceneMapobjUvScrollCatalog.TryFindTarget("SCN0012", "FIFA_GUANGGAO", out var board);
            var s = NewScroller(board);
            try
            {
                Assert.AreEqual(0f, s.Offset.y, 1e-5f, "官方是先停再掃,不是一開場就掃");
                s.Step(2.0f);
                Assert.AreEqual(0f, s.Offset.y, 1e-5f, "剛好停滿 2 秒,還沒開始掃");
                // 一秒足夠掃完 0.5 格(0.5 / 1.775 ≈ 0.282 s),掃完就停住,剩下的時間滾進停頓
                s.Step(1.0f);
                Assert.AreEqual(0.5f, s.Offset.y, 1e-4f, "掃完剛好落在 0.5 —— 不會衝過頭");
                s.Step(1.0f);
                Assert.AreEqual(0.5f, s.Offset.y, 1e-4f, "還在停頓");
                s.Step(1.0f);   // 停頓滿 2 秒後再掃 0.5 → 1.0 → 折回 0
                Assert.AreEqual(0f, s.Offset.y, 1e-4f, "第二段掃完回到 0(1.0 折回)");
                Assert.AreEqual(0f, s.Offset.x, 1e-6f, "U 全程不動");
            }
            finally { UnityEngine.Object.DestroyImmediate(s.gameObject); }
        }

        [Test]
        public void MapobjUvScroll_DwellStep_Negative_Direction_Steps_Downward()
        {
            SceneMapobjUvScrollCatalog.TryFindTarget("SCN0029", "PINGMU", out var screen);
            var s = NewScroller(screen);
            try
            {
                s.Step(5.0f);
                Assert.AreEqual(0f, s.Offset.y, 1e-5f, "先停 5 秒");
                s.Step(1.0f);   // 0.25 / 0.593 ≈ 0.42 s 掃完
                Assert.AreEqual(0.75f, s.Offset.y, 1e-4f, "遞減 0.25 → 折回後是 0.75,不是 0.25");
                s.Step(6.0f);
                Assert.AreEqual(0.5f, s.Offset.y, 1e-4f);
                s.Step(6.0f);
                Assert.AreEqual(0.25f, s.Offset.y, 1e-4f);
                s.Step(6.0f);
                Assert.AreEqual(0f, s.Offset.y, 1e-4f, "四格一輪回到起點");
            }
            finally { UnityEngine.Object.DestroyImmediate(s.gameObject); }
        }

        [Test]
        public void MapobjUvScroll_Sine_Oscillates_Around_Zero_And_Is_Never_Folded()
        {
            SceneMapobjUvScrollCatalog.TryFindTarget("SCN0004", "SEA", out var sea);
            var s = NewScroller(sea);
            try
            {
                Assert.AreEqual(0f, s.Offset.x, 1e-5f);
                // 四分之一週期 → 正峰值。ω = 0.593 rad/s,所以 π/2 在 t ≈ 2.649 s。
                s.Step(Mathf.PI * 0.5f / 0.593f);
                Assert.AreEqual(0.5f, s.Offset.x, 1e-3f, "峰值 = 振幅");
                // 再半個週期 → 負峰值。負的偏移「不能」被折進 [0,1),否則零交越時貼圖會瞬移。
                s.Step(Mathf.PI / 0.593f);
                Assert.AreEqual(-0.5f, s.Offset.x, 1e-3f, "負峰值必須保持負值");
                Assert.AreEqual(0f, s.Offset.y, 1e-6f, "V 全程不動");
            }
            finally { UnityEngine.Object.DestroyImmediate(s.gameObject); }
        }

        [Test]
        public void MapobjUvScroll_Linear_Path_Still_Accumulates_And_Wraps()
        {
            // 既有的等速目標不能被新模式弄壞(SCN0028 噴水池 U = +0.5 UV/s)。
            SceneMapobjUvScrollCatalog.TryFindTarget("SCN0028", "PENGSHUI_", out var fountain);
            var s = NewScroller(fountain);
            try
            {
                s.Step(1.0f);
                Assert.AreEqual(0.5f, s.Offset.x, 1e-4f);
                s.Step(1.5f);   // 累積 1.25 → 折回 0.25
                Assert.AreEqual(0.25f, s.Offset.x, 1e-4f, "超過 1 要折回");
                Assert.AreEqual(0f, s.Offset.y, 1e-6f);
            }
            finally { UnityEngine.Object.DestroyImmediate(s.gameObject); }
        }
    }
}
